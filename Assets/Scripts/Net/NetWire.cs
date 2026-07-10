using System.Text;
using Newtonsoft.Json;

namespace Pascension.Net
{
    /// <summary>
    /// Wire helper for the flat, game-INDEPENDENT DTOs the net layer owns itself
    /// (PauseInfo, lobby state, connection payload). Same settings the game codecs use
    /// for these shapes, so the bytes are unchanged from the pre-split EngineJson path.
    /// </summary>
    public static class NetWire
    {
        private static readonly JsonSerializerSettings Settings = new()
        {
            NullValueHandling = NullValueHandling.Ignore,
            Formatting = Formatting.None
        };

        public static byte[] Encode<T>(T value) =>
            Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(value, Settings));

        public static T Decode<T>(byte[] payload) =>
            JsonConvert.DeserializeObject<T>(Encoding.UTF8.GetString(payload), Settings);

        public static string EncodeString<T>(T value) => JsonConvert.SerializeObject(value, Settings);
        public static T DecodeString<T>(string json) => JsonConvert.DeserializeObject<T>(json, Settings);
    }
}
