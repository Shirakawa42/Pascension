using System.Collections.Generic;
using Pascension.Core;
using Pascension.Engine.Actions;
using Pascension.Engine.Events;

namespace Pascension.Engine.Serialization
{
    /// <summary>
    /// Pascension's wire format: a <see cref="WireJson"/> instance registering this
    /// game's action types and event assembly. The public surface is unchanged from the
    /// pre-split static class — callers and the wire format are byte-compatible.
    /// NOTE: events are discovered by reflection over THIS assembly (the GameEvent base
    /// now lives in Pascension.Core, which holds no concrete events).
    /// </summary>
    public static class EngineJson
    {
        public static readonly WireJson Wire = new(
            new[]
            {
                typeof(PlayCardAction),
                typeof(BuyCardAction),
                typeof(MoveStepsAction),
                typeof(AssignDamageAction),
                typeof(ActivateAbilityAction),
                typeof(UseHeroAbilityAction),
                typeof(PassPriorityAction),
                typeof(SubmitDecisionAction),
                typeof(ConcedeAction)
            },
            new[] { typeof(CardDrawnEvent).Assembly, typeof(GameEvent).Assembly });

        public static string SerializeAction(PlayerAction action) => Wire.SerializeAction(action);
        public static PlayerAction DeserializeAction(string json) => Wire.DeserializeAction(json);
        public static string SerializeEvents(IReadOnlyList<GameEvent> events) => Wire.SerializeEvents(events);
        public static List<GameEvent> DeserializeEvents(string json) => Wire.DeserializeEvents(json);
        public static string Serialize<T>(T value) => Wire.Serialize<T>(value);
        public static T Deserialize<T>(string json) => Wire.Deserialize<T>(json);
    }
}
