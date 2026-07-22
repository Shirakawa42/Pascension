namespace Shards.Stats
{
    /// <summary>Unordered pair of def ids the player bought in the same game
    /// (DefA &lt; DefB ordinal).</summary>
    public sealed class PairAgg
    {
        public string DefA;
        public string DefB;
        public int GamesTogether;
        public int WinsTogether;
        public int TiesTogether;
    }
}
