using System.Collections.Generic;
using Pascension.Core;

namespace Pascension.Net
{
    /// <summary>One toggleable expansion a game offers at setup.</summary>
    public sealed class DlcOption
    {
        public int Flag;          // bit in the DlcFlags mask
        public string Name;
        public string Description;
    }

    /// <summary>A selectable character/hero as the menus and lobby see it.</summary>
    public sealed class CharacterInfo
    {
        public string Id;
        public string DisplayName;
    }

    /// <summary>
    /// Everything the app/net layer needs to run one game: config building, engine +
    /// codec creation, character catalog, DLC options, bots, display helpers.
    /// </summary>
    public interface IGameModule
    {
        string GameId { get; }
        string DisplayName { get; }
        /// <summary>The Unity scene this game's table lives in (NGO loads by name).</summary>
        string GameSceneName { get; }
        int MinPlayers { get; }
        int MaxPlayers { get; }
        bool Playable { get; }

        IGameCodec Codec { get; }
        IReadOnlyList<DlcOption> DlcOptions { get; }

        object BuildConfig(ulong seed, List<PlayerSpec> players, int dlcFlags);
        IEngineAdapter CreateEngine(object config);
        float ResponseTimeoutOf(object config);
        /// <summary>The rules object sent to clients at resync (shape is game-specific).</summary>
        object RulesOf(object config);
        ulong SeedOf(object config);

        IReadOnlyList<CharacterInfo> CharactersFor(int dlcFlags);
        string DefaultCharacterFor(int slotIndex, int dlcFlags);
        string CharacterDisplayName(string characterId);

        /// <summary>Host-side bot creation (may capture the in-process engine).</summary>
        IBotAgent CreateBot(string botKind, ulong seed, IEngineAdapter engine);

        CardFace CardDisplay(string defId);
    }

    /// <summary>Static registry of the games this build ships. Register-once pattern
    /// (mirrors ContentRegistry): safe to call EnsureRegistered from anywhere.</summary>
    public static class GameCatalog
    {
        private static readonly List<IGameModule> Modules = new();
        private static bool _registered;

        public const string DefaultGameId = "pascension";

        public static void EnsureRegistered()
        {
            if (_registered) return;
            _registered = true;
            Modules.Add(new PascensionModule());
            Modules.Add(new ShardsModule());
        }

        public static IReadOnlyList<IGameModule> All
        {
            get
            {
                EnsureRegistered();
                return Modules;
            }
        }

        public static IGameModule Get(string gameId)
        {
            EnsureRegistered();
            foreach (var module in Modules)
                if (module.GameId == gameId)
                    return module;
            return Modules[0]; // unknown ids fall back to Pascension (forward compatibility)
        }

        public static IGameModule ByScene(string sceneName)
        {
            EnsureRegistered();
            foreach (var module in Modules)
                if (module.GameSceneName == sceneName)
                    return module;
            return null;
        }

        public static bool IsGameScene(string sceneName) => ByScene(sceneName) != null;

        /// <summary>Test hook: allows registering additional modules before a match.</summary>
        public static void Register(IGameModule module)
        {
            EnsureRegistered();
            foreach (var existing in Modules)
                if (existing.GameId == module.GameId)
                    return;
            Modules.Add(module);
        }
    }
}
