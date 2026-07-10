using System.Collections.Generic;
using System.Text;
using Pascension.Bots;
using Pascension.Content;
using Pascension.Core;
using Pascension.Engine.Actions;
using Pascension.Engine.Cards;
using Pascension.Engine.Core;
using Pascension.Engine.Events;
using Pascension.Engine.Heroes;
using Pascension.Engine.Serialization;

namespace Pascension.Net
{
    /// <summary>Pascension as a pluggable game module. Wire format is byte-identical to
    /// the pre-split implementation (EngineJson/NetJson underneath).</summary>
    public sealed class PascensionModule : IGameModule
    {
        public string GameId => "pascension";
        public string DisplayName => "Pascension";
        public string GameSceneName => "Game";
        public int MinPlayers => 2;
        public int MaxPlayers => 4;
        public bool Playable => true;

        public IGameCodec Codec { get; } = new PascensionCodec();

        private static readonly List<DlcOption> NoDlc = new();
        public IReadOnlyList<DlcOption> DlcOptions => NoDlc;

        public object BuildConfig(ulong seed, List<PlayerSpec> players, int dlcFlags)
        {
            ContentRegistry.RegisterAll();
            var configs = new List<PlayerConfig>();
            foreach (var spec in players)
                configs.Add(new PlayerConfig
                {
                    Name = spec.Name,
                    HeroId = spec.CharacterId,
                    FullControl = spec.FullControl
                });
            return ContentRegistry.StandardConfig(seed, configs);
        }

        public IEngineAdapter CreateEngine(object config) =>
            new PascensionEngineAdapter((GameConfig)config);

        public float ResponseTimeoutOf(object config) => ((GameConfig)config).Rules.ResponseTimerSeconds;

        public object RulesOf(object config) => ((GameConfig)config).Rules;

        public ulong SeedOf(object config) => ((GameConfig)config).Seed;

        public IReadOnlyList<CharacterInfo> CharactersFor(int dlcFlags)
        {
            ContentRegistry.RegisterAll();
            var list = new List<CharacterInfo>();
            foreach (var hero in HeroDatabase.All)
                list.Add(new CharacterInfo { Id = hero.Id, DisplayName = hero.Name });
            return list;
        }

        public string DefaultCharacterFor(int slotIndex, int dlcFlags)
        {
            var heroes = CharactersFor(dlcFlags);
            return heroes.Count > 0 ? heroes[slotIndex % heroes.Count].Id : null;
        }

        public string CharacterDisplayName(string characterId)
        {
            ContentRegistry.RegisterAll();
            try
            {
                return HeroDatabase.Get(characterId).Name;
            }
            catch (KeyNotFoundException)
            {
                return characterId;
            }
        }

        public IBotAgent CreateBot(string botKind, ulong seed, IEngineAdapter engine)
        {
            var inner = botKind == "random"
                ? (ISyncAgent)new RandomBot(seed)
                : new HeuristicBot(seed);
            return new SyncAgentBot(inner, ((PascensionEngineAdapter)engine).Inner);
        }

        public CardFace CardDisplay(string defId)
        {
            ContentRegistry.RegisterAll();
            if (!CardDatabase.TryGet(defId, out var def)) return null;
            return new CardFace
            {
                Id = def.Id,
                Name = def.Name,
                CostText = def.Cost.ToString(),
                TypeLine = def.TypeLine,
                RulesText = def.RulesText,
                ArtId = def.Id
            };
        }
    }

    /// <summary>Pascension's wire codec — EngineJson/NetJson exactly as before the split.</summary>
    public sealed class PascensionCodec : IGameCodec
    {
        public byte[] EncodeAction(PlayerAction action) => Utf8(EngineJson.SerializeAction(action));
        public PlayerAction DecodeAction(byte[] payload) => EngineJson.DeserializeAction(FromUtf8(payload));

        public byte[] EncodeEvents(List<GameEvent> events) => Utf8(EngineJson.SerializeEvents(events));
        public List<GameEvent> DecodeEvents(byte[] payload) => EngineJson.DeserializeEvents(FromUtf8(payload));

        public byte[] EncodeSnapshot(SnapshotBase snapshot) => Utf8(NetJson.Serialize((ClientSnapshot)snapshot));
        public SnapshotBase DecodeSnapshot(byte[] payload) => NetJson.Deserialize<ClientSnapshot>(FromUtf8(payload));

        public byte[] EncodePending(PendingSnap pending) => Utf8(NetJson.Serialize(pending));
        public PendingSnap DecodePending(byte[] payload) => NetJson.Deserialize<PendingSnap>(FromUtf8(payload));

        public object CreateRules() => new GameRules();
        public byte[] EncodeRules(object rules) => Utf8(EngineJson.Serialize((GameRules)rules));
        public void PopulateRules(byte[] payload, object rules) =>
            Newtonsoft.Json.JsonConvert.PopulateObject(FromUtf8(payload), rules);

        private static byte[] Utf8(string s) => Encoding.UTF8.GetBytes(s);
        private static string FromUtf8(byte[] b) => Encoding.UTF8.GetString(b);
    }
}
