namespace Pascension.Net
{
    /// <summary>One replicated lobby slot. Plain DTO — serialized as JSON by LobbyNetBehaviour.</summary>
    public sealed class LobbySlot
    {
        public LobbySlotKind Kind = LobbySlotKind.Empty;

        /// <summary>NGO clientId for humans; ulong.MaxValue for bots/empty.</summary>
        public ulong ClientId = ulong.MaxValue;

        /// <summary>Persistent identity GUID for humans (reconnect key).</summary>
        public string ClientGuid;

        public string Name;
        public string HeroId;
        public bool Ready;

        /// <summary>Bot implementation tag; "heuristic" is the only kind for now.</summary>
        public string BotKind;
    }
}
