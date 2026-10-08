# Fresh Shards 1v1 training and preflight

This workspace contains the systems investigation and the subsequently
implemented real-outcome trainer for this computer's RTX 5090 and Ryzen 7 5800X.
Both use the current C# engine in 1v1 Duel mode, enabling all DLC including Duel
of Doom. Neither depends on the deleted AI implementation or its results.

**Current continuation, 2026-09-26:** observation schema v3 adds seven authorized own
Allegiance counts in proven-unused input columns, retains the 2,048-feature
model, and uses full IEEE FP32 matmuls plus validated contiguous rollout copies.
The prior v2 run stopped on its unchanged behavior-likelihood guard. Its last
checkpoint was migrated with all learned state and the shared budget preserved.
The V4 host added exact single-pass faction counting and passive statistics
every 10,000 completed games. The current V5 continuation uses75% uniformly
assigned distinct hero pairs and25% ordinary drafts, with separate statistics.
Its entry is [variant_v5_entry.py](experiments/variant_v5_entry.py); older entry
points retain their strict identities. The short comparison found no clear
strength difference; the curriculum was adopted for demonstrated coverage after
passing the predeclared operational gates. See the
[hero curriculum results](results/hero-curriculum-results-2026-09-26.md) and
[continuing improvement report](results/continuing-improvement-2026-09-26.md).

As of **2026-09-26**, real self-play collection, per-seat terminal credit, PPO,
frozen opponents, recovery, supervised budget enforcement and a local dashboard
are implemented. Reviewed revision 2 uses training identity
`shards-real-selfplay-v2` and explicitly excludes administratively capped episodes
under a persistent censor-count guard; host/rule limits remain unchanged.
The authorized allocation is **12 hours shared by all real
learning pilots and the main run**; later frozen evaluation may run longer.
The historical preflight commands still perform no outcome learning. Pilot
outcomes and current launch details are recorded in the [campaign launch report](results/campaign-launch-2026-09-26.md); implementation and
short correctness tests do not establish playing strength or multi-hour stability.
Read [the training contract](training-contract.md) for the exact semantics.

## Real training path

| Component | Implemented behavior |
| --- | --- |
| [Schema-v2 host](Host/CONTRACT.md) | Authorized `2048 / 64×32 / 64` observations, exact legal choices, two seats, all DLC, selective stepping/holding through opcode 5 |
| [Learning policy and PPO](learning_model.py) | Shared actor/learner probability distribution, card embeddings, bounded numeric transform and value head, actual stored behavior actions/log-probabilities, guarded clipped PPO |
| [Adaptive actor](adaptive_actor.py) | Frozen CUDA graphs in batch buckets; packs selected active lanes, preserves original action slots, excludes padded rows from experience |
| [Complete-episode storage](learning_rollout.py) | Owned GPU inputs/behavior packets, deciding-seat credit, fixed behavior/opponent versions, finished-lane holds, no overwrite; all capped-lane rows removed before learning |
| [Orchestration](train_campaign.py) | Current self-play plus a frozen archive, all-row likelihood verification before updates, shuffled epochs, checkpointing and inline frozen champion comparisons |
| [Persistence and budget](campaign_state.py) | Exclusive shared allocation, monotonic charging, persistent attempt/censor accounting, atomic checksummed finite checkpoints, identity matching and crash accounting |
| [Supervisor](supervise_training.py) | Independent deadline/heartbeat process, graceful stop then owned-process-group cleanup, no automatic retry |
| [Frozen evaluation](learning_eval.py) | Matched seeds with swapped seats, zero updates, conservative paired-seed score bounds, optional legal-opportunity/selection counts |
| [Main launch and final audits](run_campaign_with_audit.py) | Supervised training followed by two frozen learner-versus-champion/initial audits outside the training ledger |
| [Local monitoring](monitor_training.py) | Read-only loopback dashboard, persistent resource sampling and downloadable diagnostic artifacts |
| [V3 continuation](experiments/variant_runtime.py) | Explicitly pinned runtime, separate host binary, seven own-Allegiance features, IEEE precision and reviewed data-copy/validation optimizations |
| [V4 host and runtime](experiments/HostV4/README.md) | Same observation/learning semantics, exact one-pass faction counts, batched outcome/acquisition statistics |
| [V5 hero curriculum](experiments/HostV5/PYTHON-RUNTIME.md) |75% uniform distinct-hero setup /25% natural draft, imposed setup outside PPO, strict migrations and separate natural/balanced frozen evaluation |
| [Balance monitoring](results/balance-monitoring-2026-09-26.md) | Hero/relic/card/destiny tables, exact per-hero acquisitions, matchup matrix, coverage and uncertainty; training setup populations and frozen evaluations stay separate |
| [CPU shadow evaluation](experiments/shadow_watch.py) | Bounded frozen champion/anchor comparisons, hero/seat coverage, legal-choice entropy and value diagnostics on spare CPU capacity; no optimizer or budget writes |

The actor and learner use the same `LearningPolicy`, including the fixed prior:
play `+1`, focus `+0.5`, end turn `−1`, concede `−12`, page `−1`, other kinds `0`.
Every legal choice remains available. The prior is part of both behavior and
learner probabilities; the historical exercise actor's `logits * 0.02` is absent.
Numeric features use signed `log1p` after an explicit ±1000 clip; candidate kind
bits and categorical card ID are preserved. Widths 128 and 256 share the same
architecture; the value head defaults to `tanh` for terminal utilities in `[-1,1]`.

One generation freezes acting weights and drains one episode attempt per resident
lane. Finished or capped lanes hold at their reset observation while other games
continue. Only learning-seat decisions enter the owned store; revision 2 then
removes **every row of each capped episode** before advantage normalization,
behavior verification or optimizer use. Retained returns are that seat's
terminal utility with `gamma=lambda=1`; advantages are `return-old_value`,
normalized across the retained completed batch. A frozen opponent's rows are excluded.
The trainer verifies every stored behavior likelihood before updating. Final
minibatches are filled with uniformly sampled extra rows, retaining every real
row and reporting repeated uses as optimizer example-passes.

This is sampling conditional on completion, with a possible selection bias; a
cap is never assigned a win, loss, draw, zero return or fabricated bootstrap.
Each cap saves its full seed/action trace and seat/archive metadata. The shared
ledger counts finalized attempts, true completions, censored games, retained
rows and excluded rows. Before any optimizer update, training stops at four
censors in the conservative trailing 4096-attempt window. The oldest overlapping
collection is included whole; windows and lifetime totals persist across runs.
Caps first observed in a generation later discarded at deadline remain in traces
and discard metrics, outside this finalized-cohort window; none of that
generation's rows is trained.
An all-censored collection remains unsealed and cannot produce an update.

Optional `captured_backward` computes loss/backward/gradient checks in one graph,
then permits an eager fused AdamW update only after validation. A speculative
log-ratio clamp at ±20 is inactive for every accepted batch under the default
±10 raw-ratio guard. KL/ratio rejection makes no parameter or Adam change.
Nonfinite data, gradients, parameters or optimizer moments stop the affected
run. This is genuine PPO over stored behavior actions; `LearnerLoad` remains a
separate historical fabricated-target benchmark.

## Readiness evidence and recent optimizations

The coordinator verified the following **91-test readiness inventory**. Changed
revision-2 units passed their focused tests; unchanged model/adaptive tests retain
their earlier passing evidence. This is an incremental verification record,
not a claim that one fresh combined suite or a multi-hour campaign passed.

| Tests | Count | Coverage |
| --- | ---: | --- |
| [Model/PPO](test_learning_model.py) | 13 | Actual behavior probability, extreme inputs, frozen actor, graph refresh, accepted objective/gradient/update parity, rejected-update immutability, recovery |
| [Adaptive actor](test_adaptive_actor.py) | 4 | Original lane/action mapping, padding exclusion, subset likelihoods, ownership and refresh |
| [Budget/checkpoint](test_campaign_state.py) | 27 | Exclusive locking, cumulative charging, crashes/reboots, atomic failure handling, identity/checksum/finite-state rejection, RNG/optimizer recovery and persistent censor counters/windows |
| [Credit/storage](test_learning_rollout.py) | 5 | Deciding-seat returns, unresolved/truncated rejection, owned buffers, capacity and packed-row episode mapping |
| [Collector/evaluation](test_learning_collection.py) | 16 | Frozen-opponent exclusion, defense ownership, held lanes, stopping, rewards/counters, whole-episode censor removal, mixed/all-censored cohorts, trace capture and swapped-seat scoring |
| [Supervisor](test_supervise_training.py) | 2 | Owned-child deadline enforcement and no automatic retry |
| [Monitor](test_monitor_training.py) | 11 | Bounded incremental logs, restart persistence, active/historical failure display, censor metrics/traces, unresolved evaluation intervals, loopback HTTP and download allowlist |
| [Audit orchestration](test_campaign_audit.py) | 4 | No evaluation/retry after failed training, ledger-free frozen commands, retention of completed reports on audit failure, stop between audits |
| [Evaluation CLI](test_evaluate_checkpoints.py) | 6 | Frozen checkpoint loading, identity validation, explicit policy roles and ledger-free evaluation |
| [Censored evaluation](test_censored_evaluation.py) | 3 | Unknown-outcome identification intervals, conservative paired bounds and resolved-only score labeling |

CUDA model/adaptive tests require
`RUN_CUDA_LEARNING_TESTS=1`; without it those tests are explicitly skipped.
Run tests in a separate allocated measurement window, not alongside the campaign.
The [real-engine collection checks](results/learning-collection-ready.json) also
verified every retained row's behavior probability before any outcome update;
maximum selected-log-probability error was `4.77e-7` in those short probes.
[Adaptive collection checks](results/learning-collection-adaptive.json) passed
the same boundary. Their different sampled game populations are not paired
training-throughput comparisons.

The eager PPO [profile](results/learning-ppo-eager-profile.txt) found 497 kernel
launches and roughly 1.1 ms of CUDA work in a much longer host-dispatched step.
Matched, interleaved synthetic-target probes at minibatch 2048 measured:

| Width | Eager median | Captured-backward median | Speedup |
| --- | ---: | ---: | ---: |
| 128 | 14.36 ms | 1.65 ms | 8.72× |
| 256 | 15.41 ms | 1.68 ms | 9.18× |

Each comparison used ten measured steps per mode after warmup, including
minibatch staging and numerical guards. These are repeated optimizer passes,
not fresh decisions or learning results. [Width128 data](results/learning-ppo-capture-comparison-h128.json),
[width256 data](results/learning-ppo-capture-comparison-h256.json),
[exact probe source](results/learning-ppo-capture-probe-source.txt).

The [adaptive-actor microbenchmark](results/adaptive-actor-paired.json), using
recorded authorized states and including CPU packing, measured:

| Selected rows from host256 | Full actor | Adaptive actor | Speedup |
| --- | ---: | ---: | ---: |
| 32 | 0.651 ms | 0.343 ms | 1.90× |
| 64 | 0.655 ms | 0.438 ms | 1.50× |
| 128 | 0.653 ms | 0.591 ms | 1.11× |
| 256 | 0.661 ms | 0.703 ms | 0.94× |

Three interleaved 0.15-second repeats per case included lane validation, packing,
padding, inference, sampling, transfer and owned output copies. Smaller active
subsets benefit; full occupancy adds overhead. This fixture predates schema v2
and is a data-path test, not a compatible training corpus. These measurements
do not predict game duration, retained-sample rate or eventual strength.

## Campaign operations and visibility

All actual outcome-learning sessions share
`/home/lva/.local/share/shards-training/2026-09-26/budget.json`; opening an existing
ledger cannot enlarge or restart its 43,200-second allocation. Pilots, discarded
learning runs, resumed training, and inline validation/checkpoint time consume
that allocation. Systems-only inference and fabricated-target probes do not.
Revision 2 preserves the **313.0874 seconds already charged before its
introduction**. Newly introduced collection counters start at an explicit
timestamp; older untracked game totals are not invented, and existing time and
discard history are retained. Loading an older model checkpoint cannot roll
back either the clock or the ledger's newer collection counters.
The main entry point is [run_campaign_with_audit.py](run_campaign_with_audit.py),
which calls [supervise_training.py](supervise_training.py). The trainer owns the
exclusive ledger lock, and the supervisor independently enforces its published
deadline. A failed process is not silently restarted. Following successful
training exit, the main wrapper runs two frozen 4096-game seat-swapped audits
against saved champion and initial roles on held-out `0x4000...` seeds. These
audits open no ledger session and perform no updates; each has a one-hour process
limit. Audit failures are separate from training failures and preserve the
checkpoint and completed reports in `post-evaluation.json`.

Each run records its rules/Core source hash, built host hash, ordered catalog,
observation schema, training-source hash and configuration in `identity.json`.
Resume requires an exact identity match and the same campaign ledger. Preserve
pinned production sources while a run is active. Checkpoints own finite CPU
copies of policy/Adam state, archive/champion metadata, RNG and counters; they
do not serialize C# iterators or unfinished games. Resume restarts games at the
documented seed boundary and accounts for discarded work without refunding time.

The dashboard is [http://127.0.0.1:8768](http://127.0.0.1:8768). It reads the
campaign's logs, displays generation/optimizer/budget/evaluation information,
and samples CPU, RAM, GPU and process health every three seconds. It has no
training-control endpoint or external upload. Alerts include stale heartbeats,
missing trainer processes, stale checkpoints, failures and rejected minibatches;
an alert is not itself an automatic repair. Charts are display samples, while
allowlisted raw JSON/JSONL downloads retain logged records. Driver GPU memory is
global, and CPU/RAM figures describe the WSL guest, not the entire Windows host.
Revision-2 views include censor rates, trace downloads and unresolved evaluation
intervals; active-run alerts are separated from collapsed historical incidents.

The dedicated [balance observatory](http://127.0.0.1:8768/statistics) opens from
**Open statistics** in a separate named window (or a new tab if the browser
blocks popups). Its gallery uses the actual local artwork for 189 card
definitions and five heroes, with searchable card/relic/destiny categories,
exact per-hero acquisition filters, hero matchups, a table view, and enlarged
artwork with rules text. Expansion and acquisition mode distinguish repeated
names and different ways of entering play. Images load lazily, at most 24
entries per page; the page checks the cached statistics endpoint every five
seconds and retains its view when a snapshot has not changed.

Statistics sources remain separate: random-hero training, normal-draft training,
the earlier training pool, and frozen evaluations. Training pools publish each
10,000 completed games in that source and on final flush; publication progress
between batches is unknown, not zero. The source snapshot can cover a rounded
trailing window rather than all campaign games. Headline game counts are actual
engine games; hero and item counts are player-perspective completed outcomes,
so both players can contribute to one real game's observations. Repeated
acquisitions are separate event counts. Censors are unresolved, never draws.
Intervals come from the backend's stated method; pooled changing-policy
associations and low-coverage heroes do not establish causal balance rankings.

The first budgeted width128 pilot hit an administrative-truncation gate and
stopped with an earlier valid checkpoint retained. **Its exact threshold/cause
was not recovered or reproduced**: 5,376 resumed-training games and a 15,360-game
targeted-seed frozen probe did not reproduce it. A separate deterministic legal
always-end-turn [fixture](results/round-cap-fixture.json) reached the round guard
at 802 wrapper actions, with pre-cap round400 and both players still at health50.
That proves unfinished legal games can reach a host cap; it is not a
reconstruction of the learned failure or a stability pass. Fresh revision-2
pilots use a new pinned source identity while retaining all prior budget charges.

A later revision-2 pilot captured a **different learned cap**, whose complete
3713-action prefix was [replayed exactly](results/learned-cap-replay.json): every
action was legal, no earlier terminal occurred, and the round400 pre-cap state
matched. The remaining collections were Volos's Limiter Drones/Unknown God and
Tetra's single Cinder Scars, without gem generators; health stayed 50/26 from
round42 through the guard. This is evidence of an undertrained policy reaching
a degenerate resource composition, not a balance judgment about those cards.
The revised collector excluded that lane and continued. A separate
[real-engine mixed-cohort check](results/censor-lifecycle.json) retained three
true-terminal rows and removed all 802 rows of a capped lane. Neither result
identifies the original revision-1 incident's cause or establishes long-run
stability.
See the [censoring contract](training-contract.md#truncation-auto-reset-and-revision-2-censoring)
and separately maintained run notes for the current decision.

Frozen revision-2 evaluation reports censored results as unknown. With `N`
attempts, `C` censored games and resolved score sum `S`, the identification
interval is `[S/N, (S+C)/N]`; the conservative paired-seed Hoeffding bound expands
that interval. `score_a` is null if any game is censored, and
`score_a_resolved_only` is explicitly conditional on completion. No unknown
outcome is silently scored as a draw. Opportunity telemetry covers all attempted
games, including censored trajectories, and carries that scope.

## Reproduce historical bounded preflight

Run from the repository root under WSL. Setup installs the pinned ML packages
in `/home/lva/.venvs/shards-preflight`, outside the repository; it builds and
checks the new host. It does not install or modify GPU drivers.

```bash
bash Tools/TrainingPreflight/setup.sh
```

`SHARDS_PREFLIGHT_VENV`, `SHARDS_BASE_PYTHON`, and `SHARDS_DOTNET` override the
local paths used by setup. Other scripts use `SHARDS_DOTNET` for the host.
The measured environment is Python 3.12.3, PyTorch 2.14.0+cu130, CUDA 13.0,
and driver 616.56, with native `sm_120` support. Exact dependencies are in
[requirements-lock.txt](requirements-lock.txt).

Example bounded experiment:

```bash
/home/lva/.venvs/shards-preflight/bin/python Tools/TrainingPreflight/pipeline_bench.py \
  --batch 256 --workers 4 --transport shared --shared-copy span \
  --mode graph --precision tf32 --action-source exercise --seconds 10 \
  --output Tools/TrainingPreflight/results/example.json
```

Use a process timeout around individual experiments when diagnosing a possible
compiler or pipe stall. [run_matrix.py](run_matrix.py) already creates separate
process groups, enforces wall-clock limits, kills its own timed-out children,
and saves commands, diagnostics and partial results. Its `cpu`, `pipeline`,
`compile`, and `crossover` stages run sequentially to avoid competing benchmarks.

```bash
/home/lva/.venvs/shards-preflight/bin/python Tools/TrainingPreflight/run_matrix.py \
  --stage pipeline --repeats 3 --seconds 8 --max-seconds 1200 \
  --output-dir Tools/TrainingPreflight/results/pipeline-repeat
```

Do not run multiple suites at once. Close unrelated GPU-heavy programs for
controlled comparisons. No benchmark changes clocks/power limits or terminates
other applications. Results record available telemetry; WSL's CUDA memory
accounting and system-wide `nvidia-smi` memory figures can differ.

## Historical preflight components

| Component | Scope |
| --- | --- |
| [Host](Host/CONTRACT.md) | Real rules, exact staged actions, compact authorized observations, CPU allocation/worker sweeps, pipe/shared-memory transport |
| [Policy and losses](model.py) | Shared candidate-model primitives; its historical PPO-shaped loss belongs to fabricated-target experiments |
| [GPU microbenchmarks](GPU_BENCH_NOTES.md) | Actor and disposable optimizer work, precision, eager/compile/graphs, numerical and update parity |
| [Transfer probe](transfer_bench.py) | Persistent pinned/pageable memory and completed H2D/D2H costs |
| [Pipeline](pipeline_bench.py) | Real games plus frozen neural acting, optionally a separate disposable learner load |
| [Overlap](overlap_bench.py) | Separate resident-game groups overlap CPU simulation and GPU work, with frozen-weight/counter checks |
| [CPU alternative](sparse_cpu_bench.py) | Exact sparse first layer and scoring only legal candidate slots, including packing costs |
| [Rollout storage](rollout_bench.py) | Immutable dense/compact storage, random minibatch gathering, reconstruction and upload costs |
| [Resident rollouts](resident_rollout_bench.py) | Owned GPU storage, moving append, exact GPU gathers and inference without repeated observation upload |
| [Integrated ring checks](pipeline_rollout_check.py) | Captured actor appends, wraparound, ownership and independent learner buffers |
| [Profiler](profile_pipeline.py) | Short CPU/CUDA diagnostic trace, kept separate from throughput measurements |
| [Sustained validation](soak_bench.py) | Bounded real games, lifecycle/reward checks and Python/C#/GPU memory sampling |
| [Real-state collector](collect_fixture.py) | Numeric fixtures and recorded actions from untrained real-engine games |
| [Methodology](methodology.md) | Primary sources, controls, bottleneck tests and stopping rules |

The host never silently truncates action lists. Multi-selection is staged;
large menus are paged; integer damage allocations can use 2/8/32/64-way interval
choices. Every integer remains reachable. Count **wrapper decisions** separately
from **engine submissions**: changing factorization changes their ratio.
The observation is an explicit partial-information ablation, not a claim to
capture all strategically relevant information. The host contract lists omissions.

`--action-source exercise` uses a separate seeded CPU action stream after still
executing the full neural path. It permits controlled execution/transport
comparisons with the same action prefix for equal resident-game counts. The
default model-selected path verifies the actual inference-to-engine boundary.
Both are untrained workloads. Different factorization/batch sizes or capped
warmup counts change the visited corpus and must not be called identical replays.

`--rollout-rows 16384` adds an owned FP32 GPU ring to the CUDA pipeline. Writes
occur before the actor's existing completion wait. This is a storage/scheduling
probe: the overwriting ring does not implement complete-episode retention or
training credit. The `rollout` matrix stage compares its cost with ordinary
acting, graph/eager optimizer load and overlapping hosts.

Optimizer load operates on a **separate copy** of the actor's parameters, uses
fabricated advantages/returns, and does not change the actor. It measures compute
and scheduling costs, not learning progress. Repeated minibatch example-passes
are not fresh decisions. The exercise actor also applies category biases that
must not be copied inconsistently into a future PPO likelihood calculation.

A sustained test exposed ratio overflow in the original artificial workload.
The diagnosis and corrected workload are documented in the measurement report;
historical successful short runs do not establish the old load's stability.

The bounded `validation` stage runs failure-flag checks, ring correctness, and
two sustained workloads (with and without resident storage):

```bash
/home/lva/.venvs/shards-preflight/bin/python Tools/TrainingPreflight/run_matrix.py \
  --stage validation --seconds 60 --max-seconds 300 \
  --output-dir Tools/TrainingPreflight/results/validation-repeat
```

## Results and interpretation

Start with the [measurement report and optimization decisions](results/summary.md).
It records historical improvements, negative experiments, source-linked raw
results and requirements identified before outcome learning. CPU rollout
gathering must not be omitted when estimating a fast GPU learner: the resident
storage experiment tests avoiding that repeated staging cost.

The [load matrix](results/load/manifest.json) includes graph/eager optimizer
work at matched example-passes per fresh wrapper decision, CPU/GPU overlap and
half-precision upload. The [60-second validation](results/soak-actor.json)
completed 5,389 games without a truncation or lifecycle/action failure.

The corrected synthetic learner passed the [12-case integrated comparison](results/rollout-pipeline-v2/manifest.json)
and [final validation battery](results/validation-v2/manifest.json). Its two
60-second stress runs completed 13,475 games with zero truncations and finite
loss/gradient checks, final parameters and optimizer state. The report retains
the original failure and explains the workload change; no outcome learning or
multi-hour stability is established by these tests.

The machine remained shared with another GPU workload. A bounded
[quiet-period confirmation](results/confirmation/manifest.json) did not obtain
the requested quiet repeats. Close rankings and a final configuration cannot
be certified from these contended runs; no other application was stopped.

The initial complete 108-case GPU matrix is in
[gpu-main-sweep.json](results/gpu-main-sweep.json), with
[analysis](results/gpu-analysis.md). All cases passed the numerical and execution
checks. These short synthetic measurements screen configurations; close rankings
need longer, interleaved confirmation and real-state checks.

The first complete pipeline measured about 46,600 wrapper decisions/s, of which
about 42,900/s were actual engine submissions. It completed 712 games during
eight measured seconds, with zero administrative truncations. Its time split
identified transport/bookkeeping as the largest cost. This is the initial
preflight pipeline measurement, not real training throughput.
[Raw pipe result](results/pipeline-initial-pipe.json).

The first shared-memory implementation did not improve throughput. A controlled
copy test then found that `MemoryMappedViewAccessor.WriteArray<byte>` was much
slower than a bounded span copy: median approximately 2.02 versus 47.13 GB/s for
the same cached 4.26 MB payload. This is a **copy-only** result; pipeline results
must establish the eventual benefit. Both transports passed byte-for-byte
payload and counter equivalence tests. [Copy test](Host/mapped-copy-benchmark.json),
[transport test](Host/transport-selftest.json).

The preflight and readiness results above are engineering measurements. They
do not establish playing strength, the meta, a balance recommendation, or a
reliable 12-hour sample projection. Trained policies may visit longer or
different games; budgeted pilot and frozen evaluation results must be assessed
separately.
