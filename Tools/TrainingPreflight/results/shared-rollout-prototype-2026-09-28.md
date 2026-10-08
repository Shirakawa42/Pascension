# Sharing identical rollout continuations

This is an experimental performance change, off by default. It does not change the frozen 2,500-game statistics run or the installed game.

The inspected natural positions repeated 31–63% of their final simulated states within a root option across rollout styles. The prototype shares only states created from the **same root option and sampled world**. Styles retain separate paths, counters, stopping conditions and later choices. When they choose different actions, every required copy is made before any branch mutates its parent. Completed owners retain their old state. Different hidden allocations are never merged by similarity or by observation.

Inference is deduplicated only for references to the exact same adapter in one request. That also prevents concurrent encoding of the same mutable adapter. Forced-action collapsing is retained and uses the same grouped stepping path.

The default search is unchanged unless `ShareRolloutStates` is enabled. The feature does not reduce candidates, worlds, depth, styles or menu alternatives, and does not alter weights or rewards.

Validation so far:

- Synthetic predictor: all 68 tactical/future-scry comparisons match actions, paths, horizons, final-state fingerprints and values.
- Real selected neural model, before restoring grouped forced-action collapsing: all 68 comparisons match, maximum value difference zero.
- Real selected neural model with grouped forced-action collapsing: all 68 comparisons match, maximum value difference zero. The ordinary searches counted 11,622 logical transitions. Sharing avoided 3,104 repeated transitions (26.7% of that count, excluding additional root-prefix savings) and 3,509 repeated inference rows.
- Both ordinary menus and experimental future-scry branching are included. All checks assert preservation of the live root.

These counts are not a measured games/second improvement. GPU equivalence, matching complete natural-game transcripts and a controlled throughput comparison remain required. The active statistics run has priority; those GPU checks will run afterward.

Artifacts in `/home/lva/.local/share/shards-training/2026-09-28/ten-hour-improvement`:

- `shared-rollout-audit.json`
- `shared-rollout-neural-audit.json`
- `shared-rollout-forced-neural-audit.json`

`verify_replay_equivalence.py` compares at least 20 completed replay games, identical model/seed/hero schedules and game-rule source archives. It checks chosen actions, recorded gameplay state, legal actions and terminal results while excluding floating-point prediction diagnostics. Its self-comparison against the frozen reference is a parser check only, not optimization evidence.
