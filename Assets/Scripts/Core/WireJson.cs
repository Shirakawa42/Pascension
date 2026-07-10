using System;
using System.Collections.Generic;
using System.Reflection;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Pascension.Engine.Actions;
using Pascension.Engine.Events;

namespace Pascension.Core
{
    /// <summary>
    /// The wire format shared by every game: Newtonsoft JSON with an explicit
    /// whitelist type registry ("t" discriminators). Each game constructs ONE instance
    /// registering its own action types and the assemblies holding its concrete events.
    /// Never use TypeNameHandling — only registered types can cross the network.
    /// </summary>
    public sealed class WireJson
    {
        private readonly Dictionary<string, Type> _actionTypes = new();
        private readonly Dictionary<string, Type> _eventTypes = new();
        private readonly JsonSerializerSettings _settings;

        public WireJson(IEnumerable<Type> actionTypes, IEnumerable<Assembly> eventAssemblies,
            params JsonConverter[] extraConverters)
        {
            _settings = new JsonSerializerSettings
            {
                NullValueHandling = NullValueHandling.Ignore,
                Formatting = Formatting.None,
                Converters = { new TargetRefConverter() }
            };
            foreach (var converter in extraConverters)
                _settings.Converters.Add(converter);

            foreach (var t in actionTypes)
                _actionTypes[ShortName(t)] = t;
            foreach (var assembly in eventAssemblies)
                foreach (var t in assembly.GetTypes())
                    if (!t.IsAbstract && typeof(GameEvent).IsAssignableFrom(t))
                        _eventTypes[ShortName(t)] = t;
        }

        private sealed class TargetRefConverter : JsonConverter<Engine.Targeting.TargetRef>
        {
            public override void WriteJson(JsonWriter writer, Engine.Targeting.TargetRef value, JsonSerializer serializer)
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

            public override Engine.Targeting.TargetRef ReadJson(JsonReader reader, Type objectType,
                Engine.Targeting.TargetRef existingValue, bool hasExistingValue, JsonSerializer serializer)
            {
                var obj = JObject.Load(reader);
                return Engine.Targeting.TargetRef.FromRaw(
                    (Engine.Targeting.TargetKind)(obj.Value<int?>("k") ?? 0),
                    obj.Value<int?>("a") ?? 0,
                    obj.Value<int?>("b") ?? 0);
            }
        }

        private static string ShortName(Type t)
        {
            const string actionSuffix = "Action";
            const string eventSuffix = "Event";
            string name = t.Name;
            if (name.EndsWith(actionSuffix)) name = name.Substring(0, name.Length - actionSuffix.Length);
            else if (name.EndsWith(eventSuffix)) name = name.Substring(0, name.Length - eventSuffix.Length);
            return name;
        }

        public string SerializeAction(PlayerAction action)
        {
            var obj = JObject.FromObject(action, JsonSerializer.Create(_settings));
            obj.AddFirst(new JProperty("t", ShortName(action.GetType())));
            return obj.ToString(Formatting.None);
        }

        public PlayerAction DeserializeAction(string json)
        {
            var obj = JObject.Parse(json);
            string tag = obj.Value<string>("t") ?? throw new JsonSerializationException("Missing action discriminator");
            if (!_actionTypes.TryGetValue(tag, out var type))
                throw new JsonSerializationException($"Unknown action type '{tag}'");
            return (PlayerAction)obj.ToObject(type, JsonSerializer.Create(_settings));
        }

        public string SerializeEvents(IReadOnlyList<GameEvent> events)
        {
            var array = new JArray();
            foreach (var e in events)
            {
                var obj = JObject.FromObject(e, JsonSerializer.Create(_settings));
                obj.AddFirst(new JProperty("t", ShortName(e.GetType())));
                array.Add(obj);
            }
            return array.ToString(Formatting.None);
        }

        public List<GameEvent> DeserializeEvents(string json)
        {
            var array = JArray.Parse(json);
            var result = new List<GameEvent>();
            foreach (var token in array)
            {
                var obj = (JObject)token;
                string tag = obj.Value<string>("t") ?? throw new JsonSerializationException("Missing event discriminator");
                if (!_eventTypes.TryGetValue(tag, out var type))
                    throw new JsonSerializationException($"Unknown event type '{tag}'");
                result.Add((GameEvent)obj.ToObject(type, JsonSerializer.Create(_settings)));
            }
            return result;
        }

        public string Serialize<T>(T value) => JsonConvert.SerializeObject(value, _settings);
        public T Deserialize<T>(string json) => JsonConvert.DeserializeObject<T>(json, _settings);
    }
}
