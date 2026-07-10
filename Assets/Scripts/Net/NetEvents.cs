using System;

namespace Pascension.Net
{
    /// <summary>
    /// Client-side connection notifications for UI that outlives any one session object
    /// (the Game scene's screen shows "connection lost" from this). Static — subscribers
    /// MUST unsubscribe in OnDestroy.
    /// </summary>
    public static class NetEvents
    {
        /// <summary>We lost (or were refused) the connection to the host. Argument is
        /// the transport's disconnect reason (may be empty).</summary>
        public static event Action<string> LocalClientDisconnected;

        internal static void RaiseLocalClientDisconnected(string reason) =>
            LocalClientDisconnected?.Invoke(reason ?? "");
    }
}
