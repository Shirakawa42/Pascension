# Zero-depth training stress and optimization follow-up — 2026-10-02

Preparation and validation are complete; actual outcome learning remains
unstarted. The current unused campaign is
`/home/lva/.local/share/shards-zero-depth/2026-10-02/prepared-stress-audit`.
Earlier preparations are preserved with their original pins. The current
[validation report](../../ZeroDepthTraining/VALIDATION.md) and
[guide](../../ZeroDepthTraining/README.md) identify the active campaign.

## Independent game and information checks

The new C# stress oracle runs real legal actions, without training or search.
It checks physical-instance and card-pool conservation, center/destiny and
permanent-collection tensors, remembered personal/center positions, identified
hand facts, and each remembered fact's encoded identity/range/uncertainty.
Separate hidden-state mutations verify that unrevealed order and opposing
hand allocation cannot change observations or legal candidates. Fresh buffers
are compared with reused buffers. The test oracles inspect authoritative hidden
lists solely for validation; those lists never repair or populate AI knowledge.

| Independent CPU corpus | Natural games | Decision boundaries | Buffer comparisons | Hidden permutations |
| --- | ---: | ---: | ---: | ---: |
| Seed 120000 | 24 | 8,523 | 510 | 140 |
| Seed 120100 | 512 | 200,881 | 12,047 | 3,263 |
| Seed 240200, strengthened tensor-row oracle | 1,024 | 387,233 | 23,251 | 6,281 |
| Total | 1,560 | 596,637 | 35,808 | 9,684 |

Every game finished naturally. The corpus spans all five heroes and normalized
DLC masks 0–7 and 15, includes 146 played definitions in the 512-game sweep,
and covers Scry/reorder, copy/replay, Legion, Fabricator, Longshot, shields,
splits, tutoring, recruitment, banish and monster effects. No new rules or
visibility defect was found. Sources and reports:
[oracle](../../ZeroDepthTraining/Host/StressSelfTest.cs),
[24 games](../../ZeroDepthTraining/results/visibility-stress-24-s120000.json),
[512 games](../../ZeroDepthTraining/results/visibility-stress-512-s120100.json),
[1,024 games](../../ZeroDepthTraining/results/visibility-stress-1024-s240200.json).

The existing full host selftest still passes its 14 focused reveal groups,
eight zone groups/4,140 oracle states, and original 1,166 states/four games.
The forced engine rebuild has zero warnings/errors; **278 engine tests pass**.
No rules, encoder or knowledge behavior changed during this hunting pass.
[Host results](../../ZeroDepthTraining/results/visibility-stress-host-selftest.json),
[engine test log](../../ZeroDepthTraining/results/visibility-stress-engine-tests.log).

## Frozen GPU and owned-experience checks

Four independently initialized, fixed policies with predetermined static
perturbations played 4,096 attempts: **4,094 natural finishes, two administrative
cutoffs, zero unresolved games**. They produced 1,934,614 decision rows and 850
checks of actual consumed GPU inputs and eager action likelihoods. All raw
float32 bits matched the host; measured likelihood/value errors were zero.
Neither weights nor static perturbations depend on game outcomes.
[Diversified report](../../ZeroDepthTraining/results/stress-audit-diversified-gpu.json).

Both cutoffs are still-living games at round 401, matching the explicit
`Round > 400` bound. Their exact action traces replay, with 127 natural finishes
and one cutoff per cohort. They are unknown outcomes and cannot become draws or
training targets. The diagnostic sweep explicitly enabled `--allow-censors`;
ordinary stress validation rejects a cutoff by default. The training safeguard
still stops at four censors in the conservative recent 4,096 attempts.
[First capture](../../ZeroDepthTraining/results/stress-audit-diversified-gpu-censor-s202610020.npz),
[second replay](../../ZeroDepthTraining/results/stress-audit-diversified-gpu-censor-s202613604-replay.json).

The actual configured initial policy and training seed namespace additionally
completed **1,024 games with no cutoffs**. Repeating those same games with the
13.51-GiB GPU-owned rollout retained **490,943 deciding-seat rows**, with a
largest cohort of 64,041 rows. Every action and reward digest matched the run
without rollout storage. Sealing verifies actual seat utility and excludes
censors, but no learner/optimizer exists in the stress process.
[Default policy](../../ZeroDepthTraining/results/stress-audit-default-policy-gpu.json),
[owned rollout](../../ZeroDepthTraining/results/stress-audit-owned-rollout-gpu.json).

These are validation workloads, sometimes concurrent with correctness audits;
their timings are not complete training throughput estimates. Repeated default
episodes are parity evidence, not additional independent strength samples.

## Concrete failure fixes

Six host regressions cover mapping/process startup resource leaks, a living peer
that stops reading commands, cleanup against a full pipe, inherited opponent
configuration, held-lane publication and exact seeded reset. Command writes use
nonblocking pipes with deadlines; startup/shutdown release owned resources.
An inherited `SHARDS_ZERO_OPPONENT` no longer changes an ordinary training host.
[Host regressions](../../ZeroDepthTraining/tests/test_host_lifecycle.py).

Seven recovery tests cover current collection failures before a heartbeat,
crashes with held terminal games, double discard after logging failure, durable
finalization before update failure, archive subset credit, a signal after one
explicitly fabricated-target optimizer step, and a fourth censor across resumes.
Uncommitted progress counts the entire cohort until durable finalization, then
clears immediately. Temporary mocked fixtures exercise persistence; they do not
change any prepared campaign.
[Recovery regressions](../../ZeroDepthTraining/tests/test_training_recovery.py).

Eight learner tests prove NaN/Infinity behavior rejection, final partial-batch
verification, shuffled row/target ownership, censor removal, deadline/finite
guards and ordinary-rate parameter/moment parity. The NaN acceptance and
CPU-to-CUDA pinned-source overwrite both failed before their fixes. An H2D
completion event protects optional CPU staging reuse.
[Learner regressions](../../ZeroDepthTraining/tests/test_learner_batches.py).

The full pipeline suite passes **75 tests** (64 under `tests/`, 11 learning
tests), with no skips. The separate shared persistence suite passes **27 tests**.

## Adopted optimizations and tuning

Packed learner prefixes now copy directly into owned rollout destinations.
Reordered rows gather into those destinations; float masks convert during the
copy. A full-size CUDA regression reduced peak temporary allocation from
12,583,936 bytes to below 65,536 bytes, while preserving float32 bits and
independent storage. Isolated contiguous movement measured 0.1752→0.0477 ms
per 128-row append. This is a movement-only cost, not overall training speed.
[Ownership/allocation regression](../../ZeroDepthTraining/tests/test_rollout_copies.py).

The learner uploads small generation-wide vectors once, keeps verifier maxima
on the GPU, and defers optional metrics reads while preserving all guards. A
controlled 60,000-row comparison restored identical weights, Adam state and
NumPy permutations: verification plus one fabricated-target PPO epoch measured
597.1→570.8 ms median, **4.4% lower**, with overlapping ranges. Lazy staging avoids
113,332,224 bytes of unused pinned memory in the normal CUDA-owned path. Fresh
minibatch tensors retain their ownership contract. The fixture came from an
older frozen host; its hashes and timing scope are explicit.
[Raw learner/append measurements](../../ZeroDepthTraining/results/cuda-followup/learner-batching-probe.json),
[fixture manifest](../../ZeroDepthTraining/results/cuda-followup/learner-batching-manifest.json),
[CUDA follow-up](zero-depth-cuda-followup-2026-10-02.md).

A fresh packed/graph collection sweep varied CPU workers and simultaneous games:

| Lanes | Workers | Natural games | Collection games/s |
| --- | ---: | ---: | ---: |
| 128 | 4 | 384 | 38.33 |
| 128 | 8 | 384 | 40.24 |
| 128 | 16 | 384 | 39.81 |
| 256 | 4 | 512 | 39.60 |
| 256 | 8 | 512 | 44.61 |
| 256 | 16 | 512 | 45.77 |

All 2,688 games finished naturally, with zero unresolved lanes. Action/reward
digests match across worker counts for each batch size. Different batch sizes
consume sampling RNG differently and are not identical trajectories. The small
256-lane advantage needs to be weighed against up to 124,604 cohort decisions
before archive filtering, close to the 131,072-row capacity. Defaults remain
128 lanes/eight workers. Startup, updates and final incumbent search are outside
these collection rates; this small sweep is not a multi-million-game forecast.
[Raw sweep](../../ZeroDepthTraining/results/stress-audit-worker-batch-sweep.json).

The existing lossless Triton CUDA transport and fused CUDA Adam remain enabled.
Compiled inference remains optional; broader strategic automation has no new
evidence and remains disabled. Direct CUDA rules execution would require a
separate rules port and equivalence suite; no speed claim is made for an
unimplemented port.

## Fresh pin and comparison pipeline

Current Host SHA-256:
`fb3fd1191865a8a7a328a183fe5b2643b4f23bd8b46205eae2e8ee94d3413aa1`.
Prepared source fingerprint:
`fdc31ec3e8424263a15c6e7b1bf08acb958d79fbec32d197dec7017cb69f3df8`.
Observation schema stays v2; its catalog matches the earlier visibility audit.
The frozen current incumbent policy is unchanged.

A separate temporary untrained checkpoint completed a seat-swapped held-out
pair against the actual deployed search AI. Both games finished, with no censors
or missing games; the untrained model lost both. The validation gate reports
`strength_evidence=false` and `stronger_than_current=false`. Its validation
ledger has zero charged seconds and zero sessions. The prepared campaign has
no checkpoint, budget, metrics, status or supervisor state.
[Comparison report](../../ZeroDepthTraining/results/stress-paired-validation.json),
[isolation record](../../ZeroDepthTraining/results/stress-paired-validation-isolation.json).

These checks identify no remaining concrete failure in the exercised paths;
they cannot prove that every future game or every history deduction is bug-free.
The observation's explicit historical-correlation/iterator limits remain in the
catalog. Actual strength requires later authorized learning and the predeclared
4,096-game comparison, which remains unstarted.
