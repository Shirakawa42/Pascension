using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Pascension.Core;
using Pascension.Engine.Actions;
using Pascension.Engine.Core;
using Shards.Bots;
using Shards.Content;
using Shards.Engine;

namespace SoiSim.Tests
{
    /// <summary>Standing guard against the single most expensive bug class in this project:
    /// an action the policy can never choose.
    ///
    /// Until 2026-07-25 the row reroll was priced strictly below passing, so an argmax policy
    /// could never pick it. It appeared in ZERO rollouts and ZERO training positions, and
    /// every neural generation was fit to a game in which rerolling did not exist. It survived
    /// six days and nine generations because every instrument measured WIN RATE, and a blind
    /// spot shared by both seats is invisible to win rate by construction.
    ///
    /// This test measures PRESENCE instead. It is deliberately strict about priority actions —
    /// those are exactly what the argmax controls, and a zero there is always either a bug or
    /// a scoring hole. It is deliberately NOT strict about decision contexts, which fire only
    /// when the right card is bought and would make the test flaky for no diagnostic gain;
    /// `soisim coverage` reports those.</summary>
    [TestFixture]
    public sealed class SoiSimCoverageTests
    {
        private const int Games = 400;

        private static ShardsDlc AllWithDuel =>
            ShardsDlc.RelicsOfTheFuture | ShardsDlc.ShadowOfSalvation |
            ShardsDlc.IntoTheHorizon | ShardsDlc.Duel;

        /// <summary>Every priority action a 2-player Duel game can legally offer. Adding a new
        /// action type means adding it here — and then making some policy actually take it.</summary>
        private static readonly string[] MustBeReachable =
        {
            nameof(ShardsPlayCardAction),
            nameof(ShardsBuyCardAction),
            nameof(ShardsRerollRowAction),      // the 2026-07-25 bug
            nameof(ShardsFocusAction),
            nameof(ShardsHeroAbilityAction),
            nameof(ShardsExhaustAction),
            nameof(ShardsAttackMonsterAction),
            nameof(ShardsTakeDestinyAction),
            nameof(ShardsRecruitRelicAction),
            nameof(ShardsEndTurnAction)
        };

        private static Dictionary<string, int> Play(string kind, int games)
        {
            ShardsContentRegistry.EnsureRegistered();
            var chars = ShardsContentRegistry.CharactersFor(AllWithDuel);
            var factory = new BotFactory(kind, 0);
            var counts = new Dictionary<string, int>();
            void Bump(string k) => counts[k] = counts.TryGetValue(k, out int v) ? v + 1 : 1;

            for (int g = 0; g < games; g++)
            {
                ulong seed = 424200 + (ulong)g;
                var rng = new DeterministicRng(seed * 31 + 7, 91);
                int a = rng.Next(chars.Count);
                int b = rng.Next(chars.Count - 1);
                if (b >= a) b++;
                var specs = new List<PlayerSpec>
                {
                    new() { Name = "S0", CharacterId = chars[a] },
                    new() { Name = "S1", CharacterId = chars[b] }
                };
                var adapter = new ShardsEngineAdapter(
                    ShardsContentRegistry.StandardConfig(seed, specs, AllWithDuel));
                var seats = new IBotAgent[2];
                for (int i = 0; i < 2; i++) seats[i] = factory.Create(seed, i, adapter.Inner);

                int guard = 0;
                while (!adapter.GameOver && guard++ < SimGameRunner.GuardLimit)
                {
                    var pending = adapter.PendingInput;
                    if (pending == null) break;
                    var action = seats[pending.PlayerIndex].Choose(pending, null)
                                 ?? adapter.DefaultActionFor(pending);
                    Bump(action.GetType().Name);
                    if (action is ShardsBuyCardAction { FastPlay: true }) Bump("fast-play");
                    if (!adapter.Submit(action).Accepted &&
                        !adapter.Submit(adapter.DefaultActionFor(adapter.PendingInput)).Accepted)
                        break;
                }
            }
            return counts;
        }

        [TestCase("bench:greedy-v5")]
        [TestCase("bench:heuristic")]
        public void EveryPriorityAction_IsReachable(string kind)
        {
            var counts = Play(kind, Games);
            var dead = MustBeReachable.Where(a => !counts.ContainsKey(a)).ToArray();
            Assert.IsEmpty(dead,
                $"{kind} NEVER chose: {string.Join(", ", dead)} over {Games} Duel games. " +
                "An action the policy cannot reach produces training data with a hole in it " +
                "and cannot be tuned, because it never generates the evidence that would " +
                "raise its own score. Check its price against END TURN.");
        }

        [Test]
        public void MercenaryFastPlay_IsReachable()
        {
            // Fast-play is a second, distinct use of the SAME buy action, so a histogram of
            // action TYPES cannot see it going dead. It is also dilution-free, which makes it
            // strategically distinct from recruiting the same card.
            var counts = Play("bench:greedy-v5", Games);
            Assert.IsTrue(counts.ContainsKey("fast-play"),
                $"no mercenary was ever fast-played over {Games} games — the FastPlay branch " +
                "of ShardsBuyCardAction has gone unreachable");
        }

        [Test]
        public void RezHeroAbility_IsKnownDeadAndThatIsMeasured()
        {
            // Documented exception, kept as a test so it stays TRUE rather than assumed.
            //
            // Rez's entire hero ability is Scry(2) (ShardsEngine.cs:676 — the only Scry in the
            // game). Under V5 it is unreachable, by the same arithmetic that killed the reroll:
            //     value = 2 x ScryPerCard(0.1958) = 0.392
            //     cost  = 1 gem x Gems(0.5370)    = 0.537   ->  net -0.145  ->  below END TURN
            //
            // Unlike the reroll, this one was MEASURED before being called a bug. Forcing it
            // live (`--weights-a scry-live`, ScryPerCard 0.40) scored 50.4% [49.9-50.9] over
            // 10,000 pairs — +3 Elo, a tie. So the zero is a correct strategic price, not a
            // hole, and nobody should "fix" it.
            //
            // If this assertion ever fails, the pricing moved and the +3 Elo measurement is
            // stale — re-run the probe before assuming the new behaviour is an improvement.
            double value = 2 * ShardsEvalWeights.V5[W.ScryPerCard];
            double cost = ShardsEvalWeights.V5[W.Gems];
            Assert.Less(value - cost, 0,
                "Rez's Scry is now priced above its gem cost, so the ability has become " +
                "reachable. That may be fine — but it was measured worth +3 Elo [tie] at " +
                "10,000 pairs, so re-measure rather than assume.");
        }

        [Test]
        public void BanishingPrefersTheBlaster_ThenTheCrystal()
        {
            // Player knowledge, encoded as a test: "banishing the Blaster is the best ban
            // you can make." That holds only if the Blaster is valued BELOW the Crystal,
            // because contextual thinning removes whatever sits furthest below the deck's
            // own average (ShardsValueModel.BanishValue).
            //
            // It does: Blaster is 1 POWER (W.Power 0.18) while Crystal is 1 GEM (W.Gems
            // 0.54), and in an economy-and-mastery game raw power is the least useful thing
            // a starter can offer. Shard Reactor scales to 4 gems and must never be banished.
            //
            // Aggregate banish counts look the opposite (crystal 10,579 vs blaster 1,581)
            // purely because a deck holds 7 Crystals and 1 Blaster — per copy the rates match.
            // EnsureRegistered FIRST: the model caches a slot table for every registered def
            // at construction, so building it against an empty database throws on lookup.
            ShardsContentRegistry.EnsureRegistered();
            var model = new ShardsValueModel(W.Pad(ShardsEvalWeights.V5));
            var blaster = ShardsCardDatabase.Get("blaster");
            var crystal = ShardsCardDatabase.Get("crystal");
            var reactor = ShardsCardDatabase.Get("shard_reactor");

            foreach (int mastery in new[] { 0, 5, 10, 15, 20, 25, 30 })
            {
                Assert.Less(model.CardValue(blaster, mastery), model.CardValue(crystal, mastery),
                    $"at M{mastery} the Blaster is not the cheapest starter to give up, so " +
                    "contextual thinning would banish the wrong card");
                Assert.Less(model.CardValue(crystal, mastery), model.CardValue(reactor, mastery),
                    $"at M{mastery} Shard Reactor is not valued above a Crystal — it scales " +
                    "to 4 gems and must be the LAST starter banished");
            }
        }

        [Test]
        public void ThinningIsContextual_NotAFlatPerCapacityScalar()
        {
            // Ko Syn Wu's "Sacrifice" fired 0 times in 1,622 drafted games. The cause was
            // structural, not a tuning miss: W.BanishPerCapacity is ONE scalar covering both
            // "banish a Blaster" (excellent) and "banish your engine" (terrible), so it must
            // average toward zero — V5 tuned it to -0.0257 — and near zero it can never pay
            // for the ability's cost, whatever the cost is.
            //
            // Fixed 2026-07-27 by pricing thinning against the actual deck
            // (ShardsValueModel.BanishValue, scaled by the new W.BanishBelowAverage):
            // how far below the deck's own average the best reachable card sits, and zero
            // when only good cards can be reached. Sacrifice now fires 2,338 times.
            //
            // These assertions pin the STRUCTURE, so the fix cannot silently regress into a
            // flat scalar again.
            // Current, not V5: W.Defaults now pads historical vectors with thinning at 0 so
            // the frozen benchmark cannot drift, which means V5 correctly scores 0 here.
            ShardsContentRegistry.EnsureRegistered();
            var model = new ShardsValueModel(W.Pad(ShardsEvalWeights.Current));
            var chars = ShardsContentRegistry.CharactersFor(AllWithDuel);
            var adapter = new ShardsEngineAdapter(ShardsContentRegistry.StandardConfig(
                1234, new List<PlayerSpec>
                {
                    new() { Name = "S0", CharacterId = chars[0] },
                    new() { Name = "S1", CharacterId = chars[1 % chars.Count] }
                }, AllWithDuel));
            var player = adapter.Inner.State.Players[0];

            // A fresh starter deck holds Crystals and a Blaster — all below its own average
            // once Shard Reactor and Infinity Shard are counted — so thinning must be worth
            // something. A flat scalar would return a constant regardless of contents.
            Assert.Greater(model.BanishValue(player, 1), 0,
                "thinning a starter deck scored zero — BanishValue is not reading the deck");

            // Capacity 0 is nothing; capacity 2 cannot be worth less than capacity 1.
            Assert.AreEqual(0, model.BanishValue(player, 0), 1e-12);
            Assert.GreaterOrEqual(model.BanishValue(player, 2), model.BanishValue(player, 1),
                "a second banish cannot be worth less than the first");

            // The load-bearing property: with nothing reachable, thinning is worth exactly
            // zero rather than a flat per-capacity constant. This is what stops the bot
            // paying 2 gems and 3 health to banish its own engine.
            player.Hand.Clear();
            player.Discard.Clear();
            Assert.AreEqual(0, model.BanishValue(player, 1), 1e-12,
                "with no reachable card, thinning must be worth exactly 0");
        }
    }
}
