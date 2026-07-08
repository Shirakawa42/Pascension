using System;
using System.Collections.Generic;
using Pascension.Engine.Events;
using Pascension.Engine.Serialization;

namespace Pascension.Net
{
    /// <summary>
    /// Host-side seat for a remote human: forwards everything GameHost pushes at it
    /// through the GameNetBridge to the owning client. While the client is disconnected
    /// nothing is sent — the reconnect resync (seat + full masked snapshot + pending
    /// input) covers the gap, and GameHost's response timer keeps the game moving.
    /// </summary>
    public sealed class RemoteSeat : IHostSeat
    {
        public int PlayerIndex { get; }

        /// <summary>Current NGO clientId — updated by ReconnectService when the player rejoins.</summary>
        public ulong ClientId { get; set; }

        public bool Connected { get; set; } = true;

        private readonly Func<GameNetBridge> _bridge;

        public RemoteSeat(int playerIndex, ulong clientId, Func<GameNetBridge> bridge)
        {
            PlayerIndex = playerIndex;
            ClientId = clientId;
            _bridge = bridge;
        }

        public void DeliverEvents(List<GameEvent> filteredEvents)
        {
            if (!Connected || filteredEvents == null || filteredEvents.Count == 0) return;
            _bridge()?.SendEvents(ClientId, filteredEvents[0].Seq, filteredEvents);
        }

        public void DeliverSnapshot(ClientSnapshot snapshot)
        {
            if (Connected)
                _bridge()?.SendSnapshot(ClientId, snapshot);
        }

        public void OnInputRequested(PendingSnap pending)
        {
            if (Connected)
                _bridge()?.SendInputRequest(ClientId, pending);
        }
    }
}
