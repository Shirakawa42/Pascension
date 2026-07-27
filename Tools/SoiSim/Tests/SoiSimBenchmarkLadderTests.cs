using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using Pascension.Core;
using Pascension.Engine.Core;
using Pascension.Engine.Serialization;
using Shards.Bots;
using Shards.Content;
using Shards.Engine;

namespace SoiSim.Tests
{
    /// <summary>Guards the FROZEN benchmark ladder — the four `bench:*` kinds that every
    /// future candidate is measured against.
    ///
    /// Why this fixture exists. The 2026-07-21→26 campaign gated nine neural generations
    /// net-vs-net in mirror matches at n=120. Mirror matching is structurally blind to a
    /// shared blind spot, so every probe read ~50% while the agent that actually shipped
    /// as DIAMOND was losing to instant greedy 91.5% of the time (8.5% [5.6-11.4], ≈ −410
    /// Elo). The fix is an external reference that cannot move. A yardstick that silently
    /// drifts measures nothing, so each property that makes it a yardstick is pinned here:
    /// its weights, its budgets, its determinism, and the absence of any leaf evaluator.
    ///
    /// If a change makes one of these fail, that is the test working. Re-mint deliberately;
    /// never "fix" it by loosening the assertion.</summary>
    [TestFixture]
    public sealed class SoiSimBenchmarkLadderTests
    {
        private const string Heuristic = "bench:heuristic";
        private const string GreedyV5 = "bench:greedy-v5";
        private const string Rollout1200 = "bench:rollout-1200";
        private const string Rollout4800 = "bench:rollout-4800";

        private static ShardsDlc AllWithDuel =>
            ShardsDlc.RelicsOfTheFuture | ShardsDlc.ShadowOfSalvation |
            ShardsDlc.IntoTheHorizon | ShardsDlc.Duel;

        private static ShardsEngineAdapter NewGame(ulong seed)
        {
            ShardsContentRegistry.EnsureRegistered();
            var chars = ShardsContentRegistry.CharactersFor(AllWithDuel);
            var specs = new List<PlayerSpec>
            {
                new() { Name = "S0", CharacterId = chars[0] },
                new() { Name = "S1", CharacterId = chars[1 % chars.Count] }
            };
            return new ShardsEngineAdapter(
                ShardsContentRegistry.StandardConfig(seed, specs, AllWithDuel));
        }

        private static ShardsSearchConfig ConfigOf(IBotAgent bot)
        {
            var field = typeof(ShardsSearchBot).GetField("_config",
                BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.IsNotNull(field, "ShardsSearchBot._config was renamed — update this guard");
            return (ShardsSearchConfig)field.GetValue(bot);
        }

        [Test]
        public void EveryBenchKind_ResolvesToItsIntendedAgent()
        {
            // ShardsBotRanks.Create falls back to ShardsHeuristicBot for ANY unknown kind.
            // So a typo in a bench id does not throw — it silently benchmarks against the
            // weakest bot in the repo and every candidate looks brilliant.
            var adapter = NewGame(9001);
            Assert.IsInstanceOf<ShardsHeuristicBot>(
                ShardsBotRanks.Create(Heuristic, 1, adapter.Inner));
            Assert.IsInstanceOf<ShardsGreedyEvalBot>(
                ShardsBotRanks.Create(GreedyV5, 1, adapter.Inner));
            Assert.IsInstanceOf<ShardsSearchBot>(
                ShardsBotRanks.Create(Rollout1200, 1, adapter.Inner));
            Assert.IsInstanceOf<ShardsSearchBot>(
                ShardsBotRanks.Create(Rollout4800, 1, adapter.Inner));
        }

        [Test]
        public void V5_IsContentFrozen()
        {
            // bench:greedy-v5 is only a fixed reference while V5's NUMBERS are fixed.
            Assert.AreEqual(49, ShardsEvalWeights.V5.Length,
                "V5 changed length — the frozen benchmark is no longer the vector that " +
                "produced every measurement in the campaign log");
            Assert.AreEqual(6915.68972, ShardsEvalWeights.V5.Sum(), 1e-6,
                "V5's values were edited. It is the frozen yardstick: append a V6 instead.");
        }

        [Test]
        public void BenchGreedyV5_IsPinnedToV5_NotToCurrent()
        {
            // The trap this closes: writing the bench kind as `new ShardsGreedyEvalBot(...,
            // Model.Value)` makes it follow ShardsEvalWeights.Current, so the moment the
            // tuner emits V6 the "fixed" reference silently becomes the new champion and
            // every comparison against it reads ~50%.
            var adapter = NewGame(4242);
            var bench = ShardsBotRanks.Create(GreedyV5, 77, adapter.Inner);
            var explicitV5 = new ShardsGreedyEvalBot(77, adapter.Inner,
                new ShardsValueModel(ShardsEvalWeights.V5));

            int compared = 0, guard = 0;
            while (!adapter.GameOver && guard++ < SimGameRunner.GuardLimit && compared < 200)
            {
                var pending = adapter.PendingInput;
                if (pending == null) break;
                var a = bench.Choose(pending, null);
                var b = explicitV5.Choose(pending, null);
                Assert.AreEqual(b?.Describe(), a?.Describe(),
                    $"bench:greedy-v5 diverged from an explicit-V5 bot at decision {compared} " +
                    "— it is following Current, not V5");
                compared++;
                var action = a ?? adapter.DefaultActionFor(pending);
                if (!adapter.Submit(action).Accepted &&
                    !adapter.Submit(adapter.DefaultActionFor(adapter.PendingInput)).Accepted)
                    break;
            }
            Assert.Greater(compared, 20, "too few decisions compared to be meaningful");
        }

        [Test]
        public void BenchRollouts_HaveExactPinnedBudgets()
        {
            // 1200 ≈ the measured crossover vs instant greedy (51.4%); below it MORE SEARCH
            // IS WORSE THAN NONE (300 it scores 21.2%). 4800 ≈ 71.4%. Both single-threaded:
            // root-parallel merges are CPU-independent, but one tree is bit-reproducible,
            // which is what a benchmark needs.
            var adapter = NewGame(9002);
            var c1200 = ConfigOf(ShardsBotRanks.Create(Rollout1200, 1, adapter.Inner));
            Assert.AreEqual(1200, c1200.Iterations, "bench:rollout-1200 budget moved");
            Assert.AreEqual(1, c1200.RootWorkers, "bench rollouts must stay single-tree");
            Assert.AreEqual(ShardsSearchConfig.BudgetMode.Iterations, c1200.Mode,
                "a wall-clock benchmark would measure the machine, not the bot");

            var c4800 = ConfigOf(ShardsBotRanks.Create(Rollout4800, 1, adapter.Inner));
            Assert.AreEqual(4800, c4800.Iterations, "bench:rollout-4800 budget moved");
            Assert.AreEqual(1, c4800.RootWorkers);
            Assert.AreEqual(ShardsSearchConfig.BudgetMode.Iterations, c4800.Mode);

            foreach (var config in new[] { c1200, c4800 })
            {
                Assert.IsFalse(config.PerfectInformation, "a benchmark must never cheat");
                Assert.AreEqual(1.0, config.EarlyStopBudgetFraction, 1e-12,
                    "fraction 1.0 is the move-identical guarantee; below it the benchmark " +
                    "may swap to a near-tied alternative and stop being reproducible");
            }
        }

        [Test]
        public void BenchRollout_IsBitReproducible()
        {
            // Same kind, same seed, same position ⇒ same move. Without this a benchmark
            // result cannot be reproduced or bisected.
            //
            // Must be measured at a PRIORITY point. With Duel on, the game opens on the
            // hero-draft DECISION, and ShardsSearchBot answers decisions from the tuned
            // model without searching at all — so asking the first pending input tests
            // nothing (it returned in ~0 ms, which is how this was caught).
            var adapter = NewGame(5150);
            var driver = new ShardsHeuristicBot(5150 * 13, adapter.Inner);
            PendingSnap priority = null;
            int guard = 0;
            while (!adapter.GameOver && guard++ < SimGameRunner.GuardLimit)
            {
                var pending = adapter.PendingInput;
                if (pending == null) break;
                // Round 2+: past the opening, where the row is refilled and the branching
                // factor is real rather than a near-forced first turn.
                if (pending.Kind == PendingInputKind.Priority && adapter.Inner.State.Round >= 2)
                {
                    priority = pending;
                    break;
                }
                var action = driver.Choose(pending, null) ?? adapter.DefaultActionFor(pending);
                if (!adapter.Submit(action).Accepted &&
                    !adapter.Submit(adapter.DefaultActionFor(adapter.PendingInput)).Accepted)
                    break;
            }
            Assert.IsNotNull(priority, "never reached a round-2 priority point");

            var first = ShardsBotRanks.Create(Rollout1200, 31337, adapter.Inner);
            var second = ShardsBotRanks.Create(Rollout1200, 31337, adapter.Inner);
            var moveA = first.Choose(priority, null);
            var moveB = second.Choose(priority, null);
            Assert.AreEqual(moveA?.Describe(), moveB?.Describe(),
                "bench:rollout-1200 is not reproducible at a fixed seed");
            Assert.Greater(((ShardsSearchBot)first).LastIterations, 0,
                "the search never ran — this test is not measuring what it claims to");
        }

        [Test]
        public void SearchSeatClassification_CoversTheRolloutBenches()
        {
            // A search bot on the non-worker seat blocks the UI thread; a non-search bot
            // on the worker seat is merely wasteful. Both bench rollouts are search.
            Assert.IsTrue(ShardsBotRanks.IsSearchKind(Rollout1200));
            Assert.IsTrue(ShardsBotRanks.IsSearchKind(Rollout4800));
            Assert.IsFalse(ShardsBotRanks.IsSearchKind(Heuristic));
            Assert.IsFalse(ShardsBotRanks.IsSearchKind(GreedyV5));
        }

        [Test]
        public void NoLeafEvaluator_CanBeAttachedToTheRolloutSearch()
        {
            // The mechanism that inverted the ladder: with a leaf evaluator and truncated
            // rollouts, more iterations bought better play toward a WORSE target (at 4×
            // budget the rollout agent gained 79.3%, the net agent 52.2% — search scaling
            // flattened to zero). ShardsSearchBot therefore holds no evaluator at all.
            //
            // Phase 2's clock evaluator drives the turn PLANNER, a separate agent. If an
            // evaluator is ever wired back into this ISMCTS path, it must first beat full
            // rollouts head-to-head at equal WALL-CLOCK — running that probe first, rather
            // than after nine generations, is the whole point. Update this test then.
            var evaluatorFields = typeof(ShardsSearchBot)
                .GetFields(BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public)
                .Where(f => typeof(IShardsValueEvaluator).IsAssignableFrom(f.FieldType))
                .Select(f => f.Name)
                .ToArray();
            Assert.IsEmpty(evaluatorFields,
                "ShardsSearchBot gained an IShardsValueEvaluator field (" +
                string.Join(", ", evaluatorFields) + "). Truncated-rollout evaluation is " +
                "what produced a −410 Elo top rank. Gate A first.");

            Assert.IsNull(typeof(ShardsSearchConfig).GetField("RolloutEndTurns"),
                "RolloutEndTurns is back — the rollout-truncation knob was removed with " +
                "the nets and must not return without clearing Gate A");
        }
    }
}
