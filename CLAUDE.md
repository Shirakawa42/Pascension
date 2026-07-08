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

## UI/animation overhaul (2026-07-08, second pass — all verified live)

Layout: vertical serpentine board (right strip, boss on top) · 400×550 character sheet (left) with passives + tweened XP bar + icon crystals · StS pile corners (draw+played left, discard+exile right; all piles browsable alphabetically incl. opponents' — see cards-skill ruling) · compact market (scale 0.5) · play-history bar (last 10, hover preview) · opponent sheets clickable → full detail modal.
Animations: every play showcased center-screen (player-color glow, click-to-skip), zone-flight proxies (draw/buy/discard/exile/reshuffle/refill), floating +AP/+DMG/+XP and −N damage numbers with inline TMP icon sprites, GlowBurst UI particles, attack impact (punch/flash/directional burst), stack-target arrows, live monster HP (green buffed/red damaged) and viewer-effective costs.
Rules changes: **no response timer** · full deck transparency. (A "mana abilities can't be responded to" rule was added then REVERSED the same day — see the fourth-pass section below.)
Key components: `Game/Presentation/{FlightLayer,GlowBurstLayer,FloatingNumberLayer,PresentationQueue}`, `Game/View/{CardShowcase,PlayHistoryBar,PileWidget,OpponentDetailModal}`, `GameScreen.PlayEvent` (single animation dispatcher — pace ONLY via `Queue.Wait`/`FastForwarding`). Icons: `Pascension/Setup/Build Icon Sprite Asset` (TMP m_Version reflection fix); AP icon used raw render + luminance-keyed alpha (rembg soft-mask pattern).
Gotcha: view Init() must run at RUNTIME (GameScreen.Bind), never in SceneConstruction — private refs don't survive scene serialization.

## Interaction model (2026-07-08, third pass — all verified live via logs + screenshots)

- **Drag-to-play (StS style)**: `HandCardDrag` → `HandView` — drag a hand card above `PlayLineY=390` (container-local) and release to play, or DOUBLE-CLICK it (`OnPointerClick` clickCount≥2); below the line, horizontal drags REORDER the fan with a realtime gap (visual order persists in `_order`, client-side only). Single-click-to-play was removed. The play line must stay above the hover/reorder band ~250+96 (a 330 line caused accidental plays — verified via UiLog).
- **Live hand pipeline**: `GameScreen.RefreshHandLive()` runs on EVERY snapshot (not drain-gated) — the hand re-fans instantly on plays (optimistic `RemoveCardOptimistic`) and play affordances work DURING animations; responses/decisions still wait for drain. Drawn cards render hidden (`_pendingReveal`) and pop in one-by-one as each draw-flight lands (`FlightThenReveal`; `CoalescedDrawEvent.InstanceIds`).
- **HandView.Render is a DIFF, never a rebuild** (2026-07-08 fifth pass): snapshots stream constantly during play chains, so Render keeps existing views alive (an active drag survives untouched — view, position, `_draggingId`), destroys only removed cards, creates only new ones, and re-applies poses only when membership changed. Rebuilding here breaks fast-play (every snapshot killed the drag) — don't regress this.
- **Persistent-view tween guard**: `Tween.Punch`/`Flash` capture start state as "base" — overlapping calls on PERSISTENT views (pile badges, market slots) ratchet forever (the badge-growing bug). Pattern: stop the previous coroutine and restore the true base before restarting (see `PileWidget.Pulse`, `MarketView.PunchSlot/FlashSlot`). Views that are rebuilt each render don't need this.
- **Market refills reveal per-flight**: slots start inactive and the full `Market.Render` waits for drain, so `PlayRefill` calls `MarketView.RevealSlot(tier,slot,snap,viewerLevel)` as each flight lands (bind + activate + locked-tier grey + punch) — otherwise the whole market pops in at once at batch end.
- **Hover preview is FIXED top-left** at (118,−12) anchor/pivot (0,1) — right of the history bar, never follows the card.
- **16:9 lock**: canvas scaler = Expand; all game UI lives under a fixed 1920×1080 `UiRoot` with **RectMask2D** (clips parked panels on ultrawide). Battery-verified at 4:3 / 16:9 / 21:9 via ScreenSpaceCamera render trick (scratchpad `shot_wh.cs`). Runtime-created UI (StackArrows, preview) must parent to `GameScreen.UiRootRect`, not the canvas.
- **UiLog**: verbose `[UI:Area]` console logging (Hand/Drag/Play/Draw/Flight/Showcase/Queue/Live/Stage) — grep `read_console` to verify sequencing in automated tests. Toggle `UiLog.Enabled`.

## Speculative fast-play + respondability (2026-07-08, fourth pass — verified live)

- **Everything is respondable** (user reversal of the mana-ability rule): every play and tap uses the stack and opens response windows. The `IsManaAbility`/`ManaAbility` engine flags REMAIN as a rare explicit opt-out for future cards — any card using them MUST say "can't be responded to" in its rules text (pinned by `RespondableInvariant_NoCardOptsOut_WithoutSayingSoInItsText`). No current card is flagged; `CardPlayedEvent` + its showcase path stay wired but dormant.
- **Speculative play queue** (`GameScreen`: `_stagedPlays`/`PumpStagedPlays`/`RollbackStaged`): during your own main phase you can play cards as fast as you like — each play is staged CLIENT-SIDE (opponents see nothing; a "QUEUED n" widget above the played pile shows yours, click = take all back) and submitted one by one. A card's effect applies, and it is revealed to opponents, only after the previous play VALIDATED (resolved with no response). Opponents are prompted one card at a time. If anyone responds, the queue rolls back to hand (toast + log). END TURN/PASS while staged = rollback, never a pass.
- **Pump invariants** (hard-won): local submits cascade snapshots SYNCHRONOUSLY inside `SubmitAction`, so the pump (a) is re-entrancy-guarded via `_pumping`, (b) removes the queue head BEFORE submitting, and (c) loops until the queue empties or others must act. Rollback detection needs BOTH checks: opponent item in the snapshot stack (pump) AND opponent `StackPushedEvent` in the event batch (`OnEvents`) — host fast-pass can resolve an entire counter interaction inside one submit, so the transient stack state never reaches a snapshot.
- **Auto-pass own spells**: with the mana rule gone, every play would open a self-response window when you hold instants — `RefreshAll` now auto-passes when the stack holds only YOUR items (Arena behavior; full-control keeps the window). The response window itself only shows for opponents' stack items.
- **Live pile counts**: `RenderPiles` runs in `RefreshHandLive` (every snapshot), not just on drain — pile badges track every change immediately.
- **Map sidebar**: the board track lives on a dedicated right panel ("THE ROAD", `MapSidebar`, 252×1064 at anchor (1,0.5)); glow/table shrunk to 1260 wide and the discard/exile column, action bar and stack-panel rest X moved to −268 so nothing overlaps the strip. Hero passive text sits at (14,−164) 282×78 — clear of the DMG crystal column.

## Current status (2026-07-08) & next-session checklist

**Compiles & runs in Unity 6000.3.7f1; solo game verified playable end-to-end via MCP.** 37/37 edit-mode tests green (also headless via `Tools/EngineVerify`). Scenes built (MainMenu, Game, Lobby), art index has 51 sprites, all 53 art assets present. Verified by driving the real menu→hero-select→game flow: playing cards, buying (Haggle discount applied), END TURN, bot takes its turn, round advances — zero runtime errors. Screenshots confirm the board, market (full-art cards + monster HP badges), character sheets, hand fan, and boss all render correctly.

**Bugs found & fixed this session:**
- `CastEffects.cs`: `BuildTargetDecision`/`ExtractTargets`/`DescribeTarget` were `internal` but called cross-assembly from Content → made `public`.
- **`runInBackground: 0` → `1`** (ProjectSettings): the player loop froze whenever the Game view lost focus, so `GameBootstrap.Start()` never ran after a scene load. Genuinely correct for a networked game too (no freeze on alt-tab). This was the one blocking bug.
- Removed blocking editor modal dialogs from `SceneConstruction.cs` and `NetSceneBuilder.cs` (they hung headless menu-item runs).
- Cosmetics: market tier label "ADVANCED" no longer wraps (`MarketView.cs`); "OPPONENTS" label no longer overlaps bot config (`MainMenu.cs`).

**Ollama bot**: logic tested (prompt builder + snapshot fallback); live Ollama at :11434 still untested end-to-end.

Next session, in order:
1. Multiplayer smoke test via MPPM (host + 1 virtual client) — the net layer compiled but its runtime path is unexercised.
2. Live-test the Ollama bot (needs Ollama running at 127.0.0.1:11434 with a model installed).
3. Not yet built: audio (AudioManager + CC0 SFX/music pack), help/tutorial overlay, Unity Relay for internet play, effective-HP display on buffed monsters (snapshot lacks modifier data — add `EffectiveHp` to CardSnap).
4. Balance tuning (see playtesting skill baseline: games run ~26-33 rounds, seat-0/Ignis over-wins).

MCP note: the Unity MCP server is HTTP streamable on port **8090** (not the default). `.mcp.json` points at `http://127.0.0.1:8090/mcp`. Under MCP control the Game view is unfocused, so set `Application.runInBackground = true` at runtime if frames stall (now the default via ProjectSettings). `execute_code` runs as a method body (no `using`/local-functions); screenshots via Camera.Render→ReadPixels (see scratchpad `shot.cs`), not `ScreenCapture.CaptureScreenshot`.

Key integration contract: networked play sets `Pascension.Net.SessionProvider.Current` before Game-scene `Start()`; `GameBootstrap` prefers it and only builds the solo host when it's null. The host's `GameHost.Start()` is deferred to `HostMatchStarter`'s first `Update` so UI binding always precedes the first broadcast.

## Decisions log (user-approved, 2026-07-08)
No player HP (PvP via effects only) · monsters live in market rows, their damage resets each turn · XP from kills/effects only, curve 2,2,3,3,4,4,5,5,6 · boss burst race on step 50 · inns 10/20/30/40 = choose 1 of {+2 XP, draw 2, exile ≤2 from discard} + move-back checkpoint · 4 heroes (Ignis/Wren/Cornelius/Nyx; passive L1, active L3, upgrade L6, ult L9) · NGO host mode LAN first · Arena-style auto-pass priority + full-control toggle (NO response timer — players take unlimited time, changed 2026-07-08; `GameRules.ResponseTimerSeconds=0`) · **everything is respondable** (mana-ability rule reversed 2026-07-08; future opt-out cards must say "can't be responded to" in their text) · **speculative fast-play with rollback** (effects/reveal only on validation; opponents prompted per card) · single-screen 2D tabletop · painterly-anime full-art cards (Anima via ComfyUI) · staggered start (P2 +1 AP; P3/P4 +1 AP +1 card) · target 30-45 min · basic CC0 audio · title "Pascension".
