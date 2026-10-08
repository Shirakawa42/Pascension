using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Pascension.Engine.Actions;
using Pascension.Engine.Core;
using Pascension.Engine.Decisions;
using Shards.Engine;

namespace Shards.Preflight
{
    internal static class SelfTest
    {
        internal static void Run()
        {
            var sequential = Enumerable.Range(0, 32).Select(i => Program.RunGame((ulong)i, "encode")).ToArray();
            var parallel = new Program.Result[32];
            Parallel.For(0, 32, new ParallelOptions { MaxDegreeOfParallelism = 4 },
                i => parallel[i] = Program.RunGame((ulong)i, "engine"));
            for (int i = 0; i < sequential.Length; i++)
                Check(sequential[i].Hash == parallel[i].Hash && sequential[i].Wrapper == parallel[i].Wrapper &&
                    sequential[i].Submissions == parallel[i].Submissions && !sequential[i].Capped,
                    "Replay/worker/encoding equivalence seed " + i);
            for (int seat = 0; seat < 2; seat++) Privacy(seat);
            PublicEntityDistinctions();
            OpponentDefensePrivacy();
            CandidatePublicFields();
            Check(Encoder.CardIds.Distinct().Count() == Encoder.CardIds.Length, "Unique card mapping");
            foreach (int branchLimit in new[] { 2, 8, 32, 64 })
                for (int high = 1; high <= 1000; high++)
                    CheckIntervals(0, high, branchLimit);
            PagingAndDisabled();
            DecisionCoverage(false, 0, 2, 3, 10);
            DecisionCoverage(true, 4, 4, 3, 15);
            DecisionCoverage(true, 0, 3, 2, 10);
            MappedBounds();
            Program.Print(new { passed = true, replaySeeds = 32, workersCompared = new[] { 1, 4 },
                cardDefinitions = Encoder.CardIds.Length, hiddenPermutationSeats = 2,
                observationSchema = Encoder.SchemaVersion,
                observationFixtures = "equal-aggregate-different-entity-state/public-ferrata-threshold/private-hand-order/candidate-zone-temporary-shield",
                intervalCoverageUpperBound = 1000, splitBranches = Adapter.SplitBranches,
                decisionFixtures = "paging130/disabled/ordered-optional/split-full/split-optional",
                wrapperSteps = sequential.Sum(r => r.Wrapper), engineSubmissions = sequential.Sum(r => r.Submissions) });
        }

        private static Adapter AfterDraft(ulong seed)
        {
            var game = new Adapter(seed);
            while (game.Engine.PendingInput.Decision?.Context == "soi.herodraft") game.Step(0);
            return game;
        }

        private static ShardsCard AddCard(Adapter game, ShardsPlayer owner, string definition, ShardsZone zone,
            List<ShardsCard> target)
        {
            var card = new ShardsCard { InstanceId = game.Engine.State.NextInstanceId++, DefId = definition,
                Owner = owner.Index, Zone = zone };
            target.Add(card);
            return card;
        }

        private static void RefreshFixture(Adapter game)
        {
            game.Engine.State.InvalidateCardIndex();
            game.Engine.PendingInput.LegalActions = game.Engine.LegalActions(game.Actor);
            game.Rebuild();
        }

        private static void PublicEntityDistinctions()
        {
            var game = AfterDraft(891);
            var own = game.Engine.State.Players[game.Actor];
            var definitions = ShardsCardDatabase.All.Where(d => d.IsChampion && d.DefenseAura == null &&
                d.ExhaustEffect != null && d.ExhaustGemCost == 0)
                .OrderBy(d => d.Id, StringComparer.Ordinal).Take(2).ToArray();
            var first = AddCard(game, own, definitions[0].Id, ShardsZone.Champions, own.Champions);
            var second = AddCard(game, own, definitions[1].Id, ShardsZone.Champions, own.Champions);
            first.Exhausted = true; first.DamageThisTurn = 1; second.DamageThisTurn = 2;
            RefreshFixture(game);
            var before = Encode(game);
            first.Exhausted = false; second.Exhausted = true;
            first.DamageThisTurn = 2; second.DamageThisTurn = 1;
            RefreshFixture(game);
            var after = Encode(game);
            Check(before.Take(1856).SequenceEqual(after.Take(1856)), "Entity fixture preserves aggregate observation");
            Check(!before.Skip(1856).Take(144).SequenceEqual(after.Skip(1856).Take(144)),
                "Public entity records distinguish equal aggregate board status");
            for (int i = 0; i < 30; i++)
                AddCard(game, own, definitions[0].Id, ShardsZone.Champions, own.Champions);
            RefreshFixture(game);
            var overflow = Encode(game);
            Check(overflow[62] == 32 / 64f && overflow[63] == 8 / 64f, "Entity overflow is explicit");
            Check(game.Candidates.Count > 24, "Entity compression does not prune legal choices");
        }

        private static void OpponentDefensePrivacy()
        {
            var game = AfterDraft(892);
            var opponent = game.Engine.State.Players[1 - game.Actor];
            opponent.Hand.Clear(); opponent.Deck.Clear(); opponent.Discard.Clear();
            opponent.PlayZone.Clear(); opponent.Champions.Clear();
            var guard = AddCard(game, opponent, "ferrata_guard_duel", ShardsZone.Champions, opponent.Champions);
            string homodeus = ShardsCardDatabase.All.First(d => d.Faction == ShardsFaction.Homodeus &&
                !d.CountsAsEveryFaction).Id;
            string neutral = ShardsCardDatabase.All.First(d => d.Faction == ShardsFaction.None).Id;
            for (int i = 0; i < 3; i++) AddCard(game, opponent, homodeus, ShardsZone.Hand, opponent.Hand);
            RefreshFixture(game);
            int beforeDefense = game.Engine.EffectiveDefense(opponent, guard);
            var before = Encode(game);
            opponent.Hand[0].DefId = neutral;
            RefreshFixture(game);
            Check(game.Engine.EffectiveDefense(opponent, guard) != beforeDefense,
                "Fixture changes PUBLIC full-collection enemy defense threshold");
            Check(!before.SequenceEqual(Encode(game)), "Public opponent collection and exact defense must be represented");
            var after = Encode(game);
            opponent.Hand.Reverse();
            Check(after.SequenceEqual(Encode(game)), "Private opponent hand order cannot change public collection representation");
        }

        private static void CandidatePublicFields()
        {
            var game = AfterDraft(893);
            var own = game.Engine.State.Players[game.Actor];
            own.Mastery = 17;
            var card = AddCard(game, own, "datic_robes_duel", ShardsZone.Hand, own.Hand);
            card.FastPlayed = true; card.BanishAtCleanup = true;
            RefreshFixture(game);
            var all = Encode(game);
            int slot = Enumerable.Range(0, game.VisibleCount).First(i =>
                game.Visible(i).Action is ShardsPlayCardAction action && action.CardInstanceId == card.InstanceId);
            int offset = Encoder.ObsDim + slot * Encoder.ActionDim;
            Check(all[offset + 21] == game.Engine.ShieldValue(own, card) / 20f,
                "Candidate encodes authorized dynamic shield");
            Check(all[offset + 29] == ((int)ShardsZone.Hand + 1) / 16f &&
                all[offset + 30] == 1 && all[offset + 31] == 1, "Candidate zone/temporary status");
        }

        private static void MappedBounds()
        {
            string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "shards-map-selftest-" + Environment.ProcessId);
            try
            {
                System.IO.File.WriteAllBytes(path, new byte[128]);
                bool rejected = false;
                try { using var wrong = new MappedBody(path, 127); }
                catch (InvalidOperationException) { rejected = true; }
                Check(rejected, "Mapped file length must match");
                using var body = new MappedBody(path, 128);
                rejected = false;
                try { body.Write(new byte[127], true); }
                catch (InvalidOperationException) { rejected = true; }
                Check(rejected, "Mapped copy length must match");
                body.Dispose();
                rejected = false;
                try { body.Write(new byte[128], true); }
                catch (ObjectDisposedException) { rejected = true; }
                Check(rejected, "Mapped write after dispose must fail");
            }
            finally { System.IO.File.Delete(path); }
        }

        private sealed class Fixture
        {
            internal Adapter Game;
            internal DecisionRequest Request;
            internal List<int> Answer;
        }

        private static Fixture MakeFixture(bool split, int min, int max, int optionCount, bool disabled = false)
        {
            var fixture = new Fixture { Game = new Adapter(99) };
            var game = fixture.Game;
            while (game.Engine.PendingInput.Decision?.Context == "soi.herodraft") game.Step(0);
            fixture.Request = new DecisionRequest { PlayerIndex = game.Actor, Context = split ? "soi.split" : "soi.reorder",
                Kind = DecisionKind.OrderCards, Min = min, Max = max, Ordered = true };
            for (int i = 0; i < optionCount; i++)
                fixture.Request.Options.Add(new DecisionOption(i, "Fixture option " + i) { Disabled = disabled && i % 10 == 0 });
            // A zero-quantity definition is confined to this executable's test process.
            // The real engine Submit/effect pump/decision validation execute every fixture.
            ShardsCardDatabase.Register(new ShardsCardDef { Id = "preflight_fixture", Quantity = 0,
                PlayEffect = new Custom(ctx => AwaitFixture(ctx, fixture)) });
            var card = new ShardsCard { InstanceId = game.Engine.State.NextInstanceId++, DefId = "preflight_fixture",
                Owner = game.Actor, Zone = ShardsZone.Hand };
            game.Engine.State.Players[game.Actor].Hand.Add(card);
            var result = game.Engine.Submit(new ShardsPlayCardAction { PlayerIndex = game.Actor, CardInstanceId = card.InstanceId });
            Check(result.Accepted, "Fixture card play");
            game.Rebuild();
            return fixture;
        }

        private static IEnumerable<ShardsStep> AwaitFixture(ShardsContext ctx, Fixture fixture)
        {
            yield return ShardsStep.AwaitDecision(fixture.Request);
            fixture.Answer = new List<int>(ctx.Answer.ChosenOptionIds);
        }

        private static void PagingAndDisabled()
        {
            var fixture = MakeFixture(false, 0, 2, 130, true);
            var game = fixture.Game;
            var seen = new HashSet<int>();
            int pages = 0;
            do
            {
                Check(game.VisibleCount <= Encoder.MaxActions, "Page shape");
                for (int i = 0; i < game.VisibleCount; i++)
                {
                    var c = game.Visible(i);
                    if (c.Option == null) continue;
                    Check(!c.Option.Disabled, "Disabled option in mask");
                    seen.Add(c.Option.Id);
                }
                game.Step(game.VisibleCount - 1);
                pages++;
            } while (game.Page != 0 && pages < 10);
            Check(pages == 2 && seen.Count == 117, "All enabled options reachable across pages");
            var rejected = game.Engine.Submit(new SubmitDecisionAction { PlayerIndex = game.Actor,
                Answer = new DecisionAnswer { DecisionId = fixture.Request.Id, ChosenOptionIds = new List<int> { 0 } } });
            Check(!rejected.Accepted, "Engine rejects disabled forged answer");
            game.Step(0);
            Check(game.Selected.Count == 1 && !game.Candidates.Any(c => c.Option?.Id == game.Selected[0]),
                "No duplicate generic selection");
        }

        private static void DecisionCoverage(bool split, int min, int max, int options, int expected)
        {
            var observed = new HashSet<string>();
            Visit(new List<int>());
            Check(observed.Count == expected, "Exhaustive decision coverage " + split + "/" + min);
            void Visit(List<int> prefix)
            {
                var fixture = MakeFixture(split, min, max, options);
                foreach (int index in prefix) fixture.Game.Step(index);
                if (fixture.Answer != null)
                {
                    Check(fixture.Answer.Count >= min && fixture.Answer.Count <= max, "Selection bounds");
                    string key = split
                        ? string.Join(",", Enumerable.Range(0, options).Select(id => fixture.Answer.Count(v => v == id)))
                        : string.Join(",", fixture.Answer);
                    Check(observed.Add(key), "Duplicate answer path in exact small fixture");
                    return;
                }
                Check(prefix.Count < 30, "Staged decision progresses");
                for (int i = 0; i < fixture.Game.VisibleCount; i++)
                {
                    prefix.Add(i); Visit(prefix); prefix.RemoveAt(prefix.Count - 1);
                }
            }
        }

        private static void CheckIntervals(int low, int high, int branchLimit)
        {
            if (low == high) return;
            int branches = Math.Min(branchLimit, high - low + 1), previous = low - 1;
            for (int i = 0; i < branches; i++)
            {
                var interval = Adapter.SplitInterval(low, high, branches, i);
                Check(interval.low == previous + 1 && interval.low <= interval.high && interval.high <= high,
                    "Gap/overlap in exact split intervals");
                CheckIntervals(interval.low, interval.high, branchLimit);
                previous = interval.high;
            }
            Check(previous == high, "Incomplete split interval");
        }

        private static void Privacy(int seat)
        {
            var game = new Adapter((ulong)(600 + seat));
            var rng = new Random(7);
            while (game.Actor != seat || game.Engine.PendingInput.Decision != null ||
                game.Engine.State.Players[1 - seat].Hand.Count == 0 || game.Engine.State.Players[1 - seat].Deck.Count == 0)
            {
                Check(!game.Engine.State.GameOver && !game.Truncated, "Privacy fixture reaches suitable input");
                game.Step(game.ExerciseChoice(rng));
            }
            var before = Encode(game);
            var enemy = game.Engine.State.Players[1 - seat];
            // Preserve own authorized information and public counts, change only hidden membership/order.
            int handIndex = -1, deckIndex = -1;
            for (int h = 0; h < enemy.Hand.Count && handIndex < 0; h++)
                for (int d = 0; d < enemy.Deck.Count && handIndex < 0; d++)
                    if (enemy.Hand[h].DefId != enemy.Deck[d].DefId) { handIndex = h; deckIndex = d; }
            Check(handIndex >= 0, "Privacy swap changes hidden card definitions");
            string oldHandDef = enemy.Hand[handIndex].DefId;
            var swap = enemy.Hand[handIndex]; enemy.Hand[handIndex] = enemy.Deck[deckIndex]; enemy.Deck[deckIndex] = swap;
            Check(enemy.Hand[handIndex].DefId != oldHandDef, "Private hand identity actually changed");
            enemy.Hand.Reverse(); enemy.Deck.Reverse();
            game.Engine.State.CenterDeck.Reverse();
            game.Engine.State.Players[seat].Deck.Reverse();
            game.Rebuild();
            Check(before.SequenceEqual(Encode(game)), "Hidden-zone encoding permutation seat " + seat);
        }

        private static float[] Encode(Adapter game)
        {
            var all = new float[Encoder.ObsDim + Encoder.MaxActions * Encoder.ActionDim + Encoder.MaxActions];
            Encoder.Encode(game, all.AsSpan(0, Encoder.ObsDim),
                all.AsSpan(Encoder.ObsDim, Encoder.MaxActions * Encoder.ActionDim),
                all.AsSpan(Encoder.ObsDim + Encoder.MaxActions * Encoder.ActionDim, Encoder.MaxActions));
            return all;
        }

        private static void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException("Selftest: " + message);
        }
    }
}
