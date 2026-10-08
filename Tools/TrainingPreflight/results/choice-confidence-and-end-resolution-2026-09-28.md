# Target confidence and end-of-turn resolution

Two experiments now correct the previously recorded Infinity and Shard Seer openings. Natural-game review still finds unresolved choices, including a new Rez Scry/Longshot failure. This is progress within the ten-hour campaign, not a completed strength improvement or a deployment.

Artifacts: `/home/lva/.local/share/shards-training/2026-09-28/ten-hour-improvement`.

## Separate confidence for optional target decisions

`PolicySearchSettings.ChoicePriorScale` defaults to 1, preserving the existing configuration. The experimental profile uses 2. It scales the bounded target-policy term at visible optional menus and at committed activation-plus-target plans; ordinary action priors and private Scry scoring remain unchanged. Terminal wins found across the sampled branches retain precedence. No particular card is forbidden as a target.

The new end-to-end regression reproduces an activation committing to an almost zero-policy-probability Infinity target because the critic estimates about a 0.10 advantage. It exercises both the activation and the subsequent cached menu. With scale 2 ignored, the two seat cases fail. Applying the scale corrects them. Existing cancellation, useful-alternative and saturated-critic cases also run at both scales: **20/20 checks pass**.

An initial fixture accidentally retained a second Infinity in the deck and therefore did not reproduce the intended value distinction. That fixture was corrected before the verified red/green run. Authoritative results are `choice-prior-scale-{red,green}-verified.json`, not the earlier files without `verified`.

Exact natural replay at `public-top-dominion-fresh-review`, game 14 / step 92, now produces **Ko → banish Crystal → play Infinity → End**. The isolated target menu also selects Crystal. Evidence: `choice-confidence-infinity-diagnosis/games.json`.

This confidence change alone does **not** correct the recorded Seer opening.

## Settle the end phase before evaluating it

The Seer diagnosis identifies a separate cutoff error. At `terminal-semantic-fresh-review`, game 3 / step 388, a primitive Ko activation branch is valued around **+0.585** when its 24-step budget stops during damage allocation or shield choices. Finishing that same simulated continuation produces about **−0.693**. Search was comparing an unsettled end phase with completed turns. Increasing depth to 64 fixed this particular opening; more worlds alone did not provide the same correction.

`EndTurnExtension`, default 0, grants a bounded additional budget only when the engine is already resolving End Turn. The tested value is **64 extra decisions**. Damage splits, shields and cleanup effects can settle, then search stops at the first turn-start boundary. Ordinary unfinished hand play retains its original cutoff. This is not an extra full opponent turn and is not unlimited search.

The engine exposes its existing phase flag through a read-only `IsResolvingEndTurn` property. It supplies no card identities or hidden choices and changes no rules. Both original and reference configurations can set the extension independently in the GPU driver.

Four phase-resolution cases fail before implementation. **12/12 checks pass** afterward, covering both seats, enabled/disabled extension, roots inside and before defensive resolution, bounded work, source immutability, and unchanged ordinary hand-play cutoffs. Evidence: `end-resolution-{red,green,green-verified}.json`.

With confidence scale 2 and extension 64, the natural Seer opening now plays **Shard Seer → reveal Infinity → drawn Leshai Knight → Infinity**, instead of paying HP to banish Seer. This verifies that opening, not an optimal complete turn. Solving the old banish menu after externally forcing the old activation still chooses Seer; that is an unresolved separate target valuation.

Both original-weight and semantic-weight **512-case tactical suites pass**, and the prior Infinity and Aegis corrections remain intact. Evidence: `end-resolution-{original,semantic}-tactics`, `end-resolution-{infinity,seer}-diagnosis`.

## Fresh games after each change

Both cohorts cover all twenty ordered distinct-hero pairs with new seeds and the semantic candidate model.

| Configuration | Actions | End Turn decisions | Recorded End alternatives |
|---|---:|---:|---:|
| Target confidence 2 | 6,223 | 420 | 28 |
| Target confidence 2 + end extension 64 | 5,967 | 405 | 21 |

Complete transcripts and alternatives replay through the native engine. These are different seed banks, so their throughput and error counts are not controlled estimates of improvement. Neither cohort replaces published balance statistics.

The combined cohort has 32 Crystal and six Blaster Ko banishes, plus one Kiln Drone from discard and one Cryptofist Monk from discard. No Infinity or Doom Gate banish occurs in these twenty games. Volos uses heal/power/draw/mastery **30/4/10/7** times. Small samples do not prove future errors are eliminated.

Remaining useful investigations:

- Portal Monk is left in hand in game 13 / step 354; its alternative opens a free-recruit menu.
- Pall Shades is left in hand in game 8 / step 237; replay gains three power and draws a card.
- Stolen Futures remains unused across three turns in game 6.
- Several apparent unused-ability errors have no effect: Strategic Mastermind, Soul Syphon, Primus Pilus, and one Deadly Recruits position fail their conditions. Another Deadly Recruits position does open a Warp menu and needs more investigation.

These are deliberately not all labeled dominance violations: draws, market refills, and future deck quality can matter.

## New Rez reproduction

Combined cohort game 5 / step 175: Rez has seven mastery, Longshot in hand, and privately sees **Cache Warden, The Rotten, J Chord** through Scry. The policy bottoms all three. Keeping the first two and bottoming J Chord would leave two valid Longshot targets, both costing at most three.

Across sixteen public-information worlds, the exact legal alternative is:

`bottom J Chord → finish Scry → Longshot → Cache Warden → The Rotten`

It gains **two mastery and four power** without spending the five available crystals. The recorded play instead removes those known targets and relies on unknown cards. This is the next specific policy/search diagnosis; it is not fixed by the current optional-target confidence feature, which deliberately excludes Scry.

Game 10's mastery-four Longshot is not evidence of forgetting Focus: Focus was already spent and only raised mastery from three to four. The ability was still unavailable.

Evidence: `end-resolution-fresh-review/scry-longshot-evidence-verified.json` and the original JSONL transcript.

## Audit output correction

`PositionReview.State` returned references to mutable known-card lists. Its deferred final JSON serialization could therefore display empty `before.knownCenterTop` / `before.knownDeckTop` even when knowledge existed at the recorded step. Those two fields now snapshot arrays, like the other state fields. This affected offline evidence formatting, not live policy inputs or the recorder's immediately serialized JSONL.

The new Scry replay supplies a concrete red/green check: the old stored `before` prefix is empty; the corrected output retains all three revealed definitions after the full game replay completes. See `knowledge-snapshot-regression.json`. Older deferred `before` knowledge fields should not be used as evidence of missing live information.

## Paired strength queue

The preceding semantic-model comparison has now completed: **808–792 over 1,600 games (50.50%)**, paired-bootstrap 95% interval **48.50–52.44%**, paired sign-test p = 0.664. It does not establish improvement from the value-model update. Its frozen planner predates the public-top, Aegis, target-confidence and end-resolution changes. Evidence: `terminal-semantic-followup-1600/assessment.json`.

`run_end_resolution_strength.py` verifies the validation gates, waits on the existing live 1,600-game semantic-model comparison, then runs sequentially:

1. **40-game A/A control:** identical combined configuration and semantic weights on both seats; requires identical paired outcomes, steps and rounds, and 20–20.
2. **320-game combined candidate versus installed configuration:** semantic weights and the experimental planner versus original installed weights / baseline hybrid settings. Shared current engine and public-information sampling code; not a binary-versus-binary installed application test.
3. **320-game end-extension ablation:** semantic weights and confidence scale 2 on both seats, extension 64 versus 0.

Every comparison pairs engine seeds and hero seats while alternating treatment seats. Each writes a paired-bootstrap assessment. The queue is not a claim that the candidate has already won. No model or DLL is promoted from tactical checks alone.

All jobs use the headless native engine, GPU inference and the same eight-core affinity. Main playing sources must remain stable until the queued runtimes have frozen; separate work should use an isolated source tree.
