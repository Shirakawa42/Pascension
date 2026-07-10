using System.Collections.Generic;
using Pascension.Core;
using Pascension.Engine.Actions;
using Pascension.Engine.Core;
using Pascension.Engine.Events;

namespace Pascension.Engine.Serialization
{
    /// <summary>
    /// The game-agnostic face of the Pascension engine for the host/net layer.
    /// Pure delegation — all rules stay in <see cref="GameEngine"/>.
    /// </summary>
    public sealed class PascensionEngineAdapter : IEngineAdapter
    {
        /// <summary>The wrapped engine — Pascension-aware callers (tests, bots) reach it here.</summary>
        public readonly GameEngine Inner;

        public PascensionEngineAdapter(GameConfig config) => Inner = new GameEngine(config);

        public PascensionEngineAdapter(GameEngine engine) => Inner = engine;

        public SubmitResult Submit(PlayerAction action) => Inner.Submit(action);

        public PendingSnap PendingInput
        {
            get
            {
                var pending = Inner.PendingInput;
                if (pending == null) return null;
                return new PendingSnap
                {
                    Kind = pending.Kind,
                    PlayerIndex = pending.PlayerIndex,
                    LegalActions = pending.LegalActions,
                    Decision = pending.Decision
                };
            }
        }

        public List<GameEvent> FilterEventsFor(int playerIndex, int sinceSeq) =>
            Inner.Log.FilterFor(playerIndex, sinceSeq);

        public int EventCount => Inner.Log.Count;

        public SnapshotBase BuildSnapshot(int playerIndex) => SnapshotBuilder.Build(Inner, playerIndex);

        public bool GameOver => Inner.State.GameOver;

        public int WinnerIndex => Inner.State.WinnerIndex;

        public PlayerAction DefaultActionFor(PendingSnap pending) => DefaultActions.For(pending);
    }
}
