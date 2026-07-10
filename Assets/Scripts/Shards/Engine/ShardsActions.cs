using Pascension.Engine.Actions;

namespace Shards.Engine
{
    // All actions derive from the shared PlayerAction base so the host/net layer and
    // WireJson handle them without knowing this game exists.

    public sealed class ShardsPlayCardAction : PlayerAction
    {
        public int CardInstanceId;
        public override string Describe() => $"Play card #{CardInstanceId}";
    }

    /// <summary>Buy from the center row (or, for Mercenaries, fast-play instead).</summary>
    public sealed class ShardsBuyCardAction : PlayerAction
    {
        public int SlotIndex;
        public bool FastPlay;
        public override string Describe() => (FastPlay ? "Fast-play" : "Buy") + $" slot {SlotIndex}";
    }

    /// <summary>Once per turn: exhaust the character + pay 1 gem → +1 mastery.</summary>
    public sealed class ShardsFocusAction : PlayerAction
    {
        public override string Describe() => "Focus";
    }

    /// <summary>Use a champion's (or the character's) once-per-turn exhaust power.</summary>
    public sealed class ShardsExhaustAction : PlayerAction
    {
        public int CardInstanceId;
        public override string Describe() => $"Exhaust #{CardInstanceId}";
    }

    /// <summary>Destroy an enemy champion by paying its full defense in power.</summary>
    public sealed class ShardsAttackChampionAction : PlayerAction
    {
        public int TargetPlayerIndex;
        public int CardInstanceId;
        public override string Describe() => $"Attack champion #{CardInstanceId} of P{TargetPlayerIndex}";
    }

    /// <summary>Defeat an Ingeminex monster in the center row (Into the Horizon).</summary>
    public sealed class ShardsAttackMonsterAction : PlayerAction
    {
        public int SlotIndex;
        public override string Describe() => $"Attack monster in slot {SlotIndex}";
    }

    /// <summary>Recruit one of your two set-aside relics (Mastery 10+, once per game — DLC1).</summary>
    public sealed class ShardsRecruitRelicAction : PlayerAction
    {
        public int CardInstanceId;
        public override string Describe() => $"Recruit relic #{CardInstanceId}";
    }

    /// <summary>End the turn: assign pooled power to opponents (split decision), defenders
    /// reveal shields, then cleanup + draw. The safe default for timeouts/auto-clients.</summary>
    public sealed class ShardsEndTurnAction : PlayerAction, ISafeDefaultAction
    {
        public override string Describe() => "End turn";
    }
}
