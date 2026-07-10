using System.Text;
using Pascension.Engine.Serialization;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Pascension.Net
{
    /// <summary>
    /// Public entry points for online play (call from any menu UI):
    /// HostAsync creates a Relay allocation, returns its join code (the GAME ID) and
    /// moves everyone into the Lobby scene; JoinAsync joins by game ID (NGO scene sync
    /// pulls the client into the host's scene); Shutdown tears everything down.
    /// The direct IP StartHost/StartClient remain as a dev/LAN fallback (no UI).
    /// Solo play NEVER calls into this class — the LocalSession path runs without NGO.
    /// </summary>
    public static class NetLauncher
    {
        public const ushort DefaultPort = 7777;
        public const string LobbySceneName = "Lobby";
        public const string GameSceneName = "Game";

        /// <summary>Up to 4 players = host + 3 relay connections.</summary>
        public const int MaxRelayConnections = 3;

        /// <summary>The game ID of the session we are hosting or joined (null when offline).</summary>
        public static string CurrentJoinCode { get; private set; }

        /// <summary>Remembered for <see cref="RejoinAsync"/> after a mid-game disconnect.</summary>
        public static string LastJoinCode { get; private set; }

        /// <summary>Remembered for <see cref="TryReconnect"/> (direct-IP fallback only).</summary>
        public static string LastAddress { get; private set; } = "127.0.0.1";

        public static ushort LastPort { get; private set; } = DefaultPort;

        /// <summary>Host over Unity Relay. Returns the join code players share as the
        /// game ID. Throws <see cref="UgsException"/> with a user-readable message.</summary>
        public static async System.Threading.Tasks.Task<string> HostAsync()
        {
            var manager = NetBootstrap.EnsureInitialized();
            if (manager.IsListening)
                throw new UgsException("Networking already running — leave the current game first.");

            await UgsGateway.EnsureSignedInAsync();
            var (joinCode, relayData) = await UgsGateway.CreateAllocationAsync(MaxRelayConnections);

            NetClientRegistry.Reset();
            NetLobbyData.ResetForLobby();
            NetBootstrap.ConfigureRelay(relayData);
            manager.NetworkConfig.ConnectionData = BuildConnectionPayload();

            if (!manager.StartHost())
                throw new UgsException("Could not start hosting — please try again.");

            CurrentJoinCode = LastJoinCode = joinCode;
            if (SceneManager.GetActiveScene().name != LobbySceneName)
                manager.SceneManager.LoadScene(LobbySceneName, LoadSceneMode.Single);
            return joinCode;
        }

        /// <summary>Join a hosted game by its game ID (Relay join code).</summary>
        public static async System.Threading.Tasks.Task JoinAsync(string joinCode)
        {
            var manager = NetBootstrap.EnsureInitialized();
            if (manager.IsListening)
                throw new UgsException("Networking already running — leave the current game first.");

            await UgsGateway.EnsureSignedInAsync();
            var relayData = await UgsGateway.JoinAllocationAsync(joinCode);

            NetClientRegistry.Reset();
            NetLobbyData.ResetForLobby();
            NetBootstrap.ConfigureRelay(relayData);
            manager.NetworkConfig.ConnectionData = BuildConnectionPayload();

            if (!manager.StartClient())
                throw new UgsException("Could not join — please try again.");

            CurrentJoinCode = LastJoinCode = (joinCode ?? "").Trim().ToUpperInvariant();
        }

        /// <summary>Rejoin the last game after a disconnect — the same GUID reclaims the
        /// same seat while the host's allocation (and the game) is still alive.</summary>
        public static System.Threading.Tasks.Task RejoinAsync()
        {
            if (string.IsNullOrEmpty(LastJoinCode))
                throw new UgsException("No previous game to rejoin.");
            return JoinAsync(LastJoinCode);
        }

        public static bool StartHost(ushort port = DefaultPort)
        {
            var manager = NetBootstrap.EnsureInitialized();
            if (manager.IsListening)
            {
                Debug.LogWarning("[Net] Networking already running — call Shutdown first.");
                return false;
            }

            NetClientRegistry.Reset();
            NetLobbyData.ResetForLobby();
            NetBootstrap.ConfigureTransport("127.0.0.1", port, listenAddress: "0.0.0.0");
            manager.NetworkConfig.ConnectionData = BuildConnectionPayload();

            if (!manager.StartHost())
            {
                Debug.LogError("[Net] StartHost failed (port in use?).");
                return false;
            }

            // Enter the lobby through NGO scene management so joining clients synchronize.
            if (SceneManager.GetActiveScene().name != LobbySceneName)
                manager.SceneManager.LoadScene(LobbySceneName, LoadSceneMode.Single);
            return true;
        }

        public static bool StartClient(string address, ushort port = DefaultPort)
        {
            var manager = NetBootstrap.EnsureInitialized();
            if (manager.IsListening)
            {
                Debug.LogWarning("[Net] Networking already running — call Shutdown first.");
                return false;
            }

            address = string.IsNullOrWhiteSpace(address) ? "127.0.0.1" : address.Trim();
            LastAddress = address;
            LastPort = port;

            NetClientRegistry.Reset();
            NetLobbyData.ResetForLobby();
            NetBootstrap.ConfigureTransport(address, port, listenAddress: null);
            manager.NetworkConfig.ConnectionData = BuildConnectionPayload();

            if (!manager.StartClient())
            {
                Debug.LogError("[Net] StartClient failed.");
                return false;
            }
            return true;
        }

        /// <summary>Rejoin the last host after a disconnect — same GUID reclaims the same seat.</summary>
        public static bool TryReconnect() => StartClient(LastAddress, LastPort);

        public static void Shutdown()
        {
            if (SessionProvider.Current is NetworkSession networkSession)
                networkSession.Detach();
            SessionProvider.Clear();
            NetLobbyData.ResetForLobby();
            NetClientRegistry.Reset();
            CurrentJoinCode = null; // LastJoinCode survives for RejoinAsync

            var manager = NetworkManager.Singleton;
            if (manager != null && manager.IsListening)
                manager.Shutdown();
        }

        private static byte[] BuildConnectionPayload() =>
            Encoding.UTF8.GetBytes(EngineJson.Serialize(new ConnectionPayload
            {
                ClientGuid = ClientIdentity.Guid,
                PlayerName = ClientIdentity.PlayerName
            }));
    }
}
