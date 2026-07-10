# Net layer (M7) — implementation notes

Owner: net agent. Everything under `Assets/Scripts/Net/` except `Net/Host/` (read-only contracts),
plus `Assets/Scripts/Editor/NetSceneBuilder/`.

## File map

- `SessionProvider.cs` — **UI integration contract** (see below).
- `NetLauncher.cs` — public static entry points. PRIMARY (Relay): `HostAsync()` returns the
  join code = the GAME ID players share, `JoinAsync(code)`, `RejoinAsync()`, `Shutdown()`.
  Dev/LAN fallback (no UI): `StartHost(port)`, `StartClient(ip, port)`, `TryReconnect()`.
- `UgsGateway.cs` — the ONLY file that talks to Unity Gaming Services: anonymous auth (one
  profile per MPPM virtual player via `ClientIdentity.AuthProfile`) + Relay allocations.
  Every SDK exception is normalized to a user-readable `UgsException` (raw detail is logged).
  PREREQUISITE: the project must be linked to Unity Cloud with Relay enabled, else HostAsync
  fails with a friendly error.
- `NetEvents.cs` — static client-side notifications (LocalClientDisconnected → the Game
  scene's connection-lost overlay). Subscribers MUST unsubscribe in OnDestroy.
- `NetBootstrap.cs` — code-built DontDestroyOnLoad NetworkManager + UnityTransport, connection
  approval, prefab registration, creates `HostMatchStarter` when the Game scene loads while NGO runs.
- `ClientIdentity.cs` — persistent client GUID + player name (PlayerPrefs).
- `ConnectionPayload.cs`, `NetClientRegistry.cs` — approval payload + clientId→identity map.
- `NetJson.cs` — snapshot/pending wire format (see "wire format" below).
- `Lobby/LobbySlotKind|LobbySlot|LobbyState.cs` — replicated lobby DTOs.
- `Lobby/LobbyNetBehaviour.cs` — replicated lobby (in-scene NetworkObject in Lobby.unity).
- Lobby UI lives in the GAME assembly (`Game/UI/LobbyScreen.cs`, house TMP style; asmdef
  direction forbids it here). `SceneConstruction.PopulateLobbyScene()` authors it; the old
  `Lobby/LobbyUiController.cs` (legacy uGUI) was deleted.
- `Match/GameNetBridge.cs` — the host↔client RPC pipe (spawned from `Resources/Net/GameNetBridge`).
- `Match/RemoteSeat.cs` — IHostSeat forwarding to a client via the bridge.
- `Match/NetworkSession.cs` — client-side ISession with seq-gap detection → resync.
- `Match/HostMatchStarter.cs` — builds GameHost + seats on the host / NetworkSession on clients.
- `Match/ReconnectService.cs` — static router NetBootstrap→active HostMatchStarter.
- `Match/SeatAssignment.cs`, `Match/NetLobbyData.cs` — lobby→game handoff (host statics).
- `Editor/NetSceneBuilder/NetSceneBuilder.cs` — `Pascension/Setup/Build Lobby Scene` menu item.

## UI integration contract (SessionProvider)

`Pascension.Net.SessionProvider.Current` is set (host **and** client) before the Game scene's
`Start()` callbacks run (HostMatchStarter is created from `SceneManager.sceneLoaded`, which fires
after `Awake` but before `Start`). If the UI agent's GameBootstrap does not already use it, the
3-line integration is:

```csharp
ISession session = SessionProvider.Current;         // networked play (set by HostMatchStarter)
if (session == null)
    session = BuildSoloHostAndLocalSession();       // existing solo path, unchanged
```

**GameBootstrap must read it in `Start()` (or later), not `Awake()`.** Solo play never sets it.

## Flow summary

- Host: `NetLauncher.StartHost` → approval approves the host's local client → NGO loads `Lobby` →
  in-scene `LobbyNet` spawns → host takes slot 0 (always ready). Clients `StartClient(ip)` →
  approval (identity + capacity) → NGO scene-syncs them into Lobby → slot assigned.
- Lobby state replication: server mutates plain `LobbyState`, rebroadcasts full JSON via
  `StateRpc` (SendTo.NotServer) after every change; clients pull once on spawn. Deliberately
  no NetworkList/NetworkVariable — avoids a Unity.Collections asmdef dependency and FixedString
  size limits; max 4 slots so the payload is trivial.
- Start: host validates (≥2 seats, humans ready, heroes picked), builds
  `ContentRegistry.StandardConfig(seed, players)`, stores `NetLobbyData.Config/Seats`
  (slot→clientId/guid mapping), sets `MatchRunning`, `SceneManager.LoadScene("Game")` (NGO).
- Game scene load (both sides): NetBootstrap creates HostMatchStarter.
  - Host: GameHost + LocalSession(host human) + RemoteSeat(remote humans) + BotSeat(HeuristicBot,
    seed = config.Seed ^ f(playerIndex)), spawns GameNetBridge (Configure BEFORE Spawn),
    `host.Start()`. Ticks host + bots every frame.
  - Client: NetworkSession; when its bridge replica spawns it sends `RequestResyncRpc` and the
    host answers with `SeatAssignedRpc` (LocalPlayerIndex) + `SnapshotRpc` + `InputRequestedRpc`
    (if it holds the pending input). This pull covers join, reconnect, AND any RPC that was
    dropped/deferred during scene transitions — pushes are best-effort, the pull is the guarantee.
- Sequence tracking: batches carry `seqStart` (= first event's `Seq`); NetworkSession keeps
  `expectedSeq` (from `ClientSnapshot.EventSeq` or last batch); gap → `RequestResyncRpc`;
  overlapping duplicates are trimmed by `Seq`.
- Disconnect mid-game: the match PAUSES (`GameHost.SetPaused` — submits rejected, bots hold,
  timers frozen; `HostMatchStarter.RecomputePause` orchestrates and pushes `PauseInfo` to every
  session: `LocalSession.RaisePause` for the host UI, `GameNetBridge.BroadcastPause` for clients).
  Everyone sees the pause overlay with the game ID; the host can kick → `KickSeatToBot` replaces
  the seat with a HeuristicBot via `GameHost.ReplaceSeat` (re-routes any pending input) and flips
  the `NetLobbyData` seat to Bot, which locks the kicked GUID out at approval
  (`FindSeatByGuid` matches Human seats only). Approval mid-game admits only GUIDs in
  `NetLobbyData.Seats`. On reconnect the RemoteSeat is re-pointed at the new clientId and
  resynced (push + client pull; resync also carries rules + pause state), then unpaused.
  `NetLauncher.RejoinAsync()` re-joins the last game ID with the same GUID.
  Transport `DisconnectTimeoutMS=10s` so disconnects are detected promptly.

## Wire format deviation (important)

The brief said "EngineJson.Serialize<T> for ClientSnapshot/PendingSnap", but
`EngineJson.Serialize<T>` uses plain Newtonsoft settings — it CANNOT round-trip
`PendingSnap.LegalActions` (`List<PlayerAction>`, polymorphic, abstract base) and would throw on
deserialize. `NetJson` (Net-side, doesn't touch Engine) wraps the same settings and delegates
every `PlayerAction` to `EngineJson.SerializeAction/DeserializeAction` (same whitelist, "t"
discriminators, no TypeNameHandling) and mirrors the engine's `{"k","a","b"}` TargetRef encoding
(also handling `TargetRef?` in `DecisionOption.Target`, which the engine's own generic converter
may not match for nullables). Actions and events on the wire use EngineJson directly, as specified.

## NGO API choices (2.13)

- Universal `[Rpc]` attributes only: `[Rpc(SendTo.Server)]` for client→host (default
  RequireOwnership=false — required, clients don't own the bridge) and
  `[Rpc(SendTo.SpecifiedInParams)]` + `RpcTarget.Single(clientId, RpcTargetUse.Temp)` for
  targeted host→client sends. Sender identity from `rpcParams.Receive.SenderClientId`.
  Legacy `[ServerRpc]/[ClientRpc]` deliberately not used — the 2.x targeted-send pattern is the
  documented replacement and avoids owner-only restrictions.
- NetworkManager/UnityTransport are created from code (`NetBootstrap.EnsureInitialized`) — no
  scene-authored NetworkManager anywhere. `ConnectionApproval=true`, `CreatePlayerObject=false`
  (no player objects at all; the bridge + lobby objects are server-owned).
- `GameNetBridge` must be a real prefab asset (GlobalObjectIdHash is baked at import), so
  NetSceneBuilder authors `Assets/Resources/Net/GameNetBridge.prefab` and NetBootstrap registers
  it via `NetworkManager.AddNetworkPrefab` before StartHost/StartClient. It is never regenerated
  once created (hash stability). `LobbyNetBehaviour` is in-scene placed instead (soft-sync).
- RPC payloads are `byte[]` UTF8 JSON, reliable delivery (default). NGO 2.x reliable sends are
  dynamically queued, so large snapshots are fine on LAN; `UnityTransport.MaxPayloadSize` only
  gates unreliable traffic and was left untouched.

## What NetSceneBuilder authors

Lobby.unity contains exactly: `Main Camera` (ortho, solid color), `EventSystem`
(+`InputSystemUIInputModule` — project is Input System-only, `activeInputHandler: 1`),
`LobbyNet` (NetworkObject + LobbyNetBehaviour, in-scene placed), `LobbyUI` (LobbyUiController;
all uGUI is generated at runtime in code). Plus the bridge prefab and build-settings entries
(idempotent append; existing entries untouched). Re-running the menu item asks before rebuilding
the scene and never rebuilds the prefab.

## asmdef changes (heads-up for the orchestrator)

- `Pascension.Net.asmdef`: added `"UnityEngine.UI"` (LobbyUiController uses uGUI). Additive only.
- New `Pascension.Editor.NetSceneBuilder.asmdef` (editor-only): references Pascension.Net,
  Unity.Netcode.Runtime, Unity.InputSystem, UnityEngine.UI. Needed because the existing
  Pascension.Editor asmdef doesn't reference Net/NGO and I don't own it.

## MPPM test checklist (com.unity.multiplayer.playmode 2.0.2 is installed)

1. Let the editor finish resolving packages (netcode/transport were added to manifest.json but had
   NOT been resolved when this code was written — see risks), then run
   `Pascension → Setup → Build Lobby Scene`. Verify: `Assets/Scenes/Lobby.unity`,
   `Assets/Resources/Net/GameNetBridge.prefab`, Lobby (+Game if present) in Build Settings.
2. Window → Multiplayer Play Mode: activate 1–3 virtual players.
3. Main editor: open Lobby scene, enter Play, click **Host Game** (defaults 127.0.0.1:7777).
4. Each virtual player (they enter Play automatically): click **Join Game** (defaults are
   prefilled — no typing needed). Verify each gets a slot with a unique name/identity
   (ClientIdentity salts the PlayerPrefs key per virtual-player project path, since MPPM players
   share the editor PlayerPrefs registry).
5. Lobby: cycle heroes (own slot only; host can cycle bot heroes), toggle Ready on clients,
   host Add Bot / Remove / Kick, then Start (needs ≥2 seats, all remote humans ready).
6. Game: play actions from host and client seats; verify bots act; verify a client's illegal
   action shows a rejection; kill a client and verify the pause overlay appears everywhere.
7. Disconnect test: deactivate a virtual player mid-game → host log shows disconnect, its turns
   the pause overlay on all peers. Reactivate → it boots into Lobby scene → JOIN by game ID →
   approval maps its GUID to the old seat → Game scene syncs → snapshot + pending input restored.
8. Solo regression: start a solo game from the normal path — no NetworkManager must be created.

## Open risks / TODOs

- **Could not compile-verify**: no Unity MCP this session AND the netcode/transport/MPPM packages
  were still unresolved (absent from packages-lock.json and Library). Code is written strictly
  against documented NGO 2.13 APIs; the riskiest spots if 2.13 drifted: `RpcTarget.Single(...,
  RpcTargetUse.Temp)` signature, `NetworkManager.DisconnectClient(id, reason)`, approval
  request/response field names, `NetworkManager.AddNetworkPrefab` before Start*.
- Legacy uGUI `InputField` text entry under Input System-only mode is the standard setup but has
  historical quirks; both fields are prefilled with working defaults (127.0.0.1:7777) so MPPM
  testing works even if typing misbehaves. Swap to TMP_InputField once TMP essentials exist.
- Host broadcasts a full snapshot to every remote seat after every submit (mirrors GameHost's
  LocalSession behavior). Correct but chatty (~10–100KB JSON per step per client). Fine on LAN;
  optimization: send snapshots only on request/turn boundaries and rely on events.
- Match seed is `DateTime.UtcNow.Ticks` at start (config input, not engine randomness —
  determinism inside the engine is unaffected). Store it if replays need it.
- `Assets/Scenes/Game.unity` did not exist yet; the UI agent must create it and ensure it is in
  Build Settings (NetSceneBuilder auto-adds it when it exists; rerun the menu item, it's idempotent).
- Lobby has no "return to lobby after game over" flow (Leave → Shutdown works). Duplicate hero
  picks are allowed by design. Bot kind is recorded ("heuristic") but only HeuristicBot is wired.
- If a client's socket death hasn't been noticed by the host yet (no disconnect callback), a
  reconnect with the same GUID is rejected as "already connected" until UTP times the old
  connection out (~seconds). Retry handles it.
- Engine nit (not mine to fix): `EngineJson`'s `TargetRefConverter` is `JsonConverter<TargetRef>`;
  nullable `TargetRef?` members (e.g. `DecisionOption.Target`) may bypass it on read. NetJson's
  converter handles both; if `DecisionRequest` ever needs to cross the wire via EngineJson
  directly, verify that path.
