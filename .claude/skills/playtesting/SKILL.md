---
name: playtesting
description: Run headless bot-vs-bot simulations of Pascension and tune balance (XP curve, costs, boss HP, game length). Use when validating balance changes, tuning GameRules, or checking that games terminate correctly.
---

# Playtesting & Balance

## Headless simulations
The engine is pure C#, so full games run headless in edit-mode tests (`Assets/Tests/EngineTests/SimulationTests.cs`):
- `RandomBot` soak: 100 seeds × 4 bots — asserts termination (<60 rounds), no exceptions, invariants (card conservation across zones, masked logs never leak opponents' hand ids).
- `HeuristicBot` balance runs: N seeded games collecting `SimStats`.

Run via Unity Test Runner (category `Simulation`), or batchmode when the editor is closed:
```
F:/Unity/editors/6000.3.7f1/Editor/Unity.exe -batchmode -projectPath f:/Unity/projects/pascension -runTests -testPlatform EditMode -testCategory Simulation -logFile sim.log
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
2. Card costs/values in `Assets/Scripts/Content/Sets/*.cs` (update the cards skill after any change!).
3. Monster HP/rewards and pile copy counts.
4. Hero ability numbers.

## Workflow
1. Change one knob. 2. Re-run sims (≥50 seeds). 3. Compare `SimStats` (rounds, level timing, win rates, top-bought cards). 4. Record the change + numbers in the commit message. 5. Update cards skill if card data changed.
