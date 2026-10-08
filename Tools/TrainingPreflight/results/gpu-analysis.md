# GPU microbenchmark analysis — 2026-09-25

The main sweep completed **108/108 cases successfully** in 502.68 seconds: 54 actor configurations and 54 learner configurations. Runtime was PyTorch 2.14.0+cu130 on the RTX 5090. Source: [gpu-main-sweep.json](gpu-main-sweep.json), including source hashes and complete commands. All observations below are synthetic systems measurements, not playing-strength results.

## Findings that already have useful support

Manual CUDA graphs remove a substantial synchronous dispatch cost for this small model. At width 128, batch 256 and FP32, actor roundtrip throughput increased from 251,625 to 823,751 decisions/s. At width 128, batch 1024 and TF32, the resident actor reached 5.73 million decisions/s but only 1.28 million decisions/s after input upload and output download. Improving dense matrix throughput alone cannot recover that transfer/synchronization gap.

Width 256 and batch 1024 reached essentially the same roundtrip throughput as width 128. This is a reason to retain width 256 for subsequent capacity experiments; it is not evidence that either width plays better. Candidate embeddings remain important: the bilinear throughput floor cannot represent arbitrary card identity preferences from a scalar normalized ID.

Representative graph actor cases (three 0.15-second repeats per phase):

| Width | Batch | Precision | Resident decisions/s | Roundtrip decisions/s | Median batch latency | p95 batch latency |
| --- | --- | --- | --- | --- | --- | --- |
| 128 | 32 | TF32 | 326,609 | 191,866 | 0.140 ms | 0.288 ms |
| 128 | 32 | BF16 | 197,949 | 107,942 | 0.194 ms | 0.447 ms |
| 128 | 256 | TF32 | 2,103,782 | 854,695 | 0.280 ms | 0.398 ms |
| 128 | 256 | BF16 | 1,731,063 | 798,936 | 0.311 ms | 0.385 ms |
| 128 | 1024 | TF32 | 5,729,777 | 1,281,764 | 0.772 ms | 1.069 ms |
| 256 | 1024 | TF32 | 4,896,504 | 1,278,007 | 0.782 ms | 0.992 ms |
| 256 | 1024 | BF16 | 4,852,202 | 1,263,761 | 0.793 ms | 1.046 ms |
| 512 | 1024 | BF16 | 5,204,230 | 1,253,328 | 0.789 ms | 1.081 ms |

Throughput is the median repeat rate; latency quantiles pool individual calls. They need not be exact reciprocals when pauses and call latency vary.

## Learner candidates

Manual graphs also substantially outperform eager learner dispatch in this grid. At width 128, batch 1024 and TF32, the eager learner completed 138,297 example-passes/s and the graph learner completed 1,144,619/s. Keep learner and actor choices separate: the best observed learner shape is wider than the actor throughput floor.

| Width | Batch | Precision | Graph example-passes/s | Median update latency | p95 update latency |
| --- | --- | --- | --- | --- | --- |
| 128 | 256 | TF32 | 712,766 | 0.278 ms | 0.770 ms |
| 128 | 1024 | TF32 | 1,144,619 | 0.877 ms | 1.020 ms |
| 128 | 1024 | BF16 | 1,108,567 | 0.899 ms | 1.088 ms |
| 256 | 1024 | TF32 | 1,021,404 | 0.902 ms | 1.481 ms |
| 256 | 1024 | BF16 | 718,382 | 1.446 ms | 1.559 ms |
| 512 | 1024 | FP32 | 757,314 | 1.376 ms | 2.525 ms |
| 512 | 1024 | TF32 | 2,312,027 | 0.432 ms | 0.474 ms |
| 512 | 1024 | BF16 | 2,148,088 | 0.426 ms | 0.715 ms |

The width-512 speedup over width 256 deserves investigation. Better matrix kernel shapes/utilization is one possible explanation; a different amount of external GPU contention during this sequential sweep is another. Neither explanation has been established by this run. Retain widths 256 and 512 for interleaved confirmation and compilation tests; do not downsize the model solely on parameter count. No batch larger than 1024 was measured in the main grid.

## Numerical checks

All cases passed legality, finite-output/gradient checks, invalid-row rejection, and their relevant eager-execution or graph-RNG checks. Across the entire grid:

| Check | Largest observed difference |
| --- | --- |
| Legal logit versus strict FP32 | 0.011620 absolute |
| Probability versus strict FP32 | 0.001424 absolute |
| Value versus strict FP32 | 0.001501 absolute |
| Gradient relative L2 versus strict FP32 | 0.006780 |
| Lowest gradient cosine versus strict FP32 | 0.999978 |
| Actor log-probability/value execution parity | 0 absolute |
| Learner loss execution parity | 0 absolute |
| One-step optimizer parameter execution parity | 1.19e-7 absolute |

These bounds come from random numerical fixtures, not a trained policy or an exhaustive real-state corpus. The largest process allocator peak was 317.4 MiB, including correctness/reference work and graph pools. That is not total device VRAM use.

## Noise and memory accounting

At least 16 of the 54 actor configurations had a greater than 1.3× difference between their fastest and slowest repeat. The width-256/batch-256 TF32 graph result (414,703/s roundtrip) is much worse than both width 128 and width 512 at that same batch, and its resident throughput is unusually low too. This is not a credible basis for declaring width 256 intrinsically slower. Repeat promising configurations in an interleaved order with longer measurement windows.

Four of the 54 learner configurations crossed the same 1.3× repeat-spread threshold. The largest spread was 2.03× for an actor configuration and 1.67× for a learner configuration. Low within-case spread cannot rule out a persistent external load difference between cases.

External GPU activity is material. The nearby [transfer probe](transfer-initial.json) recorded `nvidia-smi` utilization of 100%, 458.89 W and 22,684 MiB used before its work. Its CUDA memory query simultaneously suggested only about 1.73 GB used. The main sweep's CUDA memory query also remained near 1.73 GB used. These WSL queries are not interchangeable: use process allocator statistics for this process and retain the separate driver telemetry. Do not call the CUDA `used_before` field an authoritative measurement of other applications' VRAM.

The main sweep did not record per-case external GPU utilization/power. Its close precision/model comparisons therefore remain provisional even when repeat spread happens to be low. A roughly 1% TF32/BF16 difference at batch 1024 is smaller than the observed noise.

## Why BF16 can be slower here

These are hypotheses supported by the inspected execution path, pending profiler attribution:

1. Small batches do too little arithmetic to amortize casting, launches, sampling and FP32 reductions. Faster Tensor Core arithmetic does not remove those costs.
2. Autocast produces BF16 candidate projections, while the embedding lookup returns FP32. Adding them promotes the `[B,A,64]` keys to FP32; autocast then converts the result to BF16 for `bmm`. That adds movement and conversion around a small matrix operation.
3. Each eager/no-grad actor invocation enters autocast while weights are stored as FP32. Weight conversion/cache lifetime can matter for these short calls. Explicit graph replay removes Python launch overhead but still executes captured conversions.
4. Random gathers, legal masking, Gumbel noise, argmax, log-softmax and tiny output copies are not all accelerated by choosing BF16 dense layers. `torch.compile` may fuse some of these operations; manual graph replay does not itself fuse kernels.

A useful follow-up is a frozen actor copy whose weights are stored in BF16. Any descriptor conversion must explicitly validate categorical decoding and numeric feature error. The current 193 identity codes `i / 192` survive BF16 round-trip and `round(slot16 * 192)`; a categorical alias in this schema has not been demonstrated. This does not establish safety for arbitrary numeric amounts or future schemas. Another ablation casts embedding outputs to the projection dtype before addition. Both changes require fresh numerical/parity checks and should be compared with the original source-fingerprinted baseline.

## Initial follow-up plan and completion status

This table records the initial plan. The [compile suite](compile/manifest.json)
subsequently completed four actor cases at widths 256 and batches 256/1024,
and four learner cases at width 256 and batches 1024/2048. The
[CPU/GPU crossover suite](crossover/manifest.json) completed all sixteen cases.
Width-512 compiled learning and controlled precision-ranking confirmation remain
unestablished; the final report records GPU contention limits.

| Purpose | Configuration matrix | Count |
| --- | --- | --- |
| Compile actor | Width 256; batches 256 and 1024; TF32 and BF16 | 4 |
| Compile learner | Widths 256 and 512; batch 1024; TF32 and BF16 | 4 |
| CPU crossover | One CPU thread, FP32 eager; widths 128 and 256; batches 1, 8, 32 and 128 | 8 |
| Confirm precision ranking | Graph, width 256; batches 256 and 1024; TF32 and BF16; interleaved, five repeats of at least 1 second | 4 configurations |

Batch 2048 was absent from the initial 108-case grid; it was subsequently
measured in the compiled learner and integrated disposable-load experiments.
That does not establish an optimal batch or a controlled width comparison.

Use the CPU comparison's full request/response cost and account for the CPU cores it takes from simulation. Compilation has to beat the measured manual-graph reference after its cold-start cost, not merely eager execution. Preserve both actor and learner performance: actor-optimal precision need not be learner-optimal.

For the integrated pipeline, prioritize actual stage measurements before adding model complexity: legal menu generation, observation encoding, transport, H2D, actor compute, D2H, and optimizer load. If copies remain dominant, reduce zero padding with legal-candidate buckets (for example 8/16/32/64 with an overflow path), pack sparse counts or smaller safe numeric fields, or overlap batches. Validate exact legal options and card identities before accepting compression. A compact wire protocol can preserve information while reducing bytes; deleting strategically useful information is a separate strength ablation.

## What synthetic observations cannot establish

Synthetic observations are independent Gaussian features, with legal counts sampled uniformly from 1 through 64 and random card IDs. Real states contain structured sparse deck counts, repeated starter cards, correlated resources, and different menu-size frequencies. Fixed padded shapes make many kernel costs transferable, but neither the numerical error distribution nor strategy quality is validated by random tensors alone.

The actor samples an untrained distribution. The real-engine exercise policy deliberately adds action-kind biases to visit longer games; that distribution also is not a strength baseline. The integrated benchmark additionally offers a separately seeded exercise action source, so execution/transport comparisons can consume the same game prefix despite different CUDA warmup RNG consumption. Keep model-selected actions as a separate policy-boundary check. A trained policy may be highly peaked, and real DLC states can stress feature scales or rarely used action descriptors not covered by these random fixtures. Recheck probabilities/values and action legality on recorded real observations, including unusual decisions.

Learner rates count repeated example-passes through fixed synthetic observations, fabricated advantages and targets. They are not fresh experience, completed games, or a projection of learning speed over twelve hours. A real trainer still needs rollout/return correctness, policy-version handling, valid opponent sampling, and paired-seed strength evaluation.

## Recorded-state confirmation

The follow-up [real-state GPU sweep](gpu-real-states.json) passed 16/16 cases: width 256, batches 256/1024, TF32/BF16, eager/graph, actor/learner. It uses the [recorded corpus](real-states.npz), SHA-256 `90e3bd1a3f80332aca0328bc4c7ae7d2796e833bca2c504f41c5467e08cf140b`, with 2,048 states and actual legal selected actions. Shape/mask/card-ID checks cover the full corpus; numerical parity in that sweep covers its first 1,024 distinct rows, because smaller batches are prefixes. Four additional [batch-2048 graph cases](gpu-real-all-rows.json) passed and numerically exercise every row, including both precisions and actor/learner paths. The corpus has a mean of 9.73 legal candidates, maximum 28, and 144 distinct legal card-definition codes plus the zero/no-card sentinel. The initial collector's `card_definition_ids_seen` list includes that sentinel. These are coverage statistics, not limits on future states.

| Real-state precision check | TF32 worst observed | BF16 worst observed |
| --- | --- | --- |
| Legal logit absolute error | 0.0002124 | 0.0073391 |
| Probability absolute error | 0.0000265 | 0.0013121 |
| Value absolute error | 0.0002076 | 0.0035192 |
| Gradient relative L2 error | 0.0002118 | 0.0040382 |
| Lowest gradient cosine | 0.99999994 | 0.99999177 |

The largest one-step optimizer execution-parity difference was 2.98e-8. All output legality and finite checks passed. This supports both precision paths on these untrained models and recorded states; it does not establish their long-run learning equivalence. A batch-2048 parity case would cover all recorded rows numerically.

Real-state graph actor roundtrip rates were 365,990/s (TF32) and 351,274/s (BF16) at batch 256, and 707,780/s / 750,164/s at batch 1024. Learner graph rates at batch 1024 were 598,957 / 586,718 example-passes/s. These differ materially from the earlier synthetic run, reinforcing the need for controlled confirmation and live-pipeline measurements instead of treating a synthetic peak as sustainable training speed.

## Exact sparse CPU alternative

The [sparse CPU experiment](sparse-cpu.json) completed 12/12 cases in 19.84 seconds. It replaces only the first dense linear layer with a weighted `embedding_bag` over the observation's nonzero feature indices. All remaining model operations, FP32 candidate/card identity data, sampling and output contracts stay the same. It separately times index extraction/packing, a complete packing-inclusive actor, and a prepacked upper bound. Every case passed logits/value/probability parity, legal sampling, and explicit empty-row/signed-fractional-feature checks. Maximum legal-logit error was 4.47e-8.

Selected batches averaged 48–60 nonzero features out of 2,048, roughly 2.3–2.9% density. Despite that sparsity, scan/packing and the unchanged candidate head consume much of the potential saving:

| One CPU thread | Dense decisions/s | Sparse including packing | Prepacked ceiling |
| --- | --- | --- | --- |
| Width 128, batch 32 | 34,488 | 29,546 | 40,830 |
| Width 128, batch 128 | 30,673 | 31,439 | 45,874 |
| Width 256, batch 128 | 21,134 | 31,254 | 37,090 |
| Width 256, batch 256 | 22,239 | 23,021 | 30,640 |

The width-256/batch-128 result is a useful 1.48× packing-inclusive improvement, with relatively modest within-case repeat variation. However, sparse packing alone costs about 0.50 ms per batch there, and overall CPU actor rates remain far below the measured GPU actor rates. Four-thread outcomes are inconsistent and noisy: one actor path spans 4.28× between repeats, and one packing-only path spans 12.44×. Those results do not justify spending four simulation cores on inference.

This first-layer-only variant does not justify replacing the GPU actor. Its prepacked ceiling omits work that the current encoder does not yet do. The additional legal-candidate experiment below materially improves CPU prospects. Neither these PyTorch rates nor mathematical equivalence establish performance for a C# implementation or playing strength.

## Sparse CPU with legal candidate scoring

The [ragged CPU follow-up](sparse-ragged-cpu.json) passed six cases in 15.91 seconds. It keeps the sparse first layer and additionally gathers every legal candidate with its original `(row, slot)`, evaluates the same candidate projection/embedding/bias, and scatters scores back into the original 64-slot masked output. The measured batches average 8.7–9.4 legal candidates; none are removed, truncated or renumbered. An explicit non-prefix fixture verifies slots 0, 5 and 63.

| One CPU thread | Dense actor/s | Sparse + legal scoring, including both packing steps | Prepacked ceiling |
| --- | --- | --- | --- |
| Width 128, batch 64 | 41,785 | 82,043 | 148,862 |
| Width 128, batch 128 | 33,585 | 90,404 | 167,954 |
| Width 128, batch 256 | 37,322 | 94,122 | 201,802 |
| Width 256, batch 64 | 28,070 | 76,660 | 118,116 |
| Width 256, batch 128 | 29,435 | 77,660 | 137,713 |
| Width 256, batch 256 | 30,454 | 81,652 | 146,219 |

Packing-inclusive throughput improves 1.96–2.73× over dense inference. Observed repeat spreads for that path are 1.05–1.24×. Maximum legal-logit difference is 4.47e-8, probability difference 2.98e-8, and value difference 7.45e-9; greedy action agreement is 100% on these fixtures. All sampled actions remain finite/legal. These are exact transformations up to FP32 summation order.

Retain the CPU actor as an alternative for separating inference from GPU learning, subject to the integrated results below. The current 77–94k/s one-thread result is achieved with packing; 118–202k/s is only the prepacked ceiling. Emitting sparse observations and legal descriptors directly could remove roughly 0.4–0.9 ms of packing per 128–256 rows, but that encoder has not been implemented or timed here. These data support keeping the option available, not declaring it the default winner.

The subsequent [eight-run integrated comparison](cpu-ragged-pipeline/manifest.json) uses batch 256, four simulation workers, shared-memory transport, the same exercise action source and 32 warmup steps. Each variant has two eight-second measurements; no learner load is active.

| Integrated actor | Decisions/s across the two runs | Median across runs |
| --- | --- | --- |
| Dense CPU, width 128 | 11,736–13,043 | 12,389 |
| Sparse/ragged CPU, width 128 | 24,079–29,885 | 26,982 |
| Sparse/ragged CPU, width 256 | 20,471–27,992 | 24,232 |
| CUDA graph GPU, width 256 | 47,388–52,517 | 49,952 |

The optimized CPU path improves the integrated dense baseline by about 2.18× at width 128. Its standalone 94k/s result becomes 24–30k/s after accounting for the host, transport, exercise selection and shared CPU resources. The GPU route remains about 1.85× faster than width-128 ragged CPU across these runs. Startup parity checks pass, including original slots, category bias, packet log-probability/value, and the safely aliased synchronous host input buffer.

Use GPU inference as the current measured default and retain CPU ragged inference as a fallback or a future scheduling experiment. Concurrent CPU inference with GPU learning has not been implemented or measured. External utilization differs materially between these runs—CPU runs observe unrelated GPU utilization from 8% to 100%—so the comparison establishes observed system rates, not an isolated causal allocation of every slowdown.

## Replay storage is a separate throughput constraint

The [rollout storage experiment](rollout-storage.json) measured three immutable layouts, including 36 bytes of action/bookkeeping fields per stored decision. Values below extrapolate observed corpus occupancy; they do not allocate 131,072 or one million rows.

| Layout | Bytes/decision | Estimated 131,072-row arrays | Estimated 1M-row arrays | CPU packing rows/s |
| --- | --- | --- | --- | --- |
| Dense FP32, float32 wire mask | 16,676 | 2.036 GiB | 15.531 GiB | 310,259 |
| Dense FP16, bool mask, exact card IDs | 8,356 | 1.020 GiB | 7.782 GiB | 26,331 |
| Sparse exact FP32 observations + ragged FP16 legal candidates | 1,160 | 0.142 GiB | 1.080 GiB | 59,436 |

All 8,192 stored rows reconstructed with exact legal masks, original selected-action slots and categorical card identities. All 2,048 distinct corpus states passed untrained policy comparisons. Dense FP16 changes an observation feature by as much as 0.030 in this corpus; its largest probability difference is 9.27e-6. The ragged layout preserves observations exactly and has a largest probability difference of 4.98e-6. Both preserve greedy choices on this corpus. These results do not guarantee invariance for a trained policy or unseen states.

Compression alone is not a fast learner pipeline. At minibatch 2,048, CPU gather/decode followed by captured GPU actor service achieved approximately 101k rows/s for dense FP32, 76k/s for dense FP16, and 151k/s for ragged storage. At batch 8,192 those rates were approximately 81k/75k/138k. CPU reconstruction therefore overwhelms the earlier resident GPU rates, even before backward/optimizer work. Dense FP16 is especially slow to pack in this implementation because CPU conversion and finite/range/underflow guards are included.

Prioritize uploading a rollout once and gathering repeated learner minibatches on the GPU; measure that path before choosing a final layout. Reconstructing every PPO minibatch on the CPU can starve the learner across repeated epochs. Compact ragged storage remains useful for archival data or host-memory pressure, subject to its measured packing cost. A practical design may use separate hot rollout storage and archival encoding. Memory estimates exclude Python/allocator overhead, retained source arrays, staging buffers and the model.

## Removing a hidden CPU gather copy

The initial dense gather implementation used NumPy's default `take(..., out=..., mode="raise")`. NumPy documents that this mode buffers the output even when an output array is supplied. The alternative is `mode="clip"` **after explicit validation rejects every negative, oversized, noninteger or wrongly shaped index**. Nothing is actually clipped in the supported path. [NumPy `take` reference](https://numpy.org/doc/stable/reference/generated/numpy.take.html).

The [paired comparison](take-mode-paired.json) ran both modes against the same immutable store, destination buffer and seeded cyclic index stream, alternating their order across three 0.2-second repeats. It finished in 6.94 seconds. All 8,192 source rows reconstructed exactly under both modes; invalid index checks reject before output mutation.

| Minibatch | Raise-mode mean batch latency, median repeat | Guarded clip latency | Median paired speedup |
| --- | --- | --- | --- |
| 256 | 0.615 ms | 0.402 ms | 1.406× |
| 2,048 | 18.731 ms | 7.854 ms | 2.379× |
| 8,192 | 88.118 ms | 29.621 ms | 3.023× |

Every paired repeat improves. Guarded clip reaches median gather rates of roughly 636k, 261k and 277k rows/s at those batches. The median paired speedup need not equal the ratio of the two independently calculated median rates. These data justify guarded clip for the CPU gather baseline while retaining its explicit bounds checks. They revise the magnitude of the earlier CPU gather bottleneck; repeated host reconstruction and transfer still belong in the comparison with GPU-resident rollout storage.
