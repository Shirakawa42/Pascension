using System.Collections.Generic;
using NUnit.Framework;
using Pascension.Core;
using Pascension.Engine.Events;
using Shards.Bots;
using Shards.Content;
using Shards.Engine;
using Shards.Stats;

namespace Pascension.Engine.Tests
{
    /// <summary>SoiGameRecorder builds a SoiGameRecord from the REDACTED per-viewer
    /// stream. The keystone test (EngineVerify only — SoiSim sources are not a Unity
    /// assembly) proves it matches SoiSim's omniscient recorder on a full bot game.</summary>
    [TestFixture]
    public class SoiRecorderTests
    {
        private const ShardsDlc AllDlc =
            ShardsDlc.RelicsOfTheFuture | ShardsDlc.ShadowOfSalvation | ShardsDlc.IntoTheHorizon;

        [SetUp]
        public void SetUp()
        {
            ShardsCardDatabase.Clear();
            ShardsContentRegistry.EnsureRegistered();
        }

        // ---------------------------------------------------------------- helpers

        private static List<GameEvent> Stream(params GameEvent[] events)
        {
            var list = new List<GameEvent>(events);
            for (int i = 0; i < list.Count; i++)
                list[i].Seq = i;
            return list;
        }

        private static ShardsSnapshot MakeSnap(int viewer, int eventSeq, bool gameOver = true,
            int winner = 0, int round = 1)
        {
            var snap = new ShardsSnapshot
            {
                ViewerIndex = viewer,
                EventSeq = eventSeq,
                GameOver = gameOver,
                WinnerIndex = winner,
                Round = round,
                Dlc = 7
            };
            for (int i = 0; i < 2; i++)
                snap.Players.Add(new ShardsPlayerSnap
                {
                    Index = i,
                    Name = "P" + i,
                    CharacterId = i == 0 ? "decima" : "volos",
                    Health = 30 - i,
                    Mastery = 10 + i
                });
            return snap;
        }

        // ---------------------------------------------------------------- synthetic streams

        [Test]
        public void SyntheticStream_BuildsExpectedRecord()
        {
            var recorder = new SoiGameRecorder();
            recorder.OnEvents(Stream(
                new ShardsTurnStartedEvent { PlayerIndex = 0, Round = 1 },
                new ShardsCardDrawnEvent { PlayerIndex = 0, DefId = "crystal" },
                new ShardsCardDrawnEvent { PlayerIndex = 0, DefId = null }, // redacted draw still counts
                new ShardsCardPlayedEvent { PlayerIndex = 0, DefId = "crystal" },
                new ShardsCardBoughtEvent { PlayerIndex = 0, SlotIndex = 2, DefId = "gem_ally", CostPaid = 2 },
                new ShardsCardBoughtEvent { PlayerIndex = 0, SlotIndex = -1, DefId = "deck_recruit", CostPaid = 0 },
                new ShardsCardBoughtEvent { PlayerIndex = 0, SlotIndex = 1, DefId = "hired_blade", CostPaid = 3, FastPlay = true },
                new ShardsFocusedEvent { PlayerIndex = 0 },
                new ShardsMasteryChangedEvent { PlayerIndex = 0, Delta = 10, NewValue = 11 },
                new ShardsChampionDeployedEvent { PlayerIndex = 0, DefId = "guard_champ" },
                new ShardsTurnStartedEvent { PlayerIndex = 1, Round = 1 },
                new ShardsTurnStartedEvent { PlayerIndex = 0, Round = 2 },
                new ShardsDestinyTakenEvent { PlayerIndex = 0, DefId = "dest" },
                new ShardsRelicRecruitedEvent { PlayerIndex = 0, DefId = "relic" },
                new ShardsMonsterDefeatedEvent { PlayerIndex = 0, DefId = "mon" },
                new ShardsCardBanishedEvent { PlayerIndex = 0, DefId = "gone" },
                new ShardsDamageAssignedEvent { FromPlayerIndex = 0, Targets = { 1 }, Amounts = { 7 } },
                new ShardsShieldsRevealedEvent { PlayerIndex = 1, DefIds = { "shield_ally", "shield_ally" }, Prevented = 3 },
                new ShardsChampionDestroyedEvent { OwnerIndex = 1, ByPlayerIndex = 0, DefId = "their_champ" },
                new ShardsConcededEvent { PlayerIndex = 1 }));
            recorder.OnSnapshot(MakeSnap(0, 20, winner: 0, round: 2));

            var record = recorder.FinalizeRecord(new SoiRecordContext
            {
                EndedAtUtc = "2026-07-23T00:00:00Z",
                AppVersion = "9.9.9",
                DurationSeconds = 642,
                Mode = "ai",
                Seats = new List<SoiSeatIdentity>
                {
                    new() { Identity = "lucas", Name = "Lucas" },
                    new() { Identity = "bot:greedy", Name = "MASTER BOT", IsBot = true, BotKind = "greedy" }
                }
            });

            Assert.IsNotNull(record);
            Assert.AreEqual(1, record.Schema);
            Assert.IsTrue(record.Complete);
            Assert.AreEqual(0, record.MyIndex);
            Assert.AreEqual(0, record.WinnerIndex);
            Assert.AreEqual("kill", record.Termination);
            Assert.AreEqual(2, record.Rounds);
            Assert.AreEqual(3, record.Turns);
            Assert.AreEqual(7, record.Dlc);
            Assert.AreEqual("ai", record.Mode);
            Assert.AreEqual(642, record.DurationSeconds);
            Assert.AreEqual("2026-07-23T00:00:00Z", record.EndedAtUtc);
            Assert.AreEqual("9.9.9", record.AppVersion);
            Assert.IsNotNull(record.Guid);

            var p0 = record.Players[0];
            Assert.AreEqual("lucas", p0.Identity);
            Assert.AreEqual("Lucas", p0.Name);
            Assert.IsFalse(p0.IsBot);
            Assert.AreEqual("decima", p0.CharacterId);
            Assert.AreEqual(30, p0.FinalHealth);
            Assert.AreEqual(10, p0.FinalMastery);
            Assert.AreEqual(new Dictionary<string, int> { { "gem_ally", 1 }, { "hired_blade", 1 } }, p0.Buys);
            Assert.AreEqual(new Dictionary<string, int> { { "deck_recruit", 1 } }, p0.OffRowRecruits);
            Assert.AreEqual(new Dictionary<string, int> { { "hired_blade", 1 } }, p0.FastPlays);
            Assert.AreEqual(new Dictionary<string, int> { { "crystal", 1 } }, p0.Plays);
            Assert.AreEqual(new Dictionary<string, int> { { "guard_champ", 1 } }, p0.ChampionsDeployed);
            Assert.AreEqual(new Dictionary<string, int> { { "dest", 2 } }, p0.Destinies);
            Assert.AreEqual(new Dictionary<string, int> { { "mon", 2 } }, p0.MonstersDefeated);
            Assert.AreEqual(new List<string> { "relic" }, p0.Relics);
            Assert.AreEqual(5, p0.GemsSpent);
            Assert.AreEqual(1, p0.FocusCount);
            Assert.AreEqual(2, p0.CardsDrawn);
            Assert.AreEqual(1, p0.CardsBanished);
            Assert.AreEqual(7, p0.DamageDealt);
            Assert.AreEqual(7, p0.MaxSingleHit);
            Assert.AreEqual(1, p0.ChampionsKilled);
            Assert.AreEqual(0, p0.ChampionsLost);
            Assert.AreEqual(1, p0.RoundToM10, "M10 crossed in round 1");
            Assert.AreEqual(-1, p0.RoundToM20);
            Assert.AreEqual(-1, p0.RoundToM30);
            Assert.IsFalse(p0.Conceded);

            var p1 = record.Players[1];
            Assert.AreEqual("bot:greedy", p1.Identity);
            Assert.IsTrue(p1.IsBot);
            Assert.AreEqual("greedy", p1.BotKind);
            Assert.AreEqual(2, p1.ShieldReveals);
            Assert.AreEqual(3, p1.DamagePrevented);
            Assert.AreEqual(1, p1.ChampionsLost);
            Assert.AreEqual(0, p1.ChampionsKilled);
            Assert.IsTrue(p1.Conceded);
            Assert.AreEqual(29, p1.FinalHealth);
            Assert.AreEqual(11, p1.FinalMastery);
        }

        [Test]
        public void Termination_Kill_Overwhelm_Tie()
        {
            Assert.AreEqual("kill", RunTermination(winner: 0, overPowerPlayer: -1).Termination);
            Assert.AreEqual("overwhelm", RunTermination(winner: 0, overPowerPlayer: 0).Termination);
            Assert.AreEqual("kill", RunTermination(winner: 0, overPowerPlayer: 1).Termination,
                "only the WINNER going over 1000 power counts as overwhelm");
            Assert.AreEqual("tie", RunTermination(winner: -1, overPowerPlayer: -1).Termination);
        }

        private static SoiGameRecord RunTermination(int winner, int overPowerPlayer)
        {
            var recorder = new SoiGameRecorder();
            var events = new List<GameEvent> { new ShardsTurnStartedEvent { PlayerIndex = 0, Round = 1 } };
            if (overPowerPlayer >= 0)
                events.Add(new ShardsPowerChangedEvent { PlayerIndex = overPowerPlayer, NewValue = 1001 });
            for (int i = 0; i < events.Count; i++)
                events[i].Seq = i;
            recorder.OnEvents(events);
            recorder.OnSnapshot(MakeSnap(0, events.Count, winner: winner));
            return recorder.FinalizeRecord(null);
        }

        [Test]
        public void SeqGap_MarksIncomplete()
        {
            var recorder = new SoiGameRecorder();
            var first = new ShardsTurnStartedEvent { PlayerIndex = 0, Round = 1 };
            first.Seq = 0;
            var afterGap = new ShardsFocusedEvent { PlayerIndex = 0 }; // seq 1 went missing
            afterGap.Seq = 2;
            recorder.OnEvents(new List<GameEvent> { first, afterGap });
            recorder.OnSnapshot(MakeSnap(0, 3));

            var record = recorder.FinalizeRecord(null);
            Assert.IsNotNull(record);
            Assert.IsFalse(record.Complete);
            Assert.AreEqual(1, record.Players[0].FocusCount, "keeps accumulating past the gap");
        }

        [Test]
        public void MidGameFirstSnapshot_MarksIncomplete()
        {
            var recorder = new SoiGameRecorder();
            recorder.OnSnapshot(MakeSnap(0, eventSeq: 42, gameOver: false));
            var next = new ShardsFocusedEvent { PlayerIndex = 0 }; // stream resumes at the join point
            next.Seq = 42;
            recorder.OnEvents(new List<GameEvent> { next });
            recorder.OnSnapshot(MakeSnap(0, 43));

            var record = recorder.FinalizeRecord(null);
            Assert.IsNotNull(record);
            Assert.IsFalse(record.Complete);
            Assert.AreEqual(1, record.Players[0].FocusCount);
        }

        [Test]
        public void FinalizeRecord_SecondCall_ReturnsNull()
        {
            var recorder = new SoiGameRecorder();
            recorder.OnSnapshot(MakeSnap(0, 0, gameOver: false));
            Assert.IsNull(recorder.FinalizeRecord(null), "game not over yet");
            recorder.OnSnapshot(MakeSnap(0, 0));
            Assert.IsNotNull(recorder.FinalizeRecord(null));
            Assert.IsNull(recorder.FinalizeRecord(null), "finalize is one-shot");
        }

        // ---------------------------------------------------------------- snapshot stamping

        [Test]
        public void Snapshot_CarriesIsBotAndBotKind()
        {
            var adapter = new ShardsEngineAdapter(ShardsContentRegistry.StandardConfig(5,
                new List<PlayerSpec>
                {
                    new() { Name = "Alice", CharacterId = "decima" },
                    new() { Name = "Bot", CharacterId = "volos", IsBot = true, BotKind = "greedy" }
                }, AllDlc));

            var snap = (ShardsSnapshot)adapter.BuildSnapshot(0);
            Assert.IsFalse(snap.Players[0].IsBot);
            Assert.IsNull(snap.Players[0].BotKind);
            Assert.IsTrue(snap.Players[1].IsBot);
            Assert.AreEqual("greedy", snap.Players[1].BotKind);

            // Call sites without specs (search-bot forks etc.) stay null-safe.
            var bare = ShardsSnapshotBuilder.Build(adapter.Inner, 0);
            Assert.IsFalse(bare.Players[1].IsBot);
            Assert.IsNull(bare.Players[1].BotKind);
        }

#if !UNITY_5_3_OR_NEWER
        // ---------------------------------------------------------------- KEYSTONE
        // EngineVerify-only: SoiSim's omniscient recorder compiles into the same
        // csproj but is not a Unity assembly, so this test cannot build in-editor.

        [Test]
        public void FullSimGame_RedactedRecorder_MatchesOmniscientRecorder()
        {
            var characters = new[] { "decima", "volos" };
            var adapter = new ShardsEngineAdapter(ShardsContentRegistry.StandardConfig(2026,
                new List<PlayerSpec>
                {
                    new() { Name = "P0", CharacterId = characters[0] },
                    new() { Name = "P1", CharacterId = characters[1], IsBot = true, BotKind = "heuristic" }
                }, AllDlc));

            // State peeks are the SIM's privilege; the client recorders never touch them.
            var destinyRowInitial = new List<string>();
            foreach (var card in adapter.Inner.State.DestinyRow)
                destinyRowInitial.Add(card.DefId);
            var initialHealth = new List<int>();
            var initialMastery = new List<int>();
            foreach (var p in adapter.Inner.State.Players)
            {
                initialHealth.Add(p.Health);
                initialMastery.Add(p.Mastery);
            }

            // Per-viewer recorders fed exactly what a client would receive: the
            // redacted event stream (delivered before snapshots, as GameHost does).
            var recorders = new SoiGameRecorder[2];
            var since = new int[2];
            for (int v = 0; v < 2; v++)
            {
                recorders[v] = new SoiGameRecorder();
                recorders[v].OnEvents(adapter.FilterEventsFor(v, 0));
                since[v] = adapter.EventCount;
                recorders[v].OnSnapshot((ShardsSnapshot)adapter.BuildSnapshot(v));
            }

            var bots = new IBotAgent[]
            {
                new ShardsHeuristicBot(11, adapter.Inner),
                new ShardsHeuristicBot(22, adapter.Inner)
            };
            int guard = 0;
            while (!adapter.GameOver && guard++ < 30000)
            {
                var pending = adapter.PendingInput;
                Assert.IsNotNull(pending, "engine stalled");
                var action = bots[pending.PlayerIndex].Choose(pending, null)
                             ?? adapter.DefaultActionFor(pending);
                if (!adapter.Submit(action).Accepted)
                    adapter.Submit(adapter.DefaultActionFor(adapter.PendingInput));
                for (int v = 0; v < 2; v++)
                {
                    recorders[v].OnEvents(adapter.FilterEventsFor(v, since[v]));
                    since[v] = adapter.EventCount;
                }
            }
            Assert.IsTrue(adapter.GameOver, "game must finish under the guard");

            var omniscient = SoiSim.GameRecorder.Extract(adapter, 2026, characters,
                destinyRowInitial, initialHealth, initialMastery, guard, 0, 0);

            for (int v = 0; v < 2; v++)
            {
                recorders[v].OnSnapshot((ShardsSnapshot)adapter.BuildSnapshot(v));
                var record = recorders[v].FinalizeRecord(new SoiRecordContext
                {
                    EndedAtUtc = "2026-07-23T00:00:00Z",
                    AppVersion = "test",
                    DurationSeconds = 1,
                    Mode = "ai"
                });
                Assert.IsNotNull(record, $"viewer {v} record");
                Assert.IsTrue(record.Complete, $"viewer {v} saw a contiguous stream");
                Assert.AreEqual(omniscient.Winner, record.WinnerIndex, $"viewer {v} winner");
                Assert.AreEqual(omniscient.Rounds, record.Rounds, $"viewer {v} rounds");
                Assert.AreEqual(omniscient.Turns, record.Turns, $"viewer {v} turns");
                Assert.AreEqual(omniscient.Termination, record.Termination, $"viewer {v} termination");

                for (int s = 0; s < 2; s++)
                {
                    var sim = omniscient.Players[s];
                    var mine = record.Players[s];
                    string tag = $"viewer {v} seat {s}";
                    AssertDictEqual(sim.Buys, mine.Buys, tag + " Buys");
                    AssertDictEqual(sim.OffRowRecruits, mine.OffRowRecruits, tag + " OffRowRecruits");
                    AssertDictEqual(sim.FastPlays, mine.FastPlays, tag + " FastPlays");
                    AssertDictEqual(sim.ChampionsDeployed, mine.ChampionsDeployed, tag + " ChampionsDeployed");
                    CollectionAssert.AreEqual(sim.Relics, mine.Relics, tag + " Relics");
                    CollectionAssert.AreEquivalent(sim.Destinies.Keys, mine.Destinies.Keys, tag + " Destinies keys");
                    AssertDictEqual(sim.MonstersDefeated, mine.MonstersDefeated, tag + " MonstersDefeated");
                    Assert.AreEqual(sim.DamageDealt, mine.DamageDealt, tag + " DamageDealt");
                    Assert.AreEqual(sim.MaxSingleHit, mine.MaxSingleHit, tag + " MaxSingleHit");
                    Assert.AreEqual(sim.GemsSpent, mine.GemsSpent, tag + " GemsSpent");
                    Assert.AreEqual(sim.FocusCount, mine.FocusCount, tag + " FocusCount");
                    Assert.AreEqual(sim.CardsDrawn, mine.CardsDrawn, tag + " CardsDrawn");
                    Assert.AreEqual(sim.CardsBanished, mine.CardsBanished, tag + " CardsBanished");
                    Assert.AreEqual(sim.ShieldReveals, mine.ShieldReveals, tag + " ShieldReveals");
                    Assert.AreEqual(sim.DamagePrevented, mine.DamagePrevented, tag + " DamagePrevented");
                    Assert.AreEqual(sim.ChampionsLost, mine.ChampionsLost, tag + " ChampionsLost");
                    Assert.AreEqual(sim.ChampionsKilled, mine.ChampionsKilled, tag + " ChampionsKilled");
                    Assert.AreEqual(sim.RoundToM10, mine.RoundToM10, tag + " RoundToM10");
                    Assert.AreEqual(sim.RoundToM20, mine.RoundToM20, tag + " RoundToM20");
                    Assert.AreEqual(sim.RoundToM30, mine.RoundToM30, tag + " RoundToM30");
                    Assert.AreEqual(sim.FinalHealth, mine.FinalHealth, tag + " FinalHealth");
                    Assert.AreEqual(sim.FinalMastery, mine.FinalMastery, tag + " FinalMastery");
                }

                // No ctx seats — identities fall back to the snapshot stamping.
                Assert.AreEqual("p0", record.Players[0].Identity);
                Assert.AreEqual("bot:heuristic", record.Players[1].Identity);
            }
        }

        private static void AssertDictEqual(Dictionary<string, int> expected,
            Dictionary<string, int> actual, string label)
        {
            Assert.AreEqual(expected.Count, actual.Count, label + " count");
            foreach (var kv in expected)
            {
                Assert.IsTrue(actual.TryGetValue(kv.Key, out int value), label + " missing " + kv.Key);
                Assert.AreEqual(kv.Value, value, label + " " + kv.Key);
            }
        }
#endif
    }
}
