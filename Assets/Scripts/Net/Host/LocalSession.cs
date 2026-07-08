using System;
using System.Collections.Generic;
using Pascension.Engine.Actions;
using Pascension.Engine.Events;
using Pascension.Engine.Serialization;

namespace Pascension.Net
{
    /// <summary>
    /// In-process session for the host's own human player (and all of solo play —
    /// NGO is never started in solo). Implements both ends: the seat the host sees
    /// and the session the UI consumes.
    /// </summary>
    public sealed class LocalSession : ISession, IHostSeat
    {
        private readonly GameHost _host;

        public LocalSession(GameHost host, int playerIndex)
        {
            _host = host;
            LocalPlayerIndex = playerIndex;
            PlayerIndex = playerIndex;
            _host.SeatActionRejected += (player, error) =>
            {
                if (player == LocalPlayerIndex)
                    ActionRejected?.Invoke(error);
            };
        }

        // ---- ISession (UI side) ----
        public int LocalPlayerIndex { get; }
        public event Action<ClientSnapshot> SnapshotReceived;
        public event Action<List<GameEvent>> EventsReceived;
        public event Action<PendingSnap> InputRequested;
        public event Action<string> ActionRejected;

        public void SubmitAction(PlayerAction action) => _host.Submit(LocalPlayerIndex, action);

        // ---- IHostSeat (host side) ----
        public int PlayerIndex { get; }
        public void DeliverEvents(List<GameEvent> filteredEvents) => EventsReceived?.Invoke(filteredEvents);
        public void DeliverSnapshot(ClientSnapshot snapshot) => SnapshotReceived?.Invoke(snapshot);
        public void OnInputRequested(PendingSnap pending) => InputRequested?.Invoke(pending);
    }
}
