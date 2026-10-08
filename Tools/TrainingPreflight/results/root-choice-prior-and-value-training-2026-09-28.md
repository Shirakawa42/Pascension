# Target-plan regularization, mastery ordering and outcome training

Campaign remains active. This iteration made progress: two defects reproduced in both seats, implementation changes passed those regressions, recorded natural positions improved, two value-training experiments completed, and a fresh cohort exposed a separate terminal-search override defect. No overall strength increase or deployment is claimed.

## Implemented changes

`SetupPlanning.ImproveResourceOrder` now accepts an already-planned non-champion mastery card when its active effects are transparent and real-engine pair replay observes no draw, reveal, shuffle, pending choice or turn change. Inactive mastery branches are checked against a conservative upper mastery bound. Reordered known card tails are normalized only on disposable copies; the full state fingerprint must otherwise match after normalizing strictly improved gems/power. Previous played-card positions remain fixed. This accepts Anomaly Cleric before Reactor at mastery 4; it does not broadly force draw cards or rearrange unknown future cards.

Root activation-plus-target plans now include target-policy regularization. Previously the target probability only broke exact ties, while solving the menu independently applied a prior penalty. This made cached root targets behave differently from the target chooser. Optional target penalties use the same six-nat cap and uncertainty scaling in both contexts, now with a **0.05 minimum uncertainty factor**, rather than 0.001. At prior 0.015 the maximum saturated-state penalty is 0.0045. This is an experimental regularization choice, not a rule that a policy preference is always correct. It remains under the existing opt-in `OptionalChoices` setting.

Red/green evidence:

- Two mastery-card ordering cases failed before the change. All **28 setup checks pass** afterward.
- Two root/menu target-prior cases failed before regularization. Two additional saturated-value cases failed before the minimum-factor change. All **eight optional-choice checks pass**.
- **512/512 tactical cases**, 12 nested-menu checks and 40 natural-gain checks pass.
- The first attempt to invoke the tactical suite incorrectly supplied 512 as the game-count argument and failed before testing. The corrected invocation uses 16 variants of the 32 scenarios; its separate completed artifact is `root-prior-floor-tactics`.

## Recorded natural positions

The previously audited cohort is replayed exactly before each diagnosis. Eight positions are evaluated under default, depth-64, no-prior and eight-world settings, with full default-turn continuations.

- Game 16/64 now plays **Anomaly Cleric before Reactor**, preserving the extra crystal in the default configuration. Depth 64 and no-prior do likewise. Eight worlds starts with Crystal, which alone does not establish its eventual ordering.
- Game 14/168 now retains **Infinity Shard**, subsequently banishes a discard Crystal, and plays Infinity Shard. Its independently solved target menu also selects Crystal.
- Game 2/226 now banishes a **Crystal**, plays Reactor Drone, and chooses its resource/banish mode. Using Drone's own resource-producing banish mode is distinct from paying Ko's HP cost to remove the card without playing it.
- Game 17/91 still plays Reactor before a funded Focus separated by an intervening Gatekeeper activation. The current conservative repair does not handle that sequence.
- Mainframe Abbot and Legion Carrier end-turn decisions remain under review.

## Correction: Legion Carrier is not a free complete effect

The preceding audit compared HP/mastery/gems/power/hand but omitted the draw-pile/discard transition. `RevealTopForChampion` **mandatorily reveals/mills five cards**, then optionally selects a champion. Declining the selection does not undo the mill. Thus the two crystals do not establish that the complete action dominates End Turn. No mandatory Legion Carrier guard was added. The earlier report has been corrected.

`PositionReview.State` now records own discard, deck count and unordered deck composition, so this side effect is visible. These are controller-known inventory facts, not hidden draw order. GPU position diagnosis also now infers the root with the model being tested rather than reusing the transcript model's value/probabilities. It records the old/new root values and policy difference; it preserves the transcript fallback only when probabilities agree within 1e-5, otherwise uses the tested model's argmax. Existing same-policy diagnoses remain valid; upcoming trained-model diagnostics must use this corrected tool.

## Another twenty fresh games

Seeds `9074000000000000000 + index`, every ordered distinct-hero pair: **20 games, 5,151 actions, 369 end turns**. Exact replay verifies 13,336 public action-target lookups and **52 end-turn alternatives × 16 public worlds**.

Ko makes 64 previews: **43 banishes and 21 cancellations**. Targets are 33 Crystals, seven Blasters, and one each of The Dispossessed, Shard Reactor and Wraethe Skirmisher. No Infinity Shard or Doom Gate hero-banish appears. The three nonstarter cases have no discard starter target; they are not automatically judged sound, but differ from the previously confirmed availability cases.

Volos uses all modes: 20 heals, four power, ten draws and seven mastery.

Of the 52 end-turn alternatives, 38 occur on actual terminal turns. Nine have no visible first-world effect. Five need individual interpretation: paid Bound for Life, two Pall Shades plays, Cache Warden and Kiln Drone. This screening is not a universal dominance proof.

### Confirmed remaining terminal-override defect

**Game 4, round 10, step 284:** Ko has 20 power versus 20 enemy HP and retains Cache Warden and Kiln Drone. Ending the turn does not win: after the champion target and defense decisions, the opponent reveals Prism and survives at 2 HP.

An isolated CPU-only reflection probe against the cohort's frozen runtime reproduces:

```
SafeTurnGains.HasAlternative = true
TacticalSearch.Find = ShardsEndTurnAction
source fingerprint unchanged
```

The final `HybridLookahead` terminal override checks wasteful Focus but does not honor the free-gain End exclusion. `TacticalSearch.Validate` explicitly checks sampled worlds; its comment correctly disclaims mathematical certainty. The override can therefore undo a valid safeguard because none of its sampled opponent hands contained the actual shield defense.

Next fix: apply consistent admissibility at the final terminal override; separately investigate worst-case public-inventory shield bounds. Do not claim all sampled terminal lines are guaranteed wins. This defect is **not fixed in this iteration**. The three queued strength pilots deliberately retain a fixed source build so model comparisons remain interpretable.

## Completed value training

Collection finalized at **4,000 games / 218,654 rows**. The split is 3,200 training games and 800 held games, with 175,330 training rows and 43,324 held rows. The collection runtime predates the current setup and target-scoring changes; it supplies terminal outcome labels, not unquestioned target-action imitation.

| Candidate | Selected epoch | Held Brier | Equal-game Brier |
|---|---:|---:|---:|
| Original weights | — | 0.154068 | 0.151669 |
| Value head | 7 of 16 | 0.150096 | 0.147167 |
| Shared semantics + value | 8 of 8 | 0.148390 | 0.145442 |

The value-head experiment preserves policy probabilities. The semantic experiment also changes them slightly (held policy KL approximately 0.001766), so its strength effect cannot be attributed exclusively to the critic. Positions within a game are correlated; these are calibration metrics, not match win rates.

Selected hashes:

- Head: `fe8c9904f005c0e34ce7ab1ed1ac5ea90c105e34d9526ed40acb4d573d077813`.
- Semantic: `c9d751fc2987199ab77ef4b38facc77cad54278822b14dd8493f672d36eb3102`.

## Running strength tests

A sequential supervisor runs three 320-game pilots, each with 160 paired seeds and alternating treatment seats:

1. New value head versus original weights, identical current planner on both sides.
2. New semantic/value weights versus original weights, identical current planner on both sides and the same exploratory seed bank as (1).
3. Current planner/original weights versus the published hybrid configuration/original weights, separate fresh seed bank.

These are screening comparisons; promotion requires independent confirmation, tactical regression and fresh-game review. The first two comparisons share seeds and are not independent confirmations of one another. No inference of improvement should be made from an incomplete running score. All jobs inherit the same eight-core affinity, use the headless engine and GPU inference, and preserve the installed AI and published statistics.

Artifacts under `/home/lva/.local/share/shards-training/2026-09-28/ten-hour-improvement/`: `mastery-card-*`, `root-choice-prior-*`, `root-saturated-prior-*`, `root-prior-floor-*`, `outcome-value-{head,semantic}-4000/`, `terminal-override-probe/`, `terminal-override-evidence.json`, and `value-strength-supervisor.json`.
