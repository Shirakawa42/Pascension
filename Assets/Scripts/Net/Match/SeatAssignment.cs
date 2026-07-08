namespace Pascension.Net
{
    /// <summary>Slot→seat mapping captured by the host when the lobby starts a match.</summary>
    public sealed class SeatAssignment
    {
        public int PlayerIndex;
        public LobbySlotKind Kind;

        /// <summary>Current NGO clientId for remote humans; updated on reconnect.</summary>
        public ulong ClientId = ulong.MaxValue;

        /// <summary>Persistent identity — the reconnect key.</summary>
        public string ClientGuid;

        public string PlayerName;
        public string HeroId;
        public string BotKind;

        /// <summary>True for the host's own human (LocalSession instead of RemoteSeat).</summary>
        public bool IsHostHuman;
    }
}
