using Pascension.Engine.Targeting;

namespace Pascension.Engine.Actions
{
    // PlayerAction base + PassPriority/SubmitDecision/Concede live in Pascension.Core
    // (Core/PlayerAction.cs) — this file holds only Pascension-specific actions.

    /// <summary>Play a card from hand. Required targets are asked for via a decision afterwards.</summary>
    public sealed class PlayCardAction : PlayerAction
    {
        public int CardInstanceId;

        public override string Describe() => $"Play card #{CardInstanceId}";
    }

    public sealed class BuyCardAction : PlayerAction
    {
        public int TierIndex;
        public int SlotIndex;

        public override string Describe() => $"Buy tier {TierIndex} slot {SlotIndex}";
    }

    public sealed class MoveStepsAction : PlayerAction
    {
        public int Steps;

        public override string Describe() => $"Move {Steps} step(s)";
    }

    /// <summary>Commit damage from the pool at a monster (or the boss). Goes on the stack.</summary>
    public sealed class AssignDamageAction : PlayerAction
    {
        public TargetRef Target;
        public int Amount;

        public override string Describe() => $"Assign {Amount} damage to {Target}";
    }

    /// <summary>Activate an ability of an equipped permanent (tap equipment, relic ability).</summary>
    public sealed class ActivateAbilityAction : PlayerAction
    {
        public int SourceInstanceId;
        public int AbilityIndex;

        public override string Describe() => $"Activate #{SourceInstanceId} ability {AbilityIndex}";
    }

    /// <summary>Use the hero's active (Ultimate=false) or ultimate (Ultimate=true) ability.</summary>
    public sealed class UseHeroAbilityAction : PlayerAction
    {
        public bool Ultimate;

        public override string Describe() => Ultimate ? "Use hero ultimate" : "Use hero active";
    }

}
