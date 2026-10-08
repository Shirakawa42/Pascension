# Eight-core Host profiling

The existing Host already uses all eight workers while enough lanes are active.
The low average live process CPU percentage is not evidence of a one-thread Host.
Its synchronous publications wait for the GPU/Python actor, and a complete cohort
has a long tail of nearly finished games. No production C# source or Host binary
was changed during these experiments.

The CUDA profiling agent captured one complete frozen learned width-512/archive
cohort: 128 natural completions, no administrative censors, engine seed
2305843009214764416, singleton automation and 1,396 exact wrapper action vectors.
The original learned checkpoint SHA256 was
`1b6987e08fc68de9a0a49e69a2551ef7fac7bc41c12dc20801be8030e99316e7`;
the original Host SHA256 was
`cf7c1a4084bf58acb1e09475199f1205b313456b7f92afca49bc64d1d67ac814`.
These CPU experiments replay that matrix without a policy, optimizer or GPU.

The mean active count is 45.23 of 128 lanes; the median is 12.5, and the minimum
is one. The baseline uses eight distinct threads doing actual active-lane work
on 724/1,396 publications in one repeat. Approximately 440 publications have
one active lane and naturally use one productive thread. An earlier historical
trace with at least 20 active lanes used eight threads on 672/675 publications.

Instrumentation separates engine/wrapper advancement from encoding. In two
learned-trace baseline repeats, summed per-lane CPU-phase times were 2.16–2.18
seconds advancing and 6.68–6.85 seconds encoding. Histogram encoding accounted
for 2.81–2.87 seconds of that encoding time. These summed elapsed intervals are
descriptive phase measurements, not process CPU accounting; scheduling and GC
can influence them. [Phase and thread evidence](../../ZeroDepthTraining/results/eight-core-host-learned-profile-2026-10-02.json).

The isolated source/binary live under `/tmp/shards-host-perf-20261002`. One
executable selects its experiment using `SHARDS_PERF_MODE`: the untouched
`Parallel.For` scheduling baseline, a threadpool minimum override, persistent
workers, active-index dispatch, and a sparse histogram implementation. Persistent
work uses the main thread and seven parked background workers. Held lanes remain
unchanged; active indices merely avoid scheduling their no-op callbacks. Sparse
histograms retain integer accumulation and the original division by ten, while
writing only populated bins into the already cleared histogram area.

| Variant | Host service repeat 1 | Host service repeat 2 |
| --- | ---: | ---: |
| Original scheduler and histogram | 2.0211 s | 1.9724 s |
| Original scheduler, sparse histogram | 1.9544 s | 1.9181 s |
| Persistent workers, active indices, sparse histogram | 1.7523 s | 1.6186 s |

The sparse change alone reduced service by approximately 3% in these pairs. The
combined experiment reduced service by approximately 13–18%. All six replays
produced the identical SHA256 over every complete mapped publication:
`2d88f12232f2a5bb125f6d58c09f6818bc41a13c39361c503a0702144591bf60`.
Each also produced identical final rule hashes, outcomes, submission counts,
actors and rewards, with all 128 games naturally complete. Hashing sits outside
the reported Host service and changes the spacing between requests.
[Timing and exact-parity evidence](../../ZeroDepthTraining/results/eight-core-host-learned-parity-2026-10-02.json).

The active campaign ran concurrently with these CPU probes. These are matched
action replay results, not end-to-end training speed measurements or a guarantee
that every physical core will remain busy. A production decision requires the
combined frozen-actor measurement and coordinated campaign stop, source pinning
and checkpoint migration owned by the root agent. No card rule, strategic action
filter, public information field or policy parameter was changed here.

## Production port and final checks

The root agent stopped the campaign cleanly at its verified final checkpoint
before releasing the source freeze. The approved production delta contains only
[Program.cs](../../ZeroDepthTraining/Host/Program.cs),
[Encoder.cs](../../ZeroDepthTraining/Host/Encoder.cs) and the new
[LaneWorkers.cs](../../ZeroDepthTraining/Host/LaneWorkers.cs).
Program reuses the worker team, dispatches only active advance lanes, and retains
all-lane reset/observe encoding. Histogram accumulation and division stay exact.
Eight configured workers use the control thread and seven persistent background
workers. One- and two-lane tails run directly; every completed callback is joined
before the response is published. Exceptions propagate to the caller.
Experimental phase instrumentation and environment switches were not ported.

The final production Host replayed all 1,396 learned-policy action vectors and
matched the complete publication digest above. All 128 games completed naturally;
terminal rewards and gameplay/event counters matched exactly. All-held live and
terminal requests left every mapped byte unchanged. Observe produced the same
tensors. Reset at worker counts 8, 1, 16 and 8 reproduced the same seeded initial
bytes, and the process exited cleanly.
[Production replay evidence](../../ZeroDepthTraining/results/eight-core-production-host-parity-2026-10-02.json).

The production worker lifecycle checks cover 800 batches at counts 0–513 and
worker counts 1/2/8/16, three propagated callback failures followed by valid work,
idempotent disposal, rejection of work after disposal and a fresh replacement
team. The integrated Host selftest also passes its existing 1,166-state, 14-reveal,
8-zone and 3,108-card-effect checks.
[Worker checks](../../ZeroDepthTraining/results/eight-core-host-worker-selftest-2026-10-02.json),
[integrated checks](../../ZeroDepthTraining/results/eight-core-host-selftest-2026-10-02.json).

Run the focused scheduler checks with `ZeroDepthHost.dll lane-workers-selftest`;
`ZeroDepthHost.dll selftest` runs them before the integrated suite. The final
Release rebuild had zero warnings/errors and produced Host SHA256
`3fca3bf2006bb31534894c4a558afc60c6780f30ac096c341b4a69545fb53c63`.
No further C# edits or builds are pending. Campaign migration, final CUDA
validation and resume remain the root agent's work.
