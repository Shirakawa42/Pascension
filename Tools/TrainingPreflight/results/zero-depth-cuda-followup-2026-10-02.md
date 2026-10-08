# Zero-depth CUDA followup — 2026-10-02

Selective GPU optimization is justified by measurements. The useful first target
is lossless input transport, followed by compiled policy fusion. Replacing the
C# rules engine with CUDA is a separate implementation and equivalence project;
the current preparation does not do that. No outcome-learning campaign was
launched for this investigation.

## What was measured

The hardware was the same RTX 5090/Ryzen 5800X workstation. The environment was
PyTorch `2.14.0+cu130`, CUDA runtime `13.0`, with IEEE float32 arithmetic explicitly
selected. `nvcc` and `ncu` were unavailable in the shell; Nsight Systems
`2024.6.2.225` was present. The implemented custom kernel uses Triton, which
provides a compiler for custom GPU kernels. [Triton documentation](https://triton-lang.org/main/index.html).

The retained-input fixture came from 96 frozen-policy steps of 128 real Shards
lanes. Those short probes ended with 128 unresolved games and supplied no actual
outcome utilities, training updates or strength evidence. The preceding host
binary was SHA-256
`30b10b31a33054e005175051d0d47b78ecced5be59a19fb721b6b5ca836641c7`,
with catalog SHA-256
`e9cb08f6e7e61af7b2fb5bb57601b33652de02bd9958dc2dc81d895c132e13a4`.
That is the previous validated observation schema; followup annotations and
schema v2 require a new final build/preparation identity. These isolated probes
do not assert that the old prepared bundle matches the new source.

Across the 96 batches, only **1.56–2.27%** of observation entries were nonzero;
1,677 of 24,576 columns became nonzero somewhere. A retained batch still uploaded
14,188,544 bytes for the full observations, candidates and masks. The measured
GPU host-to-device interval was 0.533 ms, while a captured uncompiled forward
was 0.242 ms. The CPU shared-memory-to-pinned staging median was 0.482 ms.
These are isolated/input-prefix measurements, not a completed-game throughput
forecast. [Raw probe](../../ZeroDepthTraining/results/cuda-followup/probe.json).

This agrees with the earlier complete-cohort profile: captured active buckets
spent approximately 58–59% of collection wall time in actor service and 38% in
host roundtrips; inference alone was only one part of actor service.
[Previous completed-cohort report](zero-depth-throughput-research-2026-10-02.md).

## Lossless structured transport and a custom GPU kernel

The prototype copied the complete scalar/histogram prefix and each occupied
public table prefix into a contiguous owned pinned float32 packet. It also
copied each candidate prefix and the full mask. A single custom GPU expansion
kernel reconstructed the ordinary full float32 observation, candidate and mask
buffers, writing zero to empty table rows and padding. It never compressed
numeric precision, clipped a value, dropped a public card record, or guessed
which fields matter. This targets the transfer bottleneck directly. NVIDIA
recommends minimizing transfers and batching small regions into larger packets
when packing/unpacking is beneficial. [CUDA transfer guidance](https://docs.nvidia.com/cuda/cuda-c-best-practices-guide/index.html#data-transfer-between-host-and-device).

For one retained 128-lane batch, conservative packing reduced the packet from
**14,188,544 to 4,179,968 bytes**, a **70.54%** reduction. Its GPU upload plus
expansion median was 0.198–0.256 ms across two bounded probes, versus
0.529–0.531 ms for dense upload. A dynamic-metadata implementation using a
single captured expansion graph measured 0.244 ms at 128 lanes, 0.156 ms at
64 and 0.154 ms at 32. Timing dispersion and GPU launch gaps limit conclusions
about the individual tiny expansion kernel.
[Packing probe](../../ZeroDepthTraining/results/cuda-followup/packing-probe.json),
[dynamic 128](../../ZeroDepthTraining/results/cuda-followup/dynamic-packing-probe.json),
[dynamic 64](../../ZeroDepthTraining/results/cuda-followup/dynamic-packing-probe-b64.json),
[dynamic 32](../../ZeroDepthTraining/results/cuda-followup/dynamic-packing-probe-b32.json).

Those first probes preserved the entire staged-selection table: the old
`selected_count` is not an exact trace length because a zero split allocation
still records a trace row. Observation schema v2 therefore exports the actual
trace count in scalar 177 and explicit `count_column`/`count_scale` metadata for
every table. Current table extents are facts from the encoder, not numerical
sparsity scans. [Encoder](../../ZeroDepthTraining/Host/Encoder.cs).

The new [packed transport module](../../ZeroDepthTraining/packed_transport.py)
copies selected host lanes directly into pinned packed staging, preserving the
existing dense GPU buffers for policy inference and owned rollout storage. It
supports changing extents/packed strides without changing addresses; fixed
regions and tables without count metadata are copied completely. Malformed
counts, non-prefix action masks, incompatible float types, overlaps, shape
changes and destination-address changes fail visibly. Count columns inside
prunable tables are rejected, so their own public metadata cannot disappear.

Six focused actual CUDA tests passed. They exercise all seven current public
tables and the 32/64/128 buckets, full capacity, growth/shrink, cleared removed
rows, nonmonotonic selected lanes, dummy padding, negative zero in retained
fields, input ownership before host mutation, protected count metadata, and exact **uint32 bit parity**
for every reconstructed float32 input. Dynamic prototype graphs additionally
passed 32 changing-extent cases per bucket, including all-empty and all-full
table plans. [Regression tests](../../ZeroDepthTraining/tests/test_packed_transport.py).

Integration must preserve the staging lifetime: the normal actor synchronizes
before its pinned packet can be overwritten. Asynchronous copy does not allow
reading unfinished GPU-to-CPU output or modifying a still-uploading pinned
source. [PyTorch transfer guidance](https://docs.pytorch.org/tutorials/intermediate/pinmem_nonblock.html).

## Compiled fusion inside the existing CUDA graph

`torch.compile` can fuse pointwise operations and choose optimized kernels;
CUDA graphs separately reduce repeated dispatch overhead. The tested setting
was `fullgraph=True`, `dynamic=False`, with internal compiler CUDA graphs
disabled, followed by the existing manual actor graph capture. It keeps the
plain policy/state dictionary and float32 computation. [Compile API](https://docs.pytorch.org/docs/2.14/generated/torch.compile.html),
[PyTorch fusion guidance](https://docs.pytorch.org/tutorials/recipes/recipes/tuning_guide.html).

The captured retained-input forward median improved from **0.242 to 0.132 ms**.
Legal logit error was `5.96e-8`; maximum log-probability error was `9.54e-7`.
The first compiler setup took 6.17 seconds. Isolated compiled calls without
capture were slower, so fusion should be assessed together with graph replay.
[Raw compile forward probe](../../ZeroDepthTraining/results/cuda-followup/probe.json).

Alternating 100 complete actor calls per bucket measured:

| Bucket | Ordinary captured actor | Compiled captured actor | Reduction |
| --- | ---: | ---: | ---: |
| 32 | 0.556 ms | 0.491 ms | 11.6% |
| 64 | 1.428 ms | 1.328 ms | 7.0% |
| 128 | 2.145 ms | 2.072 ms | 3.4% |

Initial inputs, mutated inputs, and finite learned-like weight changes were
checked. The synthetic changes came from random finite tensors, not game
outcomes. In-place card-semantic cache refresh retained the same addresses and
produced nonzero updated values through the captured graph. The largest
selected-log-probability discrepancy was `2.38e-7`, and the largest value error
was `2.09e-7`, below the existing learner guards. No updated probe model was
retained. [Full actor probe](../../ZeroDepthTraining/results/cuda-followup/compile-actor-probe.json).

Graph inputs, parameters and cached semantic tables must keep stable addresses.
Graph replay rereads current data at those addresses; it does not rerun Python
control flow. Both compiler warmup and graph capture should complete before
restoring a resumed campaign's sampling RNG. [PyTorch CUDA graph semantics](https://docs.pytorch.org/docs/2.14/notes/cuda.html#cuda-graphs).

## Precision and remaining opportunities

Keep raw observations and owned experience in float32. Raw BF16 collapses public
instance IDs 256 and 257 after their exact `/65536` normalization; FP16 already
collapses IDs 2048 and 2049. Millions of nonzero observed values changed after
FP16/BF16 round trips in this fixture. This is direct information loss before
the network even computes a decision. IEEE float32 remains the verified
baseline; TF32 changes internal multiplication precision even with float32
storage and deserves separate actor/learner consistency tests.
[Collision measurements](../../ZeroDepthTraining/results/cuda-followup/probe.json),
[PyTorch precision controls](https://docs.pytorch.org/docs/2.14/notes/cuda.html#tensorfloat-32-tf32-on-ampere-and-later-devices).

| Opportunity | Current judgment |
| --- | --- |
| Structured float32 packing + GPU expansion | Adopted after raw-bit parity and repeated final-host complete-cohort checks; uploads 73.7% smaller, modest collection improvement. |
| Compiled actor fusion | Full pipeline/refresh/owned-rollout checks pass; optional because whole-game gain was inconsistent despite faster isolated forward. Cold startup is separate. |
| Asynchronous independent game groups | Could overlap CPU service with GPU work. Requires independent buffers, stream events, frozen weights and episode/lane credit ownership; it is not achieved by merely setting `non_blocking=True`. |
| Larger batches/worker count | Revisit after packing removes transfer pressure; earlier partial-prefix 256/512 measurements do not establish completed-game throughput or future rollout capacity. |
| More forced automation | Prior paired exercise games had only 0.595% forced choices and no consistent speed benefit; broader tactical rules would change the learning problem. |
| Learner compilation/optimizer fusion | Fused CUDA Adam adopted after disposable-target parity, timing and recovery checks below. Learner compilation and multi-generation outcome-learning rates remain unmeasured. |
| GPU rules engine | Potentially removes CPU service and repeated state transfer, but requires integer/structured state, deterministic RNG, equivalent legal menus/effects and privacy tracking, plus replay parity across every rules branch. Card/effect divergence can limit GPU efficiency. |

The last GPU-engine judgment is an inference from this engine's iterator/card
structure and NVIDIA's control-flow guidance, not a measured port result.
CUDA warp divergence causes different branch paths to execute separately;
putting game rules in CUDA does not automatically make them efficient.
[NVIDIA control-flow guidance](https://docs.nvidia.com/cuda/cuda-c-best-practices-guide/index.html#branching-and-divergence).

No universal maximum-speed claim is possible from these bounded probes. The
required acceptance evidence is byte-exact public observations, legal-action
and episode ownership parity, actor/learner probability consistency, and
**naturally completed games per wall second** on the final frozen source, with
setup and learning overhead clearly separated. The CUDA changes improve
execution readiness; they do not demonstrate trained strength.

Raw probe checksums and script identities are in the
[manifest](../../ZeroDepthTraining/results/cuda-followup/manifest.json).
The isolated prototypes/retained fixture remain under
`/tmp/shards-zero-cuda-followup-20261002`; runtime adoption lives only in the
new transport module and the parent task's separately validated integration.

## Fused Adam followup

A separate bounded probe compared the existing ordinary CUDA Adam default with
`Adam(fused=True)` using the same width-256 float32 policy, 24,576 observation
floats, 64×48 candidates, and retained real encoded inputs. The 1,024-row case
repeated those 128 real rows eight times. All utilities were explicitly
fabricated and all updated models were discarded. The original frozen policy
checksum remained unchanged. [Adam probe](../../ZeroDepthTraining/results/cuda-followup/adam-probe.json).

The probe called the actual PPO learner, including behavior verification,
clipped ratios, entropy/value loss, KL stopping, gradient clipping and finite
parameter/Adam-state checks. An initial eight-epoch attempt at the normal
`3e-4` learning rate correctly stopped after one optimizer step because the
next KL exceeded the existing guard. That guard was preserved. Warm-step
timings therefore used `1e-7` exclusively for disposable numerical profiling;
separate single-step checks used the normal `3e-4` rate.

| Real input minibatch | Ordinary warm Adam GPU median | Fused warm Adam GPU median | Warm samples |
| --- | ---: | ---: | ---: |
| 128 | 0.720 ms | 0.266 ms | 7 per setting |
| 1,024 | 0.441 ms | 0.268 ms | 7 per setting |

Cold behavior is separate: the first optimizer call in the new process included
lazy backend/state initialization and took 115 ms wall time for ordinary Adam
and 9.73 ms for fused Adam. Later newly allocated optimizer states took about
0.89–1.91 ms wall time. These single-call cold values and seven-sample warm
measurements do not establish an overall training speedup. Whole instrumented
eight-step updates at 1,024 rows were 88.4 versus 78.7 ms, including identical
per-step synchronization instrumentation; the first 128-row ordinary update
also included other cold autograd startup and must not be used as a causal
comparison. [Raw timing scopes](../../ZeroDepthTraining/results/cuda-followup/adam-probe.json).

At the normal rate, maximum parameter difference after one step was `1.19e-7`,
first-moment difference `9.31e-10`, second-moment difference `3.28e-11`, and
step counters matched. The maximum resulting selected-log-probability error
was `4.77e-7` and value error `5.59e-9`. Both paths had finite learned parameters
and moments; behavior verification error stayed at or below `2.38e-7`.

The official Adam API supports fused float32 and describes its horizontal and
vertical fusion. Its ordinary CUDA default attempts the foreach implementation
when neither implementation flag is set. Preserve that original fallback by
passing `fused=True` only when enabled on CUDA and `None` otherwise; explicitly
passing `False` skips automatic foreach selection in the locally installed
PyTorch implementation. [Official Adam API and linked source](https://docs.pytorch.org/docs/2.14/generated/torch.optim.Adam.html).

Five new [optimizer regression tests](../../ZeroDepthTraining/tests/test_fused_optimizer.py)
passed: CPU fallback with a fabricated update, Boolean flag validation, normal
rate CUDA fused/ordinary parameter and moment parity, unchanged likelihood and
nonfinite-moment guards, and an in-memory state serialization/load followed by
a **bit-identical next update** on the same fabricated rollout. These use a small
synthetic fixture; the separate probe above exercises the actual large schema.
No campaign checkpoint, ledger or trained outcome model was created.


## Final v2 runtime integration

The final host is SHA-256
`9ad06881d4ae98646c58c6d2a8acd4edc8860030819e4e2476a9925178c144c0`.
The new prepared campaign enables lossless packed inputs and fused CUDA Adam.
Compiler fusion is supported through `compiled_actor`, with a conservative
ordinary-forward default. Both learner and archive-opponent actors receive the
pinned flags; evaluation uses them too and explicitly selects IEEE float32.
Warmup/capture completes before resume RNG restoration. CPU execution keeps its
ordinary input and optimizer paths.

The [integration tests](../../ZeroDepthTraining/tests/test_optimized_actor.py)
verify raw input bits, dynamic extents, direct and staged paths, all captured
buckets, nonzero weight/semantic refresh, eager behavior probabilities, archive
lane mapping and owned rollouts. Together with six transport tests and five
optimizer tests, the final Python suites pass 53 tests. Existing engine tests
pass 278; final host visibility and zone oracles pass too.

Five bounded matching-workload probes used 128 lanes, eight workers, singleton
automation and CUDA graphs. All shared seed cohorts have identical action,
terminal-reward and decision-count digests. Dense runs completed 896 games each
at 43.95 and 42.24 games/s. Packed runs completed 1,024 and 896 games at 46.39 and
43.66 games/s. Their aggregate rates were **43.08 dense versus 45.07 packed**
(about 4.6% higher), with overlapping small-run variation. Packed GPU uploads
were consistently **73.7% smaller** than the same dense bucket payload. Initial
retained-input upload plus expansion measured about 0.18 ms versus about 0.55 ms
for dense upload. [First dense](../../ZeroDepthTraining/results/visibility-v2-dense-cohorts.json),
[first packed](../../ZeroDepthTraining/results/visibility-v2-packed-cohorts.json),
[repeat packed](../../ZeroDepthTraining/results/visibility-v2-packed-repeat.json),
[repeat dense](../../ZeroDepthTraining/results/visibility-v2-dense-repeat.json).

A matching compiled/packed run completed 1,024 games at 46.02 games/s. The
isolated forward/sampling interval was about 0.12 ms versus 0.23–0.26 ms ordinary,
but the whole-game result did not consistently exceed packing alone. Its setup
was 3.89 seconds with existing compiler-cache artifacts, versus 1.2–1.4 seconds
ordinary. The earlier 6.17-second cold compilation measurement remains separate.
[Compiled matching workload](../../ZeroDepthTraining/results/visibility-v2-compiled-packed-repeat.json).

An additional compiled/packed disposable optimizer validation completed a finite
normal-rate fabricated-target PPO step with selected-log-probability error
`1.19e-7`, value error zero and unchanged original frozen policy. The later frozen
collection completed 1,024 natural games at 47.58 games/s. This validation's extra
sampling call changes subsequent trajectories, so that rate is excluded from
the matching-workload comparison. Across all six final-host probes there were
**5,760 natural completions, zero censors and zero unfinished games**. No actual
game outcomes were used to update a retained model.
[Disposable integration check](../../ZeroDepthTraining/results/visibility-v2-compiled-packed-cohorts.json).

These are collection measurements plus separately scoped disposable optimizer
checks, not a measurement of multi-generation learning throughput. The complete
[current validation](../../ZeroDepthTraining/VALIDATION.md) includes exact replay
checks, fresh preparation pins, and the new untrained paired incumbent check.
A full CUDA rules-engine port remains a distinct equivalence project; this work
implements the targeted custom GPU expansion kernel without porting game rules.

## Follow-up learner batching and ownership audit

A second bounded probe exercised a **60,000-row owned CUDA rollout**, the real
24,576-float observation and 64-by-48 candidate dimensions, width 256, and 1,024-row
PPO minibatches. It repeated the retained 128-row encoded fixture above; its
host/catalog hashes are historical and do not establish current final-host
equivalence. Every return was explicitly fabricated and all resulting parameter
updates were discarded. No campaign, actual outcome learning or saved model was
created. The rollout owned 6,640,560,000 bytes; total peak allocated CUDA memory
in the probe was about 7.77 GB. [Raw results](../../ZeroDepthTraining/results/cuda-followup/learner-batching-probe.json),
[source and fixture hashes](../../ZeroDepthTraining/results/cuda-followup/learner-batching-manifest.json).

Five alternating warm comparisons restored identical initial policy weights,
zeroed already-allocated Adam moments and step counters, and used the identical
NumPy permutation. Each call verified every retained behavior row, then made
59 fabricated-target optimizer steps covering all 60,000 rows. The profiling
rate was `1e-7` to preserve the unchanged KL guard throughout the full comparison.

| Warm full verification plus one PPO epoch | Median wall time | Observed range |
| --- | ---: | ---: |
| Previous learner | 597.1 ms | 532.9–720.7 ms |
| Adopted candidate with fresh owned gathers | 570.8 ms | 492.7–610.6 ms |
| Trial with reusable dense gather outputs | 557.2 ms | 511.5–594.0 ms |

The adopted version reduced the medians by **4.4%**, with substantial overlapping
variation. The earlier sequential warm comparison showed a larger difference;
the controlled comparison above is the appropriate claim. First-process backend
initialization was separate and is not a causal speedup claim. These measure the
learner only and do not measure complete campaign throughput.

The learner now uploads retained row indices, returns and advantages once per
generation, uploads one unchanged NumPy permutation per epoch, and reorders the
small target vectors on the GPU. Behavior maxima remain on the device until the
full verification pass finishes. KL and loss each retain their finite check and
one scalar read; the final entropy and gradient norm are read when returning
metrics. All PPO clipping, target-KL stopping, gradient clipping, deadline checks,
heartbeat calls and finite parameter/Adam checks remain in place. CPU staging
is allocated only for CPU-backed rollouts, avoiding **113,332,224 bytes** of unused
pinned memory in the normal CUDA-owned path.

Reusable dense gather buffers showed no isolated gain with resident indices
(roughly 6.1 ms for 40 gathers in both versions). The runtime therefore keeps
fresh owned minibatch tensors, avoiding an added borrowed-buffer contract during
autograd. PyTorch documents that ordinary `index_select` creates storage separate
from its input. [Official index_select API](https://docs.pytorch.org/docs/2.14/generated/torch.index_select.html).

Two focused regressions demonstrated failures before correction. First,
`max(0., NaN)` could silently accept a NaN stored log probability or value and
report zero behavior error. Device `torch.maximum` now preserves the NaN so the
existing final finite guard rejects it, while checking every retained row,
including the final partial batch. [Official NaN behavior](https://docs.pytorch.org/docs/2.14/generated/torch.maximum.html).
Second, asynchronously copying reused pinned CPU staging requires an ownership
boundary once per-minibatch verifier readbacks are removed. A delayed-transfer
test showed the first batch receiving rows 16–31 instead of rows 0–15. The CPU
fallback now records an event after all staging transfers and waits for that
event before refilling any source buffer. This waits for the preceding H2D work
without requiring a device-wide barrier. The ordinary GPU-owned path does not
use this staging. [PyTorch pinned-memory mutation example](https://docs.pytorch.org/tutorials/intermediate/pinmem_nonblock.html),
[CUDA event record/synchronize API](https://docs.pytorch.org/docs/2.14/generated/torch.cuda.Event.html).

At the normal `3e-4` rate, the isolated large-input single-step comparison had
maximum parameter difference `5.96e-8`, first-moment difference `3.59e-12` and
second-moment difference below `9e-19`; step counters and NumPy RNG state matched.
Both paths remained finite. The adopted implementation's additional normal-rate
regression compares CUDA-owned experience with identical CPU-backed experience
consumed by the CUDA learner.

All **eight new learner tests pass**, covering NaN/Infinity behavior rejection,
every-row verification, repeated/reordered row selection, owned source storage,
exact shuffled observation/return/advantage correspondence, exclusion of
censored rows, deadline counters and returned metrics, nonfinite-target rejection
before an optimizer step, pinned-memory transfer lifetime, and normal-rate
parameter/moment parity. [Focused regressions](../../ZeroDepthTraining/tests/test_learner_batches.py).

An isolated 128-row append probe also measured direct contiguous source-to-owned
destination copies at **0.0477 ms versus 0.1752 ms** for the previous gather plus
copy. Reordered `index_select(out=owned_destination)` measured **0.167 versus
0.363 ms**, with noticeable timing variation. These are movement-only timings,
including index staging and float-to-bool mask conversion, not engine or
whole-game measurements. The separate rollout integration keeps destination
ownership and has its own reordered/subset and allocation regressions.
