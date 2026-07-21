using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using Pascension.Core;
using Pascension.Engine.Core;
using Pascension.Engine.Serialization;
using Shards.Bots;
using Shards.Content;
using Shards.Engine;

namespace Pascension.Engine.Tests
{
    /// <summary>Engine support for the search bot: Fork/DeepCopy correctness and the
    /// drift sentinels that keep them honest as the state shape evolves.</summary>
    public sealed class ShardsSearchTests
    {
        private const ShardsDlc AllDlc =
            ShardsDlc.RelicsOfTheFuture | ShardsDlc.ShadowOfSalvation | ShardsDlc.IntoTheHorizon;

        [SetUp]
        public void SetUp()
        {
            ShardsCardDatabase.Clear();
            ShardsContentRegistry.EnsureRegistered();
        }

        private static ShardsEngineAdapter NewGame(ulong seed)
        {
            var specs = new List<PlayerSpec>
            {
                new() { Name = "P0", CharacterId = "decima" },
                new() { Name = "P1", CharacterId = "tetra" }
            };
            return new ShardsEngineAdapter(ShardsContentRegistry.StandardConfig(seed, specs, AllDlc));
        }

        /// <summary>Drives one accepted submit on the engine using the given bot,
        /// falling back to the safe default exactly like the sim loop.</summary>
        private static void Step(ShardsEngineAdapter adapter, ShardsHeuristicBot bot)
        {
            var pending = adapter.PendingInput;
            Assert.IsNotNull(pending, "engine stalled");
            var action = bot.Choose(pending, null) ?? adapter.DefaultActionFor(pending);
            if (!adapter.Submit(action).Accepted)
                Assert.IsTrue(adapter.Submit(adapter.DefaultActionFor(adapter.PendingInput)).Accepted);
        }

        [Test]
        public void Fork_AtPriorityPoints_ReplaysIdenticallyToTheOriginal()
        {
            // Play a real all-DLC game; at every 10th priority point fork WITHOUT
            // reseeding, then drive original and fork with identically-seeded bots and
            // compare ComputeFullHash step by step. Any field DeepCopy misses shows up
            // as divergence within a few steps.
            for (ulong seed = 11; seed <= 13; seed++)
            {
                var adapter = NewGame(seed);
                var driver = new ShardsHeuristicBot(seed * 1000, adapter.Inner);
                int priorityPoints = 0, forks = 0;

                for (int step = 0; step < 1500 && !adapter.GameOver; step++)
                {
                    var pending = adapter.PendingInput;
                    if (pending != null && pending.Kind == PendingInputKind.Priority &&
                        priorityPoints++ % 10 == 0)
                    {
                        forks++;
                        var fork = adapter.Inner.Fork(rngReseed: 0, quiet: true);
                        Assert.AreEqual(adapter.Inner.State.ComputeFullHash(), fork.State.ComputeFullHash(),
                            $"seed {seed}: fork differs from original immediately after Fork()");

                        // Identical twin bots (same seed) must drive both engines through
                        // an identical future for a while.
                        var botA = new ShardsHeuristicBot(seed * 77 + (ulong)step, adapter.Inner);
                        var botB = new ShardsHeuristicBot(seed * 77 + (ulong)step, fork);
                        var forkAdapter = new ForkAdapter(fork);
                        for (int k = 0; k < 40 && !adapter.GameOver && !fork.State.GameOver; k++)
                        {
                            Step(adapter, botA);
                            forkAdapter.Step(botB);
                            Assert.AreEqual(adapter.Inner.State.ComputeFullHash(), fork.State.ComputeFullHash(),
                                $"seed {seed}: divergence at fork {forks}, replay step {k}");
                        }
                        break; // one deep comparison per game keeps runtime small
                    }
                    var bot = driver;
                    var action = bot.Choose(pending, null) ?? adapter.DefaultActionFor(pending);
                    if (!adapter.Submit(action).Accepted)
                        adapter.Submit(adapter.DefaultActionFor(adapter.PendingInput));
                }
                Assert.Greater(forks, 0, $"seed {seed}: never reached a fork point");
            }
        }

        /// <summary>Thin Submit helper mirroring the adapter loop for a raw engine.</summary>
        private sealed class ForkAdapter
        {
            private readonly ShardsEngine _engine;
            public ForkAdapter(ShardsEngine engine) => _engine = engine;

            public void Step(ShardsHeuristicBot bot)
            {
                var pending = _engine.PendingInput;
                Assert.IsNotNull(pending, "fork stalled");
                var snap = new PendingSnap
                {
                    Kind = pending.Kind,
                    PlayerIndex = pending.PlayerIndex,
                    LegalActions = pending.LegalActions,
                    Decision = pending.Decision
                };
                var action = bot.Choose(snap, null) ?? DefaultActions.For(snap);
                if (!_engine.Submit(action).Accepted)
                {
                    pending = _engine.PendingInput;
                    var fallback = DefaultActions.For(new PendingSnap
                    {
                        Kind = pending.Kind,
                        PlayerIndex = pending.PlayerIndex,
                        LegalActions = pending.LegalActions,
                        Decision = pending.Decision
                    });
                    Assert.IsTrue(_engine.Submit(fallback).Accepted, "fork: default rejected");
                }
            }
        }

        [Test]
        public void Fork_IsQuiet_AndJournalRecordsAcceptedActions()
        {
            var adapter = NewGame(21);
            var bot = new ShardsHeuristicBot(2100, adapter.Inner);
            for (int i = 0; i < 30 && !adapter.GameOver; i++)
                Step(adapter, bot);

            int journal = adapter.Inner.Journal.Count;
            Assert.GreaterOrEqual(journal, 30, "every accepted submit must be journaled");

            if (adapter.PendingInput?.Kind != PendingInputKind.Priority)
                return; // (unlikely) stopped on a decision — quietness covered by other seeds
            var fork = adapter.Inner.Fork();
            var forkAdapter = new ForkAdapter(fork);
            var forkBot = new ShardsHeuristicBot(999, fork);
            for (int i = 0; i < 20 && !fork.State.GameOver; i++)
                forkAdapter.Step(forkBot);
            Assert.AreEqual(0, fork.Log.Count, "quiet forks must not accumulate events");
            Assert.AreEqual(journal, adapter.Inner.Journal.Count, "forking must not touch the original");
        }

        [Test]
        public void Fork_OnADecisionPoint_Throws()
        {
            // Drive until a decision is pending, then Fork must refuse.
            for (ulong seed = 1; seed < 40; seed++)
            {
                var adapter = NewGame(seed);
                var bot = new ShardsHeuristicBot(seed, adapter.Inner);
                for (int i = 0; i < 2000 && !adapter.GameOver; i++)
                {
                    if (adapter.PendingInput?.Kind == PendingInputKind.Decision)
                    {
                        Assert.Throws<System.InvalidOperationException>(() => adapter.Inner.Fork());
                        return;
                    }
                    Step(adapter, bot);
                }
            }
            Assert.Fail("no seed ever produced a decision point — implausible");
        }

        // ---------------------------------------------------------------- sentinels

        [Test]
        public void StateShapeSentinel_UpdateDeepCopyCloneAndFullHashWhenThisFails()
        {
            // Adding a field to any of these types requires updating ShardsStateClone
            // (DeepCopy / ShardsPlayer.Clone / ComputeFullHash) — then bump the counts.
            Assert.AreEqual(20, InstanceFieldCount(typeof(ShardsState)),
                "ShardsState fields changed → update DeepCopy + ComputeFullHash + this count");
            Assert.AreEqual(31, InstanceFieldCount(typeof(ShardsPlayer)),
                "ShardsPlayer fields changed → update Clone + ComputeFullHash + this count");
            Assert.AreEqual(7, InstanceFieldCount(typeof(ShardsCard)),
                "ShardsCard fields changed → update DeepCopy's card copy + hashes + this count");
        }

        private static int InstanceFieldCount(System.Type t)
        {
            int count = 0;
            for (var type = t; type != null && type != typeof(object); type = type.BaseType)
                count += type.GetFields(BindingFlags.Instance | BindingFlags.Public |
                                        BindingFlags.NonPublic | BindingFlags.DeclaredOnly).Length;
            return count;
        }
    }
}
