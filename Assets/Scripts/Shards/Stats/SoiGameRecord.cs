using System.Collections.Generic;

namespace Shards.Stats
{
    /// <summary>One finished SoI game as seen from one seat (the "my" perspective).
    /// Built client-side from the redacted per-viewer stream only.</summary>
    public sealed class SoiGameRecord
    {
        public int Schema = 1;
        public string Guid;
        public string EndedAtUtc;
        public string AppVersion;
        /// <summary>"ai" | "mp2" | "mp3plus".</summary>
        public string Mode;
        public int Dlc;
        public int MyIndex;
        /// <summary>-1 = tie.</summary>
        public int WinnerIndex;
        /// <summary>"kill" | "overwhelm" | "tie".</summary>
        public string Termination;
        public int Rounds;
        public int Turns;
        public int DurationSeconds;
        /// <summary>False when the viewer missed events (mid-game join / seq gap):
        /// per-card and pair stats skip incomplete records.</summary>
        public bool Complete = true;
        public List<SoiSeatRecord> Players = new();
    }
}
