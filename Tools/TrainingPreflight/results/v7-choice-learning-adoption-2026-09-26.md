# V7 choice learning: continuation decision

V7 is continuing from the generation7330 pilot checkpoint in `main-v7`, under the original43200second campaign ledger. Its launch allocation was18205.367508046seconds; the pilot and earlier experiments remain charged. Training supervisor wrapper PID120584, trainer PID120603, CPU evaluation watcher PID121039. These are launch facts, not perpetual process-status claims.

## Changes

- A zero-initialized four-output Volos head distinguishes heal, power, draw and mastery from the exact public Volos context. Existing weights and input dimensions are preserved.
- The actual learner distribution includes6% uniform mass over legal normal relic actions and2% over legal normal destiny actions when those categories exist. These are per-menu mixture probabilities, not forced assignments of relics to a percentage of games. Stored behavior likelihood and PPO use that same distribution.
- Old retained opponent policies keep zero exploration mass and zero new heads. New trained snapshots retain their actual saved exploration settings. Evaluation does not silently switch the learner's distribution.
- A fused FP32 GPU kernel avoids the substantial overhead measured with a naive implementation. It changes neither rules nor observations. All prior finite-state and behavior-verification guards remain enabled. The pilot acceptance screen used a stricter1e-4 likelihood discrepancy criterion; the inherited runtime stop thresholds remain0.01 for likelihood and0.005 for value.

Identity: `d90a6254cdba784f67d8e487b99d25f279a25e236204820b9f034a169956debb`, schema `shards-real-selfplay-v7`. The V6 host, rules and catalog identities remain unchanged. [Runtime](../experiments/variant_v7_runtime.py), [policy](../experiments/choice_policy_v7.py), [kernel](../experiments/choice_logits_v7.py).

## Evidence and decision

| Check | Result |
|---|---|
| Independent migration comparison | All old weights, retained policies, Adam, counters, RNG and budget preserved exactly. |
| GPU derivative/parity sweep |72cases; maximum gradient error5.722e-6. |
| Frozen actual GPU collector |256complete games, zero learning updates; likelihood/value parity passed. |
| Captured forward microbenchmark |V7/V6 ratios0.904–1.032 across batches32–2048; naive ratios1.257–2.066 were rejected. |
| Charged pilot |77generations,19712complete games,0censors,9504accepted steps,2guarded rejected minibatches.270.265training seconds charged. |
| Pilot behavior likelihood discrepancy |Maximum7.582e-5, within the predeclared1e-4 pilot acceptance gate. |
| Postpilot frozen hero-balanced comparison |640complete games,0censors; V7 score46.5625%, conservative95% bound38.9705–54.1545%. |

The postpilot point score passes the predeclared45% regression-screen floor. **It does not establish statistical superiority or noninferiority.** Continuation is justified as a bounded exploration/representation improvement with preserved numerical and state contracts; the strongest old checkpoints remain available. [Gates](v7-adoption-gates.json), [balanced comparison](v7-pilot-balanced-regression.json), [pilot summary](v7-pilot-training-summary.json), [independent integrity audit](learning-integrity-audit-2026-09-26.md).

The measured pilot throughput was24156retained rows/s versus22680rows/s in the preceding V6 window. Trajectories and CPU load differed; this is not a controlled speedup claim. The controlled benchmark measures forward inference only. [Benchmark](v7-choice-gpu-microbench.json), [GPU parity](v7-cuda-choice-parity.json), [collector smoke](v7-choices-gpu-smoke.json).

An early generation7272 frozen probe found all four Volos modes selected. On769menus where all were affordable, average probabilities were approximately25.8%heal,34.4%power,12.4%draw and27.4%mastery, compared with nearly uniform probabilities in the earlier V6 probe. Several rare normal relic/destiny selections also increased. This establishes differentiated behavior and exposure, not correct tactical choices or balance. [Coverage probe](v7-pilot-choice-coverage.json).

## Ongoing checks

The watcher retains frozen snapshots and evaluates256games each against the retained champion and immutable generation1977 anchor, using reserved seeds starting at `0x7500000000000000`. It first waits300seconds, then uses100000training-game cadence, one CPU thread/host worker, nice10, no CUDA, and bounded child runtime. Final supervised evaluation is outside the training budget. Host balance statistics publish separately by hero cohort every10000completed games, with final partial snapshots.

Full training/statistics integrity findings and remaining gaps are in the [comprehensive audit](training-statistics-audit-2026-09-26.md). The new monitor labels and missing-row fixes do not alter live learner source.
