using System.Collections.Generic;

namespace Pascension.Engine.Core
{
    public sealed class PlayerConfig
    {
        public string Name = "Player";
        public string HeroId;
        public bool FullControl;
    }

    /// <summary>(defId, copies) entries describing a market pile's composition.</summary>
    public sealed class PileSpec
    {
        public List<(string defId, int copies)> Entries = new();

        public PileSpec Add(string defId, int copies)
        {
            Entries.Add((defId, copies));
            return this;
        }
    }

    public sealed class GameConfig
    {
        public ulong Seed;
        public List<PlayerConfig> Players = new();
        public GameRules Rules = new();

        /// <summary>(defId, count) for each player's starting deck.</summary>
        public List<(string defId, int copies)> DefaultDeck = new();

        public PileSpec BasicPile = new();
        public PileSpec AdvancedPile = new();
        public PileSpec ElitePile = new();

        public string BossDefId;
    }
}
