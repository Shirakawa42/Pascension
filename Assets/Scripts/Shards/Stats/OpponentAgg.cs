namespace Shards.Stats
{
    public sealed class OpponentAgg
    {
        public string IdentityKey;
        /// <summary>Display name from the LATEST game against this identity.</summary>
        public string DisplayName;
        public bool IsBot;
        public int Games;
        public int MyWins;
        public int MyLosses;
        public int Ties;
        public string LastPlayedUtc;
    }
}
