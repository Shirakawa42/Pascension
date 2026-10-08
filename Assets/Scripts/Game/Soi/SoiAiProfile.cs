namespace Pascension.Game.Soi
{
    /// <summary>Stable resource and statistics identities for the two solo opponents.</summary>
    public sealed class SoiAiProfile
    {
        public static readonly SoiAiProfile Auld = new SoiAiProfile("Auld Haïai", "hybrid-balance-20260928-2e9d7dc1", "shards", false);
        public static readonly SoiAiProfile Nyou = new SoiAiProfile("Nyou Haïai", "nyou-20261008-9d7159b9-scry64", "nyou", true);
        public string Name { get; }
        public string BotKind { get; }
        public string PolicyResource { get; }
        public string SearchResource { get; }
        public bool FullInformation { get; }
        private SoiAiProfile(string name, string botKind, string resourcePrefix, bool fullInformation)
        {
            Name = name; BotKind = botKind; FullInformation = fullInformation;
            PolicyResource = "AI/" + resourcePrefix + "-policy";
            SearchResource = "AI/" + resourcePrefix + "-search-settings";
        }
    }
}
