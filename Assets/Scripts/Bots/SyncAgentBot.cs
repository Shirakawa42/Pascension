using Pascension.Core;
using Pascension.Engine.Actions;
using Pascension.Engine.Core;
using Pascension.Engine.Serialization;

namespace Pascension.Bots
{
    /// <summary>
    /// Adapts Pascension's engine-aware <see cref="ISyncAgent"/> bots (Heuristic/Random)
    /// to the game-agnostic <see cref="IBotAgent"/> seat contract. Host-side only —
    /// the wrapped agent white-box reads the in-process engine, exactly as before.
    /// </summary>
    public sealed class SyncAgentBot : IBotAgent
    {
        private readonly ISyncAgent _inner;
        private readonly GameEngine _engine;

        public SyncAgentBot(ISyncAgent inner, GameEngine engine)
        {
            _inner = inner;
            _engine = engine;
        }

        public PlayerAction Choose(PendingSnap pending, SnapshotBase view)
        {
            if (pending.Kind == PendingInputKind.Decision)
                return new SubmitDecisionAction
                {
                    PlayerIndex = pending.PlayerIndex,
                    Answer = _inner.ChooseDecision(_engine, pending.Decision)
                };

            return _inner.ChooseAction(_engine, new PendingInput
            {
                Kind = pending.Kind,
                PlayerIndex = pending.PlayerIndex,
                LegalActions = pending.LegalActions
            });
        }
    }
}
