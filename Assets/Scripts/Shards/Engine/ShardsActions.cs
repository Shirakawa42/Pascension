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

    /// <summary>Spend power on an enemy champion. Damage accumulates WITHIN the turn;
    /// the champion is destroyed once its marks reach full defense (marks clear at end
    /// of turn — champion damage never persists between turns).</summary>
    public sealed class ShardsAttackChampionAction : PlayerAction
    {
        public int TargetPlayerIndex;
        public int CardInstanceId;
        /// <summary>Power to spend; 0 = as much as needed/possible.</summary>
        public int Amount;
        public override string Describe() => $"Attack champion #{CardInstanceId} of P{TargetPlayerIndex} for {Amount}";
    }

    /// <summary>Spend power on a revealed Ingeminex (Into the Horizon). Same accumulation
    /// model as champions; defeating it grants YOU its reward.</summary>
    public sealed class ShardsAttackMonsterAction : PlayerAction
    {
        public int CardInstanceId;
        public int Amount;
        public override string Describe() => $"Attack Ingeminex #{CardInstanceId} for {Amount}";
    }

    /// <summary>Take one destiny from the shared row (once per game, Mastery 5+, free — ItH).</summary>
    public sealed class ShardsTakeDestinyAction : PlayerAction
    {
        public int CardInstanceId;
        public override string Describe() => $"Take destiny #{CardInstanceId}";
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
