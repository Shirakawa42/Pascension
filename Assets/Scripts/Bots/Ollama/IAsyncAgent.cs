using System;
using Pascension.Engine.Actions;
using Pascension.Engine.Serialization;

namespace Pascension.Bots.Ollama
{
    /// <summary>
    /// A decision-maker that answers asynchronously (LLM, remote service). It only ever
    /// sees the masked snapshot for its own seat — never the engine. The submit callback
    /// may be invoked from ANY thread; callers must route it through a thread-safe
    /// entry point (GameHost.SubmitAsync).
    /// </summary>
    public interface IAsyncAgent
    {
        /// <summary>Start working on the pending input; call submit exactly once when decided
        /// (unless cancelled first).</summary>
        void RequestInput(ClientSnapshot view, PendingSnap pending, Action<PlayerAction> submit);

        /// <summary>Abandon any in-flight request; its submit callback must no longer fire.</summary>
        void Cancel();
    }
}
