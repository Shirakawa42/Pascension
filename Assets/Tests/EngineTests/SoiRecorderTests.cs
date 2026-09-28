using System.Collections.Generic;
using NUnit.Framework;
using Pascension.Core;
using Pascension.Engine.Events;
using Shards.Content;
using Shards.Engine;
using Shards.Stats;

namespace Pascension.Engine.Tests
{
    /// <summary>Match records are built from redacted events and snapshots.</summary>
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
                    new() { Identity = "bot:archived", Name = "Archived opponent", IsBot = true, BotKind = "archived" }
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
            Assert.AreEqual("bot:archived", p1.Identity);
            Assert.IsTrue(p1.IsBot);
            Assert.AreEqual("archived", p1.BotKind);
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

    }
}
