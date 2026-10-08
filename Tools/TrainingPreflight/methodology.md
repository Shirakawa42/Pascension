# Training preflight measurement protocol

Date: 2026-09-25. This is a fresh benchmark protocol for the current all-DLC Shards engine. It does not use the deleted AI. The agreed real-training budget remains **12 hours**, with evaluation afterward; this preflight must not start a long training run. Synthetic optimizer steps test execution and numerics, not playing strength. Any actual learning pilot must be identified and charged to the training allocation.

## Runtime selection and reproducibility

At research time the current official release is PyTorch **2.14.0**, and the official CUDA 13.0 index lists a Linux x86-64 Python 3.12 wheel. The preferred new-environment command is `pip install torch==2.14.0 --index-url https://download.pytorch.org/whl/cu130`; torchvision and torchaudio are unnecessary here. Record what the package server actually supplies and the version imported locally: a published web index is not proof that this machine installed it. [Release announcement](https://pytorch.org/blog/pytorch-2-14-release-blog/), [official wheel index](https://download.pytorch.org/whl/cu130/torch/).

PyTorch 2.12 deprecated CUDA 12.8 wheels and recommends CUDA 13.0+ for Blackwell. Its CUDA 13.0 Windows driver minimum is 580.88, below the locally reported 616.56. Actual CUDA forward/backward/optimizer and compiled execution must still succeed. [Official compatibility change](https://pytorch.org/blog/pytorch-2-12-release-blog/).

WSL uses the Windows driver: do not install a Linux display driver. NVIDIA documents limited pinned-memory availability and incomplete monitoring queries on WSL; missing NVML fields are not zero utilization. Record both NVML global memory use and CUDA `mem_get_info`, which can disagree under this environment; do not subtract one into the other or infer process ownership. Preserve other applications and keep conservative allocation headroom. Use small reusable staging buffers. [NVIDIA WSL guide](https://docs.nvidia.com/cuda/wsl-user-guide/index.html).

Every saved run needs source/configuration hashes, command, seed, runtime/package versions, device capability, thread counts, GC mode, observation/candidate dimensions, model size, dtype, compile mode, warm-up, repeats, free VRAM, measured duration and error/overflow counts. Store caches and the Python environment on the Linux filesystem; Microsoft's recommendation concerns Linux file workloads and does not imply the repository mount slows an already resident simulator. [WSL filesystems](https://learn.microsoft.com/en-us/windows/wsl/filesystems).

## Measurements that can be compared

Use one timed workload at a time; concurrent independent benchmarks compete for this single CPU/GPU. Warm the engine, CUDA libraries and each tensor shape separately. Report cold compilation time, warm median, range and p95 batch latency over at least three repeats. Select finalists with interleaved A/B repeats rather than attributing a change in unrelated application load to an optimization.

Host enqueue time is not completed GPU time. Use CUDA events for device regions and synchronized wall-clock boundaries for complete request/response measurements. Set CPU thread counts explicitly; PyTorch's benchmark timer defaults to one thread. Keep input creation out of a kernel-only timing but inside an end-to-end timing whenever the real pipeline pays that cost. [PyTorch benchmarking recipe](https://docs.pytorch.org/tutorials/recipes/recipes/benchmark.html).

Capture a short CPU/CUDA profiler trace to identify gaps and expensive operators, then disable profiling for the reported throughput. Give regions concrete labels: engine advance, legal candidates, encoding, serialization, waiting, H2D, forward, mask/sample, D2H, return calculation, backward, optimizer. Profiling overhead can distort the behavior it measures. [Profiler recipe](https://docs.pytorch.org/tutorials/recipes/recipes/profiler_recipe.html), [profiler overhead guidance](https://docs.pytorch.org/tutorials/beginner/profiler.html).

Keep these counters separate:

| Counter | Meaning |
| --- | --- |
| Engine submissions | Actions accepted by the authoritative engine |
| Policy decisions | Actual model decisions, excluding forced transitions |
| Learning samples | Valid learner-owned trajectory items; exclude frozen-opponent decisions |
| Optimizer sample uses | Minibatch rows across epochs; repeated uses are not new experience |
| Completed games | Terminal games; report administrative truncations separately |
| Adapter coverage | Ordinary actions, decision types, factorized subchoices and unsupported cases |

Report model requests/s and completed games/s alongside learner updates/s. A simulator with random choices, a model on random tensors, and a pipeline with defaulted combat answer different questions. None alone establishes all-DLC training throughput or readiness.

## Ordered experiment matrix

These are engineering candidates to measure, not claims that every option is already implemented or beneficial. Change one axis while preserving observations, masks, chosen action semantics and workload seeds. Retest the winning combination end to end.

| Suspected limit | First controlled comparison | Evidence needed before adopting |
| --- | --- | --- |
| Engine/encoder | Bare engine; compact numeric encoding; presentation snapshot reference | Stage time, allocations/submission, identical actions and terminal outcomes |
| CPU scheduling | 1/2/4/6/8/12 workers; independently 32/128/512/1,024 resident games | Useful decisions/s, queue starvation, RAM, CPU time; preserve scheduler capacity for Python |
| Managed allocation | Workstation versus server GC, then reuse simple owned arrays | GC counts/pause time and allocations, no growing retained heap |
| Transport | Binary pipe versus shared-memory payload plus a small control pipe | Complete round-trip and copy costs, bytes/decision, sequence/ownership correctness |
| Small-policy overhead | CPU FP32 inference with 1/2/4 threads versus GPU at batches 1/8/32/128/512/1,024 | Full round-trip crossover, including CPU actors competing for cores |
| Encoder size | Counts + scalars MLP versus pooled entity encoder; widths 128/256/512 | Speed and memory first; equal-time learning needed later for strength |
| Launch overhead | Eager, compiled default, reduce-overhead, then manual CUDA graph on stable shapes | Warm full-batch latency, compile amortization and numerical/mask parity |
| Dense compute | FP32 reference, TF32 permitted, BF16 autocast | Forward probabilities, finite gradients/loss, effective updates/s |
| Learner | Minibatches 256/1,024/4,096; foreach versus fused Adam/AdamW | Complete forward/loss/backward/step time; compare the same objective |
| Pipeline overlap | Sequential baseline; two reusable staging slots; actor batches interleaved with learner chunks | Queue occupancy, policy age, combined experience + update rate, no buffer races |

Server GC uses multiple heaps and dedicated threads; it is a benchmark option, not a universal improvement. Multiple independent server-GC processes can oversubscribe a machine. [Microsoft GC guidance](https://learn.microsoft.com/en-us/dotnet/standard/garbage-collection/workstation-server-gc).

PyTorch documents default, reduce-overhead, max-autotune and max-autotune-no-cudagraphs modes. Reduce-overhead targets Python launch overhead with CUDA graphs, can consume extra memory and is not guaranteed to capture every graph. Only try expensive autotuning after a stable baseline and a clear dense-compute bottleneck. Record recompile/graph-break logs and cover all expected shape buckets. [Compile API](https://docs.pytorch.org/docs/2.14/generated/torch.compile.html).

For a compilation cost `C` and per-batch saving `d > 0`, break-even is `C / d` batches. Sum compilation costs across shapes and policy variants. Warm benchmarks exclude this cost deliberately, so also calculate projected amortized time for a 12-hour run. This formula is arithmetic, not a speedup forecast.

CUDA graph replay requires stable addresses and graph-safe execution; preserve input/output buffers, warm on a side stream and explicitly manage their lifetime. Input copying and response download still cost time. Eager engine control flow is outside the captured GPU graph. [CUDA graph semantics](https://docs.pytorch.org/docs/2.14/notes/cuda.html).

Compare preallocated pinned buffers with ordinary copies. Calling `.pin_memory()` on a newly allocated pageable tensor in every hot-path batch adds a blocking copy; it can lose to direct transfer. A shared-memory mapping is not automatically CUDA-pinned. Do not overwrite a pinned upload source before completion or read a nonblocking GPU-to-CPU result early. [PyTorch transfer guide](https://docs.pytorch.org/tutorials/intermediate/pinmem_nonblock.html).

Fused optimizers reduce sequential kernel launches relative to parameter-by-parameter loops, but benchmark the real parameter sizes and dtype. Keep log probabilities, masks, return arithmetic, entropy and PPO ratios in FP32 initially. Do not increase optimizer epochs merely to fill the GPU; more repeated sample uses can change the learning result. [Optimizer implementations](https://docs.pytorch.org/docs/2.14/optim.html).

## Lower-cost approaches worth testing

1. **Elide exactly forced actions.** If the full legal action set has one member, the engine can advance without inference. Preserve rewards, termination and elapsed-transition semantics. Excluding concede, defaulting a multi-choice decision, automatically playing a hand, or always focusing is an action restriction, not a forced transition. Record restricted-policy benchmarks explicitly.
2. **Bucket candidates and keep overflow.** Compare a few capacities such as 16/32/64/128 against always padding to the largest capacity. Carry exact masks and fail visibly or route to a larger path on overflow; observed maxima are not valid bounds. Measure the padding fraction and any batching delay.
3. **Cache frozen content.** Map card definitions and static features once; keep mutable costs/defense/ownership separate. Share the state trunk across all candidates instead of rerunning the whole network for every candidate.
4. **Keep actors on CPU if they win.** A compact CPU actor can leave the GPU available for large learner batches. This is a real pipeline alternative, not a fallback selected by GPU utilization. Frozen actor versions and refresh costs remain measurable.
5. **Use a bounded buffer ring.** Overlap engine work, upload and inference only after measuring their sequential contributions. Return sampled actions in one download. Per-row `.item()`, `.cpu()` and CUDA `nonzero()` can force synchronization. [PyTorch tuning guide](https://docs.pytorch.org/tutorials/recipes/recipes/tuning_guide.html).
6. **Encode less information before rewriting the rules.** Count vectors can discard private order while retaining composition. Their speed is straightforward to measure; their strategic sufficiency requires subsequent learning and richer-opponent audits. Directly encoding current authorized state avoids presentation allocation without changing game rules.

An exact damage-allocation adapter can also compare direct integer choices for small remaining power against binary interval choices for large ranges. Both retain all allocations, but the former uses fewer policy round trips while the latter bounds menu size. Count the resulting subchoices separately. A normalized alphabetical card index alone is a weak candidate feature: a purely bilinear scorer ranks otherwise identical candidates monotonically by that arbitrary index. Compare a learned card-ID embedding or nonlinear candidate encoder before treating the fastest linear head as the final policy.

## Correctness gates and stopping criteria

Before interpreting throughput, require zero rejected engine actions, zero unreported truncation/overflow, exact pending-input ownership, and observation invariance under unauthorized hidden-state permutations. Include defended combat, temporary fast plays, Comet restrictions, extra turns, optional/ordered/multi-select choices and terminal/truncation distinctions. A speed harness may use an explicitly restricted decision subset while these remain incomplete; that harness cannot be promoted to the full trainer merely because it is fast.

For a trajectory benchmark, retain behavior log probability, exact mask, policy version and decision owner. Do not alternate the value sign on every engine action: one seat can make several consecutive choices. Synthetic PPO losses must be labeled synthetic, particularly if rewards, returns and old log probabilities are generated instead of produced by actual games.

After each accepted change, remeasure the full pipeline and identify its new largest avoidable contribution. For a sequential fraction `f`, even eliminating that stage entirely can improve speed by at most `1 / (1 - f)`; once overlap exists use the critical path and queue traces, not a sum of overlapping timers. This is an arithmetic decision aid.

Stop the current optimization branch when its reproducible end-to-end gain is below roughly 5% or ordinary run-to-run variation, it increases memory/latency materially, or correctness/strategic coverage degrades. These are proposed engineering thresholds, not statistical guarantees. Keep rejected options and their evidence. End this preflight pass when the leading profile has no unexamined large avoidable gap, finalists survive sustained repeated runs with stable memory, and remaining choices require actual learning or a disproportionate rewrite. Record untested alternatives and blockers; do not claim that every possible optimization has been exhausted.

Optimize **valid experience plus useful updates per second**, then held-out strength per training hour. NVIDIA's utilization percentage tracks time with active kernels rather than the fraction of peak arithmetic achieved; other desktop workloads can also contribute. Reaching 100% is not a success condition. [NVIDIA utilization definition](https://docs.nvidia.com/deploy/nvidia-smi/index.html).
