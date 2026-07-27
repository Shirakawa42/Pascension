using System.Collections.Generic;
using NUnit.Framework;
using Pascension.Core;
using Shards.Bots;
using Shards.Content;
using Shards.Engine;

namespace SoiSim.Tests
{
    /// <summary>Guards the basket planner (`basket` kind) — the Phase 2 candidate built
    /// from the `soisim rank` measurements (rollout-scored purchase baskets, the only
    /// selector with positive headroom; static-eval steering measured negative).
    ///
    /// Pins the two properties a search bot must never lose:
    ///  · games TERMINATE — the cursor is state-driven, so an unaffordable prescription
    ///    must underfill into END TURN rather than stall the pump;
    ///  · the bot is DETERMINISTIC given the seed — forks, CRN rollout seeds and the
    ///    per-world leaf-dedup cache all replay identically.</summary>
    [TestFixture]
    public sealed class SoiSimBasketBotTests
    {
        private static ShardsDlc AllWithDuel =>
            ShardsDlc.RelicsOfTheFuture | ShardsDlc.ShadowOfSalvation |
            ShardsDlc.IntoTheHorizon | ShardsDlc.Duel;

        /// <summary>Small search so the suite stays fast; the shape of the work is
        /// identical to the shipping config, only the budget differs.</summary>
        private static ShardsBasketPlannerConfig TestConfig() => new()
        {
            Worlds = 1,
            RolloutsPerWorld = 4
        };

        private static (int Winner, ulong Hash, int Submits) PlayGame(ulong seed)
        {
            ShardsContentRegistry.EnsureRegistered();
            var chars = ShardsContentRegistry.CharactersFor(AllWithDuel);
            var model = new ShardsValueModel(W.Pad(ShardsEvalWeights.V5));
            var adapter = new ShardsEngineAdapter(ShardsContentRegistry.StandardConfig(
                seed, new List<PlayerSpec>
                {
                    new() { Name = "S0", CharacterId = chars[(int)(seed % (ulong)chars.Count)] },
                    new() { Name = "S1", CharacterId = chars[(int)((seed + 1) % (ulong)chars.Count)] }
                }, AllWithDuel));
            var seats = new IBotAgent[]
            {
                new ShardsBasketPlannerBot(seed * 100, adapter.Inner, model, TestConfig()),
                new ShardsGreedyEvalBot(seed * 100 + 1, adapter.Inner, model)
            };

            int submits = 0, guard = 0;
            while (!adapter.GameOver && guard++ < SimGameRunner.GuardLimit)
            {
                var pending = adapter.PendingInput;
                if (pending == null) break;
                var action = seats[pending.PlayerIndex].Choose(pending, null)
                             ?? adapter.DefaultActionFor(pending);
                if (!adapter.Submit(action).Accepted &&
                    !adapter.Submit(adapter.DefaultActionFor(adapter.PendingInput)).Accepted)
                    Assert.Fail($"seed {seed}: submit rejected twice at guard {guard}");
                submits++;
            }
            Assert.IsTrue(adapter.GameOver, $"seed {seed}: game did not terminate");
            return (adapter.WinnerIndex, adapter.Inner.State.ComputeFullHash(), submits);
        }

        [Test]
        public void BasketBot_GamesTerminate_AcrossSeeds()
        {
            foreach (ulong seed in new ulong[] { 71001, 71002, 71003 })
                PlayGame(seed);
        }

        [Test]
        public void BasketBot_IsDeterministic()
        {
            var first = PlayGame(71010);
            var second = PlayGame(71010);
            Assert.AreEqual(first.Winner, second.Winner, "winner not reproducible");
            Assert.AreEqual(first.Hash, second.Hash, "final state hash not reproducible");
            Assert.AreEqual(first.Submits, second.Submits, "submit count not reproducible");
        }

        [Test]
        public void BasketEnumeration_AlwaysLeadsWithTheNaturalTurn()
        {
            // The incumbent must be candidate 0: the whole headroom methodology (and the
            // bot's "beat the natural turn to act at all" contract) keys off that index.
            ShardsContentRegistry.EnsureRegistered();
            var chars = ShardsContentRegistry.CharactersFor(AllWithDuel);
            var adapter = new ShardsEngineAdapter(ShardsContentRegistry.StandardConfig(
                71020, new List<PlayerSpec>
                {
                    new() { Name = "S0", CharacterId = chars[0] },
                    new() { Name = "S1", CharacterId = chars[1] }
                }, AllWithDuel));
            var model = new ShardsValueModel(W.Pad(ShardsEvalWeights.V5));
            var baskets = ShardsBasketPlannerBot.EnumerateBaskets(
                adapter.Inner, adapter.Inner.State.TurnPlayerIndex, model);
            Assert.Greater(baskets.Count, 4, "basket enumeration collapsed");
            Assert.IsNull(baskets[0].Defs, "candidate 0 must be the natural (unconstrained) turn");
            for (int i = 1; i < baskets.Count; i++)
                Assert.IsNotNull(baskets[i].Defs, "only candidate 0 may be natural");
        }
    }
}
