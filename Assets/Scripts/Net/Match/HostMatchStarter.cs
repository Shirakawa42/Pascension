using Pascension.Bots;
using Pascension.Engine.Serialization;
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

namespace Pascension.Net
{
    /// <summary>
    /// Created by NetBootstrap (from the sceneLoaded hook — after Awake, before Start of
    /// scene objects) whenever the Game scene loads while NGO is running.
    /// Host: builds GameHost from NetLobbyData, attaches LocalSession (host human) +
    /// RemoteSeats (remote humans) + BotSeats, spawns GameNetBridge, publishes the host's
    /// session via SessionProvider, then GameHost.Start(). Drives GameHost.Tick and bot
    /// pacing from Update. Client: creates a NetworkSession and publishes it.
    /// </summary>
    public sealed class HostMatchStarter : MonoBehaviour
    {
        private GameHost _host;
        private GameNetBridge _bridge;
        private LocalSession _localSession;
        private NetworkSession _clientSession;
        private readonly List<BotSeat> _bots = new();
        private readonly List<RemoteSeat> _remoteSeats = new();
        private Engine.Core.GameConfig _config;

        private void Awake()
        {
            var manager = NetworkManager.Singleton;
            if (manager == null || !manager.IsListening)
            {
                Destroy(gameObject); // solo play never reaches here
                return;
            }

            if (manager.IsHost)
                SetupHost(manager);
            else
                SetupClient();
        }

        private void SetupClient()
        {
            _clientSession = new NetworkSession();
            SessionProvider.Current = _clientSession;
        }

        private void SetupHost(NetworkManager manager)
        {
            var config = NetLobbyData.Config;
            if (config == null)
            {
                Debug.LogError("[Net] Game scene loaded as host without lobby data — start matches from the Lobby.");
                Destroy(gameObject);
                return;
            }

            _config = config;
            _host = new GameHost(config);

            foreach (var seat in NetLobbyData.Seats)
            {
                switch (seat.Kind)
                {
                    case LobbySlotKind.Human when seat.IsHostHuman:
                        _localSession = new LocalSession(_host, seat.PlayerIndex);
                        _host.AttachSeat(_localSession, isHuman: true);
                        break;

                    case LobbySlotKind.Human:
                        var remote = new RemoteSeat(seat.PlayerIndex, seat.ClientId, () => _bridge)
                        {
                            Connected = IsClientConnected(manager, seat.ClientId)
                        };
                        _remoteSeats.Add(remote);
                        _host.AttachSeat(remote, isHuman: true);
                        break;

                    case LobbySlotKind.Bot:
                        var agent = new HeuristicBot(config.Seed ^ (ulong)((seat.PlayerIndex + 1) * 7919));
                        var bot = new BotSeat(seat.PlayerIndex, agent);
                        bot.Bind(_host);
                        _bots.Add(bot);
                        _host.AttachSeat(bot, isHuman: false);
                        break;
                }
            }

            if (_localSession != null)
                SessionProvider.Current = _localSession;

            _host.SeatActionRejected += OnSeatActionRejected;

            SpawnBridge();
            ReconnectService.Activate(this);
            // GameHost.Start() is deferred to the first Update so it runs AFTER every
            // scene object's Start() — the UI must be bound to the session before the
            // initial snapshot/input broadcast or it would miss them.
        }

        private bool _hostStarted;

        private void SpawnBridge()
        {
            var prefab = Resources.Load<GameObject>(NetBootstrap.BridgePrefabResourcePath);
            if (prefab == null)
            {
                Debug.LogError("[Net] GameNetBridge prefab missing — run 'Pascension/Setup/Build Lobby Scene'.");
                return;
            }
            var go = Instantiate(prefab);
            _bridge = go.GetComponent<GameNetBridge>();
            _bridge.ConfigureHost(_host, SeatOfClient, ResyncClient); // wire BEFORE Spawn
            go.GetComponent<NetworkObject>().Spawn(destroyWithScene: true);
        }

        private void Update()
        {
            if (_host == null) return;
            if (!_hostStarted)
            {
                _hostStarted = true;
                _host.Start();
                // A client may have died during the scene transition — pause immediately.
                RecomputePause();
            }
            _host.Tick(Time.deltaTime);
            if (!_host.Paused)
                foreach (var bot in _bots)
                    bot.Tick(Time.deltaTime);
        }

        private void OnDestroy()
        {
            ReconnectService.Deactivate(this);
            if (_host != null)
                _host.SeatActionRejected -= OnSeatActionRejected;
            if (_clientSession != null)
                _clientSession.Detach();
            if (ReferenceEquals(SessionProvider.Current, _localSession) ||
                ReferenceEquals(SessionProvider.Current, _clientSession))
                SessionProvider.Clear();
        }

        // ---------------- host-side routing ----------------

        /// <summary>clientId → seat index for currently-connected remote humans (else -1).</summary>
        private int SeatOfClient(ulong clientId)
        {
            foreach (var seat in _remoteSeats)
                if (seat.Connected && seat.ClientId == clientId)
                    return seat.PlayerIndex;
            return -1;
        }

        /// <summary>Full per-client resync: seat, rules, masked snapshot, pending input,
        /// pause state (a rejoiner may arrive while other players are still out).</summary>
        private void ResyncClient(ulong clientId)
        {
            if (_host == null || _bridge == null) return;
            int playerIndex = SeatOfClient(clientId);
            if (playerIndex < 0) return;

            _bridge.SendSeat(clientId, playerIndex);
            _bridge.SendRules(clientId, _config.Rules); // reliable RPCs are ordered — lands before the snapshot
            _bridge.SendSnapshot(clientId, _host.SnapshotFor(playerIndex));

            var pending = _host.Engine.PendingInput;
            if (pending != null && pending.PlayerIndex == playerIndex)
                _bridge.SendInputRequest(clientId, new PendingSnap
                {
                    Kind = pending.Kind,
                    PlayerIndex = pending.PlayerIndex,
                    LegalActions = pending.LegalActions,
                    Decision = pending.Decision
                });

            _bridge.SendPauseState(clientId, BuildPauseInfo(canKick: false));
        }

        private void OnSeatActionRejected(int playerIndex, string error)
        {
            foreach (var seat in _remoteSeats)
                if (seat.PlayerIndex == playerIndex && seat.Connected)
                    _bridge?.SendRejection(seat.ClientId, error);
        }

        // ---------------- reconnect (called via ReconnectService) ----------------

        public void OnRemoteClientConnected(ulong clientId)
        {
            string guid = NetClientRegistry.GuidOf(clientId);
            int playerIndex = NetLobbyData.FindSeatByGuid(guid);
            if (playerIndex < 0) return;

            foreach (var seat in _remoteSeats)
            {
                if (seat.PlayerIndex != playerIndex) continue;
                seat.ClientId = clientId;
                seat.Connected = true;
                foreach (var assignment in NetLobbyData.Seats)
                    if (assignment.PlayerIndex == playerIndex)
                        assignment.ClientId = clientId;
                // Push a resync now; the client also pulls one when its bridge spawns.
                ResyncClient(clientId);
                RecomputePause();
                return;
            }
        }

        public void OnRemoteClientDisconnected(ulong clientId)
        {
            foreach (var seat in _remoteSeats)
                if (seat.Connected && seat.ClientId == clientId)
                    seat.Connected = false;
            // Policy: the match PAUSES until they rejoin (same game ID reclaims the
            // seat) or the host replaces them with a bot.
            RecomputePause();
        }

        // ---------------- pause / kick ----------------

        /// <summary>Freeze the match while any remote human is disconnected; thaw when
        /// everyone is back (or replaced). Pushes pause state to every session.</summary>
        private void RecomputePause()
        {
            if (_host == null) return;
            bool shouldPause = false;
            if (!_host.Engine.State.GameOver)
                foreach (var seat in _remoteSeats)
                    if (!seat.Connected)
                    {
                        shouldPause = true;
                        break;
                    }

            _host.SetPaused(shouldPause);
            _localSession?.RaisePause(BuildPauseInfo(canKick: true));
            _bridge?.BroadcastPause(BuildPauseInfo(canKick: false));
        }

        private PauseInfo BuildPauseInfo(bool canKick)
        {
            var info = new PauseInfo
            {
                Paused = _host != null && _host.Paused,
                JoinCode = NetLauncher.CurrentJoinCode,
                CanKick = canKick
            };
            foreach (var seat in _remoteSeats)
            {
                if (seat.Connected) continue;
                string name = "Player " + (seat.PlayerIndex + 1);
                foreach (var assignment in NetLobbyData.Seats)
                    if (assignment.PlayerIndex == seat.PlayerIndex && !string.IsNullOrEmpty(assignment.PlayerName))
                        name = assignment.PlayerName;
                info.Waiting.Add(new PausedSeat { PlayerIndex = seat.PlayerIndex, Name = name });
            }
            return info;
        }

        /// <summary>Host kicks a disconnected player: a heuristic bot takes the seat
        /// permanently and the kicked identity can no longer reconnect.</summary>
        public void KickSeatToBot(int playerIndex)
        {
            if (_host == null) return;
            RemoteSeat target = null;
            foreach (var seat in _remoteSeats)
                if (seat.PlayerIndex == playerIndex)
                    target = seat;
            if (target == null) return;

            var manager = NetworkManager.Singleton;
            if (target.Connected && manager != null && manager.IsServer)
                manager.DisconnectClient(target.ClientId, "Replaced by a bot"); // safety; UI only offers kick when disconnected
            _remoteSeats.Remove(target);

            // Flip the seat record to Bot and drop the GUID: FindSeatByGuid only matches
            // Human seats, so the kicked identity is rejected at connection approval.
            foreach (var assignment in NetLobbyData.Seats)
            {
                if (assignment.PlayerIndex != playerIndex) continue;
                assignment.Kind = LobbySlotKind.Bot;
                assignment.BotKind = LobbyNetBehaviour.DefaultBotKind;
                assignment.ClientGuid = null;
            }

            var agent = new HeuristicBot(_config.Seed ^ (ulong)((playerIndex + 1) * 7919));
            var bot = new BotSeat(playerIndex, agent);
            bot.Bind(_host);
            _bots.Add(bot);
            // Re-routes any pending input for this seat to the bot (it answers once unpaused).
            _host.ReplaceSeat(playerIndex, bot, isHuman: false);
            Debug.Log("[Net] Seat " + playerIndex + " replaced by a bot.");
            RecomputePause();
        }

        private static bool IsClientConnected(NetworkManager manager, ulong clientId)
        {
            var ids = manager.ConnectedClientsIds;
            for (int i = 0; i < ids.Count; i++)
                if (ids[i] == clientId)
                    return true;
            return false;
        }
    }
}
