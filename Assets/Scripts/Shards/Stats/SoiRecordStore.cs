using System.Collections.Generic;

namespace Shards.Stats
{
    /// <summary>Pure set-algebra over SoiSaveData: append with dedupe, CRDT-style merge
    /// (commutative, idempotent, convergent — same input multiset yields an identical
    /// blob regardless of order), and the eviction cap that converts the oldest full
    /// records into stubs. Canonical order everywhere: (EndedAtUtc, Guid) ordinal.</summary>
    public static class SoiRecordStore
    {
        public const int DefaultCap = 2000;

        /// <summary>Insert one freshly finalized record. Returns false (no change)
        /// when the Guid is already present as a record or stub.</summary>
        public static bool Append(SoiSaveData data, SoiGameRecord record, int cap = DefaultCap)
        {
            if (data == null || record == null || record.Guid == null) return false;
            foreach (var existing in data.Records)
                if (existing.Guid == record.Guid)
                    return false;
            foreach (var stub in data.Stubs)
                if (stub.Guid == record.Guid)
                    return false;
            data.Records.Add(record);
            ApplyCap(data, cap);
            return true;
        }

        /// <summary>Union both sides by Guid (records win over stubs), then re-cap.
        /// A Guid identifies a game's content, so first-seen wins on collision.</summary>
        public static SoiSaveData Merge(SoiSaveData local, SoiSaveData remote, int cap = DefaultCap)
        {
            var result = new SoiSaveData
            {
                ProfileKey = local?.ProfileKey ?? remote?.ProfileKey,
                DirtyUpload = (local?.DirtyUpload ?? false) || (remote?.DirtyUpload ?? false)
            };

            var recordGuids = new HashSet<string>();
            CollectRecords(local, recordGuids, result.Records);
            CollectRecords(remote, recordGuids, result.Records);

            var stubGuids = new HashSet<string>();
            CollectStubs(local, recordGuids, stubGuids, result.Stubs);
            CollectStubs(remote, recordGuids, stubGuids, result.Stubs);

            ApplyCap(result, cap);
            return result;
        }

        /// <summary>Sort records by (EndedAtUtc, Guid) ordinal; convert everything past
        /// the cap (the oldest) into stubs; keep stubs deduped and sorted.</summary>
        public static void ApplyCap(SoiSaveData data, int cap = DefaultCap)
        {
            if (data == null) return;
            if (cap < 0) cap = 0;
            data.Records.Sort(CompareRecords);

            int overflow = data.Records.Count - cap;
            if (overflow > 0)
            {
                var stubGuids = new HashSet<string>();
                foreach (var stub in data.Stubs)
                    stubGuids.Add(stub.Guid);
                for (int i = 0; i < overflow; i++)
                {
                    var oldest = data.Records[i];
                    if (stubGuids.Add(oldest.Guid))
                        data.Stubs.Add(ToStub(oldest));
                }
                data.Records.RemoveRange(0, overflow);
            }
            data.Stubs.Sort(CompareStubs);
        }

        public static SoiGameStub ToStub(SoiGameRecord record)
        {
            var stub = new SoiGameStub
            {
                Guid = record.Guid,
                EndedAtUtc = record.EndedAtUtc,
                Mode = record.Mode,
                Won = record.WinnerIndex == record.MyIndex,
                Tie = record.WinnerIndex < 0,
                MyCharacterId = record.MyIndex >= 0 && record.MyIndex < record.Players.Count
                    ? record.Players[record.MyIndex].CharacterId
                    : null
            };
            for (int i = 0; i < record.Players.Count; i++)
                if (i != record.MyIndex)
                    stub.Opponents.Add(new SoiStubOpponent
                    {
                        Identity = record.Players[i].Identity,
                        CharacterId = record.Players[i].CharacterId
                    });
            return stub;
        }

        private static void CollectRecords(SoiSaveData source, HashSet<string> guids,
            List<SoiGameRecord> into)
        {
            if (source?.Records == null) return;
            foreach (var record in source.Records)
                if (record?.Guid != null && guids.Add(record.Guid))
                    into.Add(record);
        }

        private static void CollectStubs(SoiSaveData source, HashSet<string> recordGuids,
            HashSet<string> stubGuids, List<SoiGameStub> into)
        {
            if (source?.Stubs == null) return;
            foreach (var stub in source.Stubs)
                if (stub?.Guid != null && !recordGuids.Contains(stub.Guid) && stubGuids.Add(stub.Guid))
                    into.Add(stub);
        }

        private static int CompareRecords(SoiGameRecord a, SoiGameRecord b)
        {
            int byTime = string.CompareOrdinal(a.EndedAtUtc ?? "", b.EndedAtUtc ?? "");
            return byTime != 0 ? byTime : string.CompareOrdinal(a.Guid ?? "", b.Guid ?? "");
        }

        private static int CompareStubs(SoiGameStub a, SoiGameStub b)
        {
            int byTime = string.CompareOrdinal(a.EndedAtUtc ?? "", b.EndedAtUtc ?? "");
            return byTime != 0 ? byTime : string.CompareOrdinal(a.Guid ?? "", b.Guid ?? "");
        }
    }
}
