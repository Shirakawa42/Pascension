using System.Collections.Generic;
using NUnit.Framework;
using Pascension.Core;
using Shards.Bots;
using Shards.Content;
using Shards.Engine;

namespace SoiSim.Tests
{
    /// <summary>Structural properties of the clock evaluator. These are deliberately about
    /// SHAPE rather than calibrated numbers: the retired neural evaluator had a perfectly
    /// respectable 76.8% validation accuracy and played 46%, so "predicts well on average"
    /// proves nothing. What matters is that the function responds to the right things in the
    /// right direction, and stays sane at the edges.</summary>
    [TestFixture]
    public sealed class SoiSimClockEvalTests
    {
        private static ShardsDlc AllWithDuel =>
            ShardsDlc.RelicsOfTheFuture | ShardsDlc.ShadowOfSalvation |
            ShardsDlc.IntoTheHorizon | ShardsDlc.Duel;

        private static ShardsEngineAdapter NewGame(ulong seed)
        {
            ShardsContentRegistry.EnsureRegistered();
            var chars = ShardsContentRegistry.CharactersFor(AllWithDuel);
            return new ShardsEngineAdapter(ShardsContentRegistry.StandardConfig(
                seed, new List<PlayerSpec>
                {
                    new() { Name = "S0", CharacterId = chars[0] },
                    new() { Name = "S1", CharacterId = chars[1 % chars.Count] }
                }, AllWithDuel));
        }

        [Test]
        public void MirroredOpening_IsNearlyEven_AndTempoDecidesTheRest()
        {
            // Both sides hold identical starter decks, so the only asymmetry is whose turn it
            // is. The score must therefore sit close to 0.5 and favour the player to move.
            var state = NewGame(11).Inner.State;
            var eval = new ShardsClockEval();
            double toMove = eval.Evaluate(state, state.TurnPlayerIndex);
            double waiting = eval.Evaluate(state, 1 - state.TurnPlayerIndex);

            Assert.Greater(toMove, waiting, "the player to move must not be behind");
            Assert.That(toMove, Is.InRange(0.5, 0.7), "an even opening must not read as decided");
            Assert.AreEqual(1.0, toMove + waiting, 1e-9, "the two seats' scores must sum to 1");
        }

        [Test]
        public void Terminals_AreExact()
        {
            var adapter = NewGame(12);
            var state = adapter.Inner.State;
            var eval = new ShardsClockEval();

            state.GameOver = true;
            state.WinnerIndex = 0;
            Assert.AreEqual(1.0, eval.Evaluate(state, 0), 1e-12);
            Assert.AreEqual(0.0, eval.Evaluate(state, 1), 1e-12);
            state.WinnerIndex = -1;
            Assert.AreEqual(0.5, eval.Evaluate(state, 0), 1e-12, "a tie is 0.5, not a win");
        }

        [Test]
        public void HealthMatters_ThroughTheClock_NotLinearly()
        {
            // The load-bearing property. Against a WEAK attacker, losing health barely moves
            // the score, because the kill clock is long either way. Against a STRONG attacker
            // the same health loss is decisive. A linear health coefficient rates both moves
            // identically, which is the single error every reviewer flagged in the old
            // evaluator — its largest coefficient was linear health.
            var eval = new ShardsClockEval();

            double WeakAttacker(int health)
            {
                var state = NewGame(13).Inner.State;
                state.Players[0].Health = health;
                return eval.Evaluate(state, 0);
            }
            double StrongAttacker(int health)
            {
                var state = NewGame(13).Inner.State;
                state.Players[0].Health = health;
                // Give the opponent a real damage clock via board output, which is per-turn
                // and undivided by deck size.
                // ONE champion (6 power/turn). Three would make the position already lost at
                // both health values, and two saturated scores cannot show a difference —
                // the comparison needs a live position, not a decided one.
                var champ = ShardsCardDatabase.Get("drakonarius"); // Exhaust: gain 6 power
                state.Players[1].Champions.Add(new ShardsCard
                {
                    InstanceId = state.NextInstanceId++, DefId = champ.Id,
                    Owner = 1, Zone = ShardsZone.Champions
                });
                return eval.Evaluate(state, 0);
            }

            double weakDrop = WeakAttacker(50) - WeakAttacker(25);
            double strongDrop = StrongAttacker(50) - StrongAttacker(25);
            Assert.Greater(weakDrop, -1e-9, "losing health must never IMPROVE the score");
            Assert.Greater(strongDrop, weakDrop + 0.02,
                "the same 25 health must cost far more against a fast attacker than a slow " +
                "one — otherwise health is being scored linearly");
        }

        [Test]
        public void NearM30_TheAscendClockCarriesThePosition()
        {
            // A player at M29 with no damage output at all should still be favoured: the
            // ascend route wins on its own. This is the case a damage-only evaluator gets
            // exactly backwards, and it is 51% of the games a tuned policy actually wins.
            var eval = new ShardsClockEval();
            var low = NewGame(14).Inner.State;
            double atStart = eval.Evaluate(low, 0);

            var high = NewGame(14).Inner.State;
            high.Players[0].Mastery = 29;
            double nearAscend = eval.Evaluate(high, 0);

            Assert.Greater(nearAscend, atStart + 0.05,
                "M29 must be worth much more than M0 even with identical decks and health");
        }

        [Test]
        public void BanishingTheInfinityShard_KillsTheAscendRoute()
        {
            // Discontinuous by design: several effects banish from your own discard, and a
            // bot that deletes its own win condition should see the position collapse.
            var eval = new ShardsClockEval();
            var state = NewGame(15).Inner.State;
            state.Players[0].Mastery = 29;
            double withShard = eval.Evaluate(state, 0);

            var player = state.Players[0];
            foreach (var zone in new[] { player.Deck, player.Hand, player.Discard, player.PlayZone })
                zone.RemoveAll(c => c.DefId == "infinity_shard");
            double withoutShard = eval.Evaluate(state, 0);

            Assert.Less(withoutShard, withShard - 0.05,
                "losing the Infinity Shard at M29 must collapse the ascend clock, not shave it");
        }

        [Test]
        public void AlwaysInRange_AndSeatsAreComplementary()
        {
            // Runs over real mid-game positions rather than constructed ones, so degenerate
            // states (empty deck, huge boards, one side about to die) are covered.
            var eval = new ShardsClockEval();
            // EnsureRegistered BEFORE the model: it caches a slot table for every registered
            // def at construction, so building it against an empty database throws on the
            // first lookup rather than at construction, which makes the trace misleading.
            ShardsContentRegistry.EnsureRegistered();
            var model = new ShardsValueModel(W.Pad(ShardsEvalWeights.Current));
            int checks = 0;
            for (ulong seed = 300; seed < 306; seed++)
            {
                var adapter = NewGame(seed);
                var seats = new IBotAgent[]
                {
                    new ShardsGreedyEvalBot(seed * 100, adapter.Inner, model),
                    new ShardsGreedyEvalBot(seed * 100 + 1, adapter.Inner, model)
                };
                int guard = 0;
                while (!adapter.GameOver && guard++ < SimGameRunner.GuardLimit)
                {
                    var pending = adapter.PendingInput;
                    if (pending == null) break;
                    if (guard % 17 == 0)
                    {
                        double a = eval.Evaluate(adapter.Inner.State, 0);
                        double b = eval.Evaluate(adapter.Inner.State, 1);
                        Assert.That(a, Is.InRange(0.0, 1.0));
                        Assert.AreEqual(1.0, a + b, 1e-9, "seat scores must sum to 1");
                        Assert.IsFalse(double.IsNaN(a), "NaN — a clock divided by zero");
                        checks++;
                    }
                    var action = seats[pending.PlayerIndex].Choose(pending, null)
                                 ?? adapter.DefaultActionFor(pending);
                    if (!adapter.Submit(action).Accepted &&
                        !adapter.Submit(adapter.DefaultActionFor(adapter.PendingInput)).Accepted)
                        break;
                }
            }
            Assert.Greater(checks, 50, "too few positions sampled to be meaningful");
        }
    }
}
