using System;
using System.Collections.Generic;
using Pascension.Core;
using Pascension.Engine.Events;
using Pascension.Engine.Serialization;
using Unity.Netcode;

namespace Pascension.Net
{
    /// <summary>
    /// The single host↔client pipe for a running match, spawned by the host from the
    /// Resources prefab after the Game scene loads. Host side is configured by
    /// HostMatchStarter (GameHost access + clientId→seat mapping + resync handler);
    /// client side binds a NetworkSession. All payloads are UTF8 JSON encoded by the
    /// ACTIVE GAME's <see cref="IGameCodec"/> — the bridge itself is game-agnostic.
    /// Host→client traffic uses targeted RPCs (SendTo.SpecifiedInParams +
    /// RpcTarget.Single) so hidden information only ever reaches its seat.
    /// </summary>
    public sealed class GameNetBridge : NetworkBehaviour
    {
        public static GameNetBridge Instance { get; private set; }

        /// <summary>Raised on pure clients when the bridge spawns (the host uses LocalSession).</summary>
        public static event Action<GameNetBridge> ClientSpawned;

        private GameHost _host;
        private IGameCodec _codec;
        private Func<ulong, int> _seatOfClient;
        private Action<ulong> _resyncRequested;
        private NetworkSession _session;

        /// <summary>Host-side wiring. Call BEFORE NetworkObject.Spawn().</summary>
        public void ConfigureHost(GameHost host, IGameCodec codec, Func<ulong, int> seatOfClient,
            Action<ulong> resyncRequested)
        {
            _host = host;
            _codec = codec;
            _seatOfClient = seatOfClient;
            _resyncRequested = resyncRequested;
        }

        public void BindSession(NetworkSession session) => _session = session;

        public void UnbindSession(NetworkSession session)
        {
            if (_session == session)
                _session = null;
        }

        public override void OnNetworkSpawn()
        {
            Instance = this;
            if (!IsServer)
                ClientSpawned?.Invoke(this);
        }

        public override void OnNetworkDespawn()
        {
            if (Instance == this)
                Instance = null;
        }

        // ---------------- client → host ----------------

        [Rpc(SendTo.Server)]
        public void SubmitActionRpc(byte[] actionJson, RpcParams rpcParams = default)
        {
            ulong sender = rpcParams.Receive.SenderClientId;
            int seat = _seatOfClient?.Invoke(sender) ?? -1;
            if (_host == null || _codec == null || seat < 0)
            {
                SendRejection(sender, "You have no seat in this game");
                return;
            }

            Engine.Actions.PlayerAction action;
            try
            {
                action = _codec.DecodeAction(actionJson);
            }
            catch (Exception e)
            {
                SendRejection(sender, "Malformed action: " + e.Message);
                return;
            }

            // GameHost stamps action.PlayerIndex from the seat — clients cannot spoof it.
            _host.Submit(seat, action);
        }

        /// <summary>
        /// Client asks for seat index + full masked snapshot + pending input. Sent on
        /// bridge spawn (join AND reconnect) and whenever an event-sequence gap is seen.
        /// </summary>
        [Rpc(SendTo.Server)]
        public void RequestResyncRpc(RpcParams rpcParams = default) =>
            _resyncRequested?.Invoke(rpcParams.Receive.SenderClientId);

        // ---------------- host → one client (send helpers) ----------------

        public void SendSeat(ulong clientId, int playerIndex)
        {
            if (CanSendTo(clientId))
                SeatAssignedRpc(playerIndex, RpcTarget.Single(clientId, RpcTargetUse.Temp));
        }

        public void SendEvents(ulong clientId, int seqStart, List<GameEvent> events)
        {
            if (CanSendTo(clientId))
                EventBatchRpc(_codec.EncodeEvents(events), seqStart,
                    RpcTarget.Single(clientId, RpcTargetUse.Temp));
        }

        public void SendSnapshot(ulong clientId, SnapshotBase snapshot)
        {
            if (CanSendTo(clientId))
                SnapshotRpc(_codec.EncodeSnapshot(snapshot),
                    RpcTarget.Single(clientId, RpcTargetUse.Temp));
        }

        public void SendInputRequest(ulong clientId, PendingSnap pending)
        {
            if (CanSendTo(clientId))
                InputRequestedRpc(_codec.EncodePending(pending),
                    RpcTarget.Single(clientId, RpcTargetUse.Temp));
        }

        public void SendRejection(ulong clientId, string error)
        {
            if (CanSendTo(clientId))
                ActionRejectedRpc(error, RpcTarget.Single(clientId, RpcTargetUse.Temp));
        }

        public void SendRules(ulong clientId, object rules)
        {
            if (CanSendTo(clientId))
                RulesRpc(_codec.EncodeRules(rules), RpcTarget.Single(clientId, RpcTargetUse.Temp));
        }

        /// <summary>Pause state is not hidden information — everyone gets the same copy.</summary>
        public void BroadcastPause(PauseInfo info)
        {
            if (IsServer && IsSpawned)
                PauseStateRpc(NetWire.Encode(info));
        }

        /// <summary>Targeted pause state for a resyncing client (others may still be out).</summary>
        public void SendPauseState(ulong clientId, PauseInfo info)
        {
            if (CanSendTo(clientId))
                PauseStateTargetedRpc(NetWire.Encode(info),
                    RpcTarget.Single(clientId, RpcTargetUse.Temp));
        }

        private bool CanSendTo(ulong clientId)
        {
            if (!IsServer || !IsSpawned) return false;
            if (clientId == Unity.Netcode.NetworkManager.ServerClientId) return false; // host human = LocalSession
            var ids = NetworkManager.ConnectedClientsIds;
            for (int i = 0; i < ids.Count; i++)
                if (ids[i] == clientId)
                    return true;
            return false;
        }

        // ---------------- client receive ----------------

        [Rpc(SendTo.SpecifiedInParams)]
        private void SeatAssignedRpc(int playerIndex, RpcParams rpcParams = default) =>
            _session?.HandleSeatAssigned(playerIndex);

        [Rpc(SendTo.SpecifiedInParams)]
        private void EventBatchRpc(byte[] eventsJson, int seqStart, RpcParams rpcParams = default) =>
            _session?.HandleEventBatch(eventsJson, seqStart);

        [Rpc(SendTo.SpecifiedInParams)]
        private void SnapshotRpc(byte[] snapshotJson, RpcParams rpcParams = default) =>
            _session?.HandleSnapshot(snapshotJson);

        [Rpc(SendTo.SpecifiedInParams)]
        private void InputRequestedRpc(byte[] pendingJson, RpcParams rpcParams = default) =>
            _session?.HandleInputRequested(pendingJson);

        [Rpc(SendTo.SpecifiedInParams)]
        private void ActionRejectedRpc(string error, RpcParams rpcParams = default) =>
            _session?.HandleRejected(error);

        [Rpc(SendTo.SpecifiedInParams)]
        private void RulesRpc(byte[] rulesJson, RpcParams rpcParams = default) =>
            _session?.HandleRules(rulesJson);

        [Rpc(SendTo.NotServer)]
        private void PauseStateRpc(byte[] pauseJson) =>
            _session?.HandlePauseChanged(pauseJson);

        [Rpc(SendTo.SpecifiedInParams)]
        private void PauseStateTargetedRpc(byte[] pauseJson, RpcParams rpcParams = default) =>
            _session?.HandlePauseChanged(pauseJson);
    }
}
