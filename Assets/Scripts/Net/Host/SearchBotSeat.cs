using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Pascension.Core;
using Pascension.Engine.Events;
using Pascension.Engine.Serialization;

namespace Pascension.Net
{
    /// <summary>
    /// A seat for slow synchronous bots (the SoI ISMCTS seat, ~1s per decision): the
    /// agent's Choose runs on a worker task so the frame never blocks; the answer is
    /// queued thread-safely via GameHost.SubmitAsync and applied on the next host Tick
    /// (which re-checks the pending player, covering seat-replacement races).
    ///
    /// Threading contract: while this seat holds the pending input, nothing advances
    /// the engine (single-pending-input discipline; SoI has no response timers), so the
    /// worker's engine READS (Fork's DeepCopy) never race a mutation. Main-thread
    /// snapshot builds are concurrent reads and safe.
    /// </summary>
    public sealed class SearchBotSeat : IHostSeat
    {
        public int PlayerIndex { get; }

        private readonly IBotAgent _agent;
        private readonly GameHost _host;
        private int _requestToken;

        public SearchBotSeat(int playerIndex, IBotAgent agent, GameHost host)
        {
            PlayerIndex = playerIndex;
            _agent = agent;
            _host = host;
        }

        public void DeliverEvents(List<GameEvent> filteredEvents) { }
        public void DeliverSnapshot(SnapshotBase snapshot) { }

        public void OnInputRequested(PendingSnap pending)
        {
            int token = Interlocked.Increment(ref _requestToken);
            Task.Run(() =>
            {
                var action = _agent.Choose(pending, null)
                             ?? _host.Engine.DefaultActionFor(pending);
                if (token == Volatile.Read(ref _requestToken))
                    _host.SubmitAsync(PlayerIndex, action);
            });
        }
    }
}
