using System.Collections.Generic;
using NUnit.Framework;
using Shards.Stats;

namespace Pascension.Engine.Tests
{
    /// <summary>SoiRecordStore is CRDT-shaped: merge must converge to an identical
    /// blob regardless of order, and the eviction cap must never change the lifetime
    /// winrate (evicted records leave stubs behind).</summary>
    [TestFixture]
    public class SoiRecordStoreTests
    {
        private static SoiGameRecord Rec(string guid, string endedAt, int winner = 0,
            string mode = "ai")
        {
            return new SoiGameRecord
            {
                Guid = guid,
                EndedAtUtc = endedAt,
                Mode = mode,
                MyIndex = 0,
                WinnerIndex = winner,
                Termination = winner < 0 ? "tie" : "kill",
                Rounds = 8,
                DurationSeconds = 300,
                Players = new List<SoiSeatRecord>
                {
                    new() { Identity = "me", Name = "Me", CharacterId = "decima" },
                    new() { Identity = "bot:greedy", Name = "Bot", IsBot = true, BotKind = "greedy", CharacterId = "volos" }
                }
            };
        }

        private static string Canon(SoiSaveData data) => SoiStatsJson.Serialize(data);

        [Test]
        public void Merge_Commutative_Idempotent()
        {
            var a = new SoiSaveData
            {
                ProfileKey = "k",
                Records = { Rec("g1", "2026-01-01"), Rec("g2", "2026-01-02") },
                Stubs = { SoiRecordStore.ToStub(Rec("g0", "2025-12-31")) }
            };
            var b = new SoiSaveData
            {
                ProfileKey = "k",
                Records = { Rec("g2", "2026-01-02"), Rec("g3", "2026-01-03") },
                Stubs = { SoiRecordStore.ToStub(Rec("gz", "2025-12-30")) }
            };

            var ab = SoiRecordStore.Merge(a, b);
            var ba = SoiRecordStore.Merge(b, a);
            Assert.AreEqual(Canon(ab), Canon(ba), "merge must be commutative");

            var again = SoiRecordStore.Merge(ab, b);
            Assert.AreEqual(Canon(ab), Canon(again), "merge must be idempotent");

            Assert.AreEqual(3, ab.Records.Count, "g1 g2 g3 deduped");
            Assert.AreEqual(2, ab.Stubs.Count, "g0 gz kept");
            Assert.AreEqual("g1", ab.Records[0].Guid, "canonical (EndedAtUtc, Guid) order");
            Assert.AreEqual("g3", ab.Records[2].Guid);
        }

        [Test]
        public void Merge_StubShadowedByFullRecord()
        {
            var withStub = new SoiSaveData
            {
                ProfileKey = "k",
                Stubs = { SoiRecordStore.ToStub(Rec("gx", "2026-01-05")) }
            };
            var withRecord = new SoiSaveData
            {
                ProfileKey = "k",
                Records = { Rec("gx", "2026-01-05") }
            };

            foreach (var merged in new[]
                     {
                         SoiRecordStore.Merge(withStub, withRecord),
                         SoiRecordStore.Merge(withRecord, withStub)
                     })
            {
                Assert.AreEqual(1, merged.Records.Count, "the full record survives");
                Assert.AreEqual("gx", merged.Records[0].Guid);
                Assert.AreEqual(0, merged.Stubs.Count, "the shadowed stub is dropped");
            }
        }

        [Test]
        public void Cap_EvictsOldest_Deterministically()
        {
            var forward = new SoiSaveData();
            var backward = new SoiSaveData();
            for (int i = 1; i <= 6; i++)
                forward.Records.Add(Rec("g" + i, "2026-01-0" + i));
            for (int i = 6; i >= 1; i--)
                backward.Records.Add(Rec("g" + i, "2026-01-0" + i));

            SoiRecordStore.ApplyCap(forward, 3);
            SoiRecordStore.ApplyCap(backward, 3);

            Assert.AreEqual(Canon(forward), Canon(backward), "input order must not matter");
            Assert.AreEqual(3, forward.Records.Count);
            Assert.AreEqual("g4", forward.Records[0].Guid, "the newest three survive");
            Assert.AreEqual("g6", forward.Records[2].Guid);
            Assert.AreEqual(3, forward.Stubs.Count);
            Assert.AreEqual("g1", forward.Stubs[0].Guid, "the oldest three became stubs");
            Assert.AreEqual("g3", forward.Stubs[2].Guid);
        }

        [Test]
        public void Eviction_PreservesLifetimeWinrate()
        {
            var data = new SoiSaveData();
            for (int i = 0; i < 10; i++)
            {
                int winner = i % 3 == 0 ? 0 : i % 3 == 1 ? 1 : -1;
                data.Records.Add(Rec("g" + i, "2026-01-" + (10 + i), winner));
            }

            var before = SoiStatsAggregator.Compute(data.Records, data.Stubs, null);
            SoiRecordStore.ApplyCap(data, 4);
            var after = SoiStatsAggregator.Compute(data.Records, data.Stubs, null);

            Assert.AreEqual(4, after.Games, "only capped records feed detailed stats");
            Assert.AreEqual(before.LifetimeGames, after.LifetimeGames);
            Assert.AreEqual(before.LifetimeWins, after.LifetimeWins);
            Assert.AreEqual(before.LifetimeTies, after.LifetimeTies);
        }

        [Test]
        public void Append_DedupesByGuid()
        {
            var data = new SoiSaveData();
            Assert.IsTrue(SoiRecordStore.Append(data, Rec("g1", "2026-01-01")));
            Assert.IsFalse(SoiRecordStore.Append(data, Rec("g1", "2026-01-01")), "same guid again");
            Assert.AreEqual(1, data.Records.Count);

            data.Stubs.Add(SoiRecordStore.ToStub(Rec("g2", "2025-01-01")));
            Assert.IsFalse(SoiRecordStore.Append(data, Rec("g2", "2025-01-01")),
                "a guid already stubbed is a duplicate too");
            Assert.AreEqual(1, data.Records.Count);
        }
    }
}
