# Training preflight: measured choices and remaining gates

2026-09-25, local RTX 5090 / Ryzen 7 5800X, fresh implementation.
The 12-hour learning campaign has **not started**. These tests use frozen
untrained actors and, where stated, disposable optimizer updates with fabricated
targets. They establish systems behavior, not playing strength or game balance.

## Established improvements

| Change | Evidence | Interpretation |
| --- | --- | --- |
| Direct authorized-state encoding | Paired 300-seed test: 244k wrapper steps/s with compact encoding versus 24.8k with presentation snapshots | Keep Unity/UI snapshots out of the training path; both rates include simulation |
| Remove adapter/encoder allocation | Best encoded CPU median 691k → 885k wrapper steps/s; approximately 3,042 → 1,436 allocated bytes/step | 28% better best CPU median and 53% less allocation; not an end-to-end training claim |
| Reuse pinned staging buffers | Transfer probe includes completed uploads and action downloads | Keep persistent pinned buffers; do not pin fresh allocations every step |
| Bounded mapped span publication | Same cached 4.26 MB copy: median 2.02 → 47.13 GB/s | Fixes a slow mapped accessor; cached CPU-copy speed is not PCIe bandwidth |
| CUDA graphs | All 108 initial GPU cases and 20 real-state cases pass their numerical/execution checks | Removes repeated launch overhead; whole-pipeline confirmation remains necessary |

CPU measurements, seed fingerprints, allocation counts and copy-test limits are
in [Host/PERFORMANCE.md](../Host/PERFORMANCE.md). Shared and pipe transports have
byte-for-byte payload/counter parity checks. The source uses the current real
engine and preserves staged selections, all integer allocations and overflow
paging; it does not truncate legal actions to fit the neural input.

## Alternatives that did not establish a better default

* BF16 is numerically viable on the tested corpus, but is not consistently faster
  than TF32 for this small model. TF32 is a reasonable provisional baseline.
* Eight compiled cases passed, including complete optimizer steps. Compilation
  did not establish an advantage over manual graphs, and added roughly 8–13
  seconds of cold setup in those cases.
* Dense CPU inference remains slower at useful actor batch sizes. An exact
  sparse first layer improved a one-thread case from 21.1k to 31.3k decisions/s
  (1.48×, about 32% less time). Scoring only legal candidate slots then raised
  the packing-inclusive standalone actor to 82–94k/s at width 128. All slots,
  numeric observations and weights remain represented; this is not an action
  pruning heuristic. Integrated with the real engine, that CPU actor reached
  24–30k/s, compared with 47–53k/s for GPU graphs in the same repeated suite.
  Keep GPU inference as the provisional default. Four-thread CPU timings were
  unstable, and fully concurrent CPU acting/GPU learning is still unmeasured.
* Only about 1.1% of sampled wrapper states have one legal action. Skipping
  neural inference for those states has little available benefit in this corpus.
* Larger resident batches and more CPU workers are not monotonically faster.
  Batch size, scheduling and transport must be chosen from complete-pipeline
  results, not the largest GPU matrix multiplication rate.

See [GPU analysis](gpu-analysis.md), [compiled cases](compile/manifest.json),
[CPU/GPU crossover](crossover/manifest.json), [sparse CPU results](sparse-cpu.json),
[ragged candidate test](sparse-ragged-cpu.json), and
[integrated CPU comparison](cpu-ragged-pipeline/manifest.json).

## Pipeline bottleneck and comparison limits

The initial eight-second pipe run processed 46,551 wrapper decisions/s and
42,893 actual engine submissions/s, with 712 completed games and no truncations.
Transport/bookkeeping occupied 4.25 of its eight measured seconds. The first
shared-memory accessor implementation did not improve that result, which led to
the measured span-copy and packed-command changes.

The subsequent 39-run pipeline matrix passed. Other applications repeatedly
occupied 21–26 GB of GPU memory and drove the device to 100% utilization, so
its complete aggregate rankings are **not controlled estimates**. In individual
quieter runs, several shared-memory configurations achieved about 87k–99k
wrapper decisions/s. Those are measurements of this frozen exercise workload,
not repeatably established optima, training rates, or forecasts for 12 hours.

One quieter batch-256/four-worker/eight-way-split run spent approximately 26%
of elapsed time in engine steps, 20% encoding, 23% transport/bookkeeping and
29% actor service. Its GPU forward/sampling interval was about 0.136 ms per
batch, compared with about 0.320 ms for upload. This points to CPU scheduling,
transport and overlap before merely increasing neural arithmetic. These stages
are wall-clock intervals, and synchronization can include external contention.

[Initial result](pipeline-initial-pipe.json), [complete matrix](pipeline-sweep/manifest.json),
[example quieter run](pipeline-sweep/shared-split8-r1.json).

A later 20-batch diagnostic trace after 512 warmup batches put about 63% of
CPU wall time at the combined engine/encoding/transport boundary. Actor service
also includes GPU completion waits. This supports work on the CPU boundary and
overlap; it does not establish that GPU computation is free. Captured internal
kernels are not fully attributed by this trace, so its operator percentages
must not be used as a complete GPU utilization breakdown.
[Profile summary](profile-pipeline.json).

The separate ten-minute quiet-period confirmation attempt obtained three
completed runs, but none stayed inside its quiet heuristic. All attempts are
retained in [the confirmation manifest](confirmation/manifest.json). Its power
cutoff can also reject a candidate's own useful work, so that filter is a
contention warning, not an unbiased ranking or proof of exclusive GPU access.

## Further scheduling and sustained validation

A fused C# serve path combines step/reset/encode into one worker pass and merges
task-local counters. Four-way baseline/fused × pipe/shared parity passed with
one/four workers and split factors two/eight, including terminal rewards and
auto-reset. It remains opt-in (`SHARDS_FUSED_SERVE=1`).

The CPU-only boundary comparison still selects and submits real game actions,
but executes no neural inference. Three interleaved six-second runs gave these
wrapper-rate medians:

| Workers | Original two passes | Fused pass | Change |
| ---: | ---: | ---: | ---: |
| 4 | 79,861/s | 86,922/s | +8.8% |
| 8 | 100,487/s | 97,273/s | −3.2% |

This is a mixed result, with substantial run variability; it does not justify
changing the default globally. In fused mode the first timing field includes
both simulation and encoding, and the separate encoding field is zero.
[Timing runs](fused-cpu/manifest.json), [parity checks](../Host/fused-serve-selftest-b8.json).

The 60-second frozen neural-actor run completed **5,389 games**, **2,434,304
wrapper decisions** and **2,342,244 engine submissions**, with no illegal-action
failure, nonfinite actor output, lifecycle/reward mismatch or administrative
truncation. Python RSS stayed approximately 1,214 MiB; allocated GPU tensor
memory stayed at 70.4 MiB. C# RSS rose during warmup and then varied around
101–138 MiB rather than increasing throughout the sampled minute. This short
test does not establish multi-hour memory stability or include stored learning
trajectories. System-wide GPU memory reached 31.8 GB and utilization was usually
100% due to concurrent activity; its 40.6k wrapper/s average is a contended
validation rate. [Sustained-run record](soak-actor.json).

Two interleaved eight-second repeats also measured a disposable learner at
approximately **three optimizer example-passes per new wrapper decision**:

| Frozen actor / disposable load | Wrapper decisions/s, two runs |
| --- | --- |
| One host, actor only | 50,927; 51,576 |
| One host, eager optimizer | 30,166; 31,028 |
| One host, graph optimizer | 40,971; 39,393 |
| Two overlapping hosts, actor only | 59,330; 58,408 |
| Two overlapping hosts, graph optimizer | 44,542; 45,042 |
| One host, FP16 upload, actor only | 47,557; 50,094 |

Total resident games and simulation workers stay at 256 and four for the
overlap comparison. The dispatch interval changes to preserve optimizer work
per new decision. Different group seeds mean the two-host path is not an
identical game replay. All twelve runs passed with zero truncations; overlapping
actors retained identical frozen weights. Each run still saw substantial
external GPU memory use, so these are repeat observations under contention,
not exclusive-device rankings. Keep graph optimization and overlap available;
FP16 upload did not earn a new default. [Load matrix](load/manifest.json).

The subsequent requested 60-second optimizer stress run stopped on a nonfinite
loss. An instrumented reproduction isolated a log-probability difference of
**+100.996**, overflowing FP32 `exp` in the policy ratio. Inputs, legal actions,
logits and targets were finite before that update. Replaying the exact saved
model and optimizer state eagerly reproduced the failure; changing only the
returns did not remove it. This was a defect in the artificial workload, which
repeatedly optimized frozen-actor actions that its diverging copied learner had
come to assign extremely small probability. It was not a graph-only failure.
[Preserved failure](soak-learner-failure.json), [diagnosis](learner-diagnosis.json).

The corrected disposable workload selects its own learner's legal argmax when
preparing the reference likelihood and uses fixed bounded fabricated returns.
This keeps the selected reference probability at least 1/64 for a valid masked
distribution; subsequent selected-action ratios are therefore bounded by 64.
This is a synthetic-load design, **not** a proposed learning algorithm or a
license to substitute actions in real PPO trajectories. No ratio clamp hides
the original failure. Historical load timings above use the old workload and
must be distinguished from the corrected results below.

The corrected workload subsequently passed **two 60-second stress runs**:

| Storage path | Wrapper decisions | Completed games | Optimizer updates | Truncations |
| --- | ---: | ---: | ---: | ---: |
| Current actor batch | 3,029,760 | 6,750 | 4,440 | 0 |
| Owned GPU ring | 3,022,848 | 6,725 | 4,428 | 0 |

Both exceeded the update count of the diagnosed failure. Every update group
passed sticky loss, pre-clipping gradient-norm and reference-probability checks;
all final parameters and 36 optimizer state tensors were finite. A separate
four-case eager/graph regression proved that a transient bad loss or gradient
remains detectable even after a later healthy replay. The ring run also passed
final ownership, cursor, packet legality and frozen-actor checks.

Allocated GPU tensors remained constant at about 240 MiB without the ring and
503 MiB with it. Allocator reservations rose and were reclaimable by the
separate post-run cache cleanup. Python RSS changed by about 0.2/2.9 MiB;
C# RSS warmed from about 70 MiB toward 139 MiB, with managed-heap cycles.
These runs validate the revised synthetic load for the sampled minute, not
multi-hour stability or convergence of a real learner. Background GPU activity
was still substantial. [Validation manifest](validation-v2/manifest.json),
[no-ring stress run](validation-v2/soak-learner.json),
[ring stress run](validation-v2/soak-ring-learner.json),
[failure-flag regression](validation-v2/learner-health.json),
[final ring checks](validation-v2/ring-correctness.json).

## Rollout storage is a separate bottleneck

The storage probe includes immutable copies, random minibatch gather/decode,
and optional completed upload/actor/download. It actually allocated 8,192 rows,
repeating the 2,048-row real corpus. All three layouts preserved masks, selected
slots and categorical IDs; decoded inputs and captured inference passed parity.

| Layout | Bytes/decision including 36 bytes bookkeeping | Estimated arrays for 131,072 decisions | B2048 CPU gather/decode |
| --- | ---: | ---: | ---: |
| Dense FP32 | 16,676 | 2.04 GiB | 16.2 ms |
| Dense FP16 + exact IDs | 8,356 | 1.02 GiB | 25.5 ms |
| Sparse FP32 observations + ragged FP16 legal candidates/exact IDs | 1,160 average | 0.142 GiB | 11.0 ms |

Compact storage reduced this corpus's array footprint by **14.4×**, but was not
free: the current checked NumPy packer reached only about 59k rows/s, versus
310k for a dense FP32 copy. Dense FP16 packing was worse at 26k rows/s. Allocation,
validation and conversion are included in these packing costs. Large-batch
timings have few iterations and need longer confirmation; GPU timings also
remain exposed to external contention.

The 131,072-row and million-row sizes are extrapolations of observed occupancy,
excluding source/staging arrays, allocator and object overhead. Candidate
numbers in compact storage are quantized; exact masks/IDs do not make every
numeric feature lossless. The maximum observed probability change was below
0.00001 for these untrained weights, which is not a future-policy guarantee.
[Storage results](rollout-storage.json).

Investigation then found that NumPy's default `take(..., out=..., mode='raise')`
buffers its output. The corrected CPU path checks all indices explicitly before
using unbuffered `clip` mode; it still rejects invalid indices, without silently
clipping any. Three paired repeats using the same arrays and index stream
improved gather throughput by 1.41× at B256, 2.38× at B2048 and 3.02× at B8192.
Every paired repeat improved, and both modes passed full reconstruction and
invalid-index checks. Guarded `clip` is now the CPU gathering default.
[Paired test](take-mode-paired.json), [NumPy contract](https://numpy.org/doc/stable/reference/generated/numpy.take.html).

The GPU-resident prototype stores exact FP32 inputs once and gathers minibatches
without uploading observations again. Two runs produced approximately 0.57 ms
for B2048 gather and 0.89 ms including the complete actor/sample/packet download.
An improved CPU-staging run measured 6.38/9.34 ms respectively. These were not
interleaved exclusive-GPU comparisons, but the magnitude and repeated resident
result support keeping rollout data on the GPU before further small-kernel tuning.
[Unbuffered CPU staging](rollout-dense-unbuffered.json),
[resident storage](rollout-gpu-resident.json),
[resident follow-up](rollout-gpu-resident-append.json).

The prototype's 8,192-row payload is about 130 MiB. A 131,072-row dense rollout
would require about 2.04 GiB for the recorded arrays; that larger working set
was not allocated or timed. Model, optimizer, graph pools, source/staging data
and allocator caching require additional memory. The test proves owned copies,
wraparound, changing post-capture inputs, exact random gathers and legal outputs.
It does not manage the lifetime of incomplete game trajectories.

Capturing the moving append itself did **not** establish a useful gain:
approximately 429k versus 436k rows/s, with nearly identical 0.54 ms completed
request latency. Both paths passed cursor/ownership checks. Keep the simpler
append baseline for the standalone store.

The integrated experiment then placed storage writes inside the actor graph,
before its existing completion wait. This avoids a separate append barrier.
Its 16,384-row ring owns about **260 MiB** of FP32 inputs and actor packets;
unlike the standalone layout estimate, this payload has only the 12-byte actor
packet and no fabricated 36-byte trajectory bookkeeping. Eager and graph checks
passed non-divisible wraparound, changing inputs, source mutation, exact packet
retention, private learner gathers and unchanged actor weights.

Twelve interleaved eight-second cases used the corrected synthetic workload:

| Pipeline | Wrapper decisions/s, two runs |
| --- | --- |
| Actor only | 54,038; 56,148 |
| Actor plus owned ring | 54,191; 59,110 |
| Eager optimizer load | 16,356; 31,859 |
| Graph optimizer load | 43,627; 45,310 |
| Graph optimizer plus owned ring | 42,938; 41,376 |
| Two overlapping hosts plus graph optimizer | 14,904; 48,853 |

All completed with zero administrative truncations. Ring gathering samples
stored rows, whereas the no-ring load repeats the current actor batch; these
are different input working sets with matched example-pass counts. Ring storage
is extra required work, so its proximity to the no-storage rate is useful; this
is not a measured acceleration of a complete trainer. Large variation between
repeats, especially the overlapping case, prevents an exclusive-device ranking.
The graph-plus-ring runs still spent 4.44–5.18 of eight seconds at the host
boundary and 0.96–1.38 seconds on synthetic optimizer work. The next substantial
systems target remains simulation/encoding/transport scheduling.
[Corrected pipeline matrix](rollout-pipeline-v2/manifest.json),
[initial integrated correctness](pipeline-rollout-correctness.json).

## Numerical and rules coverage

The real-state fixture contains 2,048 sampled rows collected while driving
65,536 wrapper decisions / 63,407 engine submissions, with 120 completed games
and no truncations. It includes ordinary and out-of-turn decisions. This is
coverage from an untrained exercise policy; rare mechanics and future trained
strategies are not proven covered. Artificial action-adapter fixtures separately
exercise overflow menus and exhaustive small selection/allocation cases.

All 20 real-state GPU cases passed, including four additional cases that cover
the entire 2,048-row corpus. The largest measured probability errors
against FP32 were approximately 0.000027 for TF32 and 0.00132 for BF16; maximum
relative gradient L2 errors were 0.000212 and 0.00404 respectively. These cases
include deterministic batches of 256/1024/2048 rows. Optimizer graph/eager update
parity passed. FP16 input round-trip also passed its separate 2,048-row check,
including all 193 reserved categorical identity codes; numeric amounts remain
lossy, so this is not permission to treat quantized observations as identical.
[Fixture metadata](real-states.json), [initial real-state results](gpu-real-states.json),
[full-corpus cases](gpu-real-all-rows.json), [input quantization](transfer-quantization.json).

## Before spending the training budget

The provisional systems path is the real headless C# engine, shared-memory
span publication, persistent pinned FP32 staging, a compact embedded candidate
policy with TF32/manual graphs, and owned FP32 GPU rollout storage. Batch 256,
four workers and eight-way integer splitting are a tested starting point,
not a proven optimum. Keep overlap configurable: its benefit needs a quiet
repeat with the actual trajectory/credit workload. CPU sparse/ragged inference,
precision alternatives and fused host execution remain available experiments.

This pass stops at measured engineering improvements and identified limits.
Repeated quiet-GPU comparisons were not obtainable, and real return/advantage
construction, archive refresh and checkpoint costs are absent from the load.
Fully concurrent CPU acting/GPU learning, compact host-to-GPU transport and
larger live trajectory buffers remain unmeasured alternatives. No claim is made
that all possible optimizations have been exhausted. Profile again after the
real trajectory path exists; optimize held-out strength per training hour when
choosing information, model capacity and update frequency.

The real outcome-learning trainer is not implemented by these benchmarks.
Implement and test immutable per-seat trajectories, behavior likelihoods,
policy/opponent versions, terminal/truncation credit, checkpoints and budget
recovery before a learning campaign. Add the small public-state distinctions
identified by the observation review, or explicitly test their omission.
[Training contract](../training-contract.md).

Choose playing strength using short, equal-time learning pilots, not GPU
utilization. Those pilots and discarded outcome-learning runs must count toward
the same 12-hour allocation. Keep final frozen evaluation separate, retain an
opponent archive, and collect opportunity/selection/outcome telemetry before
making balance claims. No preparation command automatically starts training.
