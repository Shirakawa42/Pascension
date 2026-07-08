using System;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Pascension.Engine.Actions;
using Pascension.Engine.Serialization;
using Pascension.Engine.Targeting;

namespace Pascension.Net
{
    /// <summary>
    /// Net-side JSON for DTOs that EngineJson.Serialize&lt;T&gt; cannot round-trip on its
    /// own: ClientSnapshot and PendingSnap contain polymorphic PlayerAction lists which
    /// need the EngineJson whitelist ("t" discriminators). Every PlayerAction is delegated
    /// to EngineJson.SerializeAction/DeserializeAction (same whitelist, no TypeNameHandling)
    /// and TargetRef mirrors the engine's compact {"k","a","b"} encoding.
    /// </summary>
    public static class NetJson
    {
        private static readonly JsonSerializerSettings Settings = new()
        {
            NullValueHandling = NullValueHandling.Ignore,
            Formatting = Formatting.None,
            Converters = { new PlayerActionConverter(), new TargetRefConverter() }
        };

        public static string Serialize<T>(T value) => JsonConvert.SerializeObject(value, Settings);

        public static T Deserialize<T>(string json) => JsonConvert.DeserializeObject<T>(json, Settings);

        private sealed class PlayerActionConverter : JsonConverter
        {
            public override bool CanConvert(Type objectType) => typeof(PlayerAction).IsAssignableFrom(objectType);

            public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer) =>
                writer.WriteRawValue(EngineJson.SerializeAction((PlayerAction)value));

            public override object ReadJson(JsonReader reader, Type objectType, object existingValue, JsonSerializer serializer)
            {
                if (reader.TokenType == JsonToken.Null) return null;
                return EngineJson.DeserializeAction(JObject.Load(reader).ToString(Formatting.None));
            }
        }

        private sealed class TargetRefConverter : JsonConverter
        {
            public override bool CanConvert(Type objectType) =>
                objectType == typeof(TargetRef) || objectType == typeof(TargetRef?);

            public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer)
            {
                var target = (TargetRef)value;
                writer.WriteStartObject();
                writer.WritePropertyName("k");
                writer.WriteValue((int)target.Kind);
                writer.WritePropertyName("a");
                writer.WriteValue(target.A);
                writer.WritePropertyName("b");
                writer.WriteValue(target.B);
                writer.WriteEndObject();
            }

            public override object ReadJson(JsonReader reader, Type objectType, object existingValue, JsonSerializer serializer)
            {
                if (reader.TokenType == JsonToken.Null) return null;
                var obj = JObject.Load(reader);
                return TargetRef.FromRaw(
                    (TargetKind)(obj.Value<int?>("k") ?? 0),
                    obj.Value<int?>("a") ?? 0,
                    obj.Value<int?>("b") ?? 0);
            }
        }
    }
}
