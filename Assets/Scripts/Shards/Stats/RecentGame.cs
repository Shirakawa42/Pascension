using System.Collections.Generic;

namespace Shards.Stats
{
    public sealed class RecentGame
    {
        public string Id;
        public string EndedAtUtc;
        public string Mode;
        public string Termination;
        public bool Won;
        public bool Tie;
        public string MyCharacterId;
        public int MyMastery;
        public int MyHealth;
        public int Rounds;
        public int DurationSeconds;
        public List<string> OpponentNames = new();
        public Dictionary<string, int> MyBuys = new();
    }
}
