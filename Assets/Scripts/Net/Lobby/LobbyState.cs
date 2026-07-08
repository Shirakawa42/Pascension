using System.Collections.Generic;

namespace Pascension.Net
{
    /// <summary>
    /// Full replicated lobby state. The server owns it and rebroadcasts the whole thing
    /// as JSON after every change (max 4 slots — the payload is tiny and this keeps
    /// replication trivially correct with no NetworkList/Unity.Collections dependency).
    /// </summary>
    public sealed class LobbyState
    {
        public List<LobbySlot> Slots = new();

        public ulong HostClientId;
    }
}
