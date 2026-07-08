using System;
using System.Collections.Generic;
using Pascension.Engine.Actions;
using Pascension.Engine.Events;
using Pascension.Engine.Serialization;

namespace Pascension.Net
{
    /// <summary>
    /// The client-facing surface the UI talks to. Identical for solo (LocalSession, in-process)
    /// and online (NetworkSession over NGO RPCs) — the UI never knows the difference.
    /// </summary>
    public interface ISession
    {
        int LocalPlayerIndex { get; }

        /// <summary>Full masked view (join, reconnect, and after every engine step in local play).</summary>
        event Action<ClientSnapshot> SnapshotReceived;

        /// <summary>Ordered, per-player-filtered event batches (drives animations).</summary>
        event Action<List<GameEvent>> EventsReceived;

        /// <summary>The engine is waiting on THIS player (legal actions / decision inside).</summary>
        event Action<PendingSnap> InputRequested;

        /// <summary>Submit an intent. Errors surface via ActionRejected.</summary>
        void SubmitAction(PlayerAction action);

        event Action<string> ActionRejected;
    }
}
