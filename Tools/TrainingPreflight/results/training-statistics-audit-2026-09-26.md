# Training and statistics audit

The inspected recorded training generations, raw statistics windows, and independent learner tests did not reproduce a current learning/accounting defect. Three statistics presentation defects were fixed. This is evidence within the tested scope, not a guarantee of no remaining game or strategy bugs.

## Corrected monitoring

1. Fast-play entry is no longer always labelled temporary ownership: later effects can retain the card.
2. Per-hero filters now retain eligible unobserved choices, with zero acquisitions and unknown outcome scores. They never borrow global wins, acquisitions or rounds; foreign-hero relics are excluded.
3. Exact terminal training rounds and latest pre-action frozen observations have distinct labels.

Training charts additionally expose behavior-value parity, accepted optimizer steps and rejected minibatches. Text explains that loss/KL summarize attempted minibatches, entropy includes mandatory actions, value loss is a clipped training objective, and archive scores compare changing opponents.

Both views are served directly from the updated HTML; reload the [monitor](http://localhost:8768/) and [statistics gallery](http://localhost:8768/statistics). No live learner source was edited for these display fixes.

## What was checked

| Area | Verification and limits |
|---|---|
| Raw game outcomes | All4published sources rebuilt from raw windows/reports. Seat wins plus draws match completed games. Training has2player outcomes per game; frozen rankings track1learner. |
| Heroes, seats, matchups | Per-hero totals and oriented matchup rows reconcile. Random and natural cohorts remain separate. Policy versions and archive participants still confound balance interpretation. |
| Cards, relics, destinies | Every ranking and per-hero row reconciles. Repeated acquisition events are separate from once-per-player-game outcome counts. All15active relics have acquisition exposure; that does not demonstrate competent use. |
| Sampling and uncertainty | Censors are unknown, not draws. Duplicate frozen reports, incompatible identities and intervention cohorts are excluded. Confidence units respect game/pair clustering. Rankings are observational and bounds are pointwise. |
| Live counters | 7,688 recorded current-contract generations checked for terminal/censor partition, action-histogram totals, archive subset, behavior version, cumulative games/rows, timing partitions, finite metrics and state, legal metric ranges. Historical branches are inspected separately. |
| Budget | Original43200second allocation retained; discard-event sums and finalized outcome partitions reconcile; closed sessions have no recorded overruns. Historical failed experiments remain charged. |
| Learning | Deciding-seat terminal reward, archive-row exclusion, immutable behavior policy, actual mixture likelihood, correct derivative, rejected-update immutability and checkpoint/Adam/RNG continuation tested. |
| GPU | Existing72-case numerical sweep and256-game real collector smoke retained as evidence; no competing GPU audit was launched during training. |

Detailed sources, exact commands and caveats: [statistics semantics audit](statistics-semantics-audit-2026-09-26.md), [learner integrity audit](learning-integrity-audit-2026-09-26.md), [counter audit evidence](training-counter-audit-2026-09-26.json), [reproducible counter checker](../experiments/audit_training_counters.py), [live raw-statistics recheck](statistics-semantics-probe-recheck-2026-09-26.json).

Validation in this work:64CPU learner/accounting contracts;55monitor/statistics/artwork/browser tests;4counter-checker tests that deliberately inject corrupted counts, nonfinite metrics and invalid empty-update summaries. All passed. Browser checks include desktop and mobile layouts. A historical deadline-before-update generation correctly has no loss metrics and zero attempted minibatches; the audit accepts that explicitly rather than inventing zero-valued losses.

## Reading the current rankings

The inspected random-hero window had104947games and roughly42000appearances per hero: Tetra62.1%, Decima52.5%, Volos49.5%, Rez43.5%, Ko Syn Wu42.5%. This indicates performance differences under these evolving policies. It does not separate hero strength from competence at using the hero.

The natural-draft window is strongly selected: Tetra62749appearances, Decima58430, Volos4267, Ko167 and Rez125. Ko/Rez scores of approximately2.4%/1.6% in that source are **not comparable** to the much better-covered random assignments. The frozen1024-game gallery at this inspection covered Tetra510times, Decima508 and Volos6; it cannot rank Rez or Ko at all. Fresh hero-balanced frozen evaluation is the appropriate strength check.

Terminal Crescents' approximately74.6% acquisition-associated score in the random window is not a causal relic advantage. Hero, mastery, survival to acquisition, deck and policy all affect that conditional sample. Similarly, Whatever it Takes' approximately70.1% acquisition score may partly reflect which games make its selection attractive. Training pools do not record opportunity denominators, so their pick rate remains unknown rather than a fabricated acquisition/game ratio.

Terminal summaries are internally consistent: random-window winners average21.6mastery and29.8HP versus losers'16.3mastery; average final permanent collection size21.1. These describe completed games, not recommended targets. A high winner HP does not by itself establish that every HP-cost action is bad.

These figures are a moving-window inspection, not permanently current values. A subsequent10000-game publication can replace10000older games while leaving the displayed total unchanged; draws and rankings can therefore change with an unchanged sample count.

## Remaining gaps and decision

The game contains interactions not exhaustively covered by tests. Public observation truncation, policy memory limits, rare menu branches and optimal sequencing are not certified by finite losses or coverage. Acquisition data cannot prove relic activation quality. Self-play can reinforce shared strategic weaknesses; broad legal-action exposure is not mastery.

V7 continues under the original budget after its validation gates. Its640-game balanced comparison scored46.6% against the prechange model, with95% bound39.0–54.2%: no strength improvement is established yet. Old checkpoints remain retained and frozen evaluation continues. [Decision and evidence](v7-choice-learning-adoption-2026-09-26.md).

Final live check: main V7 generation 7432, 26,112 completed continuation games, zero censors, 12,616 accepted optimizer steps and six guarded rejected minibatches. Weights/Adam finite; CPU watcher evaluating its first retained snapshot. Approximately 4.96 training hours remained in the original ledger at that inspection.
