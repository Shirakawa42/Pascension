namespace Pascension.Net
{
    /// <summary>One replicated lobby slot. Plain DTO — serialized as JSON by LobbyNetBehaviour.</summary>
    public sealed class LobbySlot
    {
        public LobbySlotKind Kind = LobbySlotKind.Empty;

        /// <summary>NGO clientId for humans; ulong.MaxValue for empty seats.</summary>
        public ulong ClientId = ulong.MaxValue;

        /// <summary>Persistent identity GUID for humans (reconnect key).</summary>
        public string ClientGuid;

        public string Name;
        public string HeroId;
        public bool Ready;

    }
}
