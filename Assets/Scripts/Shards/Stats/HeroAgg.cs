namespace Shards.Stats
{
    public sealed class HeroAgg
    {
        public string CharacterId;
        public int Games;
        public int Wins;
        public int Ties;
        public int MaxSingleHit;
        public float AvgRounds;
        /// <summary>-1 = M30 never reached with this hero.</summary>
        public float AvgRoundToM30 = -1;
    }
}
