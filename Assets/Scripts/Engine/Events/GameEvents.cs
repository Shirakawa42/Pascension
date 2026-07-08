using System.Collections.Generic;
using Pascension.Engine.Core;
using Pascension.Engine.Targeting;

namespace Pascension.Engine.Events
{
    public sealed class GameStartedEvent : GameEvent
    {
        public int PlayerCount;
        public List<string> HeroIds = new();
    }

    public sealed class TurnStartedEvent : GameEvent
    {
        public int PlayerIndex;
        public int Round;
    }

    public sealed class PhaseChangedEvent : GameEvent
    {
        public int PlayerIndex;
        public Phase Phase;
    }

    public sealed class CardDrawnEvent : GameEvent
    {
        public int PlayerIndex;
        public int InstanceId;
        public string DefId;

        public override GameEvent RedactFor(int viewerIndex) =>
            viewerIndex == PlayerIndex ? this : new CardDrawnEvent { Seq = Seq, PlayerIndex = PlayerIndex, InstanceId = InstanceId, DefId = null };
    }

    /// <summary>Generic zone change. DefId is redacted for viewers who may not see the card.</summary>
    public sealed class CardMovedEvent : GameEvent
    {
        public int InstanceId;
        public string DefId;
        public int OwnerIndex;
        public ZoneType From;
        public ZoneType To;

        private static bool Hidden(ZoneType z) => z == ZoneType.Deck || z == ZoneType.Hand || z == ZoneType.Pile;

        public override GameEvent RedactFor(int viewerIndex)
        {
            if (viewerIndex == OwnerIndex) return this;
            // A card is revealed unless it moves between two zones hidden from this viewer.
            if (Hidden(From) && Hidden(To))
                return new CardMovedEvent { Seq = Seq, InstanceId = InstanceId, DefId = null, OwnerIndex = OwnerIndex, From = From, To = To };
            return this;
        }
    }

    public sealed class DeckShuffledEvent : GameEvent
    {
        public int PlayerIndex;
    }

    public sealed class ApChangedEvent : GameEvent
    {
        public int PlayerIndex;
        public int Delta;
        public int NewValue;
    }

    public sealed class DamagePoolChangedEvent : GameEvent
    {
        public int PlayerIndex;
        public int Delta;
        public int NewValue;
    }

    public sealed class XpGainedEvent : GameEvent
    {
        public int PlayerIndex;
        public int Amount;
        public int NewXp;
    }

    public sealed class LeveledUpEvent : GameEvent
    {
        public int PlayerIndex;
        public int NewLevel;
    }

    public sealed class PlayerMovedEvent : GameEvent
    {
        public int PlayerIndex;
        public int FromStep;
        public int ToStep;
    }

    public sealed class InnReachedEvent : GameEvent
    {
        public int PlayerIndex;
        public int InnStep;
    }

    public sealed class CardBoughtEvent : GameEvent
    {
        public int PlayerIndex;
        public int InstanceId;
        public string DefId;
        public CardTier Tier;
        public int SlotIndex;
        public int CostPaid;
    }

    /// <summary>A market slot was refilled from its pile. InstanceId -1 when the pile ran out.</summary>
    public sealed class MarketRefilledEvent : GameEvent
    {
        public CardTier Tier;
        public int SlotIndex;
        public int InstanceId = -1;
        public string DefId;
    }

    /// <summary>A card was played WITHOUT using the stack (mana-ability rule: pure-AP cards).
    /// Stacked plays are announced by StackPushedEvent instead. Always public.</summary>
    public sealed class CardPlayedEvent : GameEvent
    {
        public int PlayerIndex;
        public int InstanceId;
        public string DefId;
    }

    public sealed class StackPushedEvent : GameEvent
    {
        public int StackItemId;
        public string Kind;
        public int ControllerIndex;
        /// <summary>Card definition for spells; null for abilities/triggers.</summary>
        public string DefId;
        public int SourceInstanceId = -1;
        public string Description;
        public List<TargetRef> Targets = new();
    }

    public sealed class StackResolvedEvent : GameEvent
    {
        public int StackItemId;
    }

    public sealed class SpellCounteredEvent : GameEvent
    {
        public int StackItemId;
        public int ByStackItemId;
    }

    /// <summary>All targets of a stack item became illegal; it resolves with no effect.</summary>
    public sealed class StackFizzledEvent : GameEvent
    {
        public int StackItemId;
    }

    public sealed class DamageMarkedEvent : GameEvent
    {
        public TargetRef Target;
        public int TargetInstanceId;
        public int Amount;
        public int NewMarked;
        public int ByPlayerIndex;
    }

    public sealed class MonsterDiedEvent : GameEvent
    {
        public int InstanceId;
        public string DefId;
        public CardTier Tier;
        public int SlotIndex;
        public int KillerIndex;
    }

    public sealed class CardTappedEvent : GameEvent
    {
        public int InstanceId;
        public bool Tapped;
    }

    public sealed class PermanentsUntappedEvent : GameEvent
    {
        public int PlayerIndex;
    }

    /// <summary>End-of-turn: all marked damage and until-EOT modifiers cleared.</summary>
    public sealed class TurnDamageClearedEvent : GameEvent
    {
    }

    public sealed class DecisionRequestedEvent : GameEvent
    {
        public int PlayerIndex;
        public int DecisionId;
        public string Title;
    }

    public sealed class DecisionMadeEvent : GameEvent
    {
        public int PlayerIndex;
        public int DecisionId;
    }

    public sealed class ExtraTurnEvent : GameEvent
    {
        public int PlayerIndex;
    }

    public sealed class PlayerConcededEvent : GameEvent
    {
        public int PlayerIndex;
    }

    public sealed class GameEndedEvent : GameEvent
    {
        public int WinnerIndex;
        public string Reason;
    }
}
