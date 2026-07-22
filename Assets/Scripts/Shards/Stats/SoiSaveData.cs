using System.Collections.Generic;

namespace Shards.Stats
{
    /// <summary>The persisted stats blob for one profile: capped full records plus
    /// stubs of evicted games (lifetime counters stay exact).</summary>
    public sealed class SoiSaveData
    {
        public int Schema = 1;
        public string ProfileKey;
        /// <summary>Local changes not yet pushed to the cloud copy.</summary>
        public bool DirtyUpload;
        public List<SoiGameRecord> Records = new();
        public List<SoiGameStub> Stubs = new();
    }
}
