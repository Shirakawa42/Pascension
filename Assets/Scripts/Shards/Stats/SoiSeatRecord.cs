using System.Collections.Generic;

namespace Shards.Stats
{
    /// <summary>Per-seat accumulators for one game. Identity: humans =
    /// lowercase(username), bots = "bot:"+BotKind, solo local guest = "guest".</summary>
    public sealed class SoiSeatRecord
    {
        public string Identity;
        public string Name;
        public bool IsBot;
        public string BotKind;
        public string CharacterId;
        public bool Eliminated;
        public bool Conceded;
        public int FinalHealth;
        public int FinalMastery;
        /// <summary>Row acquisitions (SlotIndex >= 0), def id → count.</summary>
        public Dictionary<string, int> Buys = new();
        /// <summary>Recruited off the center DECK (SlotIndex == -1).</summary>
        public Dictionary<string, int> OffRowRecruits = new();
        public Dictionary<string, int> FastPlays = new();
        public Dictionary<string, int> Plays = new();
        public Dictionary<string, int> ChampionsDeployed = new();
        public int ChampionsLost;
        public int ChampionsKilled;
        public List<string> Relics = new();
        /// <summary>Destiny def id → round taken.</summary>
        public Dictionary<string, int> Destinies = new();
        /// <summary>Monster def id → round defeated by this seat.</summary>
        public Dictionary<string, int> MonstersDefeated = new();
        public int DamageDealt;
        public int MaxSingleHit;
        public int GemsSpent;
        public int FocusCount;
        public int CardsDrawn;
        public int CardsBanished;
        public int ShieldReveals;
        public int DamagePrevented;
        /// <summary>-1 = threshold never reached.</summary>
        public int RoundToM10 = -1;
        public int RoundToM20 = -1;
        public int RoundToM30 = -1;
    }
}
