---
name: pascension-balance
description: Headless bot-vs-bot simulations of Pascension and balance tuning (XP curve, costs, boss HP, game length), plus the bot seats themselves — HeuristicBot, the experimental Ollama LLM bot and its gotchas, AsyncBotSeat. Use when validating balance changes, tuning GameRules, checking game termination, or working on any Pascension bot.
---

# Pascension Playtesting, Balance & Bots

## Headless simulations
The engine is pure C#, so full games run headless in edit-mode tests (`Assets/Tests/EngineTests/SimulationTests.cs`):
- `RandomBot` soak: 100 seeds × 4 bots — asserts termination (<60 rounds), no exceptions, invariants (card conservation across zones, masked logs never leak opponents' hand ids).
- `HeuristicBot` balance runs: N seeded games collecting `SimStats`.

Run via Unity Test Runner (category `Simulation`), or batchmode when the editor is closed:
```
F:/Unity/editors/6000.3.7f1/Editor/Unity.exe -batchmode -projectPath f:/Unity/projects/pascension -runTests -testPlatform EditMode -testCategory Simulation -logFile sim.log
```

## Fast headless loop (no Unity)
```
cd Tools/EngineVerify
dotnet test --nologo --filter Balance --logger "console;verbosity=detailed"
```

## Balance targets (from the approved plan)
| Metric | Target |
|---|---|
| Median game length (4 heuristic bots) | 15-25 rounds (~30-45 min human time) |
| Level 4 reached (median) | round 5-7 |
| Level 8 reached (median) | round 11-14 |
| Hero win rates | each within 15-35% |
| Boss kill | achievable by round 15-18 with a tuned deck |

## Tuning knobs (in order of preference)
1. `GameRules` (`Assets/Scripts/Engine/Core/GameRules.cs`): XP curve (default 2,2,3,3,4,4,5,5,6), boss HP (20), hand size, response timer, staggered-start table.
2. Card costs/values in `Assets/Scripts/Content/Sets/*.cs` (update the pascension-cards skill after any change!).
3. Monster HP/rewards and pile copy counts.
4. Hero ability numbers.

## Workflow
1. Change one knob. 2. Re-run sims (≥50 seeds). 3. Compare stats (rounds, level timing, win rates, top-bought cards). 4. Record the change + numbers in the commit message. 5. Update the pascension-cards skill if card data changed. 6. If a `GameRules` fact the Ollama prompt states changed (board length, boss HP, tier gates, inns, hand size) — update `PromptBuilder.SystemPrompt` too (see below).

## Baseline (2026-07-08, initial content, 10 seeds × 4 HeuristicBots — re-run before trusting)
- 7/10 games reached a boss kill within 60 rounds; winning games took **26-33 rounds** (target 15-25 → pacing is a bit slow; candidate knobs: cheaper movement cards, softer XP curve tail, boss HP 18).
- **Seat 0 / Ignis over-wins** (6/7 wins): part turn-order, part damage-hero advantage at boss burst. Revisit after real UI playtests: consider Kindle +1 only pre-L6, boss HP scaling per attempt, or stronger stagger compensation.
- Bots always reach position 50 and level 6-10 — economy and race loops function.

## Bot seats

- **HeuristicBot** (`Assets/Scripts/Bots/`): host-side, direct engine access, used for sims and bot seats (incl. kick-replacement in multiplayer). `RandomBot` for soaks only.
- **AsyncBotSeat** (`Assets/Scripts/Net/Host/AsyncBotSeat.cs`): async seat wrapper, compiled by EngineVerify too.

### OllamaBot (experimental LLM bot — logic tested, live Ollama at :11434 still UNTESTED end-to-end)
- Ollama `think` flag is sent as a top-level boolean on `/api/chat`. Non-reasoning models may return an HTTP error on `think:true` — that error path falls into `SnapshotFallbackPolicy`, so the seat never stalls.
- ⚠ **`PromptBuilder.SystemPrompt` hardcodes rules facts** (50-step board, 20 HP boss, tier gates L4/L8, inns 10/20/30/40, draw 5). `ClientSnapshot` carries no `GameRules`, only `BossHp`. **If GameRules knobs are tuned, update the system prompt text** (step 6 of the workflow above).
- Monster HP / buy costs shown to the model are BASE values (continuous modifiers and buy-cost discounts aren't in the snapshot; the `EffectiveHp` gap is on the backlog — see project-map). Marked damage IS shown, and the legal-action menu itself is always accurate (engine-generated amounts).
- **Fallback priority policy**: first `BuyCard`/`AssignDamage`/`PlayCard` in the legal list, else Pass. Deliberately never Moves or uses hero abilities — safety net, not strategy. Follow-up decisions get default answers via the decision branch (mirror of `GameHost.DefaultActionFor`).
- **Stale-submit safety is double-layered**: OllamaBot's per-request token stops a superseded task from submitting, and `GameHost.Tick` drops queued async submissions whose player no longer holds the pending input.
- Decision answers: unknown option ids or a count outside Min..Max → full fallback (defaults). Duplicated ids from the model are de-duplicated first.
- `System.Net.Http.HttpClient` is available in the .NET Standard 2.1 profile Unity uses; if the Bots asmdef ever sets an API level that drops it, OllamaClient breaks.
- Known asmdef gap (pre-existing): `Pascension.Engine.Tests.asmdef` doesn't reference `Pascension.Net`, yet `SerializationTests.cs` uses it — Unity-side test compilation issue only; EngineVerify compiles everything into one assembly so dotnet tests pass.
