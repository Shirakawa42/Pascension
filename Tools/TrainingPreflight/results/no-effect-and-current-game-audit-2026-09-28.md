# No-effect cleanup and current natural-game audit

The experimental hybrid still makes questionable draw, relic and Scry decisions. Removing redundant activations improves the observed action sequences, but has not demonstrated a match-strength improvement. This work is not deployed to the installed game and does not replace the published 2,500-game statistics.

## Changes and validation

`PruneNoEffectPlans` is an optional search setting, disabled by default. It excludes currently inactive, zero-cost destiny effects only when the effect structure proves inactivity. It does not evaluate arbitrary conditions, remove legal engine actions, or assume champion exhaustion is irrelevant. An activation becomes available again when its mastery threshold is reached.

For Ko's sacrifice preview, search removes activation-and-cancel paths only after it has represented every actual target and has another root action. It retains the primitive action when coverage is incomplete. The terminal-search routine separately removes certified empty prefixes from a saved winning line, then revalidates the shortened line before accepting it.

- The focused audit passes **34 cases**, covering both seats, thresholds, incomplete menu coverage, opaque callbacks, terminal-prefix removal and source immutability.
- The pre-terminal version passes both original-model and semantic-model **512-case tactical suites**. The final terminal correction also passes the semantic-model 512-case suite.
- A fresh cohort covers all 20 ordered distinct-hero matchups: seeds `9095000000000000000 + index`.
- With the final correction, its **6,241 actions / 418 End Turn decisions** contain **zero canceled Ko activations and zero below-threshold Synthesis activations**. Four Synthesis activations occur at or above mastery 15.
- Comparing the same seeds before and after the terminal correction preserves every winner and final round. One game drops exactly two actions: Ko's activation and immediate cancellation. The other 19 traces have unchanged action counts.
- The earlier 160-game paired pilot of no-effect pruning scores **78–82 (48.75%)**, paired 95% interval **44.375–53.125%**. It is inconclusive and predates the terminal-prefix correction.

These counts establish removal of the observed unnecessary actions in this cohort, not universal tactical correctness or a measured speed improvement.

## Fresh game review

All transcripts were validated against their recorded terminal outcomes. The screen identifies 60 nonwinning end-turn alternatives; exact replay compares alternatives in 16 public-information worlds. Complete turn sequences in games 0, 2, 6, 15, 17 and 19 were additionally read, covering all five heroes.

| Position | Observed choice | Why investigate |
|---|---|---|
| 15 / 157, round 7, Decima | Ends with Mining Drones in hand | Its immediate effect is one gem and one draw. The policy assigns playing it essentially all probability, but lookahead overrides it. All 16 public replays resolve the draw normally. |
| 17 / 138 and 186, rounds 7–8, Decima | Delays all three relic choices at mastery 10–11 | Recruits Praetorian-01 on round 9. This is a two-turn delay, not a missing recruitment action. |
| 19 / 203, round 8, Rez | Plays Longshot at mastery 6 before Scry | Scry is legal and unused; the center top is unknown. Rez later uses Scry during the same turn. |
| 17 / 314, round 10, Tetra | Leaves Oblivion Gatekeeper unexhausted at mastery 24 | At this mastery its health-loss effect hits the opponent, and it draws a card. Tetra has 42 HP, Decima 27. |
| 2 / 294, round 12, Decima | Leaves Arach Devotees unplayed | Public replays draw potentially useful cards including Swyft, Shard Seer, Fabricator and Praetorian-03. |

GPU diagnosis reproduces the Mining Drones, Arach Devotees, relic and Longshot decisions with depth 64, no policy prior, and eight sampled worlds. The relevant alternatives are included. Gatekeeper changes to activation with either no prior or eight worlds. Its default best leaf values are extremely close: +0.9992170 for ending versus +0.9992247 for activating, before policy regularization. Mining Drones is ranked about −0.9491 versus −0.9291 for ending; the first relic delay ranks the best recruitment about −0.9596 versus −0.9285 for ending. These are critic estimates, not measured win probabilities.

Unused legal actions alone are not proof of a lost win. The ordinary search ends its continuation at a turn boundary and compares critic estimates; raising the nominal action depth does not automatically add a full opponent turn. Paired full-game continuations are recorded separately below.

## Confirmed premature conditional activation

Game 2 / step 230, Ko's round 10, is a stronger finding. Unconditional Conscription activates before two qualifying allies have been played and grants nothing. The recorded turn then exhausts Thornshell Warden, plays Infinity Shard, kills a monster, takes World Piercer and plays it. Delaying Conscription until after those same actions grants **four additional power**.

Exact-key replays of both sequences succeed in **all 16 paired public worlds**. The resulting snapshots differ only in power, 0 versus 4; both paths use the same actions and consume the destiny activation. This establishes missed turn resources, not a demonstrated change in the match winner.

A second occurrence at step 393 is flagged by the transcript: its condition becomes true after later draws. Only two of sixteen public-world copies reproduce the exact recorded subsequent hand sequence; both gain four power when activation is delayed. The other fourteen are blocked by different hidden draws and are **not** counted as successful counterfactuals. The first occurrence supplies the clean regression position.

This remains unfixed by the current no-effect filter: it deliberately does not evaluate callback-based `If` conditions on the pre-activation state. Extending the proof requires respecting effects that inspect post-exhaust state and preserving public-information boundaries. A broader screen finds 132 destiny activations with unchanged selected visible fields, but includes legitimate persistent modifiers; that number is not a count of bugs.

Several warnings are understandable. Stolen Futures below mastery 10 and Synthesis below 15 do nothing. Skipping Bleak Communion at 13 HP avoids a four-HP payment. Bound for Life also costs health. Ko's unusual Kiln Drone banish in game 3 occurs on a turn that reaches mastery 30 and wins, so the expensive-card target alone does not establish a tactical failure.

All four Volos modes appear (38 / 6 / 4 / 4 uses). This establishes coverage in these games, not optimal mode selection.

## Paired terminal continuations

Both subsequent players use the same fixed hybrid. Each opening is tested in 32 paired public root worlds, without reading the actual hidden hand or draw order. Different actions can change later random events; these are estimates of this continuation policy, not optimal-play proofs.

| Position | Forced opening | Wins / 32 |
|---|---|---:|
| Rez 19 / 203 | Longshot / Scry | 0 / 0 |
| Decima 15 / 157 | Mining Drones / End Turn | 1 / 0 |
| Decima 17 / 138 | Praetorian-01 / Praetorian-02 / Praetorian-03 / End Turn | 0 / 0 / 0 / 0 |
| Tetra 17 / 314 | Oblivion Gatekeeper / End Turn | 32 / 32 |
| Decima 2 / 294 | Arach Devotees / End Turn | 15 / 10 |

All **384 continuations** completed, with no censoring. The single Mining Drones win is insufficient to establish a reliable match-strength effect. Arach Devotees has eight paired improvements, three worsenings and 21 unchanged results (two-sided paired sign p = 0.2266). This makes skipping Arach a worthwhile target for further investigation, not a statistically established blunder. Uniform losses or wins in the other positions cannot tell us whether the intermediate move was best. The diagnostic supervisors have finished and resumed the expert-experience collector.

## Separate value-target experiment

`hybrid_learning.py` supports an opt-in round-discounted terminal target for value training. The default discount remains 1, preserving existing queued actor training. At discount 0.985, targets are signed outcomes multiplied by the discount raised to remaining full rounds. Future outcome metadata is used only for labels, never observation features. These discounted outputs are utilities, not calibrated win probabilities.

Both experiments pass their 512-case tactical suites. The value-head experiment scores **80–80 / 160 paired games** against the current semantic teacher, with paired 95% interval **43.125–56.875%**. Semantic-scope training scores **79–81 / 160**, interval **43.75–55%**. Neither demonstrates a strength gain. Held-out target error improves, but the head experiment's saturation does not; neither metric substitutes for game results. Neither candidate is promoted.

## Artifacts

All artifacts live under `/home/lva/.local/share/shards-training/2026-09-28/ten-hour-improvement`:

- `no-effect-terminal-fresh-review`: complete transcripts and exact-key replay evidence.
- `no-effect-terminal-green.json`: focused regression results.
- `no-effect-{original,semantic}-tactics` and `no-effect-terminal-tactics`.
- `no-effect-paired-pilot/assessment.json`.
- `discounted-value-{head,semantic}-4000`, corresponding tactical and paired runs.
- `current-game-position-diagnosis` and `current-game-outcomes-*`: follow-up diagnostics, with completion recorded in `current-game-audit-status.json`.

All evaluation uses the headless engine, GPU inference and at most the existing eight-core affinity. GPU jobs are serialized; the expert-experience collector is suspended during these diagnostic batches and resumed by their supervisor afterward.
