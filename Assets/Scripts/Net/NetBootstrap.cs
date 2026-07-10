using System.Text;
using Pascension.Engine.Serialization;
using Unity.Netcode;
using Unity.Netcode.Transports.UTP;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Pascension.Net
{
    /// <summary>
    /// Owns the code-built NetworkManager (nothing net-related is authored into scenes
    /// except the Lobby scene's in-scene LobbyNet object): UnityTransport, connection
    /// approval (identity + capacity + mid-game reconnect gate), GameNetBridge prefab
    /// registration, connect/disconnect routing, and creation of HostMatchStarter
    /// whenever the Game scene loads while NGO is active.
    /// </summary>
    public sealed class NetBootstrap : MonoBehaviour
    {
        /// <summary>Resources path of the bridge prefab authored by NetSceneBuilder.</summary>
        public const string BridgePrefabResourcePath = "Net/GameNetBridge";

        private const int MaxConnectionPayloadBytes = 1024;

        private static NetBootstrap _instance;

        private NetworkManager _networkManager;
        private UnityTransport _transport;

        /// <summary>Create (once) and return the code-built, DontDestroyOnLoad NetworkManager.</summary>
        public static NetworkManager EnsureInitialized()
        {
            if (_instance == null)
            {
                var go = new GameObject("PascensionNet");
                DontDestroyOnLoad(go);
                _instance = go.AddComponent<NetBootstrap>();
                _instance.Initialize();
            }
            return _instance._networkManager;
        }

        /// <summary>listenAddress non-null → hosting (bind address); null → joining.
        /// Direct-IP/LAN path — the join-code UI uses <see cref="ConfigureRelay"/>.</summary>
        public static void ConfigureTransport(string address, ushort port, string listenAddress)
        {
            EnsureInitialized();
            if (listenAddress != null)
                _instance._transport.SetConnectionData(address, port, listenAddress);
            else
                _instance._transport.SetConnectionData(address, port);
        }

        /// <summary>Route all traffic through a Unity Relay allocation (host and client).</summary>
        public static void ConfigureRelay(Unity.Networking.Transport.Relay.RelayServerData data)
        {
            EnsureInitialized();
            _instance._transport.SetRelayServerData(data);
        }

        private void Initialize()
        {
            _networkManager = gameObject.AddComponent<NetworkManager>();
            _transport = gameObject.AddComponent<UnityTransport>();
            // Default 30s makes disconnect detection (and the "identity already
            // connected" rejoin window) feel broken — pause UX needs ~10s.
            _transport.DisconnectTimeoutMS = 10000;

            _networkManager.NetworkConfig = new NetworkConfig
            {
                NetworkTransport = _transport,
                ConnectionApproval = true,
                EnableSceneManagement = true
            };

            _networkManager.ConnectionApprovalCallback = ApproveConnection;
            _networkManager.OnClientConnectedCallback += OnClientConnected;
            _networkManager.OnClientDisconnectCallback += OnClientDisconnected;

            var bridgePrefab = Resources.Load<GameObject>(BridgePrefabResourcePath);
            if (bridgePrefab != null)
                _networkManager.AddNetworkPrefab(bridgePrefab);
            else
                Debug.LogError("[Net] Missing Resources/" + BridgePrefabResourcePath +
                               ".prefab — run 'Pascension/Setup/Build Lobby Scene' once in the editor.");

            SceneManager.sceneLoaded += OnSceneLoaded;
        }

        private void OnDestroy()
        {
            SceneManager.sceneLoaded -= OnSceneLoaded;
            if (_instance == this)
                _instance = null;
        }

        // ---------------- connection approval (host only) ----------------

        private void ApproveConnection(NetworkManager.ConnectionApprovalRequest request,
            NetworkManager.ConnectionApprovalResponse response)
        {
            response.Approved = false;
            response.CreatePlayerObject = false;
            response.Pending = false;

            if (request.ClientNetworkId == NetworkManager.ServerClientId)
            {
                // The host's own local client is always approved.
                NetClientRegistry.Register(request.ClientNetworkId,
                    ParsePayload(request.Payload) ?? new ConnectionPayload
                    {
                        ClientGuid = ClientIdentity.Guid,
                        PlayerName = ClientIdentity.PlayerName
                    });
                response.Approved = true;
                return;
            }

            var payload = ParsePayload(request.Payload);
            if (payload == null || string.IsNullOrEmpty(payload.ClientGuid))
            {
                response.Reason = "Missing or malformed client identity";
                return;
            }
            if (NetClientRegistry.IsGuidConnected(payload.ClientGuid))
            {
                response.Reason = "This identity is already connected";
                return;
            }

            if (NetLobbyData.MatchRunning)
            {
                // Mid-game: only a known seat's identity may (re)connect.
                if (NetLobbyData.FindSeatByGuid(payload.ClientGuid) < 0)
                {
                    response.Reason = "A game is already in progress";
                    return;
                }
            }
            else
            {
                var lobby = LobbyNetBehaviour.Instance;
                bool hasRoom = lobby != null && lobby.IsSpawned
                    ? lobby.HasRoomFor(payload.ClientGuid)
                    : _networkManager.ConnectedClientsIds.Count < LobbyNetBehaviour.MaxSlots;
                if (!hasRoom)
                {
                    response.Reason = "The lobby is full";
                    return;
                }
            }

            NetClientRegistry.Register(request.ClientNetworkId, payload);
            response.Approved = true;
        }

        private static ConnectionPayload ParsePayload(byte[] payload)
        {
            if (payload == null || payload.Length == 0 || payload.Length > MaxConnectionPayloadBytes)
                return null;
            try
            {
                return EngineJson.Deserialize<ConnectionPayload>(Encoding.UTF8.GetString(payload));
            }
            catch
            {
                return null;
            }
        }

        // ---------------- connect / disconnect routing ----------------

        private void OnClientConnected(ulong clientId)
        {
            if (!_networkManager.IsServer || clientId == NetworkManager.ServerClientId)
                return;
            if (NetLobbyData.MatchRunning)
                ReconnectService.HandleClientConnected(clientId);
            else if (LobbyNetBehaviour.Instance != null && LobbyNetBehaviour.Instance.IsSpawned)
                LobbyNetBehaviour.Instance.HandleClientConnected(clientId);
        }

        private void OnClientDisconnected(ulong clientId)
        {
            if (_networkManager.IsServer)
            {
                if (clientId == NetworkManager.ServerClientId)
                    return;
                if (NetLobbyData.MatchRunning)
                    ReconnectService.HandleClientDisconnected(clientId);
                else if (LobbyNetBehaviour.Instance != null && LobbyNetBehaviour.Instance.IsSpawned)
                    LobbyNetBehaviour.Instance.HandleClientDisconnected(clientId);
                NetClientRegistry.Unregister(clientId);
            }
            else
            {
                // We are the client and lost (or were refused) the connection.
                if (SessionProvider.Current is NetworkSession session)
                {
                    session.Detach();
                    SessionProvider.Clear();
                }
                string reason = _networkManager.DisconnectReason;
                Debug.Log("[Net] Disconnected from host" +
                          (string.IsNullOrEmpty(reason) ? "." : ": " + reason));
                NetEvents.RaiseLocalClientDisconnected(reason);
            }
        }

        // ---------------- per-scene match wiring ----------------

        private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            if (_networkManager == null || !_networkManager.IsListening)
                return;
            if (scene.name != NetLauncher.GameSceneName)
                return;
            if (FindFirstObjectByType<HostMatchStarter>() != null)
                return;
            new GameObject("HostMatchStarter").AddComponent<HostMatchStarter>();
        }
    }
}
