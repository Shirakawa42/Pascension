using System;
using System.Collections.Generic;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Pascension.Core;
using Pascension.Engine.Actions;
using Pascension.Engine.Events;
using Pascension.Engine.Serialization;
using Shards.Bots;
using Shards.Content;
using Shards.Engine;

namespace Pascension.Net
{
    /// <summary>Shards of Infinity as a pluggable game module. Playable flips to true in
    /// M6 when the GameShards scene + views ship — until then the menus show it as
    /// coming soon (banner disabled, lobby cycle skips it).</summary>
    public sealed class ShardsModule : IGameModule
    {
        public string GameId => "shards";
        public string DisplayName => "Shards of Infinity";
        public string GameSceneName => "GameShards";
        public int MinPlayers => 2;
        public int MaxPlayers => 4;
        public bool Playable => true; // M6: GameShards scene + SoiGameScreen shipped

        public IGameCodec Codec { get; } = new ShardsCodec();

        private static readonly List<DlcOption> Dlc = new()
        {
            new DlcOption { Flag = (int)ShardsDlc.RelicsOfTheFuture, Name = "Relics of the Future", Description = "+24 center cards; recruit one of your 2 relics at Mastery 10" },
            new DlcOption { Flag = (int)ShardsDlc.ShadowOfSalvation, Name = "Shadow of Salvation", Description = "+12 center cards; Rez as a 5th character (competitive content)" },
            new DlcOption { Flag = (int)ShardsDlc.IntoTheHorizon, Name = "Into the Horizon", Description = "+30 center cards with Ingeminex monsters; destinies at Mastery 5" }
        };
        public IReadOnlyList<DlcOption> DlcOptions => Dlc;

        public object BuildConfig(ulong seed, List<PlayerSpec> players, int dlcFlags) =>
            ShardsContentRegistry.StandardConfig(seed, players, (ShardsDlc)dlcFlags);

        public IEngineAdapter CreateEngine(object config) =>
            new ShardsEngineAdapter((ShardsConfig)config);

        public float ResponseTimeoutOf(object config) => 0f; // SoI has no response windows

        public object RulesOf(object config) => ((ShardsConfig)config).Rules;

        public ulong SeedOf(object config) => ((ShardsConfig)config).Seed;

        public IReadOnlyList<CharacterInfo> CharactersFor(int dlcFlags)
        {
            var list = new List<CharacterInfo>();
            foreach (var id in ShardsContentRegistry.CharactersFor((ShardsDlc)dlcFlags))
                list.Add(new CharacterInfo { Id = id, DisplayName = ShardsContentRegistry.CharacterDisplayName(id) });
            return list;
        }

        public string DefaultCharacterFor(int slotIndex, int dlcFlags)
        {
            var characters = CharactersFor(dlcFlags);
            return characters.Count > 0 ? characters[slotIndex % characters.Count].Id : null;
        }

        public string CharacterDisplayName(string characterId) =>
            ShardsContentRegistry.CharacterDisplayName(characterId);

        /// <summary>Shared read-only value model for greedy/strong seats (weights are
        /// static; card statics are immutable after registration).</summary>
        private static readonly Lazy<ShardsValueModel> Model = new(() =>
        {
            ShardsContentRegistry.EnsureRegistered();
            return new ShardsValueModel();
        });

        public IBotAgent CreateBot(string botKind, ulong seed, IEngineAdapter engine)
        {
            var inner = ((ShardsEngineAdapter)engine).Inner;
            return botKind switch
            {
                "random" => new ShardsHeuristicBot(seed, inner, random: true),
                "greedy" => new ShardsGreedyEvalBot(seed, inner, Model.Value),
                "strong" => new ShardsSearchBot(seed, inner,
                    ShardsSearchConfig.ForRealGames(1.0), Model.Value),
                "strong-fast" => new ShardsSearchBot(seed, inner,
                    ShardsSearchConfig.ForRealGames(0.25), Model.Value),
                _ => new ShardsHeuristicBot(seed, inner)
            };
        }

        public CardFace CardDisplay(string defId)
        {
            ShardsContentRegistry.EnsureRegistered();
            if (!ShardsCardDatabase.TryGet(defId, out var def)) return null;
            string typeLine = def.Faction switch
            {
                ShardsFaction.None => def.Type.ToString(),
                ShardsFaction.Monster => "Ingeminex",
                _ => def.Faction + " " + def.Type
            };
            return new CardFace
            {
                Id = def.Id,
                Name = def.Name,
                CostText = def.Type == ShardsCardType.Monster ? def.Defense.ToString() : def.Cost.ToString(),
                TypeLine = typeLine,
                RulesText = def.RulesText,
                ArtId = def.Id
            };
        }
    }

    /// <summary>Shards' wire codec: ShardsJson (the "t"-discriminator registry) for
    /// actions/events, plus a snapshot/pending serializer that delegates embedded
    /// PlayerActions to the same registry.</summary>
    public sealed class ShardsCodec : IGameCodec
    {
        private static readonly JsonSerializerSettings Settings = new()
        {
            NullValueHandling = NullValueHandling.Ignore,
            Formatting = Formatting.None,
            Converters = { new ShardsActionConverter() }
        };

        public byte[] EncodeAction(PlayerAction action) => Utf8(ShardsJson.Wire.SerializeAction(action));
        public PlayerAction DecodeAction(byte[] payload) => ShardsJson.Wire.DeserializeAction(FromUtf8(payload));

        public byte[] EncodeEvents(List<GameEvent> events) => Utf8(ShardsJson.Wire.SerializeEvents(events));
        public List<GameEvent> DecodeEvents(byte[] payload) => ShardsJson.Wire.DeserializeEvents(FromUtf8(payload));

        public byte[] EncodeSnapshot(SnapshotBase snapshot) =>
            Utf8(JsonConvert.SerializeObject((ShardsSnapshot)snapshot, Settings));
        public SnapshotBase DecodeSnapshot(byte[] payload) =>
            JsonConvert.DeserializeObject<ShardsSnapshot>(FromUtf8(payload), Settings);

        public byte[] EncodePending(PendingSnap pending) =>
            Utf8(JsonConvert.SerializeObject(pending, Settings));
        public PendingSnap DecodePending(byte[] payload) =>
            JsonConvert.DeserializeObject<PendingSnap>(FromUtf8(payload), Settings);

        public object CreateRules() => new ShardsRules();
        public byte[] EncodeRules(object rules) =>
            Utf8(JsonConvert.SerializeObject((ShardsRules)rules, Settings));
        public void PopulateRules(byte[] payload, object rules) =>
            JsonConvert.PopulateObject(FromUtf8(payload), rules);

        private static byte[] Utf8(string s) => Encoding.UTF8.GetBytes(s);
        private static string FromUtf8(byte[] b) => Encoding.UTF8.GetString(b);

        /// <summary>Polymorphic PlayerAction lists inside PendingSnap ride the ShardsJson
        /// whitelist (same pattern as Pascension's NetJson.PlayerActionConverter).</summary>
        private sealed class ShardsActionConverter : JsonConverter
        {
            public override bool CanConvert(Type objectType) => typeof(PlayerAction).IsAssignableFrom(objectType);

            public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer) =>
                writer.WriteRawValue(ShardsJson.Wire.SerializeAction((PlayerAction)value));

            public override object ReadJson(JsonReader reader, Type objectType, object existingValue, JsonSerializer serializer)
            {
                if (reader.TokenType == JsonToken.Null) return null;
                return ShardsJson.Wire.DeserializeAction(JObject.Load(reader).ToString(Formatting.None));
            }
        }
    }
}
