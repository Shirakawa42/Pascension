---
name: project-map
description: Two-game repo map — how Pascension and the Shards of Infinity fan port share Pascension.Core/Net/Game, the full assembly dependency graph, where every subsystem lives, and every verification workflow (EngineVerify dotnet tests, golden wire files, Unity MCP on :8090, scene builders). Use at the start of any session, when deciding where new code belongs, or when unsure how to run/verify something.
---

# Project Map

One Unity 6000.3.7f1 repo (URP 2D, uGUI+TMP), TWO games:

- **Pascension** — original competitive deck-building race game. MTG-like rules engine (stack, APNAP priority, instants, triggers). 2-4 players, solo vs bots or NGO host-mode online (Unity Relay join codes as game IDs).
- **Shards of Infinity (SoI)** — personal fan re-implementation. Its OWN engine: no stack, single-pending-input pump. `Shards.Engine` depends ONLY on `Pascension.Core` — never on `Pascension.Engine`.

Shared between the games: `Pascension.Core` (sim substrate), `Pascension.Net` (game-agnostic host/sessions/Relay), the `Pascension.Game` presentation stack (SoI's screen reuses it wholesale), the art pipeline, CI + the self-updater.

## Assemblies (arrows = depends on)

| Assembly | Path | Depends on | Notes |
|---|---|---|---|
| `Pascension.Core` | `Assets/Scripts/Core/` | — | Pure C#, `noEngineReferences`. RNG (seeded PCG32), `EventLog`/`GameEvent`+`RedactFor`, decisions (+Context tag), `PendingInput`/`PendingSnap`, `PlayerAction` base + Pass/Submit/Concede + `ISafeDefaultAction`, `TargetRef`, `SubmitResult`, `WireJson` per-game registries, `IEngineAdapter`/`SnapshotBase`/`IBotAgent`/`IGameCodec`/`PlayerSpec`/`DefaultActions`, `Core/Update` (updater manifest + swap-script logic) |
| `Pascension.Engine` | `Assets/Scripts/Engine/` | Core | Pascension rules engine (see pascension-engine skill). `EngineJson` = WireJson registration scanning Engine+Core |
| `Pascension.Content` | `Assets/Scripts/Content/` | Core, Engine | Pascension cards/heroes/boss (see pascension-cards skill) |
| `Pascension.Bots` | `Assets/Scripts/Bots/` | Core, Engine, Content | HeuristicBot + Ollama LLM bot (see pascension-balance skill) |
| `Shards.Engine` | `Assets/Scripts/Shards/Engine/` | **Core only** | SoI engine (see shards-engine skill) |
| `Shards.Content` | `Assets/Scripts/Shards/Content/` | Core, Shards.Engine | SoI card DB (see shards-cards skill) |
| `Shards.Bots` | `Assets/Scripts/Shards/Bots/` | Core, Shards.Engine | `ShardsHeuristicBot` |
| `Pascension.Net` | `Assets/Scripts/Net/` | Core, Engine, Content, Bots, Shards.* | Game-agnostic sessions/GameHost/NGO/Relay (see networking skill) |
| `Pascension.Game` | `Assets/Scripts/Game/` | everything | Unity presentation (see ui-presentation skill) |
| `Pascension.Editor` (+`.NetSceneBuilder`) | `Assets/Scripts/Editor/` | varies | Art pipeline, CiBuild, scene builders (editor-only) |
| `Pascension.Engine.Tests` | `Assets/Tests/EngineTests/` | all runtime | Edit-mode NUnit; also compiled headless by EngineVerify |

Rules sources of truth: Pascension — `Assets/GDD.txt` + the decisions log in the **pascension-engine** skill. SoI — `Tools/ShardsData/rules-notes.md` (+ `cards-table.md`, generated). Original full design/implementation plan: `C:\Users\Shira\.claude\plans\this-is-an-empty-quirky-sunbeam.md`.

## Game selection plumbing

- `Net/Games/GameCatalog` + `IGameModule` (`PascensionModule`, `ShardsModule`) — a game = engine adapter (`PascensionEngineAdapter` / the Shards adapter) + codec (`ShardsCodec` for SoI) + bot factory + scene name.
- `GameId` + `DlcFlags` flow: `MatchSetup` → solo, and `LobbyState` → `NetLobbyData` → `HostMatchStarter` (host resolves by GameId, client by scene name — rejoin safe).
- Menu: NEW GAME/MULTIPLAYER → CHOOSE A GAME panel; lobby has host game-cycle + DLC checkboxes (replicated).
- `PUBLIC_RELEASE` define (CI env, see ci-release skill) gates `ShardsModule` registration and drops the GameShards scene from public builds.

## Verification workflows

- **Fastest (no Unity)**: `cd Tools/EngineVerify && dotnet test --nologo` — `Engine.Verify.csproj` compiles Core/Engine/Content/Bots/Shards/Net-Host + the full NUnit suite headless (mirrors the Unity asmdefs), currently ~129 tests. Keep it green.
- **Wire safety net**: `Tools/EngineVerify/golden/*.json` — seeded Pascension actions/events/snapshots/state-hash pinned via JToken.DeepEquals (`WireFormatGoldenTests`, which finds the repo root by walking up to `ProjectSettings/ProjectVersion.txt` / `CLAUDE.md`). Regenerate ONLY for deliberate wire changes: delete the files + re-run.
- **Unity-side tests**: Test Runner via MCP, or `Unity.exe -batchmode -runTests -testPlatform EditMode -projectPath .` when the editor is closed.
- **Exports as tests**: `dotnet test --filter Balance` (sims, see pascension-balance) · `--filter ExportShardsCardTable` (SoI table + art prompts) · `--filter ExportArtManifest` (Pascension art manifest).
- **UI**: Unity MCP play mode + screenshots (below). Scenes are built programmatically — menu `Pascension/Setup/Build All Scenes` (+ `Build Lobby Scene`). Scene-builder menu items must never open blocking editor modals (they hang headless runs).

## Unity MCP

- Configured in `.mcp.json`: HTTP streamable on port **8090** (not the default) → `http://127.0.0.1:8090/mcp`; server = uvx `mcpforunityserver==10.0.0`, in-editor bridge = `com.coplaydev.unity-mcp` package (Window > MCP for Unity).
- `execute_code` runs as a method body — no `using` directives, no local functions.
- Screenshots: Camera.Render→ReadPixels pattern (see scratchpad `shot.cs` style), NOT `ScreenCapture.CaptureScreenshot`. Aspect-ratio testing via ScreenSpaceCamera render trick.
- Under MCP the Game view is unfocused — `runInBackground=1` is set in ProjectSettings (also genuinely correct for a networked game; without it the player loop froze whenever the view lost focus and scene-load `Start()` never ran).
- For driving REAL UI events end-to-end (clicks, drags, mid-animation screenshots): raw JSON-RPC over MCP-HTTP works (scratchpad `mcp.py` pattern) — used to verify the SoI table live.

## Backlog (known gaps, not yet built)

- Audio (AudioManager + CC0 SFX/music pack) · help/tutorial overlay.
- `EffectiveHp` on `CardSnap`: snapshots carry base monster HP only — continuous-modifier-adjusted HP (e.g. Hex −2) can't display, and bots see base values (legal-action menus are still accurate). Engine-side field needed.
- SoI-over-Relay battery, SoI balance sims, SoI 4:3/21:9 screenshot pass (see shards-engine / networking skills).
- Live Ollama end-to-end test (see pascension-balance skill).
