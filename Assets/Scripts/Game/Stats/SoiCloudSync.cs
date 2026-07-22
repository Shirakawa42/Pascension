using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Threading.Tasks;
using Pascension.Net;
using Shards.Stats;
using Unity.Services.CloudSave;
using Unity.Services.CloudSave.Models;
using UnityEngine;

namespace Pascension.Game.Stats
{
    /// <summary>
    /// Unity Cloud Save sync for the SoI match-history save. Player-scoped key layout:
    /// <c>soi_stats_meta</c> holds plain JSON <c>{"schema":1,"chunkCount":N,"recordCount":M}</c>
    /// (unsigned — the chunks carry their own MACs) and <c>soi_stats_chunk_000..NNN</c> each
    /// hold base64(gzip(utf8(SoiSaveCodec.Encode(chunkData)))) where chunk 000 carries the
    /// stubs plus the first 250 records in canonical (EndedAtUtc, Guid) order and later
    /// chunks carry 250 records each, ProfileKey stamped on every chunk.
    ///
    /// Convergence model: <see cref="SoiRecordStore.Merge"/> is a commutative union by
    /// Guid, so any interleaving of devices eventually converges; the meta key adds a
    /// best-effort compare-and-set (its Cloud Save WriteLock) so racing full syncs
    /// re-download and re-merge instead of silently overwriting each other.
    ///
    /// Cloud calls happen ONLY while <see cref="AccountService.State"/> is
    /// <see cref="AccountState.SignedIn"/> — guests and offline sessions never touch the
    /// network (their local DirtyUpload flag persists until a real sign-in). All entry
    /// points swallow + log exceptions; nothing here ever throws into a Unity callback,
    /// and no UI is referenced (the Stats footer reads <see cref="CloudState"/>).
    /// </summary>
    public static class SoiCloudSync
    {
        private const string MetaKey = "soi_stats_meta";
        private const string ChunkKeyPrefix = "soi_stats_chunk_";
        private const int ChunkSize = 250;
        /// <summary>Sanity clamp on meta.chunkCount (cap 2000 records = 8 chunks).</summary>
        private const int MaxChunks = 64;
        private const int MaxConflictRetries = 3;

        /// <summary>Last sync failure, English, for a later toast; null when healthy.</summary>
        public static string LastSyncError;

        /// <summary>Read-only state string for the Stats UI footer.</summary>
        public static string CloudState =>
            LastSyncError != null ? "error: " + LastSyncError
            : SoiStatsService.DirtyUpload ? "dirty"
            : "synced";

        /// <summary>The in-flight cloud operation (full sync or append upload) — all ops
        /// run on the Unity main thread, so this field serializes them by interleaving.</summary>
        private static Task _op;
        /// <summary>Set while _op runs to request a follow-up FULL sync once it ends
        /// (covers records appended mid-sync, whose dirty flag MarkUploaded clears).</summary>
        private static bool _pendingFullSync;
        /// <summary>Cloud Save WriteLock last seen on the meta key (null = unknown; a
        /// null lock on save skips the conflict check, which the union merge tolerates).</summary>
        private static string _metaWriteLock;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Init()
        {
            if (Application.isBatchMode) return; // headless sims / -runTests: no cloud
            // -= before += so an editor replay without domain reload never double-subscribes.
            AccountService.Changed -= OnAccountChanged;
            AccountService.Changed += OnAccountChanged;
            SoiStatsService.AppendedForUpload -= OnAppended;
            SoiStatsService.AppendedForUpload += OnAppended;
            OnAccountChanged(); // initial evaluation (also retries a leftover DirtyUpload)
        }

        // ------------------------------------------------------------------ triggers

        private static void OnAccountChanged()
        {
            // The profile may have switched — repoint the local store BEFORE any sync.
            try
            {
                SoiStatsService.ReloadForCurrentProfile();
            }
            catch (Exception e)
            {
                Debug.LogWarning("[CloudSync] stats reload failed: " + e.Message);
            }
            if (AccountService.State != AccountState.SignedIn) return;
            _metaWriteLock = null; // new session/account — any cached lock is stale
            // Always full-sync on sign-in: downloads remote games AND flushes a local
            // DirtyUpload left over from playing offline (the spec's retry-at-start path).
            StartOp(SyncAsync);
        }

        private static void OnAppended(SoiSaveData data)
        {
            // Not signed in: DirtyUpload is already set locally; nothing to do here.
            if (AccountService.State != AccountState.SignedIn) return;
            StartOp(QuickUploadAsync);
        }

        /// <summary>Start an op, or — if one is in flight — queue a full sync behind it.</summary>
        private static void StartOp(Func<Task> op)
        {
            if (_op != null && !_op.IsCompleted)
            {
                _pendingFullSync = true;
                return;
            }
            _op = GuardedAsync(op);
        }

        private static async Task GuardedAsync(Func<Task> op)
        {
            try
            {
                await op();
            }
            catch (Exception e)
            {
                Fail(e); // ops catch internally — this is belt-and-braces
            }
            while (_pendingFullSync)
            {
                _pendingFullSync = false;
                try
                {
                    await SyncAsync();
                }
                catch (Exception e)
                {
                    Fail(e);
                }
            }
        }

        // ------------------------------------------------------------------ full sync

        /// <summary>Download + merge + upload, retrying the meta compare-and-set.</summary>
        private static async Task SyncAsync()
        {
            for (int attempt = 1; attempt <= MaxConflictRetries; attempt++)
            {
                try
                {
                    await SyncOnceAsync();
                    LastSyncError = null;
                    return;
                }
                catch (CloudSaveConflictException e)
                {
                    // Another device won the meta write lock — re-download, re-merge, retry.
                    Debug.LogWarning("[CloudSync] meta write conflict (attempt " + attempt +
                                     "/" + MaxConflictRetries + "): " + e.Message);
                    _metaWriteLock = null;
                }
                catch (Exception e)
                {
                    Fail(e);
                    return;
                }
            }
            // Converges anyway at the next trigger — the union merge makes retries safe.
            Fail(new Exception("cloud save stayed contended after " + MaxConflictRetries + " attempts"));
        }

        private static async Task SyncOnceAsync()
        {
            if (AccountService.State != AccountState.SignedIn) return; // may change mid-flight
            string profileKey = SoiStatsService.ProfileKey;
            var downloaded = new Dictionary<string, string>(); // key -> stored string value

            // 1. Meta (missing = first ever sync: remote empty, upload everything).
            int remoteChunkCount = 0;
            var metaItems = await CloudSaveService.Instance.Data.Player.LoadAsync(
                new HashSet<string> { MetaKey });
            Item metaItem = null;
            if (metaItems != null) metaItems.TryGetValue(MetaKey, out metaItem);
            if (metaItem != null)
            {
                _metaWriteLock = metaItem.WriteLock;
                string metaJson = SafeGetString(metaItem);
                if (metaJson != null) downloaded[MetaKey] = metaJson;
                var meta = ParseMeta(metaJson);
                if (meta != null)
                {
                    if (meta.Schema > 1)
                    {
                        // A newer build owns the cloud copy — never clobber it.
                        Debug.LogWarning("[CloudSync] cloud schema " + meta.Schema +
                                         " is newer than this build — sync skipped.");
                        return;
                    }
                    remoteChunkCount = Math.Clamp(meta.ChunkCount, 0, MaxChunks);
                }
            }
            else
            {
                _metaWriteLock = null; // nothing to compare-and-set against yet
            }

            // 2. Chunks: verify-fail skips that chunk only, the rest still merges.
            var remote = new SoiSaveData
            {
                ProfileKey = profileKey,
                Records = new List<SoiGameRecord>(),
                Stubs = new List<SoiGameStub>()
            };
            if (remoteChunkCount > 0)
            {
                var keys = new HashSet<string>();
                for (int i = 0; i < remoteChunkCount; i++) keys.Add(ChunkKey(i));
                var items = await CloudSaveService.Instance.Data.Player.LoadAsync(keys);
                for (int i = 0; i < remoteChunkCount; i++)
                {
                    string key = ChunkKey(i);
                    Item item = null;
                    if (items != null) items.TryGetValue(key, out item);
                    if (item == null) continue;
                    string encoded = SafeGetString(item);
                    if (encoded == null) continue;
                    downloaded[key] = encoded;
                    if (!TryDecodeChunk(encoded, profileKey, out var part))
                    {
                        Debug.LogWarning("[CloudSync] " + key + " failed verification — skipped.");
                        continue;
                    }
                    if (part.Records != null) remote.Records.AddRange(part.Records);
                    if (i == 0 && part.Stubs != null) remote.Stubs.AddRange(part.Stubs);
                }
            }

            // 3. Merge into the local store (commutative union by Guid, then re-cap).
            var merged = SoiRecordStore.Merge(SoiStatsService.Data, remote);
            SoiStatsService.ImportMerged(merged);

            // 4. Upload only what differs from what the cloud already holds, in ONE call.
            //    (Re-read Data: it is the merged instance now, plus any mid-sync appends.)
            var chunks = BuildChunks(SoiStatsService.Data, profileKey, out string newMetaJson);
            var upload = new Dictionary<string, SaveItem>();
            for (int i = 0; i < chunks.Count; i++)
            {
                string key = ChunkKey(i);
                if (!downloaded.TryGetValue(key, out string had) || had != chunks[i])
                    upload[key] = new SaveItem(chunks[i], null); // chunks: merge makes conflicts benign
            }
            bool metaChanged = !downloaded.TryGetValue(MetaKey, out string hadMeta)
                               || hadMeta != newMetaJson;
            if (upload.Count > 0 || metaChanged)
            {
                // Meta is the compare-and-set anchor: if its WriteLock moved since the
                // download above, SaveAsync throws CloudSaveConflictException → retry.
                upload[MetaKey] = new SaveItem(newMetaJson, _metaWriteLock);
                var locks = await CloudSaveService.Instance.Data.Player.SaveAsync(upload);
                if (locks != null && locks.TryGetValue(MetaKey, out string newLock))
                    _metaWriteLock = newLock;
            }
            SoiStatsService.MarkUploaded();
        }

        // ------------------------------------------------------------------ append upload

        /// <summary>After a game ends: push only the open (last) chunk + meta. Stale
        /// middle chunks (cap eviction shifting boundaries) are healed by the next full
        /// sync — the union merge tolerates them in the meantime.</summary>
        private static async Task QuickUploadAsync()
        {
            try
            {
                if (AccountService.State != AccountState.SignedIn) return;
                string profileKey = SoiStatsService.ProfileKey;
                var chunks = BuildChunks(SoiStatsService.Data, profileKey, out string metaJson);
                var upload = new Dictionary<string, SaveItem>
                {
                    [ChunkKey(chunks.Count - 1)] = new SaveItem(chunks[chunks.Count - 1], null),
                    [MetaKey] = new SaveItem(metaJson, _metaWriteLock)
                };
                var locks = await CloudSaveService.Instance.Data.Player.SaveAsync(upload);
                if (locks != null && locks.TryGetValue(MetaKey, out string newLock))
                    _metaWriteLock = newLock;
                SoiStatsService.MarkUploaded();
                LastSyncError = null;
            }
            catch (CloudSaveConflictException e)
            {
                // Another device moved the meta — reconcile via a full download+merge.
                Debug.LogWarning("[CloudSync] append upload hit a write conflict — full sync queued: "
                                 + e.Message);
                _metaWriteLock = null;
                _pendingFullSync = true; // GuardedAsync runs SyncAsync right after this op
            }
            catch (Exception e)
            {
                // DirtyUpload stays set — retried at the next game end / sign-in / app start.
                Fail(e);
            }
        }

        // ------------------------------------------------------------------ chunk codec

        private static string ChunkKey(int index) => ChunkKeyPrefix + index.ToString("000");

        /// <summary>Slice data into encoded cloud chunks (index = chunk number) in the
        /// canonical (EndedAtUtc, Guid) ordinal order; chunk 000 also carries the stubs.
        /// Deterministic for identical data, so string equality detects real changes.</summary>
        private static List<string> BuildChunks(SoiSaveData data, string profileKey,
            out string metaJson)
        {
            var records = data?.Records != null
                ? new List<SoiGameRecord>(data.Records)
                : new List<SoiGameRecord>();
            records.Sort(CompareRecords); // sort the copy — never reorder the live list

            int chunkCount = 1 + Math.Max(0, records.Count - 1) / ChunkSize;
            var chunks = new List<string>(chunkCount);
            for (int i = 0; i < chunkCount; i++)
            {
                int start = i * ChunkSize;
                int length = Math.Min(ChunkSize, records.Count - start);
                if (length < 0) length = 0;
                var slice = new SoiSaveData
                {
                    Schema = 1,
                    ProfileKey = profileKey,
                    DirtyUpload = false,
                    Records = records.GetRange(start, length),
                    Stubs = i == 0 && data?.Stubs != null
                        ? new List<SoiGameStub>(data.Stubs)
                        : new List<SoiGameStub>()
                };
                chunks.Add(EncodeChunk(slice));
            }
            // Hand-built so the JSON is byte-stable across runs (equality = unchanged).
            metaJson = "{\"schema\":1,\"chunkCount\":" + chunkCount +
                       ",\"recordCount\":" + records.Count + "}";
            return chunks;
        }

        private static int CompareRecords(SoiGameRecord a, SoiGameRecord b)
        {
            int byTime = string.CompareOrdinal(a.EndedAtUtc ?? "", b.EndedAtUtc ?? "");
            return byTime != 0 ? byTime : string.CompareOrdinal(a.Guid ?? "", b.Guid ?? "");
        }

        private static string EncodeChunk(SoiSaveData chunk) =>
            Convert.ToBase64String(Gzip(Encoding.UTF8.GetBytes(
                SoiSaveCodec.Encode(chunk, SoiStatsSecret.Key))));

        private static bool TryDecodeChunk(string encoded, string profileKey,
            out SoiSaveData part)
        {
            part = null;
            try
            {
                string text = Encoding.UTF8.GetString(Gunzip(Convert.FromBase64String(encoded)));
                return SoiSaveCodec.TryDecode(text, SoiStatsSecret.Key, profileKey, out part);
            }
            catch (Exception)
            {
                return false; // bad base64/gzip counts as a verify failure — chunk skipped
            }
        }

        private static byte[] Gzip(byte[] raw)
        {
            using var buffer = new MemoryStream();
            using (var gz = new GZipStream(buffer, System.IO.Compression.CompressionLevel.Optimal, leaveOpen: true))
                gz.Write(raw, 0, raw.Length);
            return buffer.ToArray();
        }

        private static byte[] Gunzip(byte[] packed)
        {
            using var input = new MemoryStream(packed);
            using var gz = new GZipStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream();
            gz.CopyTo(output);
            return output.ToArray();
        }

        // ------------------------------------------------------------------ helpers

        private static string SafeGetString(Item item)
        {
            try
            {
                return item.Value != null ? item.Value.GetAs<string>() : null;
            }
            catch (Exception e)
            {
                Debug.LogWarning("[CloudSync] could not read " + item.Key + " as string: "
                                 + e.Message);
                return null;
            }
        }

        private static SyncMeta ParseMeta(string json)
        {
            if (string.IsNullOrEmpty(json)) return null;
            try
            {
                return SoiStatsJson.Deserialize<SyncMeta>(json);
            }
            catch (Exception e)
            {
                Debug.LogWarning("[CloudSync] bad meta JSON — treating remote as empty: "
                                 + e.Message);
                return null;
            }
        }

        private static void Fail(Exception e)
        {
            LastSyncError = e.Message;
            Debug.LogWarning("[CloudSync] sync failed: " + e);
        }

        /// <summary>Shape of the plain-JSON soi_stats_meta value.</summary>
        private sealed class SyncMeta
        {
            public int Schema = 1;
            public int ChunkCount;
            public int RecordCount;
        }
    }
}
