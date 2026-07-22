using System;
using System.Collections.Generic;
using Pascension.Core;
using Pascension.Engine.Events;
using Pascension.Game.Stats;
using Pascension.Net;
using Shards.Engine;
using Shards.Stats;
using UnityEngine;

namespace Pascension.Game.Soi
{
    /// <summary>
    /// Feeds the SoI stats recorder from the RAW session stream — its own event
    /// subscriptions, independent of the screen's handlers and the PresentationQueue,
    /// so pacing/fast-forward never affects what gets recorded. On the game-over
    /// snapshot it finalizes one SoiGameRecord and hands it to SoiStatsService.
    /// Recording is fail-soft end to end: a failure here must never reach the table.
    /// </summary>
    public sealed class SoiMatchRecorderBridge : IDisposable
    {
        private readonly ISession _session;
        private readonly Func<string> _localIdentity;
        private readonly SoiGameRecorder _recorder = new SoiGameRecorder();
        private readonly float _startedRealtime;
        private bool _finalized;

        public SoiMatchRecorderBridge(ISession session, Func<string> localIdentity)
        {
            _session = session;
            _localIdentity = localIdentity;
            _startedRealtime = Time.realtimeSinceStartup;
            // GameHost delivers events BEFORE the snapshot within a tick — the
            // recorder relies on that order to detect gaps (mid-game join/resync).
            session.EventsReceived += OnEvents;
            session.SnapshotReceived += OnSnapshot;
        }

        public void Dispose()
        {
            if (_session == null) return;
            _session.EventsReceived -= OnEvents;
            _session.SnapshotReceived -= OnSnapshot;
        }

        private void OnEvents(List<GameEvent> batch)
        {
            try
            {
                _recorder.OnEvents(batch);
            }
            catch (Exception e)
            {
                Debug.LogError("SoI stats: event feed failed: " + e);
            }
        }

        private void OnSnapshot(SnapshotBase snapshotBase)
        {
            try
            {
                if (!(snapshotBase is ShardsSnapshot snap)) return;
                _recorder.OnSnapshot(snap);
                if (snap.GameOver && !_finalized)
                {
                    _finalized = true; // guarded here too: never re-enter on a throw
                    CompleteRecord(snap);
                }
            }
            catch (Exception e)
            {
                Debug.LogError("SoI stats: recording failed: " + e);
            }
        }

        private void CompleteRecord(ShardsSnapshot snap)
        {
            var ctx = new SoiRecordContext
            {
                EndedAtUtc = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"),
                AppVersion = Application.version,
                DurationSeconds = (int)(Time.realtimeSinceStartup - _startedRealtime),
                Mode = DeriveMode(snap)
            };
            for (int i = 0; i < snap.Players.Count; i++)
                ctx.Seats.Add(BuildSeat(snap.Players[i], snap.ViewerIndex));
            var record = _recorder.FinalizeRecord(ctx);
            if (record != null)
                SoiStatsService.Append(record);
        }

        /// <summary>Mode comes from the FINAL snapshot's seats, never the session type —
        /// the multiplayer host also sits on a LocalSession.</summary>
        private static string DeriveMode(ShardsSnapshot snap)
        {
            bool allOpponentsBots = true;
            for (int i = 0; i < snap.Players.Count; i++)
                if (snap.Players[i].Index != snap.ViewerIndex && !snap.Players[i].IsBot)
                    allOpponentsBots = false;
            if (allOpponentsBots) return "ai";
            return snap.Players.Count == 2 ? "mp2" : "mp3plus";
        }

        private SoiSeatIdentity BuildSeat(ShardsPlayerSnap player, int viewerIndex)
        {
            var seat = new SoiSeatIdentity
            {
                Name = player.Name,
                IsBot = player.IsBot,
                BotKind = player.BotKind
            };
            if (player.IsBot)
                // Old hosts may not stamp BotKind into the snapshot.
                seat.Identity = "bot:" + (player.BotKind ?? "unknown");
            else if (player.Index == viewerIndex)
                seat.Identity = _localIdentity != null ? _localIdentity() : "guest";
            else
                seat.Identity = player.Name?.Trim().ToLowerInvariant();
            return seat;
        }
    }
}
