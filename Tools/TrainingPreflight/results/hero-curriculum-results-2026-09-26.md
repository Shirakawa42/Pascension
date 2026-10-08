# Mixed hero training continuation

The main run now uses **75% uniformly assigned distinct hero pairs and 25%
ordinary drafts**. This was adopted for broader training/data coverage after
passing the predeclared operational comparison gates. The short experiment
does **not** establish a playing-strength improvement.

Both branches started from the complete generation3368 V4 checkpoint: identical
policy, Adam moments, RNG, archive, counters and learning configuration. The
two300-second grants each reserved30 seconds for shutdown; all actual learning
time was charged to the original shared12-hour ledger.

| Branch | Charged seconds | Generations | Retained rows | Completed training cohorts' games |
| --- | ---: | ---: | ---: | ---: |
| Normal draft |270.2215 |69 |5,348,424 |17,664 |
|75% random /25% normal |270.3008 |47 |4,049,430 |12,032 |

The machine was contended by `bg3_dx11` on Windows during these pilots.
Throughput fell substantially relative to the earlier quiet measurements.
These are equal wall-time branches under shared hardware, not an isolated
throughput measurement or replicated causal test of the curriculum.
Neither branch had a capped training game. The unchanged PPO minibatch guard
rejected one baseline minibatch and two mixed minibatches. Maximum mixed
behavior-log-probability error was0.0002423, below the unchanged0.01 limit.

## Frozen comparisons

| Predeclared test | Games | Mixed policy score | Conservative paired95% bound | Censors |
| --- | ---: | ---: | ---: | ---: |
| Equal allocation over all20 ordered hero pairs |4,000 |50.325% |47.288–53.362% |0 |
| Ordinary, policy-selected drafts |4,096 |50.537% |47.536–53.538% |0 |

The balanced test uses100 distinct paired engine seeds per hero cell, with
disjoint seed blocks between cells. Each seed has both physical seat assignments.
Only the two initial legal hero choices are imposed. Natural retention forces
neither choice. Both evaluations strictly load each checkpoint's identity and
use the reviewed natural-setup host; training randomization never leaks into
ordinary evaluation.

The protocol required a primary point score at least50%, a natural-retention
point score at least45%, complete evaluations with no censors, and verified
coverage/correctness. Those operational gates passed. Both95% intervals include
50%, so the strength difference remains inconclusive. The45% threshold is not
a statistical noninferiority claim.
[Protocol](hero-curriculum-protocol-2026-09-26.json),
[selection record](hero-curriculum-selection-2026-09-26.json).

## Coverage and monitoring

The pilot's passive counters recorded9,210 completed random-assignment games,
with all20 ordered pairings. Hero appearances were Decima3,724, Tetra3,635,
Volos3,637, Ko Syn Wu3,680 and Rez3,744. It separately recorded3,062 completed
ordinary-draft games. These real simulated-game counts include completed games
from the final collection whose PPO work was discarded at the deadline;
16 unfinished games have no invented outcome.

The [dedicated statistics window](http://localhost:8768/statistics) keeps random,
normal-draft, earlier training, and frozen-evaluation observations separate.
Each live cohort publishes every10,000 completions plus final partial snapshots.
The approximately100,000-game recent window is a retention target, not a
minimum required before data appears. The page shows existing game artwork,
rules previews, exact per-hero acquisitions, coverage and supplied intervals.

## Correctness and continuation

The real256-lane frozen GPU smoke contained202 assigned and54 natural games,
covering all20 assigned pairs. Every retained likelihood passed; zero imposed
draft decisions appeared in PPO experience. Final cohort histograms matched
the original assignments. Models, actor buckets, checkpoint and ledger were
unchanged. [GPU check](hero-curriculum-gpu-smoke.json).

Forced setup submits the normal legal draft actions without advancing engine
RNG. Adapter cap counters include them, while policy counters/experience do
not. Censored traces preserve setup/replay provenance. CPU tests and exact
transport comparisons cover reset, hold, partial batches, statistics separation,
strict migrations and paired evaluation allocation.

The selected generation3415 mixed checkpoint resumed as `main-v5`, with
32,932.8476 training seconds remaining at launch. The shared ledger remains
authoritative. Runtime source hash:
`93358e40bfb57b975037803e7d2caca870c6c9541220aa40c2d0477e7c3794ec`.
Host hash:
`0b00ef87c842c320f368367be73349076902b74fd49355dcfd3824eccdfee797`.

A further [refill feasibility audit](refill-feasibility-2026-09-26.md) identifies
terminal-drain occupancy as a possible improvement. It is not adopted: fixed
episode admission, seed progression, storage capacity and changed update cadence
need separate correctness, throughput and strength tests.
