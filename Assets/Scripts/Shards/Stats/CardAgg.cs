namespace Shards.Stats
{
    /// <summary>Only Complete records feed card aggregation (a partial stream would
    /// under-count buys/plays and skew the winrates).</summary>
    public sealed class CardAgg
    {
        public string DefId;
        public int TimesBought;
        public int TimesPlayed;
        public int ChampionDeploys;
        public int GamesBought;
        public int WinsWhenBought;
        public int TiesWhenBought;
        public int GamesPlayed;
        public int WinsWhenPlayed;
        public int TiesWhenPlayed;
    }
}
