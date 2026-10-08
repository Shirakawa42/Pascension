# GPU microbenchmark contract

This is a fresh systems preflight, not a trained agent or a strength evaluation. It uses synthetic inputs by default and can instead validate a recorded real-observation corpus. It does not load previous AI code or checkpoints. `gpu_bench.py` isolates each case in a subprocess and kills its process group on timeout, including compiler children. Default limits are 36 cases and 600 seconds total; a case has a 180-second cold-start timeout. Compilation time counts toward those bounds.

## Inputs and models

Adapter v1 supplies observations `[B,2048]`, candidate descriptors `[B,A,32]`, and a boolean legal mask `[B,A]`; the normal action bucket is 64. Candidate slot 16 is stable card ID divided by 192. This field is categorical in the default `embedded` model: a learned 193-entry embedding is added to a shared nonlinear candidate projection. State is encoded once. The `encoded` ablation omits that embedding; `bilinear` scores original candidate features directly and is a deliberately limited throughput floor. In particular, a scalar ID cannot represent arbitrary card identity preferences.

Every row must contain at least one legal action. `validate_legal_mask` rejects all-invalid rows before GPU dispatch; omit terminal/padded requests. Never silently truncate a real menu to fit a bucket. The model itself omits CPU synchronization from this hot-path precondition.

`sample_actions` returns one contiguous FP32 packet `[action index, selected log-probability, value]` per row. It uses categorical Gumbel-max sampling. The caller must consume or copy graph-owned output before replaying that graph. Autocast applies to dense operations; logits, log-softmax, entropy, PPO ratios and reductions remain FP32.

## What is measured

- Actor **resident**: forward, legal masking, stochastic sampling, log-probability and value output, with inputs already on device.
- Actor **host roundtrip**: copies from reusable pinned host inputs to stable device addresses, the resident work, and a batched device-to-pinned-host output copy. Every iteration waits for completion before CPU reuse. It includes Python dispatch and synchronization.
- Learner **resident**: PPO-shaped loss on fixed observations, fabricated advantages/returns, backward, gradient norm clipping and fused AdamW. Repeated toy updates are numerical/load tests, including when their observations came from real games. This is not a reinforcement-learning run. Minibatch uploads and actor/learner scheduling belong in the integrated benchmark.

These are synchronous request/response measurements. The resident metric is not pure kernel time: it includes launch and Python overhead, intentionally exposing the costs graphs/compilation can remove. Each repeat runs for `--seconds`, and actor cases have two measured phases. Reports include repeat throughput, per-call median/p95/p99 latency, compile/capture/warmup time, process allocator peak, and a CUDA free/total-derived memory-use estimate before the case. That estimate is not authoritative system-wide VRAM accounting under WSL; retain separate NVML telemetry. Other applications are neither terminated nor counted as this process's allocated memory.

The harness uses eager FP32, TF32 permitted for FP32 matrix multiplies, or BF16 autocast. It compares eager, `torch.compile(mode="reduce-overhead", fullgraph=True)`, and explicit CUDA graph replay. Compile mode optimizes the forward/loss and its generated backward; the fused optimizer and Python learner wrapper remain outside that compilation. Manual learner graphs capture the entire forward/backward/clip/optimizer update.

## Correctness gates

Each case compares eager logits, probabilities, values and PPO gradients with strict FP32. It also checks selected-action legality and finite values. Execution-mode parity compares actor log-probability/value with eager, and compares one learner update with an eager reference that has independently cloned model and Adam buffers. A separate uniform categorical fixture verifies that graph replays advance RNG; an actual small policy may legitimately repeat its draw. Terminal all-invalid rejection is explicitly tested. These gates detect implementation/numerical failures, not strategic effects of lower precision.

Results contain SHA-256 fingerprints of both Python source files, runtime versions, full configuration and command line. Synthetic random data is deterministic for a given seed across execution/precision cases. Timing data remains sensitive to external GPU/CPU load and clock state; run comparison groups without overlapping other preflight work and repeat promising cases for longer.

## Recorded real observations

Pass `--fixture path/to/corpus.npz` to replace Gaussian inputs with adapter-v1 observations. Required arrays are `obs[N,2048]` and `candidates[N,64,32]`, both exactly float32, plus a binary `mask[N,64]`. An optional integer `actions[N]` contains actual recorded legal choices. It is used by the precision-gradient check and learner workload; without it, the harness selects a legal argmax from the recorded menu. Actor tests still draw fresh actions and validate them against that menu.

The loader checks the entire corpus's shapes, finite numeric features, binary masks, nonempty legal rows, and categorical card IDs in candidate slot 16. It also rejects out-of-bounds or illegal recorded actions. NPZ arrays are loaded with pickle disabled. Selected row `i` is `i % N`, starting at zero, so every precision/execution variant sees the same batch; batches larger than the corpus intentionally repeat rows and report the unique-row count. Use batches that cover the corpus when broad numerical coverage is needed. These are fixed-batch measurements, not a streaming corpus benchmark.

Each subprocess hashes and parses the same file bytes, verifies the hash against the parent manifest, and records the full filename and SHA-256. Reports label these inputs `recorded-observation-corpus-v1-not-strength-evidence`, record menu-size/card-ID coverage, and distinguish whether real actions were supplied. The precision check covers **all selected rows** for a recorded fixture; the synthetic smoke check retains its 64-row bound. Host inputs remain in reusable pinned storage on CUDA.

Real observations improve distribution coverage, but their collection policy and finite sample size still limit rare-state coverage. File provenance and information visibility are the collector/adapter's responsibility; this loader cannot infer that a supplied feature is authorized just because it is numeric. Learner targets remain fabricated even when recorded actions are supplied.

```bash
/home/lva/.venvs/shards-preflight/bin/python Tools/TrainingPreflight/gpu_bench.py \
  --fixture /tmp/shards-real-observations.npz \
  --kinds actor,learner --widths 256 --batches 1024 \
  --precisions fp32,tf32,bf16 --modes eager,graph \
  --seconds .1 --repeats 1 --max-cases 12 --max-total-seconds 180 \
  --output /tmp/shards-real-numerical-parity.json
```

## Example bounded sweeps

Use the isolated environment prepared by the coordinator:

```bash
PYTHON=/home/lva/.venvs/shards-preflight/bin/python

# 108 cases, maximum 15 minutes; includes width/batch/precision/manual-graph grid.
$PYTHON Tools/TrainingPreflight/gpu_bench.py \
  --kinds actor,learner --widths 128,256,512 --batches 32,256,1024 \
  --scorers embedded --precisions fp32,tf32,bf16 --modes eager,graph \
  --seconds .2 --repeats 3 --max-cases 108 --max-total-seconds 900 \
  --output /tmp/shards-gpu-grid.json

# Compile only the promising shapes; cold-start cost is recorded separately.
$PYTHON Tools/TrainingPreflight/gpu_bench.py \
  --kinds actor,learner --widths 256 --batches 256,1024 \
  --precisions fp32,bf16 --modes compile --seconds .5 --repeats 3 \
  --max-cases 8 --max-total-seconds 600 --output /tmp/shards-gpu-compile.json

# CPU crossover uses one CPU thread; compare with the same GPU model/schema.
$PYTHON Tools/TrainingPreflight/gpu_bench.py \
  --device cpu --kinds actor --widths 128,256 --batches 1,8,32,128 \
  --precisions fp32 --modes eager --cpu-threads 1 --max-cases 8 \
  --seconds .3 --repeats 3 --output /tmp/shards-cpu-policy.json
```

Sweep order is kind, width, batch, scorer, precision, mode. `--max-cases` truncates that order, so explicitly select groups or raise the limit when a complete grid is needed. Batch size is conservatively capped at 2048 while other GPU applications occupy memory. This is a preflight guard, not a model limitation.

## Primary implementation references

PyTorch documents graph lifetime, side-stream warmup, stable addresses, replay RNG handling and captured training in its [CUDA semantics](https://docs.pytorch.org/docs/2.14/notes/cuda.html). The same reference describes the current per-backend TF32 precision API; this harness does not mix it with deprecated `allow_tf32` settings.

The [torch.compile reference](https://docs.pytorch.org/docs/2.14/generated/torch.compile.html) describes reduce-overhead mode and full-graph behavior. [AMP documentation](https://docs.pytorch.org/docs/2.14/amp.html) explains autocast operation selection. [AdamW documentation](https://docs.pytorch.org/docs/2.14/generated/torch.optim.AdamW.html) documents fused and capturable optimizer variants. These references explain implementation choices; the measured local results determine which choices are retained.
