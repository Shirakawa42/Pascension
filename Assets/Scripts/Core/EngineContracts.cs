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

    /// <summary>A seat requested at game creation, game-agnostically.</summary>
    public sealed class PlayerSpec
    {
        public string Name;
        public string CharacterId;
        public bool IsBot;
        public string BotKind;
        public bool FullControl;
    }

    /// <summary>
    /// Per-game wire codec: everything that crosses the network bridge for one game.
    /// Encodings must match that game's engine serializer exactly.
    /// </summary>
    public interface IGameCodec
    {
        byte[] EncodeAction(PlayerAction action);
        PlayerAction DecodeAction(byte[] payload);
        byte[] EncodeEvents(List<GameEvent> events);
        List<GameEvent> DecodeEvents(byte[] payload);
        byte[] EncodeSnapshot(SnapshotBase snapshot);
        SnapshotBase DecodeSnapshot(byte[] payload);
        byte[] EncodePending(PendingSnap pending);
        PendingSnap DecodePending(byte[] payload);
        /// <summary>Client-side rules object, populated in place from the host's bytes.</summary>
        object CreateRules();
        byte[] EncodeRules(object rules);
        void PopulateRules(byte[] payload, object rules);
    }

    /// <summary>Game-agnostic safe defaults for timeouts/disconnects/auto-clients.</summary>
    public static class DefaultActions
    {
        /// <summary>Pass/end-turn when priority (via ISafeDefaultAction), else the
        /// decision's declared defaults padded to Min and clamped to Max.</summary>
        public static PlayerAction For(PendingSnap pending)
        {
            if (pending == null) return null;
            if (pending.Kind == Engine.Core.PendingInputKind.Priority)
            {
                if (pending.LegalActions != null)
                {
                    foreach (var action in pending.LegalActions)
                        if (action is ISafeDefaultAction)
                            return action;
                    if (pending.LegalActions.Count > 0)
                        return pending.LegalActions[0];
                }
                return new PassPriorityAction { PlayerIndex = pending.PlayerIndex };
            }

            var req = pending.Decision;
            if (req == null) return null;
            var answer = new Engine.Decisions.DecisionAnswer { DecisionId = req.Id };
            answer.ChosenOptionIds.AddRange(req.DefaultOptionIds);
            for (int i = 0; answer.ChosenOptionIds.Count < req.Min && i < req.Options.Count; i++)
                if (!answer.ChosenOptionIds.Contains(req.Options[i].Id))
                    answer.ChosenOptionIds.Add(req.Options[i].Id);
            while (answer.ChosenOptionIds.Count > req.Max)
                answer.ChosenOptionIds.RemoveAt(answer.ChosenOptionIds.Count - 1);
            return new SubmitDecisionAction { PlayerIndex = pending.PlayerIndex, Answer = answer };
        }
    }
}
