using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Pascension.Engine.Actions;
using Pascension.Engine.Events;

namespace Pascension.Engine.Serialization
{
    /// <summary>
    /// Wire format for actions/events/snapshots: Newtonsoft JSON with an explicit
    /// whitelist type registry ("t" discriminators). Never use TypeNameHandling —
    /// only registered types can cross the network.
    /// </summary>
    public static class EngineJson
    {
        private static readonly Dictionary<string, Type> ActionTypes = new();
        private static readonly Dictionary<string, Type> EventTypes = new();
        private static readonly JsonSerializerSettings Settings = new()
        {
            NullValueHandling = NullValueHandling.Ignore,
            Formatting = Formatting.None,
            Converters = { new TargetRefConverter() }
        };

        private sealed class TargetRefConverter : JsonConverter<Targeting.TargetRef>
        {
            public override void WriteJson(JsonWriter writer, Targeting.TargetRef value, JsonSerializer serializer)
            {
                writer.WriteStartObject();
                writer.WritePropertyName("k");
                writer.WriteValue((int)value.Kind);
                writer.WritePropertyName("a");
                writer.WriteValue(value.A);
                writer.WritePropertyName("b");
                writer.WriteValue(value.B);
                writer.WriteEndObject();
            }

            public override Targeting.TargetRef ReadJson(JsonReader reader, Type objectType, Targeting.TargetRef existingValue,
                bool hasExistingValue, JsonSerializer serializer)
            {
                var obj = JObject.Load(reader);
                return Targeting.TargetRef.FromRaw(
                    (Targeting.TargetKind)(obj.Value<int?>("k") ?? 0),
                    obj.Value<int?>("a") ?? 0,
                    obj.Value<int?>("b") ?? 0);
            }
        }

        static EngineJson()
        {
            RegisterAction(typeof(PlayCardAction));
            RegisterAction(typeof(BuyCardAction));
            RegisterAction(typeof(MoveStepsAction));
            RegisterAction(typeof(AssignDamageAction));
            RegisterAction(typeof(ActivateAbilityAction));
            RegisterAction(typeof(UseHeroAbilityAction));
            RegisterAction(typeof(PassPriorityAction));
            RegisterAction(typeof(SubmitDecisionAction));
            RegisterAction(typeof(ConcedeAction));

            foreach (var t in typeof(GameEvent).Assembly.GetTypes())
                if (!t.IsAbstract && typeof(GameEvent).IsAssignableFrom(t))
                    EventTypes[ShortName(t)] = t;
        }

        private static void RegisterAction(Type t) => ActionTypes[ShortName(t)] = t;

        private static string ShortName(Type t)
        {
            const string actionSuffix = "Action";
            const string eventSuffix = "Event";
            string name = t.Name;
            if (name.EndsWith(actionSuffix)) name = name.Substring(0, name.Length - actionSuffix.Length);
            else if (name.EndsWith(eventSuffix)) name = name.Substring(0, name.Length - eventSuffix.Length);
            return name;
        }

        public static string SerializeAction(PlayerAction action)
        {
            var obj = JObject.FromObject(action, JsonSerializer.Create(Settings));
            obj.AddFirst(new JProperty("t", ShortName(action.GetType())));
            return obj.ToString(Formatting.None);
        }

        public static PlayerAction DeserializeAction(string json)
        {
            var obj = JObject.Parse(json);
            string tag = obj.Value<string>("t") ?? throw new JsonSerializationException("Missing action discriminator");
            if (!ActionTypes.TryGetValue(tag, out var type))
                throw new JsonSerializationException($"Unknown action type '{tag}'");
            return (PlayerAction)obj.ToObject(type, JsonSerializer.Create(Settings));
        }

        public static string SerializeEvents(IReadOnlyList<GameEvent> events)
        {
            var array = new JArray();
            foreach (var e in events)
            {
                var obj = JObject.FromObject(e, JsonSerializer.Create(Settings));
                obj.AddFirst(new JProperty("t", ShortName(e.GetType())));
                array.Add(obj);
            }
            return array.ToString(Formatting.None);
        }

        public static List<GameEvent> DeserializeEvents(string json)
        {
            var array = JArray.Parse(json);
            var result = new List<GameEvent>();
            foreach (var token in array)
            {
                var obj = (JObject)token;
                string tag = obj.Value<string>("t") ?? throw new JsonSerializationException("Missing event discriminator");
                if (!EventTypes.TryGetValue(tag, out var type))
                    throw new JsonSerializationException($"Unknown event type '{tag}'");
                result.Add((GameEvent)obj.ToObject(type, JsonSerializer.Create(Settings)));
            }
            return result;
        }

        public static string Serialize<T>(T value) => JsonConvert.SerializeObject(value, Settings);
        public static T Deserialize<T>(string json) => JsonConvert.DeserializeObject<T>(json, Settings);
    }
}
