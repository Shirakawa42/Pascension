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
3. **Cleanup**: fast-plays → BOTTOM of center deck · play zone → discard · discard hand · ready champions/destinies/character AT END PHASE (not turn start) · redraw 5 with mid-draw reshuffle (never deck out) · pools/turn-flags reset.
4. **Ingeminex end-of-reveal-turn attacks** (ItH DLC) — AFTER the redraw (locked 2026-07-21: Agony's discard hits the active player's FRESH hand). `FinishEndTurn` queues `QueueMonsterAttacks` then parks the turn-advance (`AdvanceFlow`→`AdvanceAfterEndTurn`) BEHIND the attack effects; `_endTurnInProgress` stays true until the advance so Concede/RoutePriority can't advance mid-attack. Pinned by `IngeminexAttack_FiresAfterActivePlayerRedraws`.

## Core rules encoded (each pinned by a test — details per card in shards-cards)
- Champion damage ACCUMULATES within a turn; marks clear at end phase. Destruction needs full effective defense assigned in one split.
- **Unify counts any CARD of the faction, champions included** (user decision 2026-08-23; deliberate deviation from the printed "Ally" reminder). Played this turn OR revealed from hand; a champion already in play satisfies nothing, and a card never satisfies its own Unify. `Unify` reads `FactionPlays`, NOT `FactionAllyPlays` — the ally-only counter still exists and is what Order's "2 Order allies" cards read.
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

## Bots & AI (2026-07-21 — the strong-AI stack)
Difficulty ladder: **7 minted ranks** in `ShardsBotRanks.Minted` (IRON→DIAMOND); `Tools/ShardsData/bot-ranks.md` is the authoritative spec sheet. The legacy `heuristic`/`greedy`/`strong`/`strong-fast` kinds still resolve for tooling.

### ⚠ THE DUEL BLIND SPOT (found + fixed 2026-07-25 — read before trusting any pre-2026-07-25 AI artifact)
`SimConfig.AllDlc` excluded `ShardsDlc.Duel` until 2026-07-25, and Duel was opt-in via `--dlc duel` which no campaign run ever passed. **Every net and every tuned weight vector through V4 was fit to a game without hero drafts, hero abilities or row rerolls.** Compounding it, the two Duel actions were scored with hardcoded constants — `EndTurnBase + 0.05` for the hero ability (so greedy fired it *unconditionally*, every turn, regardless of cost) and `EndTurnBase - 0.01` for the reroll (strictly below passing, so an argmax policy could **never** select it → zero rerolls in every rollout and every training position). `ShardsHeuristicBot.PickAction` had no case for either action at all.

Three lessons that generalize:
1. **Mirror-match benchmarking is blind to shared blind spots.** Both bots misplayed Duel identically, so every probe read 50% while a human using the mechanics took the whole edge. A capability gap will not show up in self-play; only an *ablation* (`probe --weights-b duel-blind`) measures it.
2. **An additive base swamps a value term.** The first fix priced the hero ability as `HeroAbilityBase(200) + net(±2)` — still unconditional, and worth **49.8% [47.9–51.7] over 784 pairs, i.e. nothing**. Making it multiplicative (`net * W.HeroAbilityValueScale`, so the SIGN of net decides) moved the same change to **56.0% [54.1–58.0] over 1000 pairs, +42 Elo**. Whether-to-act questions need the value in charge, not the ladder position.
3. **A weight defaulting to 0.0 is untunable.** CMA-ES scales each dimension by `max(|start|, 0.05)`, so a zero default gets a search range of ~zero and never moves — which is why `EndTurnBase` has sat at ~0 since V1. Give every appended weight a non-zero default in `W.Defaults`.

`W.Defaults` + `W.Pad` are the layout contract: appending a weight means appending its default, `TuneCommand` pads the champion up to `W.Count` so new dimensions actually get tuned, and `ShardsValueModel` pads on construction so older vectors stay loadable. Pinned by `WeightLayout_DefaultsCoverEveryIndex`. Bots using both Duel actions is pinned behaviourally by `SoiSimDuelBotTests` — a `case` in a switch proves nothing if the score keeps it below END TURN forever.

### Measurement (rebuilt 2026-07-25 — `probe` is the instrument every strength claim rests on)
- **Paired scoring is the default and the number to quote.** The unit of work is a mirrored PAIR (same seed + matchup, seats swapped), which cancels the seed, the matchup and the 56.5% first-player advantage. Typically ~1.4x tighter half-width than pooling games independently = ~2x fewer games for the same resolution. The unpaired Wilson figure is still printed for continuity with older log lines.
- **`--sprt`** (GSPRT, elo0=0/elo1=+15/α=β=0.05) stops as soon as the result is decided. Verdict is recorded at the crossing; in-flight parallel pairs still land in the point estimate and can drag the final LLR back inside the bounds — that does not un-decide it.
- **n=120 is below the noise floor** (±8.9pt): it cannot see a true 55% effect, and it produced several false conclusions in `campaign-log.md`. `probe` now refuses to write a conclusion under 200 pairs without `--allow-small` (a completed SPRT is exempt). **n≥1000 paired for a promotion gate, n≥2000 to publish.**
- **`--weights-a/-b`** picks `current` | `duel-blind` | `V1…Vn` — the promotion gate and the ablation lever.
- **Never gate on validation accuracy**: the log's own counterexample is a net at 80.7% val acc that played 46.0%.
- `SoiSimProbeCalibrationTests` is the standing null calibration: an agent against an identical copy of itself must straddle 50%, pairing must beat pooling, and SPRT must not fire on a true null.
- **ShardsHeuristicBot** — legacy greedy ladder, kept as tuner anchor + rollout-order reference.
- **ShardsValueModel** (`Shards.Bots`) — tuned value core: `ShardsCardStatics` walks effect trees once per def into atoms at 7 mastery buckets; `ShardsCustomAnnotations` covers every Custom/Do (guard test `EveryCustomOrDoEffect_HasAnAnnotation` fails on new unannotated ones — balance patches trip it on purpose); weights in generated `ShardsEvalWeights.g.cs` (V2 = CMA-ES self-play tuned, 81.9% vs heuristic).
- **ShardsGreedyEvalBot** — argmax over the model; instant.
- **ShardsSearchBot** (`Shards.Bots/Search`) — SO-ISMCTS: forks the engine at priority points (`ShardsEngine.Fork` — quiescent-only, quiet clones, DeepCopy), determinizes hidden zones (`ShardsDeterminizer`, canonical-sort-then-shuffle — FAIR: no peeking, pinned by the invariance test), descends by real Submits, ε-greedy model rollouts to terminal (ε=0.03 is load-bearing — see ShardsSearchConfig comment), plan cursor serves own-chain decision answers from the searched subtree. 600 iters ≈ 0.6s/decision ≈ 77% vs greedy.
- **Engine support**: `ShardsState.DeepCopy/ComputeFullHash/ComputePublicHash` (`ShardsStateClone.cs` — field-count sentinel test forces updates), `ShardsEngine.Fork/Journal`, quiet mode. Copy effects: one copy per card per resolution chain (`ShardsContext.InCopyChain` — Fabricator recursion crash fix).
- **Retune after any card change**: `dotnet run -c Release --project Tools/SoiSim -- tune` (~3 min, ~1.2M games) → `evaluate` gate (heuristic ≥65% greedy / ≥95% vs random) → commit the regenerated `.g.cs`.

## SoiSim (Tools/SoiSim — mass sims & balance stats)
Console: `bench | run | analyze | tune | evaluate | probe | smoke`. 30k greedy games ≈ 10s. `run` writes JSONL to gitignored `Tools/ShardsData/sim/`; `analyze` → committed `Tools/ShardsData/balance-report.md/.json` + `sim-summary.csv` (goal-3 input; per-card impact = matchup×seat-stratified, BH-corrected). Sim/ + Tests/ compile-link into EngineVerify (smoke tests gate CI; exe never built there). Headline finding (30k heuristic AND 30k greedy-V2): **P0 wins 58.6%** — the +1 mastery stagger undercompensates.

## Tests
`ShardsEngineTests` (structural, stub set), `ShardsRulingsTests` (one test per FAQ ruling), `ShardsContentTests` (counts, setup, termination, card conservation), 3-seed all-DLC bot sims. Keep `Tools/EngineVerify` green.

## Open items
- SoI-over-Relay battery UNVERIFIED (net layer is game-agnostic and Pascension's Relay battery passed 2026-07-10, so risk is low — see networking skill).
- 4:3/21:9 screenshot pass for the SoI table (see ui-presentation).
- Known simplifications to revisit: listed at the bottom of the shards-cards skill.
- AI follow-ups (optional): search-in-loop weight retune (greedy-tuned weights transfer well but weren't retuned under search); full-size ISMCTS stats run (`soisim run --bots strong --budget 400 --games-per-matchup 400`, hours — the committed strong report used a smaller prefix); truncated-rollout evaluator if more strength per second is ever needed; in-game play-mode frame-freeze check for MASTER bots (SearchBotSeat is designed non-blocking but unverified in play mode).
