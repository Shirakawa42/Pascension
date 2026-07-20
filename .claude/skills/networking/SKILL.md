---
name: networking
description: Game-agnostic networking for both games — GameHost/seats/sessions, the NGO 2.x GameNetBridge, Unity Relay game IDs, lobby, pause/rejoin/kick, resync-pull guarantees, the version gate, the AutoClientDriver test battery, and hard-won NGO gotchas (GlobalObjectIdHash, scene-sync races). Use before touching Assets/Scripts/Net, debugging multiplayer or lobby issues, or wiring a game screen to a session.
---

# Networking (game-agnostic — serves Pascension AND Shards of Infinity)

The Net layer is game-agnostic: `GameHost(IEngineAdapter)`, seats/sessions on `SnapshotBase`, `GameNetBridge` encodes via `IGameCodec`, `NetWire` for flat DTOs. Game selection plumbing (GameCatalog/IGameModule/GameId+DlcFlags) is described in the project-map skill.

## File map (`Assets/Scripts/Net/`)
- `SessionProvider.cs` — **the UI integration contract** (below).
- `NetLauncher.cs` — public static entry points. PRIMARY (Relay): `HostAsync()` returns the join code = the GAME ID players share, `JoinAsync(code)`, `RejoinAsync()`, `Shutdown()`. Dev/LAN fallback: `StartHost(port)`, `StartClient(ip, port)`, `TryReconnect()`.
- `UgsGateway.cs` — **the ONLY file that talks to Unity Gaming Services**: anonymous auth (one profile per MPPM virtual player via `ClientIdentity.AuthProfile` — anonymous sign-ins collide otherwise) + Relay allocations. Every SDK exception normalized to a user-readable `UgsException` (raw detail logged). Prerequisite: project linked to Unity Cloud with Relay enabled (done 2026-07-10, org lucas-2684634), else HostAsync fails with a friendly error.
- `NetEvents.cs` — static client-side notifications (`LocalClientDisconnected` → the Game scene's CONNECTION LOST overlay with REJOIN/LEAVE). Subscribers MUST unsubscribe in OnDestroy.
- `NetBootstrap.cs` — code-built DontDestroyOnLoad NetworkManager + UnityTransport, connection approval (incl. the version gate, below), prefab registration, creates `HostMatchStarter` when the Game scene loads while NGO runs.
- `ClientIdentity.cs` — persistent client GUID + player name (PlayerPrefs; key salted per MPPM virtual-player project path).
- `ConnectionPayload.cs`, `NetClientRegistry.cs` — approval payload + clientId→identity map.
- `NetJson.cs` — snapshot/pending wire format (see "wire format" below).
- `Lobby/` — `LobbySlotKind|LobbySlot|LobbyState` DTOs + `LobbyNetBehaviour` (in-scene NetworkObject in Lobby.unity). Lobby UI lives in the GAME assembly (`Game/UI/LobbyScreen.cs`, TMP house style — asmdef direction forbids it here).
- `Match/GameNetBridge.cs` — the host↔client RPC pipe (spawned from `Resources/Net/GameNetBridge`). Also relays the card-hover broadcast (see ui-presentation).
- `Match/RemoteSeat.cs` — IHostSeat forwarding to a client via the bridge · `Match/NetworkSession.cs` — client-side ISession with seq-gap detection → resync · `Match/HostMatchStarter.cs` — builds GameHost + seats on the host / NetworkSession on clients · `Match/ReconnectService.cs` — static router NetBootstrap→active HostMatchStarter · `Match/SeatAssignment.cs`, `Match/NetLobbyData.cs` — lobby→game handoff (host statics).
- `Net/Host/` — GameHost, seat interfaces, AsyncBotSeat (compiled by EngineVerify).
- `Net/AutoClientDriver.cs` — the test battery client (below).
- `Editor/NetSceneBuilder/NetSceneBuilder.cs` — `Pascension/Setup/Build Lobby Scene`. Lobby.unity contains exactly: Main Camera (ortho, solid color), EventSystem + InputSystemUIInputModule (project is Input System-only), in-scene `LobbyNet`, lobby UI root. Build-settings entries are appended idempotently (existing entries untouched — SceneConstruction likewise no longer clobbers build settings, so Lobby.unity survives `Build All Scenes`); re-running asks before rebuilding the scene and NEVER regenerates the bridge prefab.
- Lobby UI (`Game/UI/LobbyScreen.cs`): connect panel (name + game ID + HOST/JOIN, all async with friendly `UgsException` errors) + lobby panel (GAME ID header + copy, 4 slots, hero cycle — own slot only, host may cycle bot heroes; the cycle appends RANDOM (`CharacterPick.RandomId`) and skips heroes other slots hold — ready, add/remove bot, start).

## SessionProvider contract (how a game screen goes online)
`Pascension.Net.SessionProvider.Current` is set (host AND client) before the Game scene's `Start()` callbacks run (HostMatchStarter is created from `SceneManager.sceneLoaded`, which fires after `Awake` but before `Start`). GameBootstrap/SoiBootstrap read it in `Start()` — **never `Awake()`** — and build the solo host only when it's null. The host's `GameHost.Start()` is deferred to HostMatchStarter's first `Update` so UI binding always precedes the first broadcast.

## Flow summary
- Host: `HostAsync` → approval approves the host's local client → NGO loads `Lobby` → in-scene `LobbyNet` spawns → host takes slot 0 (always ready). Clients join → approval (identity + capacity + version) → NGO scene-syncs them into Lobby → slot assigned.
- Lobby replication: server mutates plain `LobbyState`, rebroadcasts full JSON via `StateRpc` (SendTo.NotServer) after every change; clients pull once on spawn. Deliberately no NetworkList/NetworkVariable (avoids Unity.Collections dependency + FixedString limits; ≤4 slots, trivial payload).
- Start: host validates (≥2 seats, humans ready, heroes picked, no duplicate heroes), **shuffles seat order** (seeded `DeterministicRng(seed, seq 131)` — random first player; players+seats built from the same shuffled list so identity travels with the seat), resolves RANDOM picks via `Pascension.Core.CharacterPick.ResolveRandoms` (distinct ids, seeded), builds the game config, stores `NetLobbyData.Config/Seats`, sets `MatchRunning`, loads "Game"/"GameShards" via NGO.
- Game scene load: host builds GameHost + LocalSession + RemoteSeats + BotSeats (seed = config.Seed ^ f(playerIndex)), spawns GameNetBridge (Configure BEFORE Spawn), and ticks host + bot seats every frame. Client: NetworkSession; when its bridge replica spawns it sends `RequestResyncRpc` → host answers `SeatAssignedRpc` + `SnapshotRpc` + `InputRequestedRpc`. **Pushes are best-effort; the resync pull is the delivery guarantee** (covers join, reconnect, and RPCs dropped/deferred during scene transitions).
- Sequence tracking: batches carry `seqStart` (= first event's `Seq`); NetworkSession keeps `expectedSeq` (from `ClientSnapshot.EventSeq` or the last batch); gap → `RequestResyncRpc`; overlapping duplicates trimmed by `Seq`.
- **Rules handoff**: host sends `GameRules` in resync (`JsonConvert.PopulateObject` into `NetworkSession.Rules`, in place); GameBootstrap binds host→`NetLobbyData.Config.Rules`, client→session rules.

## Disconnect policy = PAUSE (locked decision — see pascension-engine decisions log)
- `GameHost.Paused` (pure C#, headless-tested — submits rejected, bots hold, timers frozen) driven by `HostMatchStarter.RecomputePause`; everyone gets a `PauseInfo` overlay (waiting names + game ID + host-only REPLACE WITH BOT).
- Kick → `GameHost.ReplaceSeat` (re-routes pending input to the bot, fresh snapshot, no stale event flood) + NetLobbyData seat flips to Bot → kicked GUID locked out at approval (`FindSeatByGuid` matches Human seats only; mid-game approval admits only GUIDs in `NetLobbyData.Seats`).
- Rejoin: same game ID + persistent GUID reclaims the seat — RemoteSeat re-pointed at the new clientId, resync carries seat/rules/snapshot/pending/pause, then unpause. `RejoinAsync()` re-joins the last game ID with the same GUID.
- Client-side host loss → CONNECTION LOST overlay (REJOIN / LEAVE) via `NetEvents.LocalClientDisconnected`. Host loss = game over (no host migration).
- Transport `DisconnectTimeoutMS=10s`. If the host hasn't noticed a client's socket death yet, a same-GUID reconnect is rejected as "already connected" until UTP times out (~seconds) — retry handles it.

## ⚠ Session teardown rule
**Every path that leaves an online context MUST call `NetLauncher.Shutdown()`** (the SoI game-over button does; `MainMenu.Start` has a defense-in-depth guard when `NetworkManager.IsListening`). A leaked listening NGO + stale `NetLobbyData` used to rebuild the OLD online match on the next solo start (HostMatchStarter's 10 s scene-sync failsafe = the "slow load", old opponents re-seated).

## Version gate
`ConnectionPayload.GameVersion` → `NetBootstrap.ApproveConnection` rejects ANY mismatch BEFORE capacity checks (`"Update required: …"` / `"Host update required: …"` — English wire strings; LobbyScreen localizes by prefix). Editor/MPPM peers all report "1.0" so dev flows are unaffected; pre-gate clients (null version → "0") get the update message. The update contract itself (Application.version == manifest) lives in ci-release.

## Wire format deviation (important)
`EngineJson.Serialize<T>` uses plain Newtonsoft settings — it CANNOT round-trip `PendingSnap.LegalActions` (`List<PlayerAction>`, polymorphic, abstract base). `NetJson` (Net-side, doesn't touch Engine) wraps the same settings and delegates every `PlayerAction` to `EngineJson.SerializeAction/DeserializeAction` (same whitelist, `"t"` discriminators, no TypeNameHandling) and mirrors the engine's `{"k","a","b"}` TargetRef encoding — also handling `TargetRef?` in `DecisionOption.Target` (the engine's generic `JsonConverter<TargetRef>` may not match nullables; if `DecisionRequest` ever crosses the wire via EngineJson directly, verify that path). Actions and events on the wire use EngineJson directly.

## NGO API choices (2.13)
- Universal `[Rpc]` attributes only: `[Rpc(SendTo.Server)]` for client→host (default RequireOwnership=false — required, clients don't own the bridge) and `[Rpc(SendTo.SpecifiedInParams)]` + `RpcTarget.Single(clientId, RpcTargetUse.Temp)` for targeted host→client. Sender identity from `rpcParams.Receive.SenderClientId`. Legacy `[ServerRpc]/[ClientRpc]` deliberately unused.
- NetworkManager/UnityTransport created from code (`NetBootstrap.EnsureInitialized`) — no scene-authored NetworkManager. `ConnectionApproval=true`, `CreatePlayerObject=false` (no player objects; bridge + lobby objects are server-owned).
- `GameNetBridge` must be a real prefab asset (GlobalObjectIdHash baked at import): NetSceneBuilder authors `Assets/Resources/Net/GameNetBridge.prefab`, NetBootstrap registers it via `AddNetworkPrefab` before Start*. Never regenerated once created (hash stability). `LobbyNetBehaviour` is in-scene placed (soft-sync).
- RPC payloads are `byte[]` UTF8 JSON, reliable delivery. NGO 2.x reliable sends are dynamically queued so large snapshots are fine; `MaxPayloadSize` only gates unreliable traffic.

## ⚠ Two NGO gotchas found by the Relay battery (invisible to host-only testing)
1. **Script-authored scenes never run `NetworkObject.OnValidate`**, so the in-scene LobbyNet shipped with GlobalObjectIdHash 0 → client scene-sync NRE. NetSceneBuilder now saves → invokes OnValidate via reflection → saves again, and asserts the hash.
2. **Spawning GameNetBridge during the Game-scene load** (from the `sceneLoaded` hook) races NGO's open scene event — synchronizing clients never receive the spawn and its RPCs die as deferred messages. HostMatchStarter waits for `SceneManager.OnLoadEventCompleted` (10 s failsafe) before bridge spawn + `GameHost.Start()`.

## Test battery (FULLY VERIFIED over real Relay, 2026-07-10 — Pascension)
`Net/AutoClientDriver.cs`: launch a standalone build with `-joincode ABC123` → it joins, auto-readies, auto-plays; real disconnects driven by process kill. Verified: join by game ID, lobby replication, full multi-round game with a real remote seat, kill → pause overlay ≤10 s with game ID, rejoin (same GUID reclaims seat, resync, unpause), kick → bot takeover, kicked GUID rejected with "A game is already in progress" delivered to the kicked client.

MPPM (com.unity.multiplayer.playmode): activate virtual players, host in the main editor, join from the others (each gets its own UGS auth profile + salted identity); test lobby ops, mid-game disconnect → pause everywhere, reactivate → rejoin by game ID, solo regression (no NetworkManager created on the solo path).

## Open items
- **SoI-over-Relay battery not yet run** (host lobby → AutoClientDriver `-joincode`, DLC flags replicate, remote seat plays). Net layer is game-agnostic so risk is low, but it is UNVERIFIED for SoI.
- Host broadcasts a full snapshot to every remote seat after every submit — correct but chatty (~10-100 KB JSON per step per client). Fine over Relay/LAN; optimization: snapshots on request/turn boundaries only.
- Match seed is `DateTime.UtcNow.Ticks` at start (config input, not engine randomness — engine determinism unaffected). Store it if replays need it.
- No "return to lobby after game over" flow (Leave → Shutdown works). Bot kind recorded ("heuristic") but only HeuristicBot is wired for kick-replacement.
- Duplicate hero picks are REJECTED since 2026-07-20 (user request; reversed the earlier allowed-by-design call): `SetHeroRpc`/`HostSetBotHero`/`RevalidateSlots` guard via `HeroTakenByOther`, defaults pick the first free hero, `HostStartGame` re-validates. The RANDOM sentinel is exempt (any number of slots may hold it).
