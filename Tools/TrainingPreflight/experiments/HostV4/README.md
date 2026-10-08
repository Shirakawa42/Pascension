# HostV4: exact counts and batched outcome statistics

This is an isolated candidate, with no live adoption or campaign checkpoint publication. It preserves `shards-observation-v3`, all feature meanings/dimensions, card order, hero setup, and rule sources. Its only encoder change replaces seven `OwnedCount` traversals with the [exact one-pass counter](FactionCounter.cs). [Program.cs](Program.cs) adds observation-independent event/outcome hooks to the frozen v3 host; Adapter, transport, rules, and existing selftests remain linked frozen sources. Original HostV3 sources and DLL remain unchanged. [manifest.json](manifest.json) records final source and binary hashes.

[TrainingStatistics.cs](TrainingStatistics.cs) observes accepted acquisition events and terminal outcomes. It does not add model inputs or change actions. The explicit training-only Python wrapper enables statistics; all inherited statistics settings are stripped for evaluation constructors. Output lives in `<run-dir>/training-statistics/`. The exact [fixture](statistics-fixture.json) is the dashboard schema contract.

Per session, an atomic cumulative `session-*.json` is published initially, after each 10,000 true terminal games, and at close. A background writer serializes once and writes the same bytes to immutable `history/` files, retaining the newest 512 per process. One pending snapshot is coalesced; incomplete histories are discarded and same-batch lane arrays reused across resets. Final close allows two seconds for the publisher; a timeout is reported on stderr. An abrupt process kill can lose statistics since the latest published snapshot. No outcome is fed back into gameplay.

Rows distinguish selected paid/free `buy` and `fastplay` actions from unmatched `effect_acquire` / `effect_fastplay` event paths, plus actual `relic` and `destiny` events. An effect path is not a claim about final ownership. Each row records player exposures, distinct real-game clusters, wins/draws/losses, picks, acquisition rounds and known costs. Caps remain separate unknown outcomes; unfinished reset games do not enter outcome rows. Hero-specific rows, hero-seat/matchup totals, final-round histograms, and terminal state sums are included. These are **simulated training-pool outcomes**, including completed simulations later discarded by PPO, under changing policies and uncontrolled archive assignments. They do not establish controlled strength or causal card effects; opportunity counts are unknown. Snapshot and history failures are isolated from game execution and reported independently.

Final telemetry correctness passed: typed event/outcome fixtures cover all six paths, duplicate same-card purchases by both seats, same-hero clusters, caps, partial resets, array reuse, terminal sums, final flush, 10k publication, and publication error isolation. [Result](statistics-selftest.json). Three-way V3/V4-off/V4-on wire equality covers pipe/shared, fused/unfused, holds/resets/terminal credit, and the exact 3,713-action learned-cap replay. [Result](statistics-wire-differential.json). Current-binary privacy/alias/Prism/Yggdrasil checks also passed. [Result](v3-selftest-statistics.json).

| Current V4 with statistics | Median request speedup over V3 | Individual ratios | Extra cost over V4-off |
| --- | ---: | --- | ---: |
| Shared, 256 active lanes | 1.085× | 1.125, 1.023, 1.085 | Median +0.53%; range −1.74%..+6.99% |
| Shared, 8 active / 256 | 1.006× | 1.006, 0.994, 1.047 | Median +0.49% |
| Actual 10k publication workload | 1.026× | One bounded comparison | +5.64% |

The [current net-cost comparison](statistics-request-benchmark.json) uses matched legal streams, rotating execution order, one low-priority CPU worker, shared transport, fixed JIT, and no GPU. Its publication case creates 10,438 actual terminal games after an acquisition prefix, observes the nonfinal publication at 10,182, then verifies final flush and immutable history. The final file contains 251 acquisition rows and is about 406 KB. Concessions make this a deliberately terminal-heavy publication check, not a trained-policy sample. Every response byte remains identical. Timing excludes Python action selection/parity checks and final process close; periodic snapshot enqueue and concurrent serialization/publication occur inside the active workload. Other training work may contend.

Using the earlier quiet-window host fraction 33.23%, the current full-active median implies a conditional generation speedup of about 1.027× (2.6% less wall time), not a measured campaign gain. Held tails, eight production workers, runtime tiering, and the newer epoch count can change this. A same-frozen-policy GPU pipeline comparison is still required before adoption.

The following measurements describe the **earlier counter-only binary**, retained for attribution; their binary hash differs from the final telemetry candidate.

Correctness passed:

- Existing v3 targeted selftests: legacy-feature equality, the temporary-card alias distinguished, exact Prism/Yggdrasil counts, both-seat hidden composition privacy, and catalog overflow rejection. [Result](v3-selftest.json).
- Full wire payload and deterministic counter equality over 385 responses × eight lanes × pipe/shared × fused/unfused. This covers reset, observe, selective holds, real terminal outcomes, and automatic resets. [Result](wire-differential.json).
- All 3,713 actions from the previously captured learned cap trace replay identically, with no earlier terminal outcome, the same final cap and zero reward, and identical cleared lifecycle values when the fresh reset lane is held. This checks a real learned action history, including later-game collections. The trace is a correctness fixture, not a timing workload. [Same result](wire-differential.json).
- The copied counter source is identical to the separately tested [all-189-definition probe](../faction_count_probe/README.md), covering 2,646 membership comparisons, 3,780 owned-zone/temporary fixtures, 100 mixed collections, and 1,177 legal replay rows.

Paired request timing uses identical seeded legal action streams, rotating which process runs first each request, with one low-priority CPU worker and no GPU calls. Every returned payload is compared outside the timed request. Shared transport uses preallocated NumPy buffers and the existing span-copy publication. The request interval includes command write, host step/encode/publication, response read/copy, and framing checks; Python action selection and parity comparison are excluded. Other campaign work may contend.

| Workload | Median request speedup | Three paired ratios | Interpretation |
| --- | ---: | --- | --- |
| Shared, 256 active / 256 lanes | 1.150× | 1.156, 1.095, 1.150 | About 20% less encoding time; total request gain includes noisy changes in unchanged work. |
| Shared, 8 active / 256 lanes | 1.055× | 1.012, 1.097, 1.055 | Held lanes already skip encoding, so payload transfer and fixed costs dominate. |
| Pipe, 64 active / 64 lanes | 1.088× | 1.058, 1.088, 1.106 | Transport reduces the fraction addressable by the counter. |

These [fixed-JIT results](request-benchmark-fixed-jit.json) use 64 warmup and 192 measured requests per process, three repeats, and disabled tiered compilation/PGO. The stream buys and plays real cards, occasionally concedes to exercise resets, and has a median 13 permanent owned cards. It is not a trained-policy population sample, and workers=1 differs from the campaign's workers=8. The full current catalog and observation bytes match exactly.

A further [default-JIT check](request-benchmark-default-jit.json), with 512 warmup and 128 measured requests, produced 2.185×, 1.218×, and 1.142× full-active request ratios. The first also sped unchanged engine-step work by 36%, showing that tiering/contention still distorts attribution. Do not use its 2.185× outlier or overall median as a forecast. Later repeats support the direction, but a mature default-runtime comparison under production worker settings remains necessary. No claim that all observed request gain comes from the counter is justified.

For campaign context, the [last 20 whole generations inside the coordinator's quiet v3 windows](reference-host-fraction.json), generations 2042–2061, took 45.950 s, including 15.269 s measured host time: **33.23%**. Removing all host time would therefore cap generation throughput at **1.498×**, holding other phases constant. Transferring the measured full-active request ratio 1.150× gives the conditional Amdahl estimate `1 / (1 − 0.3323 × (1 − 1/1.150)) ≈ 1.045×`, about 4.3% less generation time. The tail ratio gives about 1.018×. Neither is an end-to-end training measurement; lane occupancy, workers, runtime tiering, and changed learner epoch count affect the actual result.

The independent [runtime](../variant_v4_runtime.py) pins both the exact v3 lineage and final V4 binary, includes every new execution source in its identity, and enables telemetry only for the actual training Host constructor. The [migration helper](../migrate_runtime_v4.py) changes identity/provenance only: every learned parameter, optimizer value, RNG state, archive, seed, original metadata, and charged-budget counter is protected by a canonical full-payload digest. Publication requires the existing inactive ledger lock, a new destination, and strict target reload. No observation/model surgery or fresh budget is performed. CPU fixtures publish only temporary synthetic checkpoints. [Tests](../test_runtime_v4.py), [test output](runtime-cpu-tests.txt).

Reproduce from repository root:

```sh
DOTNET_PROCESSOR_COUNT=1 nice -n 10 /home/lva/.dotnet/dotnet build Tools/TrainingPreflight/experiments/HostV4/TrainingHostV4.csproj -c Release --nologo -m:1 -v:q
DOTNET_PROCESSOR_COUNT=1 SHARDS_SPLIT_BRANCHES=8 nice -n 10 /home/lva/.dotnet/dotnet Tools/TrainingPreflight/experiments/HostV4/bin/Release/net8.0/TrainingHostV4.dll v3selftest
OPENBLAS_NUM_THREADS=1 OMP_NUM_THREADS=1 nice -n 10 /home/lva/.venvs/shards-preflight/bin/python Tools/TrainingPreflight/experiments/HostV4/check_and_benchmark.py --mode check --output /tmp/host-v4-check.json
OPENBLAS_NUM_THREADS=1 OMP_NUM_THREADS=1 nice -n 10 /home/lva/.venvs/shards-preflight/bin/python Tools/TrainingPreflight/experiments/HostV4/check_and_benchmark.py --mode benchmark --output /tmp/host-v4-bench.json
DOTNET_PROCESSOR_COUNT=1 SHARDS_SPLIT_BRANCHES=8 nice -n 10 /home/lva/.dotnet/dotnet Tools/TrainingPreflight/experiments/HostV4/bin/Release/net8.0/TrainingHostV4.dll stats-selftest /tmp/statistics-fixture.json
OPENBLAS_NUM_THREADS=1 OMP_NUM_THREADS=1 nice -n 10 /home/lva/.venvs/shards-preflight/bin/python Tools/TrainingPreflight/experiments/HostV4/benchmark_statistics.py --output /tmp/host-v4-statistics-bench.json
PYTHONDONTWRITEBYTECODE=1 DOTNET_PROCESSOR_COUNT=1 OMP_NUM_THREADS=1 MKL_NUM_THREADS=1 OPENBLAS_NUM_THREADS=1 PYTHONPATH=Tools/TrainingPreflight:Tools/TrainingPreflight/experiments nice -n 10 /home/lva/.venvs/shards-preflight/bin/python -m unittest test_runtime_v4 -v
```

Add `--default-jit --workload full --warmup 512 --steps 128` for the default-runtime diagnostic. The build writes only HostV4 output; it does not reference a HostV3 project or rebuild its binary.
