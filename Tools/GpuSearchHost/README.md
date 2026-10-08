# GPU-batched turn planning

This executable runs the real Shards rules without Unity. The Python driver evaluates the frozen policy and value network on the GPU using CUDA graphs. Observations are encoded directly into shared memory; host workers simulate independent game positions and branches. There are no artificial delays or optimizer updates. The explicit `statistics` mode captures real-game telemetry; benchmarks remain isolated.

The production planner is `Assets/Scripts/Shards/AI/PolicyLookahead.cs`. Its default `PolicySearchSettings` consider eight high-prior root actions plus the original sampled action, two public hidden-state samples, and up to eight wrapper decisions per rollout. Continuations follow the frozen policy's greedy choice. At a turn boundary or the horizon, the existing value head evaluates the resulting position from the root player's perspective. A value margin and a small policy prior limit opportunistic exploitation of an imperfect critic.

Near a possible finish, exact rules-based beam search remains available: 512 expansions per narrow/wide attempt, 12 decision steps, and a larger Scry/reorder attempt. A concrete winning line must replay successfully in four public-information samples. Search preserves different first moves while pruning, so a slower setup move is less likely to disappear behind immediate resource gains. This remains bounded approximate planning, not perfect information, multi-turn minimax, or a proof against every hidden state.

`PolicyEngine` uses the same planner in the game with native inference and background execution. The GPU driver is for headless tests; the live game does not start Python or a GPU server.

Build and run from the repository root:

```sh
/home/lva/.dotnet/dotnet build Tools/GpuSearchHost -c Release
/home/lva/.venvs/shards-preflight/bin/python Tools/TrainingPreflight/gpu_search.py \
  --output /tmp/unique-search-evaluation --mode paired --opponent terminal \
  --games 160 --batch 80 --candidates 8 --depth 8 --worlds 2 --terminal-nodes 512
```

Modes:

- `baseline`: sampled network on both seats, no search.
- `greedy-paired`: highest-probability network action versus sampled network.
- `paired`: turn planner versus `--opponent network`, `greedy`, or `terminal` (the previous finish-only search, original 1,536-node budget).
- `both`: turn planner on both seats; useful for self-play throughput.
- `tactics` / `tactics-baseline`: engine-verified hero scenarios, additional cards and shuffled hands. `--variants 16 --samples 8` runs 4,608 attempts. `--case ID` isolates a failure.
- `optimization-audit`: compare state, observations, legal actions and transitions against the frozen pre-optimization copier; also compare terminal-search choices.
- `copy-audit`: microbenchmark the typed reflection and optional headless compiled copiers.
- `replay --replay FILE`: reproduce selected original game indices/seeds/heroes; `review: true` records public decision traces and copied-state counterfactual checks. `--review-depth 64` adds passive longer-horizon diagnostics for the recorded probe positions, without changing the acting policy.
- `statistics`: identical all-turn search on both seats, unique seeds, randomly ordered balanced hero pairs, full acquisition/strategy/victory telemetry.

Each match cohort uses every ordered distinct-hero pairing equally. Timing/strength modes play each seed twice with opposite treatment seats and separate action RNG streams per seat. **Statistics mode instead uses one unique engine seed per game**, with exactly equal counts of all 20 ordered distinct-hero pairs in a separately shuffled schedule. Use a positive multiple of 40 games. Frozen host binaries and source snapshots are saved in each new output directory, preventing subsequent builds from changing a running test.

The driver rejects existing output directories. Native/GPU probability and value parity is checked before consuming the early decisions and again for newly encountered hero/decision contexts. The fused logits kernel requires contiguous GPU tensors; do not remove those conversions. Tactical tests verify hidden-state perturbation invariance, transition parity, and preservation of the real game state.

Reports are isolated from the balance window unless `--mode statistics --campaign PATH` is explicitly selected. Strength cohorts with different agents on the two seats cannot be published as balance data. Compare speed at the same settings with competing jobs stopped. Timings omit CUDA setup, recorded separately. Statistics publication occurs every 100 completed games; full strategy and victory attribution is reconciled at completion. Hypothetical search branches never reach the collector.

The driver defaults to eight workers and server GC. It pins its entire process tree to at most eight logical CPUs, selecting one per physical core; `DOTNET_PROCESSOR_COUNT` also caps GC/thread-pool sizing. `--workers` accepts 1..8. GPU inference stays in FP32 with native parity checks.

The optimized planner uses typed card/container copies, a reusable per-thread identity table, allocation-free beam pruning, and an exact action-prefix cache between narrow/wide search attempts. It preserves list versions and graph aliases needed by paused effect iterators. The cache never merges different sequences using incomplete state hashes. `--copy compiled` additionally compiles field visitors in the headless .NET host; the game retains its portable reflection fallback.

For a new 5,000-game statistics cohort:

```sh
/home/lva/.venvs/shards-preflight/bin/python Tools/TrainingPreflight/gpu_search.py \
  --mode statistics --games 5000 --batch 160 --workers 8 --copy compiled \
  --seed 8793210000000000000 --output /absolute/path/to/new-cohort \
  --campaign /absolute/path/to/monitored-campaign
```

The output includes frozen policy/host binaries, source snapshots, native/GPU parity metrics, raw acquisition aggregates, one strategy record per completed game, and `statistics.json`. Publication rejects duplicate seeds, unbalanced final hero counts, censored/discarded games, mixed sessions and mismatched outcome totals. Only the new cohort appears in the campaign statistics view.

The 2026-09-27 manual review found existing depth-8 premature-end and sampled-fallback/concession failures. Faster execution preserves these decisions; see `Tools/TrainingPreflight/results/manual-game-review-2026-09-27.md` before interpreting fine balance differences.
