using System;
using Pascension.Engine.Cards;
using Pascension.Engine.Events;

namespace Pascension.Engine.Effects
{
    /// <summary>The source context a trigger filter is evaluated against.</summary>
    public readonly struct TriggerSource
    {
        /// <summary>The permanent (equipment/relic) owning the trigger, or null for hero passives.</summary>
        public readonly CardInstance SourceCard;
        public readonly int ControllerIndex;

        public TriggerSource(CardInstance sourceCard, int controllerIndex)
        {
            SourceCard = sourceCard;
            ControllerIndex = controllerIndex;
        }
    }

    /// <summary>
    /// "When X happens, do Y." Filters are code (definitions live in the Content assembly),
    /// so any event predicate is allowed. Matching triggers are pushed onto the stack as
    /// abilities in APNAP order.
    /// </summary>
    public sealed class TriggeredAbility
    {
        public string Description;
        public Func<GameEvent, TriggerSource, bool> Filter;
        public IEffect Effect;

        public TriggeredAbility(string description, Func<GameEvent, TriggerSource, bool> filter, IEffect effect)
        {
            Description = description;
            Filter = filter;
            Effect = effect;
        }
    }
}
