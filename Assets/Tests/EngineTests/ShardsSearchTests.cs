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
        public void Fork_WithArena_RecycledCloneMatchesFreshFork()
        {
            // The arena hands the SAME state object back on every Fork — after playing
            // the previous clone forward (mutating every zone, counters, the card
            // index), the next arena fork must be indistinguishable from a fresh fork.
            var adapter = NewGame(31);
            var driver = new ShardsHeuristicBot(3100, adapter.Inner);
            for (int i = 0; i < 60 && !adapter.GameOver; i++)
                Step(adapter, driver);
            if (adapter.PendingInput?.Kind != PendingInputKind.Priority || adapter.GameOver)
                Assert.Inconclusive("seed 31 did not stop on a live priority point");

            var arena = new ShardsCloneArena();
            for (int round = 0; round < 3; round++)
            {
                var fresh = adapter.Inner.Fork(rngReseed: 555, quiet: true);
                var pooled = adapter.Inner.Fork(rngReseed: 555, quiet: true, arena: arena);
                Assert.AreEqual(fresh.State.ComputeFullHash(), pooled.State.ComputeFullHash(),
                    $"round {round}: recycled clone differs from a fresh fork");

                // Dirty the pooled clone so the next round must fully reset it.
                var bot = new ShardsHeuristicBot(999 + (ulong)round, pooled);
                var pooledAdapter = new ForkAdapter(pooled);
                for (int k = 0; k < 25 && !pooled.State.GameOver; k++)
                    pooledAdapter.Step(bot);
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

        // ---------------------------------------------------------------- determinizer

        [Test]
        public void Determinizer_PreservesCompositionCountsAndPublicProjection()
        {
            var adapter = NewGame(51);
            var bot = new ShardsHeuristicBot(5100, adapter.Inner);
            for (int i = 0; i < 60 && !adapter.GameOver; i++)
                Step(adapter, bot);
            if (adapter.PendingInput?.Kind != PendingInputKind.Priority || adapter.GameOver)
                Assert.Inconclusive("seed 51 did not stop on a live priority point");

            int viewer = adapter.PendingInput.PlayerIndex;
            var fork = adapter.Inner.Fork(rngReseed: 999);
            var before = Snapshot(fork.State);
            ulong publicBefore = fork.State.ComputePublicHash(viewer);
            var ownHandBefore = fork.State.Players[viewer].Hand.ConvertAll(c => c.InstanceId);

            ShardsDeterminizer.Sample(fork.State, viewer, fork.State.Rng);

            var after = Snapshot(fork.State);
            Assert.AreEqual(before.Counts, after.Counts, "zone counts must be preserved");
            CollectionAssert.AreEqual(before.Compositions, after.Compositions,
                "per-player composition multisets must be preserved");
            Assert.AreEqual(publicBefore, fork.State.ComputePublicHash(viewer),
                "the viewer's public projection must be unchanged");
            CollectionAssert.AreEqual(ownHandBefore,
                fork.State.Players[viewer].Hand.ConvertAll(c => c.InstanceId),
                "the viewer's own hand must be untouched");
        }

        private static (string Counts, List<string> Compositions) Snapshot(ShardsState state)
        {
            string counts = $"{state.CenterDeck.Count}/{state.DestinyDeck.Count}";
            var compositions = new List<string>();
            foreach (var p in state.Players)
            {
                counts += $"/{p.Hand.Count}:{p.Deck.Count}";
                var pool = new List<string>();
                foreach (var c in p.Hand) pool.Add(c.DefId);
                foreach (var c in p.Deck) pool.Add(c.DefId);
                pool.Sort(System.StringComparer.Ordinal);
                compositions.Add(p.Index + "=" + string.Join(",", pool));
            }
            return (counts, compositions);
        }

        [Test]
        public void Search_IsInvariantToHiddenInformation()
        {
            // Two identical games; in B, swap an opponent HAND card with an opponent
            // DECK card (different def ids). Public information is identical, hidden
            // information differs — a fair search must choose the identical action.
            var a = NewGame(61);
            var b = NewGame(61);
            var botA = new ShardsHeuristicBot(6100, a.Inner);
            var botB = new ShardsHeuristicBot(6100, b.Inner);
            for (int i = 0; i < 40 && !a.GameOver; i++)
            {
                Step(a, botA);
                Step(b, botB);
            }
            Assert.AreEqual(a.Inner.State.ComputeFullHash(), b.Inner.State.ComputeFullHash(),
                "the twin games must be identical before surgery");
            if (a.PendingInput?.Kind != PendingInputKind.Priority || a.GameOver)
                Assert.Inconclusive("seed 61 did not stop on a live priority point");

            int viewer = a.PendingInput.PlayerIndex;
            var opponent = b.Inner.State.Players[1 - viewer];
            int hi = -1, di = -1;
            for (int h = 0; h < opponent.Hand.Count && hi < 0; h++)
                for (int d = 0; d < opponent.Deck.Count; d++)
                    if (opponent.Hand[h].DefId != opponent.Deck[d].DefId)
                    {
                        hi = h;
                        di = d;
                        break;
                    }
            if (hi < 0)
                Assert.Inconclusive("opponent hand/deck were def-uniform at the probe point");

            var handCard = opponent.Hand[hi];
            var deckCard = opponent.Deck[di];
            opponent.Hand[hi] = deckCard;
            opponent.Deck[di] = handCard;
            (handCard.Zone, deckCard.Zone) = (deckCard.Zone, handCard.Zone);
            b.Inner.State.InvalidateCardIndex();
            Assert.AreEqual(a.Inner.State.ComputePublicHash(viewer), b.Inner.State.ComputePublicHash(viewer),
                "surgery must not change the public projection");

            var config = ShardsSearchConfig.ForSims(80);
            var model = new ShardsValueModel();
            var searchA = new ShardsSearchBot(777, a.Inner, config, model);
            var searchB = new ShardsSearchBot(777, b.Inner, config, model);
            var actionA = searchA.Choose(a.PendingInput, null);
            var actionB = searchB.Choose(b.PendingInput, null);
            Assert.AreEqual(actionA.Describe(), actionB.Describe(),
                "hidden information leaked into the search");
            Assert.That(searchA.LastRootQ, Is.InRange(0.0, 1.0),
                "a completed root search must expose its chosen child's mean value");
            Assert.AreEqual(searchA.LastRootQ, searchB.LastRootQ, 1e-9,
                "hidden information leaked into the root Q");
        }

        [Test]
        public void SearchBot_BeatsRandom_Smoke()
        {
            int wins = 0;
            for (ulong seed = 71; seed <= 73; seed++)
            {
                var adapter = NewGame(seed);
                var seats = new IBotAgent[]
                {
                    new ShardsSearchBot(seed, adapter.Inner, ShardsSearchConfig.ForSims(48)),
                    new ShardsHeuristicBot(seed * 100 + 1, adapter.Inner, random: true)
                };
                int guard = 0;
                while (!adapter.GameOver && guard++ < 30000)
                {
                    var pending = adapter.PendingInput;
                    Assert.IsNotNull(pending);
                    var action = seats[pending.PlayerIndex].Choose(pending, null)
                                 ?? adapter.DefaultActionFor(pending);
                    if (!adapter.Submit(action).Accepted)
                        Assert.IsTrue(adapter.Submit(adapter.DefaultActionFor(adapter.PendingInput)).Accepted);
                }
                if (adapter.WinnerIndex == 0) wins++;
            }
            Assert.GreaterOrEqual(wins, 2, "the search bot must dominate a random bot");
        }

        // ---------------------------------------------------------------- sentinels

        [Test]
        public void StateShapeSentinel_UpdateDeepCopyCloneAndFullHashWhenThisFails()
        {
            // Adding a field to any of these types requires updating ShardsStateClone
            // (DeepCopy / ShardsPlayer.Clone / ComputeFullHash) — then bump the counts.
            // 21 = 18 data fields + _cardIndex/_cardIndexBuiltAt/_cardIndexLock (the
            // index trio is rebuilt lazily and deliberately NOT copied or hashed).
            Assert.AreEqual(21, InstanceFieldCount(typeof(ShardsState)),
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
