namespace Shards.Stats
{
    /// <summary>What slice of the record list an aggregation runs over. OpponentKey
    /// non-null = only games where that Identity sat in an opponent seat.</summary>
    public sealed class SoiStatsFilter
    {
        public bool IncludeAi = true;
        public bool IncludeMp2 = true;
        public bool IncludeMp3Plus = true;
        public string OpponentKey;
    }
}
