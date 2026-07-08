using System.Text;
using Pascension.Engine.Serialization;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Pascension.Net
{
    /// <summary>
    /// Public entry points for online play (call from any menu UI):
    /// StartHost hosts on a port and moves everyone into the Lobby scene;
    /// StartClient joins ip:port (NGO scene sync pulls the client into the host's scene);
    /// Shutdown tears everything down. Solo play NEVER calls into this class —
    /// the LocalSession path runs without NGO.
    /// </summary>
    public static class NetLauncher
    {
        public const ushort DefaultPort = 7777;
        public const string LobbySceneName = "Lobby";
        public const string GameSceneName = "Game";

        /// <summary>Remembered for <see cref="TryReconnect"/> after a mid-game disconnect.</summary>
        public static string LastAddress { get; private set; } = "127.0.0.1";

        public static ushort LastPort { get; private set; } = DefaultPort;

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
