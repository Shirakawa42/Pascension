using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;

namespace Shards.Stats
{
    /// <summary>Shared JSON settings for SoI stats records (mirrors SoiSim's SimJson):
    /// camelCase, nulls omitted, single line.</summary>
    public static class SoiStatsJson
    {
        public static readonly JsonSerializerSettings Settings = new()
        {
            ContractResolver = new DefaultContractResolver
            {
                NamingStrategy = new CamelCaseNamingStrategy()
            },
            NullValueHandling = NullValueHandling.Ignore,
            Formatting = Formatting.None
        };

        public static string Serialize(object o) => JsonConvert.SerializeObject(o, Settings);

        public static T Deserialize<T>(string json) => JsonConvert.DeserializeObject<T>(json, Settings);
    }
}
