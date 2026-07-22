using System.Collections.Generic;

namespace Shards.Stats
{
    /// <summary>What survives when a full record is evicted past the cap — just enough
    /// to keep lifetime winrate and opponent history exact.</summary>
    public sealed class SoiGameStub
    {
        public string Guid;
        public string EndedAtUtc;
        public string Mode;
        public string MyCharacterId;
        public bool Won;
        public bool Tie;
        public List<SoiStubOpponent> Opponents = new();
    }
}
