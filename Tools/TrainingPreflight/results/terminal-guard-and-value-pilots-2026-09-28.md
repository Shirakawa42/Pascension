# Terminal override guard and first completed value pilots

The previous goal iteration made progress. This iteration integrates another reproduced defect fix, verifies the complete recorded turn, completes two model comparisons, and schedules broader validation. Installed AI and published statistics remain unchanged. The ten-hour campaign remains active.

## Terminal override fix

`HybridLookahead` now rejects a terminal-search End Turn override when `TacticalGuards` is enabled and `SafeTurnGains.HasAlternative` finds an existing verified free gain. Previously the ordinary candidates excluded End, but the separate `TacticalSearch.Find` pass could put it back. That search validates sampled hidden worlds, not every possible opponent hand.

The change was first developed in `terminal-guard-worktree` while the three strength pilots retained their existing source build. Integration waited until the third pilot's runtime and source archive were frozen; it checked source hashes before copying the three changed files into the main worktree. It did not alter any running pilot binary or model.

Four regression cases cover both seats with the guard enabled/disabled. The two guarded cases failed before the fix; all four now pass. The disabled profile retains its original End behavior. The integrated build also passes 28 setup, eight optional-choice, 40 natural-gain and 12 nested-menu checks. The GPU 512-case suites are scheduled after the current strength job; they have not yet completed for this final override fix.

### Natural position and full-turn check

Fresh cohort `root-prior-floor-fresh-review`, game 4 / step 284: Ko has 20 power against 20 HP. In the original transcript, the opponent's Prism prevents lethal and leaves 2 HP. A probe of the frozen original runtime confirmed that the free-gain guard returned true while tactical search returned End Turn.

With the fix and original weights, exact replay chooses a purchase first and subsequently plays **Cache Warden → World Piercer → Kiln Drone** before End Turn. Mastery rises from 25 to 28, and Kiln Drone supplies four crystals. The opponent still survives; this is not a newly proven lethal. The turn also includes a buy/banish sequence and ends with four unspent crystals, so the full turn should not be described as optimal. The narrow confirmed improvement is that terminal search no longer suppresses the free-gain safeguard.

The CPU review harness runs the real frozen policy, encodes each actor's current observation, preserves the source fingerprint, and replays complete original transcripts before evaluating alternatives. It records the full alternative turn until a real turn-start event, including defensive responses.

## Completed model pilots

Both comparisons use the same planner on both seats, 160 paired seeds / 320 games, original weights as reference, and the same exploratory seed bank. Intervals bootstrap paired seeds (20,000 resamples); the two comparisons are not independent confirmations of each other.

| Candidate | Wins–losses | Score | Paired bootstrap 95% interval |
|---|---:|---:|---:|
| Value-head update | 152–168 | 47.50% | 43.13–51.88% |
| Semantic/value update | 167–153 | 52.19% | 48.44–56.25% |

The head update does not qualify as an upgrade. The semantic candidate is worth a larger test, but its result does not yet demonstrate improvement. CPU regression work shared the eight-core affinity during the pilots, so their approximately 0.85–0.87 games/s is not a clean throughput benchmark.

These pilots use the target-prior/mastery-order changes **before** the terminal override fix. The larger follow-up uses that fix on both seats. It tests the intended updated configuration, rather than being an exact reproduction of the pilot configuration.

## Recorded positions with all three models

Eight earlier natural positions were evaluated with the isolated fixed planner and each model's own root prediction. Complete default-turn continuations are saved.

- All three models retain the corrected Cleric-before-Reactor ordering.
- The semantic model retains Infinity Shard and selects a Crystal in the tested Ko turn.
- The value-head model reintroduces the bad Infinity banish later in that same turn, despite initially choosing the same resource play. Better aggregate calibration did not prevent this ranking regression.
- The semantic model improves game 17/91: it plays Crystals and Focus before Reactor. Original and head-only models retain the inferior order there.
- Head-only plays the previously skipped Mainframe Abbot; original and semantic models still end. Legion Carrier is still skipped, which remains inconclusive because its mandatory mill changes future draws.

These are diagnostics, not additional independent win-rate evidence.

## Running and queued work

`value-strength-supervisor.json` tracks the final 320-game planner comparison: current planner/original weights versus the published hybrid configuration/original weights. Its frozen build predates the terminal guard fix.

`post-terminal-validation.json` waits for that exact live supervisor, then sequentially runs:

1. 512 tactical cases with the integrated guard and original weights.
2. 512 tactical cases with the integrated guard and semantic weights.
3. Twenty fresh semantic-model games, all ordered distinct-hero pairs, seeds `9077000000000000000 + index`.
4. A **1,600-game / 800-paired-seed** semantic-versus-original comparison with identical fixed planners, starting at seed `9078000000000000000`.

Any tactical failure stops the queue. None of these jobs deploys a model or replaces balance statistics. Do not change the main C#/driver sources while pending jobs still need to freeze their runtimes; use an isolated checkout for independent work.

Artifacts are under `/home/lva/.local/share/shards-training/2026-09-28/ten-hour-improvement`: `terminal-guard-{red,green,natural-green,natural-turn}.json`, `terminal-guard-{original,head,semantic}-positions.json`, `integrated-*-audit.json`, `terminal-guard-integration.json`, the two pilot `assessment.json` files, and the two live supervisor status files. `integrate_terminal_guard.py` has completed; `post_terminal_validation.py` is the queued workflow.
