using System.Collections.Generic;

namespace Shards.Stats
{
    /// <summary>Everything the recorder cannot know from the game stream: wall-clock
    /// timestamps, app version, mode and seat identities (supplied by the caller —
    /// no wall clock inside Shards.*).</summary>
    public sealed class SoiRecordContext
    {
        public string EndedAtUtc;
        public string AppVersion;
        public int DurationSeconds;
        public string Mode;
        public List<SoiSeatIdentity> Seats = new();
    }
}
