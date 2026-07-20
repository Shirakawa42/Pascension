---
name: shards-engine
description: Shards of Infinity engine architecture (Shards.Engine, depends ONLY on Pascension.Core — no stack, single-pending-input pump) — turn flow, the end-turn split/shield/cleanup chain, the three hard-won pump gotchas, DLC gating, host-side glow hints, ShardsHeuristicBot. Use before modifying anything under Assets/Scripts/Shards or debugging SoI game flow. Rules spec: Tools/ShardsData/rules-notes.md.
---

# Shards of Infinity Engine

`Shards.Engine` (`Assets/Scripts/Shards/Engine/`) is pure C#, depends **only on `Pascension.Core`** — it does NOT use Pascension's rules engine. No stack, no priority: a **single-pending-input pump** (one `DecisionInput` at a time, all mutation via `Submit`). Rules verified against the official Stone Blade rulebooks: spec in `Tools/ShardsData/rules-notes.md`, card data in `Tools/ShardsData/cards-table.md` (generated). Card registry + per-card rulings: **shards-cards** skill. `Shards.Content` and `Shards.Bots` sit on top (Core + Shards.Engine only).

## Turn & end-turn flow
End-turn is a chained decision flow, not a phase machine:
1. **Damage split** — decision Context `"soi.split"`, one option id per damage point, defenders clockwise from the attacker. Full power assignment is MANDATORY (Min=Power; DefaultOptionIds pre-fill a legal full assignment for timeouts/bots).
2. **Per-defender shield reveal** — Context `"soi.shields"`, defenders clockwise. Shields reveal from HAND and STAY in hand; champion printed shields are INERT in play (Praetorian-02 is the lone in-play exception).
3. **Ingeminex end-of-reveal-turn attacks** (ItH DLC).
4. **Cleanup**: fast-plays → BOTTOM of center deck · play zone → discard · discard hand · ready champions/destinies/character AT END PHASE (not turn start) · redraw 5 with mid-draw reshuffle (never deck out) · pools/turn-flags reset.

## Core rules encoded (each pinned by a test — details per card in shards-cards)
- Champion damage ACCUMULATES within a turn; marks clear at end phase. Destruction needs full effective defense assigned in one split.
- **Champions die ONLY in the end-of-turn damage split or via destroy-EFFECTS** (locked decision 2026-07-20): `ShardsAttackChampionAction` is rejected outright and never advertised. Ingeminex remain the only mid-turn power targets. `CanBeAttacked` vetoes (Li Hin/Raidian/Drakonarius) filter the split's target list.
- Focus once/turn (exhaust character + 1 gem → +1 mastery). Staggered start mastery 0/1/2/3; cap 30.
- Mastery thresholds check at play/exhaust time; a card's own gain counts for its own threshold.
- "Lose health" ≠ damage: no shields apply; simultaneous drop below 1 = TIE (`WinnerIndex` −1); eliminations are checked AFTER all simultaneous losses land. The active player can eliminate themselves mid-turn — RoutePriority passes the turn instead of deadlocking.
- Factions: Homodeus/Order/Undergrowth/Wraethe + Aion (SoS/ItH).
- DLC gating: **RotF** — relics; both set aside, recruit ONE free at M10, the unchosen stays set-aside (only the Ingeminex Corruption reward fetches it). **SoS-competitive** — Rez + Cloud Oracles errata replacement. **ItH** — shared destiny ROW of 6, one free at M5+ once/game; Ingeminex bypass the row into their own space.
- `ExhaustGemCost` ("Pay N gems, Exhaust:") is a real activation COST: the tap is ILLEGAL while unaffordable (engine rejects, LegalActions filters, UI greys + toasts), paid by `ExhaustCard`; effects never check gems.

## ⚠ THE THREE PUMP GOTCHAS (all shipped bugs, all pinned by tests)
1. **Never resume a decision-parked effect iterator** — `PumpEffects` guards on pending decision.
2. **The end-turn chain (`BeginEndTurn`/`NextDefense`/`AfterDefenses`) must QUEUE effects only, never call Pump** — it runs inside effect iterators, and a nested pump clobbers the parked iterator.
3. **`AfterDefenses` must queue `FinishFlow` behind the effect queue whenever `_effectQueue.Count > 0`** — `ApplyDamage` queues owned-destiny triggers (Blood for Blood) during the defense chain, and synchronous cleanup would empty the play zone before they resolve (pinned by `BloodForBlood_TriggersOnFivePlusUnblockedDamage_BanishesPlayedCard`).

## Host-side glow hints (UI affordances computed in the snapshot)
- `ShardsSnapshotBuilder.BuildGlowHints` → `ConditionGlowIds` / `KillableIds` / `BuyableSlots`.
- `IShardsConditionalEffect` implemented on If/FactionTrigger/Unify/Dominion/PerCount + the `ShardsGlowProbe` walker — probes are pure reads; Source never counts itself. **AtMastery/BestByMastery deliberately excluded** (past the threshold they'd glow forever — noise).
- Champion red glow (`KillableIds`) means "your end-turn split can kill this".
- **`BuyableSlots` is only filled while the viewer holds priority on their own turn** (pinned by `SnapshotBuyableSlots_OnlyWhileTheViewerHoldsPriority`) — gems persist until cleanup but the affordable halo must not linger after END TURN.
- Rendering of the glow channels lives in ui-presentation (3-ring system).

## ShardsHeuristicBot
`Shards.Bots/ShardsHeuristicBot` — `IBotAgent`, host-side engine access, effect-tree value scoring. Its mid-turn champion-kill branch was removed with the champions-die-only-in-split decision.

## Tests
`ShardsEngineTests` (structural, stub set), `ShardsRulingsTests` (one test per FAQ ruling), `ShardsContentTests` (counts, setup, termination, card conservation), 3-seed all-DLC bot sims. Keep `Tools/EngineVerify` green.

## Open items (was M8)
- SoI-over-Relay battery UNVERIFIED (net layer is game-agnostic and Pascension's Relay battery passed 2026-07-10, so risk is low — see networking skill).
- ShardsHeuristicBot balance sims.
- 4:3/21:9 screenshot pass for the SoI table (see ui-presentation).
- Known simplifications to revisit: listed at the bottom of the shards-cards skill.
