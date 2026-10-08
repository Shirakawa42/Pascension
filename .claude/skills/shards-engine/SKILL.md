---
name: shards-engine
description: Shards of Infinity engine architecture (Shards.Engine, depends ONLY on Pascension.Core — no stack, single-pending-input pump) — turn flow, the end-turn split/shield/cleanup chain, the three hard-won pump gotchas, DLC gating, host-side glow hints. Use before modifying anything under Assets/Scripts/Shards or debugging SoI game flow. Rules spec: Tools/ShardsData/rules-notes.md.
---

# Shards of Infinity Engine

`Shards.Engine` (`Assets/Scripts/Shards/Engine/`) is pure C#, depends **only on `Pascension.Core`** — it does NOT use Pascension's rules engine. No stack, no priority: a **single-pending-input pump** (one `DecisionInput` at a time, all mutation via `Submit`). Rules verified against the official Stone Blade rulebooks: spec in `Tools/ShardsData/rules-notes.md`, card data in `Tools/ShardsData/cards-table.md` (generated). Card registry + per-card rulings: **shards-cards** skill. `Shards.Content` sits on top (Core + Shards.Engine only).

## Turn & end-turn flow
End-turn is a chained decision flow, not a phase machine:
1. **Damage split** — Duel assigns remaining power to players only; champions are attacked during the play phase. Non-Duel retains its combined champion/player split. Context `"soi.split"`, defenders clockwise; all assignable power is mandatory and timeout defaults are legal. If all players are guarded in Duel, remaining power expires.
2. **Per-defender shield reveal** — Context `"soi.shields"`, defenders clockwise. Shields reveal from HAND and STAY in hand; champion printed shields are INERT in play (Praetorian-02 is the lone in-play exception).
3. **Cleanup**: fast-plays → BOTTOM of center deck · play zone → discard · discard hand · ready champions/destinies/character AT END PHASE (not turn start) · redraw 5 with mid-draw reshuffle (never deck out) · pools/turn-flags reset.
4. **Ingeminex end-of-reveal-turn attacks** (ItH DLC) — AFTER the redraw (locked 2026-07-21: Agony's discard hits the active player's FRESH hand). `FinishEndTurn` queues `QueueMonsterAttacks` then parks the turn-advance (`AdvanceFlow`→`AdvanceAfterEndTurn`) BEHIND the attack effects; `_endTurnInProgress` stays true until the advance so Concede/RoutePriority can't advance mid-attack. Pinned by `IngeminexAttack_FiresAfterActivePlayerRedraws`.

## Core rules encoded (each pinned by a test — details per card in shards-cards)
- Duel champion attacks spend exactly remaining effective defense and resolve immediately. Effective defense includes auras and temporary grants. Any marked damage clears at end phase; non-Duel retains its end-turn champion split.
- **Unify counts any CARD of the faction, champions included** (user decision 2026-08-23; deliberate deviation from the printed "Ally" reminder). Played this turn OR automatically reveal the first matching card from hand (2026-09-16; no optional decision); a champion already in play satisfies nothing, and a card never satisfies its own Unify. `Unify` reads `FactionPlays`, NOT `FactionAllyPlays` — the ally-only counter still exists and is what Order's "2 Order allies" cards read.
- **Duel champion attacks happen during the turn** (2026-10-08): `ShardsAttackChampionAction` is advertised for legal affordable targets. Zetta must leave play before protected targets unlock; multiple guards protect non-guards and players but remain attackable. Per-card `CanBeAttacked` restrictions still apply. Non-Duel retains the former end-turn-only rule.
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
  Condition hints are viewer-only: own hand, market evaluated for the viewer, own ready
  champions/destinies. Opponent conditions can inspect private hands/decks and must not
  be probed or included. `ShardsSeatSafetyTests` pins hidden-zone swap invariance for both seats.
- `IShardsConditionalEffect` implemented on If/FactionTrigger/Unify/Dominion/PerCount + the `ShardsGlowProbe` walker — probes are pure reads; Source never counts itself. **AtMastery/BestByMastery deliberately excluded** (past the threshold they'd glow forever — noise).
- Champion red glow (`KillableIds`) uses public current defense and attack restrictions. In Duel, clicking a champion submits the host-advertised attack action; non-Duel uses the end-turn split.
- `AutomaticEndTurnVictory` is a public-state hint for the active viewer holding priority, computed by `WouldEndTurnWinAutomatically`. It matches the overwhelming-power shortcut when every living opponent will be defeated; mastery-30 Infinity Shard bypasses Zetta protection. The UI uses it to skip the redundant champion reminder, never to calculate damage or inspect shields. Ordinary multiplayer power allocation still uses the engine's `soi.split` flow. Comet directly destroys its chosen opponent regardless of Zetta; multiplayer still requires a target choice.
- **`BuyableSlots` is only filled while the viewer holds priority on their own turn** (pinned by `SnapshotBuyableSlots_OnlyWhileTheViewerHoldsPriority`) — gems persist until cleanup but the affordable halo must not linger after END TURN.
- Rendering of the glow channels lives in ui-presentation (3-ring system).

## Tests
`ShardsEngineTests` (structural, stub set), `ShardsRulingsTests` (one test per FAQ ruling), `ShardsContentTests` (counts, setup, termination, card conservation). Keep `Tools/EngineVerify` green.

## Open items
- SoI-over-Relay battery UNVERIFIED (net layer is game-agnostic and Pascension's Relay battery passed 2026-07-10, so risk is low — see networking skill).
- 4:3/21:9 screenshot pass for the SoI table (see ui-presentation).
- Known simplifications to revisit: listed at the bottom of the shards-cards skill.

## September 2026 balance state

In 1v1 only, seat 1's opening hand is `HandSize + 1`; subsequent cleanup draws
remain `HandSize`. Seat 1 retains its 1 starting mastery.
Rez at M5 passively reduces every reroll price by 1, without activating Scry 3.
`RerollCost` is authoritative for legal actions, snapshots and AI candidates.
`NextRerollDiscount` remains a legacy one-use effect token; Rez no longer grants it.
`soi.return` accepts zero through two cards for Duel World Piercer.

## October 2026 Duel patch

- Opening six slots: printed cost at most 5 or Comet. Hold other cards privately and shuffle them back before hero draft; later refills are unrestricted.
- Testudo uses `ChampionDefensePerShieldPlay`: actual plays with a positive live shield value grant current champions +1 per Testudo, until their owner’s next turn. Reveals and copied effects are not plays. Grants persist after Testudo leaves and clear when the recipient leaves play.
- DNA uses `PendingRecruitCopies` and `NotifyRecruit`: pay 4 and exhaust to copy the next actual recruitment this turn directly to discard. All armed copies apply together. Includes free and relic recruits, and a normally bought Comet; destiny-taking, loans, returns and generated copies do not count as new recruits.
- `GeneratedCardCounts` records public created instances separately from immutable setup counts, including Doom Gate. AI conservation must use both, with no second flood estimate.
- Snapshot champion `EffectiveDefense` and `TemporaryDefenseUntilNextTurn`, player pending copies and generated counts are public state. Clone/hash/wire paths must preserve them.
- Current detailed rulings: `Tools/ShardsData/rules-notes.md`, October section. Focused regressions: `ShardsBalancePatchTests`.
