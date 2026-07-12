using System.Collections.Generic;
using Pascension.Core;
using Pascension.Engine.Actions;
using Pascension.Engine.Core;
using Pascension.Engine.Events;
using Pascension.Engine.Serialization;

namespace Shards.Engine
{
    /// <summary>The game-agnostic face of the Shards engine for the host/net layer.</summary>
    public sealed class ShardsEngineAdapter : IEngineAdapter
    {
        public readonly ShardsEngine Inner;

        public ShardsEngineAdapter(ShardsConfig config) => Inner = new ShardsEngine(config);

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

        public SnapshotBase BuildSnapshot(int playerIndex) => ShardsSnapshotBuilder.Build(Inner, playerIndex);

        public bool GameOver => Inner.State.GameOver;

        public int WinnerIndex => Inner.State.WinnerIndex;

        public PlayerAction DefaultActionFor(PendingSnap pending) => DefaultActions.For(pending);
    }

    /// <summary>Shards' wire format: its own action registry + event assembly.</summary>
    public static class ShardsJson
    {
        public static readonly WireJson Wire = new(
            new[]
            {
                typeof(ShardsPlayCardAction),
                typeof(ShardsBuyCardAction),
                typeof(ShardsFocusAction),
                typeof(ShardsExhaustAction),
                typeof(ShardsAttackChampionAction),
                typeof(ShardsAttackMonsterAction),
                typeof(ShardsTakeDestinyAction),
                typeof(ShardsRecruitRelicAction),
                typeof(ShardsEndTurnAction),
                typeof(PassPriorityAction),
                typeof(SubmitDecisionAction),
                typeof(ConcedeAction)
            },
            new[] { typeof(ShardsCardPlayedEvent).Assembly, typeof(GameEvent).Assembly });
    }
}
