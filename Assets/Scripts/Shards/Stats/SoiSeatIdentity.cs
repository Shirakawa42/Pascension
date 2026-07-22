namespace Shards.Stats
{
    /// <summary>Who sat in a seat, resolved by the session layer (index-aligned with
    /// the engine's seat order).</summary>
    public sealed class SoiSeatIdentity
    {
        public string Identity;
        public string Name;
        public bool IsBot;
        public string BotKind;
    }
}
