using System.Collections.Generic;

namespace Shards.Stats
{
    /// <summary>Everything the stats screen renders, computed in one pass over the
    /// filtered records (+ stubs for the Lifetime counters).</summary>
    public sealed class SoiStatsAggregates
    {
        public int Games;
        public int Wins;
        public int Losses;
        public int Ties;
        public ModeAgg Ai = new();
        public ModeAgg Mp2 = new();
        public ModeAgg Mp3Plus = new();
        public int WinsByKill;
        public int WinsByOverwhelm;
        public int WinsByConcede;
        public int LossesByKill;
        public int LossesByOverwhelm;
        public int LossesByConcede;
        public int CurrentWinStreak;
        public int CurrentLossStreak;
        public int BestWinStreak;
        public float AvgRounds;
        public float AvgDurationSeconds;
        public float AvgDamageDealt;
        public int MaxSingleHit;
        /// <summary>-1 = no qualifying games (threshold never reached).</summary>
        public float AvgRoundToM10 = -1;
        public float AvgRoundToM20 = -1;
        public float AvgRoundToM30 = -1;
        public float M30ReachRate;
        public string BestHeroCharacterId;
        /// <summary>False = fallback most-played hero (fewer than 5 decisive games each).</summary>
        public bool BestHeroQualified;
        public List<HeroAgg> Heroes = new();
        public List<CardAgg> Cards = new();
        /// <summary>Mode filter only — deliberately ignores OpponentKey.</summary>
        public List<OpponentAgg> Opponents = new();
        /// <summary>Newest 50 filtered records, newest first.</summary>
        public List<RecentGame> Recent = new();
        /// <summary>Only populated when the filter names an opponent.</summary>
        public HeadToHead H2H;
        public int LifetimeGames;
        public int LifetimeWins;
        public int LifetimeTies;
    }
}
