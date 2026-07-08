using System;
using System.Collections.Generic;
using System.Text;
using Pascension.Content;
using Pascension.Engine.Core;
using Pascension.Engine.Heroes;
using Pascension.Engine.Serialization;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Pascension.Net
{
    /// <summary>
    /// Replicated lobby: up to 4 slots (human / bot / empty), hero pick + ready flag per
    /// slot, host-only add-bot/remove/start controls. The server mutates plain C# state
    /// and rebroadcasts the full state as JSON after every change; clients pull it once
    /// on spawn. Lives in the Lobby scene as an in-scene placed NetworkObject (authored
    /// by NetSceneBuilder). On start the host snapshots slot→client mapping into
    /// NetLobbyData and loads the Game scene through NGO scene management.
    /// </summary>
    public sealed class LobbyNetBehaviour : NetworkBehaviour
    {
        public const int MaxSlots = 4;
        public const string DefaultBotKind = "heuristic";

        public static LobbyNetBehaviour Instance { get; private set; }

        /// <summary>Latest lobby state — authoritative on the server, replica on clients.</summary>
        public LobbyState State { get; private set; } = EmptyState();

        /// <summary>Raised after every state change (UI may poll State instead).</summary>
        public event Action<LobbyState> StateChanged;

        private void Awake()
        {
            Instance = this;
            ContentRegistry.RegisterAll(); // hero catalog needed for default picks
        }

        public override void OnNetworkSpawn()
        {
            Instance = this;
            if (IsServer)
            {
                State = EmptyState();
                State.HostClientId = NetworkManager.LocalClientId;

                // The host's human always occupies slot 0 and is implicitly ready.
                OccupySlot(0, NetworkManager.LocalClientId, NetClientRegistry.Get(NetworkManager.LocalClientId));
                State.Slots[0].Ready = true;

                // Clients that connected before this object spawned still need slots.
                var ids = NetworkManager.ConnectedClientsIds;
                for (int i = 0; i < ids.Count; i++)
                    if (ids[i] != NetworkManager.LocalClientId)
                        AssignSlotFor(ids[i]);

                BroadcastState();
            }
            else
            {
                RequestStateRpc();
            }
        }

        // No OnDestroy override: Unity's fake-null comparison makes a destroyed Instance
        // fail `Instance != null` checks, and after Shutdown the inert in-scene copy keeps
        // IsSpawned == false, which callers already test.

        private static LobbyState EmptyState()
        {
            var state = new LobbyState();
            for (int i = 0; i < MaxSlots; i++)
                state.Slots.Add(new LobbySlot());
            return state;
        }

        // ---------------- server-side slot management ----------------

        /// <summary>Approval helper: can this identity get a slot (fresh join or lobby rejoin)?</summary>
        public bool HasRoomFor(string clientGuid)
        {
            foreach (var slot in State.Slots)
            {
                if (slot.Kind == LobbySlotKind.Human && slot.ClientGuid == clientGuid) return true;
                if (slot.Kind == LobbySlotKind.Empty) return true;
            }
            return false;
        }

        /// <summary>Server: an approved client finished connecting — seat it (or re-seat it).</summary>
        public void HandleClientConnected(ulong clientId)
        {
            if (!IsServer || !IsSpawned) return;
            AssignSlotFor(clientId);
            BroadcastState();
        }

        public void HandleClientDisconnected(ulong clientId)
        {
            if (!IsServer || !IsSpawned) return;
            for (int i = 0; i < MaxSlots; i++)
            {
                var slot = State.Slots[i];
                if (slot.Kind == LobbySlotKind.Human && slot.ClientId == clientId)
                {
                    State.Slots[i] = new LobbySlot();
                    BroadcastState();
                    return;
                }
            }
        }

        private void AssignSlotFor(ulong clientId)
        {
            var payload = NetClientRegistry.Get(clientId);
            string guid = payload?.ClientGuid;

            for (int i = 0; i < MaxSlots; i++) // rejoin: the same identity reclaims its slot
            {
                var slot = State.Slots[i];
                if (slot.Kind == LobbySlotKind.Human && guid != null && slot.ClientGuid == guid)
                {
                    slot.ClientId = clientId;
                    slot.Ready = false;
                    return;
                }
            }
            for (int i = 0; i < MaxSlots; i++)
            {
                if (State.Slots[i].Kind != LobbySlotKind.Empty) continue;
                OccupySlot(i, clientId, payload);
                return;
            }
            // Approval guards capacity; this is a belt-and-braces fallback.
            NetworkManager.DisconnectClient(clientId, "The lobby is full");
        }

        private void OccupySlot(int index, ulong clientId, ConnectionPayload payload)
        {
            State.Slots[index] = new LobbySlot
            {
                Kind = LobbySlotKind.Human,
                ClientId = clientId,
                ClientGuid = payload?.ClientGuid ?? "host",
                Name = string.IsNullOrEmpty(payload?.PlayerName) ? "Player " + (index + 1) : payload.PlayerName,
                HeroId = DefaultHeroFor(index),
                Ready = false
            };
        }

        private static string DefaultHeroFor(int index)
        {
            var heroes = HeroDatabase.All;
            return heroes.Count == 0 ? null : heroes[index % heroes.Count].Id;
        }

        private static bool IsKnownHero(string heroId)
        {
            if (string.IsNullOrEmpty(heroId)) return false;
            try
            {
                HeroDatabase.Get(heroId);
                return true;
            }
            catch (KeyNotFoundException)
            {
                return false;
            }
        }

        private int SlotOfClient(ulong clientId)
        {
            for (int i = 0; i < MaxSlots; i++)
            {
                var slot = State.Slots[i];
                if (slot.Kind == LobbySlotKind.Human && slot.ClientId == clientId)
                    return i;
            }
            return -1;
        }

        // ---------------- client intents (the host invokes these too — they run locally) ----------------

        [Rpc(SendTo.Server)]
        public void SetHeroRpc(string heroId, RpcParams rpcParams = default)
        {
            int slot = SlotOfClient(rpcParams.Receive.SenderClientId);
            if (slot < 0 || !IsKnownHero(heroId)) return;
            State.Slots[slot].HeroId = heroId;
            BroadcastState();
        }

        [Rpc(SendTo.Server)]
        public void SetReadyRpc(bool ready, RpcParams rpcParams = default)
        {
            int slot = SlotOfClient(rpcParams.Receive.SenderClientId);
            if (slot < 0) return;
            if (State.Slots[slot].ClientId == State.HostClientId) return; // host is always ready
            State.Slots[slot].Ready = ready;
            BroadcastState();
        }

        [Rpc(SendTo.Server)]
        private void RequestStateRpc(RpcParams rpcParams = default) => BroadcastState();

        // ---------------- host-only controls (called directly by the host's UI) ----------------

        public bool HostAddBot(int slotIndex)
        {
            if (!IsServer || slotIndex is < 0 or >= MaxSlots) return false;
            if (State.Slots[slotIndex].Kind != LobbySlotKind.Empty) return false;
            State.Slots[slotIndex] = new LobbySlot
            {
                Kind = LobbySlotKind.Bot,
                Name = "Bot " + (slotIndex + 1),
                HeroId = DefaultHeroFor(slotIndex),
                Ready = true,
                BotKind = DefaultBotKind
            };
            BroadcastState();
            return true;
        }

        /// <summary>Remove a bot or kick a remote human. The host's own slot is untouchable.</summary>
        public bool HostRemoveSlot(int slotIndex)
        {
            if (!IsServer || slotIndex is < 0 or >= MaxSlots) return false;
            var slot = State.Slots[slotIndex];
            if (slot.Kind == LobbySlotKind.Empty) return false;
            if (slot.Kind == LobbySlotKind.Human && slot.ClientId == State.HostClientId) return false;

            if (slot.Kind == LobbySlotKind.Human)
            {
                // The disconnect callback frees the slot and rebroadcasts.
                NetworkManager.DisconnectClient(slot.ClientId, "Removed by the host");
            }
            else
            {
                State.Slots[slotIndex] = new LobbySlot();
                BroadcastState();
            }
            return true;
        }

        public void HostSetBotHero(int slotIndex, string heroId)
        {
            if (!IsServer || slotIndex is < 0 or >= MaxSlots) return;
            if (State.Slots[slotIndex].Kind != LobbySlotKind.Bot || !IsKnownHero(heroId)) return;
            State.Slots[slotIndex].HeroId = heroId;
            BroadcastState();
        }

        /// <summary>Host: validate and launch the match. Returns null on success, else a reason.</summary>
        public string HostStartGame()
        {
            if (!IsServer) return "Only the host can start the game";

            var picked = new List<LobbySlot>();
            foreach (var slot in State.Slots)
                if (slot.Kind != LobbySlotKind.Empty)
                    picked.Add(slot);

            if (picked.Count < 2) return "Need at least 2 players (add a bot?)";
            foreach (var slot in picked)
            {
                if (slot.Kind == LobbySlotKind.Human && slot.ClientId != State.HostClientId && !slot.Ready)
                    return slot.Name + " is not ready";
                if (string.IsNullOrEmpty(slot.HeroId))
                    return slot.Name + " has no hero";
            }

            // Lobby-level seed generation (config input, not engine randomness).
            ulong seed = unchecked((ulong)DateTime.UtcNow.Ticks);

            var players = new List<PlayerConfig>();
            var seats = new List<SeatAssignment>();
            for (int i = 0; i < picked.Count; i++)
            {
                var slot = picked[i];
                players.Add(new PlayerConfig { Name = slot.Name, HeroId = slot.HeroId });
                seats.Add(new SeatAssignment
                {
                    PlayerIndex = i,
                    Kind = slot.Kind,
                    ClientId = slot.ClientId,
                    ClientGuid = slot.ClientGuid,
                    PlayerName = slot.Name,
                    HeroId = slot.HeroId,
                    BotKind = slot.BotKind,
                    IsHostHuman = slot.Kind == LobbySlotKind.Human && slot.ClientId == State.HostClientId
                });
            }

            NetLobbyData.Config = ContentRegistry.StandardConfig(seed, players);
            NetLobbyData.Seats = seats;
            NetLobbyData.MatchRunning = true;

            var status = NetworkManager.SceneManager.LoadScene(NetLauncher.GameSceneName, LoadSceneMode.Single);
            if (status != SceneEventProgressStatus.Started)
            {
                NetLobbyData.ResetForLobby();
                return "Could not load the Game scene: " + status +
                       " (is Assets/Scenes/Game.unity in Build Settings?)";
            }
            return null;
        }

        // ---------------- replication ----------------

        private void BroadcastState()
        {
            if (!IsServer) return;
            StateChanged?.Invoke(State);
            StateRpc(Encoding.UTF8.GetBytes(EngineJson.Serialize(State)));
        }

        [Rpc(SendTo.NotServer)]
        private void StateRpc(byte[] stateJson)
        {
            State = EngineJson.Deserialize<LobbyState>(Encoding.UTF8.GetString(stateJson));
            StateChanged?.Invoke(State);
        }
    }
}
