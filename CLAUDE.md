# Pascension

Competitive deck-building race game (Unity 6000.3.7f1, URP 2D, uGUI+TMP). 2-4 players — solo vs bots or host-based online (NGO 2.x, host mode, no server). Players buy cards Ascension-style from a shared market, race a 50-step board, level a hero 1→10, and win by bursting down the boss (The Gatekeeper, 20 HP) on step 50. Full MTG-like rules engine: stack, APNAP priority, instants, triggered/activated/continuous effects.

Full design + implementation plan: `C:\Users\Shira\.claude\plans\this-is-an-empty-quirky-sunbeam.md`. Rules source of truth: `Assets/GDD.txt` (original) + the decisions log below.

## Skills (read before working on these areas)

- `.claude/skills/cards/SKILL.md` — **the card registry**. ⚠️ MANDATORY: update it whenever ANY card is added, changed, or removed. It lists every card's exact rules text, cost, tier, copies, art prompt, and implementation status.
- `.claude/skills/card-art/SKILL.md` — ComfyUI + Anima art generation pipeline and prompting standards.
- `.claude/skills/rules-engine/SKILL.md` — engine architecture; how to add effects, triggers, keywords, decisions.
- `.claude/skills/playtesting/SKILL.md` — headless bot sims and balance tuning.

## Architecture (assemblies → dependency direction)

```
Pascension.Engine   Assets/Scripts/Engine/   pure C#, NO UnityEngine (noEngineReferences)
Pascension.Content  Assets/Scripts/Content/  card/hero definitions → Engine
Pascension.Bots     Assets/Scripts/Bots/     heuristic + Ollama agents → Engine, Content
Pascension.Net      Assets/Scripts/Net/      sessions, GameHost, NGO bridge → Engine, Content, Bots
Pascension.Game     Assets/Scripts/Game/     Unity presentation → everything
Pascension.Editor   Assets/Scripts/Editor/   art pipeline tools (editor-only)
Pascension.Engine.Tests  Assets/Tests/EngineTests/  edit-mode NUnit tests
```

### Non-negotiable engine rules
1. **No UnityEngine types** in Engine/Content/Bots. They must compile and run headless.
2. **Single pending input**: the engine awaits exactly one `PriorityInput` or `DecisionInput` at a time. All mutation flows through `GameEngine.Submit(PlayerAction)`.
3. **Effects are iterators**: `IEnumerable<EngineStep> Resolve(EffectContext)`; pause for choices with `yield return EngineStep.AwaitDecision(...)`. Never block, never recurse into Submit.
4. **Determinism**: randomness ONLY via `GameState.Rng` (seeded PCG32). Never iterate `Dictionary`/`HashSet` where order affects outcomes. No DateTime/wall-clock in rules. Replays must reproduce `GameState.ComputeHash()`.
5. **Hidden information** leaves the host only through `EventLog.FilterFor(player)` and `SnapshotBuilder` — never add other egress points.
6. **Damage assignment is an on-stack ability** (responses like Protective Barrier must be able to deny kills). Movement and buying are off-stack special actions (buys still emit trigger events).
7. Cards played this turn live in the `PlayedThisTurn` zone until cleanup (GDD reshuffle rule).
8. UI contains zero rules logic — it renders `ClientGameView` built from snapshots + filtered events, and submits `PlayerAction`s.

### Key terms
- GDD card type "nothing" = `CardType.Action` in code; the card's type line displays "Nothing".
- "AP" = action points. Damage pool + AP clear at end of the active player's turn (for all players).
- Keyword **Ethereal**: exiled (not discarded) at cleanup if still in hand.

## Conventions
- C# 9, explicit namespaces matching assembly (`Pascension.Engine.*` etc.), one public type per file.
- New card = builder entry in `Assets/Scripts/Content/Sets/*.cs` (+ optional `Content/Effects/*.cs` effect class + test + art prompt). Then update the cards skill. See rules-engine skill for the full checklist.
- Tests: NUnit edit-mode in `Assets/Tests/EngineTests/`, use `TestGameFactory` for seeded scripted games.
- Commit per milestone or coherent unit of work; never commit `Library/`.

## Verification
- **Fastest (no Unity needed)**: `cd Tools/EngineVerify && dotnet test --nologo` — compiles Engine/Content/Bots/Net-Host + runs the full NUnit suite headless (mirrors the Unity asmdefs). Keep it green.
- Engine in Unity: Test Runner via MCP, or `Unity.exe -batchmode -runTests -testPlatform EditMode -projectPath .` when the editor is closed.
- Balance: headless sims — see playtesting skill (`--filter Balance`).
- UI: Unity MCP play mode + screenshots. Scenes are built programmatically: menu `Pascension/Setup/Build All Scenes` (+ `Build Lobby Scene`).
- Art: `Tools/art_manifest.json` is exported via `dotnet test --filter ExportArtManifest`; generation via the editor window (Pascension/Card Art Generator) or `scratchpad generate_art.ps1`-style API loop. ComfyUI must be running (see card-art skill).
- Unity MCP: configured in `.mcp.json` (uvx → `mcpforunityserver==10.0.0`); the in-editor bridge is the `com.coplaydev.unity-mcp` package (Window > MCP for Unity).

## Current status (2026-07-08) & next-session checklist

**Done & verified headless (37/37 NUnit tests green via Tools/EngineVerify):** full rules engine, all 42 cards + 4 heroes + boss, RandomBot/HeuristicBot + balance sims, EngineJson/snapshots/masking, GameHost/LocalSession/BotSeat/AsyncBotSeat, Ollama bot (prompt builder + fallback tested; live Ollama untested). **Done, generated:** all 53 art assets (Assets/Art/…). **Written but NOT yet compiled by Unity** (no Unity MCP this session): the whole UI layer (Assets/Scripts/Game), netcode (Assets/Scripts/Net minus Host/), editor tooling (SceneBuilder, NetSceneBuilder, ArtPipeline). Agents' open questions live in `Assets/Scripts/Game/NOTES.md`, `Assets/Scripts/Net/NOTES.md`, `Assets/Scripts/Bots/Ollama/NOTES.md`, `Assets/Scripts/Editor/ArtPipeline/NOTES.md`.

Next session, in order:
1. Focus the Unity editor → let it resolve packages (NGO 2.13, LitMotion, MPPM, Newtonsoft) and compile; fix compile errors (expect a handful in Game/Net — they were written blind). Unity MCP should connect via `.mcp.json` after a session restart.
2. Run the edit-mode test suite in Unity (should mirror the green headless run).
3. Menu: `Pascension/Setup/Build All Scenes`, then `Pascension/Setup/Build Lobby Scene`, then `Pascension/Rebuild Card Art Index`.
4. Play MainMenu → solo game vs bots end-to-end; screenshot review; iterate on look & feel.
5. Multiplayer smoke test via MPPM (host + 1 virtual client).
6. TODO not yet built: audio (AudioManager + CC0 SFX/music pack), help/tutorial overlay, balance tuning (see playtesting skill baseline), Unity Relay for internet play, effective-HP display on buffed monsters (snapshot lacks modifier data — add `EffectiveHp` to CardSnap).

Key integration contract: networked play sets `Pascension.Net.SessionProvider.Current` before Game-scene `Start()`; `GameBootstrap` prefers it and only builds the solo host when it's null. The host's `GameHost.Start()` is deferred to `HostMatchStarter`'s first `Update` so UI binding always precedes the first broadcast.

## Decisions log (user-approved, 2026-07-08)
No player HP (PvP via effects only) · monsters live in market rows, their damage resets each turn · XP from kills/effects only, curve 2,2,3,3,4,4,5,5,6 · boss burst race on step 50 · inns 10/20/30/40 = choose 1 of {+2 XP, draw 2, exile ≤2 from discard} + move-back checkpoint · 4 heroes (Ignis/Wren/Cornelius/Nyx; passive L1, active L3, upgrade L6, ult L9) · NGO host mode LAN first · Arena-style auto-pass priority + 25s response timer + full-control toggle · single-screen 2D tabletop · painterly-anime full-art cards (Anima via ComfyUI) · staggered start (P2 +1 AP; P3/P4 +1 AP +1 card) · target 30-45 min · basic CC0 audio · title "Pascension".
