using System.Collections.Generic;

namespace Pascension.Net
{
    /// <summary>
    /// Host-side map of approved NGO clientIds to their persistent identity payload.
    /// Populated by the connection-approval callback, cleared on shutdown.
    /// </summary>
    public static class NetClientRegistry
    {
        private static readonly Dictionary<ulong, ConnectionPayload> ByClient = new();

        public static void Reset() => ByClient.Clear();

        public static void Register(ulong clientId, ConnectionPayload payload) => ByClient[clientId] = payload;

        public static void Unregister(ulong clientId) => ByClient.Remove(clientId);

        public static ConnectionPayload Get(ulong clientId) =>
            ByClient.TryGetValue(clientId, out var payload) ? payload : null;

        public static string GuidOf(ulong clientId) => Get(clientId)?.ClientGuid;

        public static bool IsGuidConnected(string clientGuid)
        {
            foreach (var pair in ByClient)
                if (pair.Value.ClientGuid == clientGuid)
                    return true;
            return false;
        }
    }
}
