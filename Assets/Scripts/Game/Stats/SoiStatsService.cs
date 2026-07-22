using System;
using System.Collections.Generic;
using System.IO;
using Pascension.Net;
using Shards.Stats;
using UnityEngine;

namespace Pascension.Game.Stats
{
    /// <summary>
    /// Local per-profile persistence for SoI match records: one HMAC-sealed JSON file
    /// per profile under persistentDataPath/soi/, loaded lazily on first access.
    /// Main-thread only. Cloud sync stays decoupled: it consumes Data/DirtyUpload/
    /// AppendedForUpload and pushes back through ImportMerged/MarkUploaded — this
    /// class never references UI or network code.
    /// </summary>
    public static class SoiStatsService
    {
        private static SoiSaveData _data;
        private static string _profileId;

        /// <summary>Fires after the record set changed (append, reload, merge import).</summary>
        public static event Action RecordsChanged;

        /// <summary>Fires after a record was appended AND saved locally — the future
        /// SoiCloudSync subscribes here; nothing in this class uploads.</summary>
        public static event Action<SoiSaveData> AppendedForUpload;

        /// <summary>Set when a load found a tampered/corrupt file (quarantined aside);
        /// a UI toast can consume it later. Never read by this class.</summary>
        public static string LastLoadWarning;

        public static SoiSaveData Data
        {
            get { EnsureLoaded(); return _data; }
        }

        public static IReadOnlyList<SoiGameRecord> Records => Data.Records;

        public static IReadOnlyList<SoiGameStub> Stubs => Data.Stubs;

        public static string ProfileKey
        {
            get { EnsureLoaded(); return _profileId; }
        }

        /// <summary>Local changes not yet pushed to the cloud copy.</summary>
        public static bool DirtyUpload => Data.DirtyUpload;

        public static void Append(SoiGameRecord record)
        {
            EnsureLoaded();
            if (!SoiRecordStore.Append(_data, record)) return;
            _data.DirtyUpload = true;
            Save();
            RecordsChanged?.Invoke();
            AppendedForUpload?.Invoke(_data);
        }

        /// <summary>Re-resolves the profile id and reloads its file. Account
        /// integration calls this on AccountService.Changed.</summary>
        public static void ReloadForCurrentProfile()
        {
            _data = null;
            _profileId = null;
            EnsureLoaded();
            RecordsChanged?.Invoke();
        }

        /// <summary>Cloud sync confirmed the current blob is uploaded.</summary>
        public static void MarkUploaded()
        {
            EnsureLoaded();
            if (!_data.DirtyUpload) return;
            _data.DirtyUpload = false;
            Save();
        }

        /// <summary>Replace the local blob with a cloud-merged result (see
        /// SoiRecordStore.Merge) and persist it.</summary>
        public static void ImportMerged(SoiSaveData remoteMerged)
        {
            if (remoteMerged == null) return;
            EnsureLoaded();
            _data = remoteMerged;
            // Next load verifies ProfileKey — a merge must never re-key the file.
            _data.ProfileKey = _profileId;
            Save();
            RecordsChanged?.Invoke();
        }

        // ------------------------------------------------------------------ internals

        private static void EnsureLoaded()
        {
            if (_data != null) return;
            _profileId = ResolveProfileId();
            _data = LoadFrom(PathFor(_profileId), _profileId);
        }

        private static string ResolveProfileId() =>
            AccountService.LocalProfileId ?? "guest";

        private static string PathFor(string profileId) =>
            Path.Combine(Application.persistentDataPath, "soi", "stats_" + profileId + ".json");

        private static SoiSaveData LoadFrom(string path, string profileId)
        {
            if (File.Exists(path))
            {
                string text = null;
                try
                {
                    text = File.ReadAllText(path);
                }
                catch (Exception e)
                {
                    Debug.LogWarning("SoI stats: could not read " + path + ": " + e.Message);
                }
                if (text != null)
                {
                    if (SoiSaveCodec.TryDecode(text, SoiStatsSecret.Key, profileId, out var data))
                        return data;
                    Quarantine(path);
                }
            }
            return new SoiSaveData
            {
                ProfileKey = profileId,
                Records = new List<SoiGameRecord>(),
                Stubs = new List<SoiGameStub>()
            };
        }

        /// <summary>A file that fails the MAC/profile check is moved aside, never
        /// deleted — the games inside may still be hand-recoverable.</summary>
        private static void Quarantine(string path)
        {
            LastLoadWarning = "Match history file failed verification and was set aside.";
            Debug.LogWarning("SoI stats: " + path + " failed verification — starting fresh.");
            try
            {
                string aside = path + ".invalid-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss");
                if (File.Exists(aside)) File.Delete(aside);
                File.Move(path, aside);
            }
            catch (Exception e)
            {
                Debug.LogWarning("SoI stats: could not quarantine " + path + ": " + e.Message);
            }
        }

        private static void Save()
        {
            string path = PathFor(_profileId);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string text = SoiSaveCodec.Encode(_data, SoiStatsSecret.Key);
            // Atomic-enough swap: never leave a half-written file at the real path.
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, text);
            if (File.Exists(path)) File.Delete(path);
            File.Move(tmp, path);
        }
    }
}
