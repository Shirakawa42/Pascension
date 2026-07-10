using System.Collections.Generic;
using Pascension.Engine.Actions;
using Pascension.Engine.Core;
using Pascension.Engine.Events;
using Pascension.Engine.Serialization;

namespace Pascension.Core
{
    /// <summary>
    /// Every game's snapshot derives from this — the host/net/session layers only need
    /// the viewer identity and the event-log watermark; everything else is game-shaped
    /// and consumed by that game's UI after a single downcast.
    /// </summary>
    public abstract class SnapshotBase
    {
        public int ViewerIndex;
        /// <summary>Event-log length when the snapshot was taken (clients resync gaps from here).</summary>
        public int EventSeq;
    }

    /// <summary>
    /// The game-agnostic surface a rules engine exposes to the host/net layer.
    /// One implementation per game wraps that game's engine.
    /// </summary>
    public interface IEngineAdapter
    {
        SubmitResult Submit(PlayerAction action);
        /// <summary>Null when the game is over.</summary>
        PendingSnap PendingInput { get; }
        List<GameEvent> FilterEventsFor(int playerIndex, int sinceSeq);
        int EventCount { get; }
        SnapshotBase BuildSnapshot(int playerIndex);
        bool GameOver { get; }
        int WinnerIndex { get; }
        /// <summary>The always-safe action for a seat (timeouts/disconnect defaults).</summary>
        PlayerAction DefaultActionFor(PendingSnap pending);
    }

    /// <summary>
    /// Game-agnostic bot contract: choose from the pending surface only. Snapshot is
    /// the bot's masked view (downcast per game for smarter play).
    /// </summary>
    public interface IBotAgent
    {
        PlayerAction Choose(PendingSnap pending, SnapshotBase view);
    }

    /// <summary>Display-ready card face for UI layers that must not reference a game's
    /// content assembly directly.</summary>
    public sealed class CardFace
    {
        public string Id;
        public string Name;
        public string CostText;
        public string TypeLine;
        public string RulesText;
        public string ArtId;
    }
}
