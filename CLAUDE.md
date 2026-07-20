# Pascension — two games, one core

Unity 6000.3.7f1 repo (URP 2D, uGUI+TMP) holding TWO games:
- **Pascension** — competitive deck-building race game, MTG-like stack engine (APNAP priority, instants, triggers). 2-4 players; solo vs bots or NGO host-mode online (Unity Relay join codes as game IDs). Buy cards Ascension-style, race a 50-step board, level a hero 1→10, burst the boss (The Gatekeeper, 20 HP) on step 50.
- **Shards of Infinity** — personal fan re-implementation with its OWN engine (no stack; depends only on `Pascension.Core`). `PUBLIC_RELEASE` builds strip it.

Shared: `Pascension.Core` (RNG/events/decisions/wire), `Pascension.Net` (game-agnostic host/sessions), the `Pascension.Game` presentation stack, the art pipeline, CI + self-updater.
Rules sources of truth: Pascension — `Assets/GDD.txt` + the decisions log in the **pascension-engine** skill · SoI — `Tools/ShardsData/rules-notes.md`.

## Skills — read the matching skill BEFORE working in its area

| Skill | Read when |
|---|---|
| `project-map` | session start · "where does X live" · choosing an assembly · how to run/verify anything |
| `pascension-engine` | Assets/Scripts/Engine · card effects · Pascension rules questions (holds the decisions log) |
| `pascension-cards` | ⚠ MANDATORY registry — any Pascension card/hero/boss add, change, or lookup |
| `pascension-balance` | GameRules tuning · headless sims · HeuristicBot/Ollama bot work |
| `shards-engine` | Assets/Scripts/Shards · SoI flow/rules debugging · the pump gotchas |
| `shards-cards` | ⚠ MANDATORY registry — any SoI card/character/relic/destiny/Ingeminex change |
| `art-pipeline` | generating any art · writing ArtPrompts · ComfyUI · CardArtIndex |
| `networking` | Assets/Scripts/Net · lobby/Relay/reconnect · multiplayer debugging |
| `ui-presentation` | Assets/Scripts/Game (View/Presentation/UI/Soi) · animations · layout · hand/drag bugs |
| `ci-release` | .github/workflows · releases · self-updater · CI failures |
| `localization` | any user-facing string · new SoI cards need French entries |

## Non-negotiables

1. **No UnityEngine types** in Core/Engine/Content/Bots/Shards.* — everything rules-side compiles and runs headless.
2. **Exactly ONE pending input** at a time (both engines); all mutation flows through `Submit(PlayerAction)`.
3. **Effects are iterators**; pause with `EngineStep.AwaitDecision` — never block, never recurse into Submit.
4. **Determinism**: randomness only via `GameState.Rng` (seeded); never iterate Dictionary/HashSet where order matters; no wall clock in rules. Replays must reproduce `ComputeHash()`.
5. **Hidden information** leaves the host ONLY via `EventLog.FilterFor(player)` / snapshot builders — never add other egress points.
6. **UI contains zero rules logic** — it renders snapshots + filtered events and submits PlayerActions.
7. **Card registries** (pascension-cards / shards-cards skills) are updated IN THE SAME CHANGE as any card change; SoI card changes also update `SoiFrenchCards.cs`.
8. **Engine/wire strings stay English** (localization is display-only; wire goldens are pinned).

## Verify

- Fastest (no Unity): `cd Tools/EngineVerify && dotnet test --nologo` — full NUnit suite headless (mirrors the Unity asmdefs). Keep it green.
- Unity: Test Runner via MCP, or `Unity.exe -batchmode -runTests -testPlatform EditMode -projectPath .` (editor closed).
- UI: Unity MCP play mode + screenshots — HTTP server on **:8090** (details in project-map). Scenes are code-built: `Pascension/Setup/Build All Scenes` (+ `Build Lobby Scene`).
- Exports: `dotnet test --filter Balance` (sims) · `--filter ExportShardsCardTable` (SoI table) · `--filter ExportArtManifest` (art manifest).

## Conventions

- C# 9, explicit namespaces matching assemblies (`Pascension.*`, `Shards.*`), one public type per file.
- New card → follow the matching registry skill's checklist end-to-end.
- Commit per milestone or coherent unit of work; never commit `Library/`.
- **This file is a lean index.** Session history, gotchas, and dated logs live in the skills — do NOT append dated session sections here.

## graphify

A knowledge graph of `Assets/Scripts` lives in `graphify-out/` (gitignored, machine-local; AST-only — free to rebuild). **For "where is X / what calls Y / how do A and B connect" questions, query the graph FIRST instead of reading files**: `graphify query "<question>"` (also `graphify path "A" "B"`, `graphify explain "Node"`). Division of labor: graphify answers what/where; the skills carry procedures and invariants. A post-commit hook re-indexes changed code automatically; after big refactors or on a fresh clone run `/graphify Assets/Scripts --update` (or without `--update` to rebuild). Report/browser view: `graphify-out/GRAPH_REPORT.md` + `graph.html`.
