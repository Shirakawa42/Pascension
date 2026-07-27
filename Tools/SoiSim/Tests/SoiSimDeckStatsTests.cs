using System.Collections.Generic;
using NUnit.Framework;
using Pascension.Core;
using Shards.Bots;
using Shards.Content;
using Shards.Engine;

namespace SoiSim.Tests
{
    /// <summary>Pins the derived per-deck statistics that the clock evaluator will consume.
    ///
    /// These are the quantities the old neural encoder could not express. Its value target
    /// was built from ratios — per-turn output is `D x Sum(stat)/N`, the ascend clock is
    /// `(30-mastery)/masteryPerTurn` — and a net fed summed bags of card vectors never sees
    /// N multiplicatively against the sum, so it cannot compute a division at all. It
    /// measured 40.6% against having NO evaluator. Computing the ratios exactly is the fix,
    /// which makes their correctness load-bearing rather than cosmetic.
    ///
    /// The opening deck is used because it is exactly computable by hand.</summary>
    [TestFixture]
    public sealed class SoiSimDeckStatsTests
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
        public void OpeningDeck_RatesAreExact()
        {
            // The 10 starters: 7 Crystal (1 gem), 1 Shard Reactor (2 gems at M0),
            // 1 Blaster (1 power), 1 Infinity Shard (2 power at M0).
            //   gems 9 · power 3 · draws 0 · N 10
            //   D = handSize / (1 - 0)          = 5      cards seen per turn
            //   perTurn = D/N                   = 0.5    of the deck each turn
            //   gems/turn = 9 x 0.5             = 4.5
            //   power/turn = 3 x 0.5            = 1.5
            //   cycle = N/D                     = 2      turns to see the whole deck
            var adapter = NewGame(4242);
            var stats = ShardsDeckStats.For(adapter.Inner.State, 0);

            Assert.AreEqual(10, stats.N, "opening cycle is 10 starters");
            Assert.AreEqual(5.0, stats.D, 1e-9, "a deck with no draw effects sees exactly a hand");
            Assert.AreEqual(2.0, stats.CycleTurns, 1e-9);
            Assert.AreEqual(4.5, stats.GemsPerTurn, 1e-9, "7 Crystals + a 2-gem Shard Reactor over 2 turns");
            Assert.AreEqual(1.5, stats.PowerPerTurn, 1e-9, "Blaster 1 + Infinity Shard 2 over 2 turns");
            Assert.AreEqual(0.0, stats.DrawsPerTurn, 1e-9, "no starter draws");
            Assert.AreEqual(0.0, stats.MasteryPerTurn, 1e-9, "no starter gains mastery");
            Assert.IsFalse(stats.ShardBanished, "the Infinity Shard starts in the deck");
        }

        [Test]
        public void StartersAreFactionless_SoNoConditionIsLive()
        {
            // Crystal / Blaster / Shard Reactor / Infinity Shard are all Faction.None and
            // satisfy nothing. An opening hand therefore has zero Unify liveness and zero
            // Allegiance progress — which is why early conditional cards are near-worthless
            // and the evaluator must price liveness rather than count keywords.
            var stats = ShardsDeckStats.For(NewGame(77).Inner.State, 0);
            Assert.AreEqual(0, stats.DistinctFactions,
                "starters are Faction.None — nothing should count toward a faction condition");
            Assert.AreEqual(0, stats.FactionsAtAllegiance4);
            Assert.AreEqual(0.0, stats.UnifyLiveness, 1e-9);
            Assert.AreEqual(0, stats.WraetheInDiscard);
        }

        [Test]
        public void DrawEffects_RaiseCardsSeenAboveHandSize()
        {
            // D is a fixed point, not a constant: a drawn card can itself draw. This is what
            // makes card draw compound twice — it raises every per-turn rate AND shortens the
            // cycle, which shortens the Infinity Shard wait.
            var adapter = NewGame(99);
            var state = adapter.Inner.State;
            var before = ShardsDeckStats.For(state, 0);

            // Find a real card with an UNCONDITIONAL draw rather than naming one: a card
            // whose draw sits behind Unify or Echo contributes nothing to the base rate (by
            // design — conditional gains are priced through liveness instead), so hardcoding
            // an id silently tests nothing the day that card's text changes.
            ShardsCardDef drawDef = null;
            foreach (var def in ShardsCardDatabase.All)
            {
                var play = ShardsCardStatics.Get(def).Play[0];
                if (play.Gains[EffectAtoms.Unconditional, EffectAtoms.Draw] > 0) { drawDef = def; break; }
            }
            Assert.IsNotNull(drawDef, "no card in the database has an unconditional draw");
            var player = state.Players[0];
            for (int i = 0; i < 4; i++)
                player.Deck.Add(new ShardsCard
                {
                    InstanceId = state.NextInstanceId++,
                    DefId = drawDef.Id,
                    Owner = 0,
                    Zone = ShardsZone.Deck
                });

            var after = ShardsDeckStats.For(state, 0);
            Assert.Greater(after.DrawsPerTurn, 0, $"{drawDef.Id} must contribute draw");
            Assert.Greater(after.D, before.D,
                "adding draw effects must raise cards-seen-per-turn above the flat hand size");
            Assert.Greater(after.N, before.N);
        }

        [Test]
        public void BoardPermanents_AreOutsideTheCycle()
        {
            // A champion fires EVERY turn and occupies no deck slot, while the same effect on
            // a card fires D/N ~ 0.5 times a turn. Counting a champion inside N would both
            // dilute the deck it is not in and under-credit its output by ~2-4x — the single
            // biggest thing a naive evaluator gets wrong about board state.
            var adapter = NewGame(1234);
            var state = adapter.Inner.State;
            var player = state.Players[0];
            var before = ShardsDeckStats.For(state, 0);

            var champDef = ShardsCardDatabase.Get("drakonarius"); // Exhaust: gain 6 power
            player.Champions.Add(new ShardsCard
            {
                InstanceId = state.NextInstanceId++,
                DefId = champDef.Id,
                Owner = 0,
                Zone = ShardsZone.Champions
            });

            var after = ShardsDeckStats.For(state, 0);
            Assert.AreEqual(before.N, after.N, "a champion must NOT enter the drawn cycle");
            Assert.AreEqual(before.CycleTurns, after.CycleTurns, 1e-9,
                "board permanents must not change how long the deck takes to cycle");
            Assert.Greater(after.BoardPower, before.BoardPower, "the exhaust must be counted");
            Assert.AreEqual(before.PowerPerTurn + after.BoardPower, after.PowerPerTurn, 1e-9,
                "champion output is added per TURN, undivided by deck size");
        }
    }
}
