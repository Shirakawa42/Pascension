namespace Pascension.Net
{
    /// <summary>
    /// Host-side reconnect coordination. Static because NetBootstrap (DontDestroyOnLoad)
    /// needs a stable routing target while HostMatchStarter instances come and go with
    /// the Game scene. Policy: on disconnect the seat stays attached and GameHost's
    /// response timer keeps the game moving (auto-pass / default decisions); on
    /// reconnect the approval callback has already matched the GUID to a seat, so we
    /// just re-point the RemoteSeat at the new clientId and resync.
    /// </summary>
    public static class ReconnectService
    {
        private static HostMatchStarter _active;

        public static void Activate(HostMatchStarter starter) => _active = starter;

        public static void Deactivate(HostMatchStarter starter)
        {
            if (_active == starter)
                _active = null;
        }

        /// <summary>An approved mid-game connection (identity already validated by approval).</summary>
        public static void HandleClientConnected(ulong clientId) =>
            _active?.OnRemoteClientConnected(clientId);

        public static void HandleClientDisconnected(ulong clientId) =>
            _active?.OnRemoteClientDisconnected(clientId);
    }
}
