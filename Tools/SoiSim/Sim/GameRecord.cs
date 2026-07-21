using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;

namespace SoiSim
{
    /// <summary>Shared JSON settings: camelCase, no nulls, one line per record.</summary>
    public static class SimJson
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

        public static string Line(object o) => JsonConvert.SerializeObject(o, Settings);
    }

    /// <summary>First line of every JSONL file. The analyzer refuses to merge files
    /// whose ConfigHash differs (unless --allow-mixed).</summary>
    public sealed class RunHeader
    {
        public string Type = "header";
        public int Schema = SimConfig.SchemaVersion;
        public string ConfigHash;
        public string Date;
        public int Dlc;
        public string Bots;
        public int Budget;
        public string BotVersion;
        public string GitRev;
        public ulong SeedBase;
        public string Tag;
        public RulesSnapshot Rules;

        public sealed class RulesSnapshot
        {
            public int StartingHealth;
            public int MaxHealth;
            public int HandSize;
            public int MasteryCap;
            public int CenterRowSize;
        }
    }

    public sealed class GameRecord
    {
        public string Type = "game";
        public ulong Seed;
        /// <summary>Character ids in SEAT order (index 0 = first player).</summary>
        public List<string> Chars = new();
        /// <summary>Winning seat, -1 = tie (only meaningful when Termination is decisive).</summary>
        public int Winner;
        /// <summary>kill | overwhelm | tie | guard_cap | stall | error.</summary>
        public string Termination;
        public int Rounds;
        public int Turns;
        public int GuardSubmits;
        public int RejectedActions;
        public double WallMs;
        public ulong FinalHash;
        /// <summary>Def ids dealt to the shared destiny row at setup (dealt silently —
        /// state peek, not an event).</summary>
        public List<string> DestinyRowInitial = new();
        /// <summary>Center-row offer count per def id (initial fill + refills).</summary>
        public Dictionary<string, int> RowOffers = new();
        public Dictionary<string, int> MonstersRevealed = new();
        public int MonsterAttacksLanded;
        public List<PlayerRecord> Players = new();
        /// <summary>Only set when Termination == "error".</summary>
        public string Error;
    }

    public sealed class PlayerRecord
    {
        public string Character;
        public int FinalHealth;
        public int FinalMastery;
        /// <summary>Row acquisitions (buys, warps, free row recruits) — SlotIndex ≥ 0.
        /// Buy-rate denominators come from RowOffers.</summary>
        public Dictionary<string, int> Buys = new();
        /// <summary>Round of the FIRST acquisition per def id (row or off-row).</summary>
        public Dictionary<string, int> BuyRounds = new();
        /// <summary>Cards recruited off the center DECK (Shard Defiant's "recruit it")
        /// — never offered in the row, so excluded from buy-rate math.</summary>
        public Dictionary<string, int> OffRowRecruits = new();
        public Dictionary<string, int> FastPlays = new();
        public int GemsSpent;
        public int FocusCount;
        public int RoundToM10 = -1;
        public int RoundToM20 = -1;
        public int RoundToM30 = -1;
        public int DamageDealt;
        public int MaxSingleHit;
        /// <summary>Sampled at each of this player's turn starts.</summary>
        public List<int> HealthByRound = new();
        public List<int> MasteryByRound = new();
        public Dictionary<string, int> ChampionsDeployed = new();
        public int ChampionsLost;
        public int ChampionsKilled;
        public int ShieldReveals;
        public int DamagePrevented;
        public List<string> Relics = new();
        /// <summary>Destiny def id → round taken.</summary>
        public Dictionary<string, int> Destinies = new();
        /// <summary>Monster def id → round defeated (by this player).</summary>
        public Dictionary<string, int> MonstersDefeated = new();
        public int CardsBanished;
        public int CardsDrawn;
    }
}
