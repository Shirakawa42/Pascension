namespace Pascension.Net
{
    /// <summary>
    /// Host-side reconnect coordination. Static because NetBootstrap (DontDestroyOnLoad)
    /// needs a stable routing target while HostMatchStarter instances come and go with
    /// the Game scene. Policy: on disconnect the match PAUSES (overlay for everyone)
    /// until the player rejoins with the same game ID — the approval callback matches
    /// their GUID to the seat and we re-point the RemoteSeat + resync — or the host
    /// kicks them, which hands the seat to a bot permanently.
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

        /// <summary>Host-UI entry point: replace a disconnected player's seat with a bot.</summary>
        public static void KickToBot(int playerIndex) => _active?.KickSeatToBot(playerIndex);
    }
}
