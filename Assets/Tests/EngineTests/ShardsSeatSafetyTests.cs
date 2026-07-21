using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using NUnit.Framework;
using Pascension.Core;
using Pascension.Engine.Serialization;
using Pascension.Net;
using Shards.Bots;
using Shards.Content;
using Shards.Engine;

namespace Pascension.Engine.Tests
{
    /// <summary>The MASTER-freeze regression suite: a faulting search seat must never
    /// hang the game, the card index must survive concurrent access, and every minted
    /// ladder rank must resolve to a working bot.</summary>
    public sealed class ShardsSeatSafetyTests
    {
        private const ShardsDlc AllDlc =
            ShardsDlc.RelicsOfTheFuture | ShardsDlc.ShadowOfSalvation | ShardsDlc.IntoTheHorizon;

        [SetUp]
        public void SetUp()
        {
            ShardsCardDatabase.Clear();
            ShardsContentRegistry.EnsureRegistered();
        }

        private sealed class ThrowingAgent : IBotAgent
        {
            public int Calls;
            public Pascension.Engine.Actions.PlayerAction Choose(PendingSnap pending, SnapshotBase view)
            {
                Calls++;
                throw new InvalidOperationException("deliberate test fault");
            }
        }

        [Test]
        public void FaultingSearchSeat_NeverHangsTheGame()
        {
            // Pre-fix, a single throw inside the seat's fire-and-forget task froze the
            // game forever (no bot response timeout exists). Now every fault must fall
            // back to the engine's safe default and the game must still END.
            var specs = new List<PlayerSpec>
            {
                new() { Name = "Faulty", CharacterId = "decima", IsBot = true },
                new() { Name = "Heuristic", CharacterId = "tetra", IsBot = true }
            };
            var adapter = new ShardsEngineAdapter(ShardsContentRegistry.StandardConfig(91, specs, AllDlc));
            var host = new GameHost(adapter, 2, 0f);

            var throwing = new ThrowingAgent();
            var faultySeat = new SearchBotSeat(0, throwing, host);
            int faults = 0;
            faultySeat.SeatFaulted += (_, _) => Interlocked.Increment(ref faults);
            host.AttachSeat(faultySeat, isHuman: false);

            var normalSeat = new BotSeat(1, new ShardsHeuristicBot(9100, adapter.Inner), thinkDelaySeconds: 0f);
            normalSeat.Bind(host);
            host.AttachSeat(normalSeat, isHuman: false);

            host.Start();
            var sw = Stopwatch.StartNew();
            while (!adapter.GameOver && sw.Elapsed.TotalSeconds < 30)
            {
                host.Tick(0.02f);
                normalSeat.Tick(0.02f);
                Thread.Sleep(1); // let the faulty seat's worker task run
            }

            Assert.IsTrue(adapter.GameOver,
                $"game did not finish within 30s — the faulting seat hung it (faults so far: {faults})");
            Assert.Greater(faults, 0, "the throwing agent should have faulted at least once");
            Assert.Greater(throwing.Calls, 0);
        }

        [Test]
        public void FindCard_SurvivesConcurrentLookupsAndInvalidation()
        {
            var specs = new List<PlayerSpec>
            {
                new() { Name = "A", CharacterId = "decima" },
                new() { Name = "B", CharacterId = "volos" }
            };
            var adapter = new ShardsEngineAdapter(ShardsContentRegistry.StandardConfig(92, specs, AllDlc));
            var state = adapter.Inner.State;
            int probeId = state.Players[0].Hand[0].InstanceId;

            Exception failure = null;
            var threads = new List<Thread>();
            for (int t = 0; t < 8; t++)
            {
                bool invalidator = t == 0;
                threads.Add(new Thread(() =>
                {
                    try
                    {
                        for (int i = 0; i < 20000; i++)
                        {
                            if (invalidator && i % 50 == 0)
                                state.InvalidateCardIndex();
                            var card = state.FindCard(probeId);
                            if (card == null || card.InstanceId != probeId)
                                throw new InvalidOperationException("index returned a wrong card");
                        }
                    }
                    catch (Exception ex)
                    {
                        failure = ex;
                    }
                }));
            }
            foreach (var thread in threads) thread.Start();
            foreach (var thread in threads)
                Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(20)),
                    "a thread hung — the card index corrupted under concurrency");
            Assert.IsNull(failure, failure?.ToString());
        }

        [Test]
        public void RankRegistry_EveryMintedRankResolvesToAWorkingBot()
        {
            var specs = new List<PlayerSpec>
            {
                new() { Name = "A", CharacterId = "decima" },
                new() { Name = "B", CharacterId = "kosynwu" }
            };
            var adapter = new ShardsEngineAdapter(ShardsContentRegistry.StandardConfig(93, specs, AllDlc));

            var seenKinds = new HashSet<string>();
            var seenNames = new HashSet<string>();
            foreach (var rank in ShardsBotRanks.Minted)
            {
                Assert.IsTrue(seenKinds.Add(rank.KindString), $"duplicate kind {rank.KindString}");
                Assert.IsTrue(seenNames.Add(rank.DisplayName), $"duplicate display {rank.DisplayName}");
                var bot = ShardsBotRanks.Create(rank.KindString, 5, adapter.Inner);
                Assert.IsNotNull(bot, rank.Id);
                Assert.AreEqual(rank.IsSearch, ShardsBotRanks.IsSearchKind(rank.KindString), rank.Id);
                var action = bot.Choose(adapter.PendingInput, null);
                Assert.IsNotNull(action, $"{rank.Id} produced no opening action");
            }
            // Ladder floor today: iron, bronze, silver (higher ranks minted by training).
            Assert.GreaterOrEqual(ShardsBotRanks.Minted.Count, 3);
            Assert.AreEqual("rank:iron", ShardsBotRanks.Minted[0].KindString);
        }
    }
}
