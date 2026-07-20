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

        private string DefaultHeroFor(int index)
        {
            // First roster hero nobody else holds — defaults must respect the
            // no-duplicate rule too (a joiner's index-based default could collide
            // with a hero the host already cycled to).
            foreach (var character in GameCatalog.Get(State.GameId).CharactersFor(State.DlcFlags))
                if (!HeroTakenByOther(index, character.Id))
                    return character.Id;
            return Pascension.Core.CharacterPick.RandomId; // roster < lobby size — resolves at start
        }

        private bool IsKnownHero(string heroId)
        {
            if (string.IsNullOrEmpty(heroId)) return false;
            foreach (var character in GameCatalog.Get(State.GameId).CharactersFor(State.DlcFlags))
                if (character.Id == heroId)
                    return true;
            return false;
        }

        /// <summary>A legal pick = a roster hero or the RANDOM sentinel (resolved at start).</summary>
        private bool IsValidPick(string heroId) =>
            Pascension.Core.CharacterPick.IsRandom(heroId) || IsKnownHero(heroId);

        /// <summary>No-duplicate rule: does another occupied slot hold this hero?
        /// The RANDOM sentinel never collides (any number of slots may pick it).</summary>
        private bool HeroTakenByOther(int slotIndex, string heroId)
        {
            if (Pascension.Core.CharacterPick.IsRandom(heroId)) return false;
            for (int i = 0; i < State.Slots.Count; i++)
                if (i != slotIndex && State.Slots[i].Kind != LobbySlotKind.Empty &&
                    State.Slots[i].HeroId == heroId)
                    return true;
            return false;
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
            if (slot < 0) return;
            if (!IsValidPick(heroId) || HeroTakenByOther(slot, heroId))
            {
                // Lost a pick race: the sender computed this hero from a stale replica.
                // Rebroadcast so its next click works from the authoritative state.
                BroadcastState();
                return;
            }
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
            if (State.Slots[slotIndex].Kind != LobbySlotKind.Bot ||
                !IsValidPick(heroId) || HeroTakenByOther(slotIndex, heroId)) return;
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
            for (int i = 0; i < picked.Count; i++)
            {
                var slot = picked[i];
                if (slot.Kind == LobbySlotKind.Human && slot.ClientId != State.HostClientId && !slot.Ready)
                    return slot.Name + " is not ready";
                if (string.IsNullOrEmpty(slot.HeroId))
                    return slot.Name + " has no hero";
                // Defensive — the pick paths already reject duplicates.
                for (int j = 0; j < i; j++)
                    if (!Pascension.Core.CharacterPick.IsRandom(slot.HeroId) &&
                        picked[j].HeroId == slot.HeroId)
                        return slot.Name + " and " + picked[j].Name + " have the same hero";
            }

            var module = GameCatalog.Get(State.GameId);
            if (picked.Count < module.MinPlayers)
                return module.DisplayName + " needs at least " + module.MinPlayers + " players";
            if (picked.Count > module.MaxPlayers)
                return module.DisplayName + " supports at most " + module.MaxPlayers + " players";

            // Lobby-level seed generation (config input, not engine randomness).
            ulong seed = unchecked((ulong)DateTime.UtcNow.Ticks);

            // Random first player: the engine always gives seat 0 the first turn, so
            // shuffle WHO occupies each seat — players and seats are built from the
            // same shuffled list, so identity (ClientId/Guid) travels with its seat
            // and the staggered-start compensation stays keyed to turn position.
            new DeterministicRng(seed, sequence: 131UL).Shuffle(picked);

            // Resolve RANDOM picks to concrete, distinct heroes (seeded — the resolved
            // ids land in both the config and the seat assignments).
            var pickIds = new List<string>();
            foreach (var slot in picked) pickIds.Add(slot.HeroId);
            var roster = new List<string>();
            foreach (var character in module.CharactersFor(State.DlcFlags)) roster.Add(character.Id);
            var heroIds = Pascension.Core.CharacterPick.ResolveRandoms(pickIds, roster, seed);

            var players = new List<Pascension.Core.PlayerSpec>();
            var seats = new List<SeatAssignment>();
            for (int i = 0; i < picked.Count; i++)
            {
                var slot = picked[i];
                players.Add(new Pascension.Core.PlayerSpec
                {
                    Name = slot.Name,
                    CharacterId = heroIds[i],
                    IsBot = slot.Kind == LobbySlotKind.Bot,
                    BotKind = slot.BotKind
                });
                seats.Add(new SeatAssignment
                {
                    PlayerIndex = i,
                    Kind = slot.Kind,
                    ClientId = slot.ClientId,
                    ClientGuid = slot.ClientGuid,
                    PlayerName = slot.Name,
                    HeroId = heroIds[i],
                    BotKind = slot.BotKind,
                    IsHostHuman = slot.Kind == LobbySlotKind.Human && slot.ClientId == State.HostClientId
                });
            }

            NetLobbyData.GameId = State.GameId;
            NetLobbyData.DlcFlags = State.DlcFlags;
            NetLobbyData.Config = module.BuildConfig(seed, players, State.DlcFlags);
            NetLobbyData.Seats = seats;
            NetLobbyData.MatchRunning = true;

            var status = NetworkManager.SceneManager.LoadScene(module.GameSceneName, LoadSceneMode.Single);
            if (status != SceneEventProgressStatus.Started)
            {
                NetLobbyData.ResetForLobby();
                return "Could not load the game scene: " + status +
                       " (is " + module.GameSceneName + ".unity in Build Settings?)";
            }
            return null;
        }

        /// <summary>Host picks which game this lobby starts. Character picks that do not
        /// exist in the new game reset to defaults; non-host humans get un-readied.</summary>
        public void HostSetGame(string gameId)
        {
            if (!IsServer || State.GameId == gameId) return;
            State.GameId = gameId;
            State.DlcFlags = 0;
            RevalidateSlots();
            BroadcastState();
        }

        public void HostSetDlc(int dlcFlags)
        {
            if (!IsServer || State.DlcFlags == dlcFlags) return;
            State.DlcFlags = dlcFlags;
            RevalidateSlots();
            BroadcastState();
        }

        private void RevalidateSlots()
        {
            for (int i = 0; i < State.Slots.Count; i++)
            {
                var slot = State.Slots[i];
                if (slot.Kind == LobbySlotKind.Empty) continue;
                if (!IsValidPick(slot.HeroId) || HeroTakenByOther(i, slot.HeroId))
                    slot.HeroId = DefaultHeroFor(i);
                if (slot.Kind == LobbySlotKind.Human && slot.ClientId != State.HostClientId)
                    slot.Ready = false;
            }
        }

        // ---------------- replication ----------------

        private void BroadcastState()
        {
            if (!IsServer) return;
            StateChanged?.Invoke(State);
            StateRpc(NetWire.Encode(State));
        }

        [Rpc(SendTo.NotServer)]
        private void StateRpc(byte[] stateJson)
        {
            State = NetWire.Decode<LobbyState>(stateJson);
            StateChanged?.Invoke(State);
        }
    }
}
