using System.Collections.Generic;
using Pascension.Bots.Ollama;
using Pascension.Engine.Events;
using Pascension.Engine.Serialization;

namespace Pascension.Net
{
    /// <summary>
    /// A seat driven by an asynchronous agent (the Ollama LLM bot). When the engine
    /// waits on this seat, the agent receives a fresh masked snapshot plus the pending
    /// input; its answer is queued thread-safely via GameHost.SubmitAsync and applied
    /// on the next host Tick. Events/snapshots pushes are ignored — async agents always
    /// work from the snapshot taken at request time.
    /// </summary>
    public sealed class AsyncBotSeat : IHostSeat
    {
        public int PlayerIndex { get; }

        private readonly IAsyncAgent _agent;
        private readonly GameHost _host;

        public AsyncBotSeat(int playerIndex, IAsyncAgent agent, GameHost host)
        {
            PlayerIndex = playerIndex;
            _agent = agent;
            _host = host;
        }

        public void DeliverEvents(List<GameEvent> filteredEvents) { }
        public void DeliverSnapshot(ClientSnapshot snapshot) { }

        public void OnInputRequested(PendingSnap pending) =>
            _agent.RequestInput(_host.SnapshotFor(PlayerIndex), pending,
                action => _host.SubmitAsync(PlayerIndex, action));
    }
}
