using System;
using System.Collections.Generic;
using Pascension.Core;
using Pascension.Engine.Actions;
using Pascension.Engine.Events;
using Pascension.Engine.Serialization;

namespace Pascension.Net
{
    /// <summary>
    /// Client-side ISession: everything arrives through GameNetBridge RPCs. Tracks event
    /// sequence numbers; on any gap (or before the first snapshot baseline) it requests a
    /// full resync — the host answers with seat index + masked snapshot + pending input.
    /// The UI consumes this exactly like a LocalSession.
    /// </summary>
    public sealed class NetworkSession : ISession
    {
        public int LocalPlayerIndex { get; private set; } = -1;

        public event Action<SnapshotBase> SnapshotReceived;
        public event Action<List<GameEvent>> EventsReceived;
        public event Action<PendingSnap> InputRequested;
        public event Action<string> ActionRejected;
        public event Action<PauseInfo> PauseChanged;

        /// <summary>Latest pause state — lets a UI that binds after the RPC catch up.</summary>
        public PauseInfo CurrentPause { get; private set; }

        /// <summary>The host's game rules, populated in place during resync (the UI holds
        /// this reference from bind time, so in-place mutation keeps it correct).</summary>
        public object Rules { get; }

        private readonly IGameCodec _codec;
        private GameNetBridge _bridge;

        /// <summary>Next event Seq we expect; -1 until the first snapshot baseline arrives.</summary>
        private int _expectedSeq = -1;

        private bool _resyncPending;

        public NetworkSession(IGameCodec codec)
        {
            _codec = codec;
            Rules = codec.CreateRules();
            GameNetBridge.ClientSpawned += OnBridgeSpawned;
            if (GameNetBridge.Instance != null)
                OnBridgeSpawned(GameNetBridge.Instance);
        }

        /// <summary>Unhook from the static bridge event; call when discarding the session.</summary>
        public void Detach()
        {
            GameNetBridge.ClientSpawned -= OnBridgeSpawned;
            if (_bridge != null)
                _bridge.UnbindSession(this);
            _bridge = null;
        }

        private void OnBridgeSpawned(GameNetBridge bridge)
        {
            _bridge = bridge;
            _resyncPending = false;
            bridge.BindSession(this);
            RequestResync();
        }

        public void SubmitAction(PlayerAction action)
        {
            if (_bridge == null)
            {
                ActionRejected?.Invoke("Not connected to the host");
                return;
            }
            if (LocalPlayerIndex >= 0)
                action.PlayerIndex = LocalPlayerIndex; // cosmetic — the host re-stamps it from the seat
            _bridge.SubmitActionRpc(_codec.EncodeAction(action));
        }

        private void RequestResync()
        {
            if (_bridge == null || _resyncPending) return;
            _resyncPending = true;
            _bridge.RequestResyncRpc();
        }

        // ---------------- bridge callbacks ----------------

        internal void HandleSeatAssigned(int playerIndex) => LocalPlayerIndex = playerIndex;

        internal void HandleSnapshot(byte[] snapshotJson)
        {
            var snapshot = _codec.DecodeSnapshot(snapshotJson);
            if (LocalPlayerIndex < 0)
                LocalPlayerIndex = snapshot.ViewerIndex; // redundant with SeatAssignedRpc, but harmless
            _expectedSeq = snapshot.EventSeq;
            _resyncPending = false;
            SnapshotReceived?.Invoke(snapshot);
        }

        internal void HandleEventBatch(byte[] eventsJson, int seqStart)
        {
            if (_expectedSeq < 0)
            {
                RequestResync(); // no baseline yet — a snapshot is on its way (or being requested)
                return;
            }
            if (seqStart > _expectedSeq)
            {
                RequestResync(); // gap — resync from a fresh snapshot
                return;
            }

            var events = _codec.DecodeEvents(eventsJson);
            if (events.Count == 0) return;
            int lastSeq = events[events.Count - 1].Seq;
            if (lastSeq < _expectedSeq) return; // stale duplicate, already covered by a snapshot

            var fresh = new List<GameEvent>();
            foreach (var e in events)
                if (e.Seq >= _expectedSeq)
                    fresh.Add(e);
            _expectedSeq = lastSeq + 1;
            EventsReceived?.Invoke(fresh);
        }

        internal void HandleInputRequested(byte[] pendingJson)
        {
            var pending = _codec.DecodePending(pendingJson);
            InputRequested?.Invoke(pending);
        }

        internal void HandleRejected(string error) => ActionRejected?.Invoke(error);

        internal void HandleRules(byte[] rulesJson)
        {
            _codec.PopulateRules(rulesJson, Rules);
        }

        internal void HandlePauseChanged(byte[] pauseJson)
        {
            var info = NetWire.Decode<PauseInfo>(pauseJson);
            if (info == null) return;
            info.CanKick = false; // only the host may kick, whatever the wire says
            if (string.IsNullOrEmpty(info.JoinCode))
                info.JoinCode = NetLauncher.LastJoinCode;
            CurrentPause = info;
            PauseChanged?.Invoke(info);
        }
    }
}
