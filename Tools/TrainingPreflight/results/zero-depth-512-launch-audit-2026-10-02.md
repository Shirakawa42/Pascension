# Width-512 twelve-hour launch audit

Audited the launcher, supervisor, campaign ledger, checkpoint loader and final
evaluation path. This audit did not launch outcome training or use the GPU.
Five new CPU-only lifecycle tests pass in 1.54 seconds.

The width-512 policy has **13,746,737 parameters**, confirmed by constructing it
on the CPU with the retained real observation/card schema. Parameters occupy
54,986,948 float32 bytes and Adam's two full-sized moments occupy 109,973,896
bytes. Capacity 131,072 owns 14,506,524,672 rollout bytes. Twelve archived policies
occupy about 660 MB on the CPU; policy, Adam moments and all twelve archives total
about 825 MB before serialization overhead, below the checkpoint loader's 4 GiB
limit. This is static sizing, not a replacement for the final CUDA smoke test.
The trainer checks rollout allocation against 70% of currently available device
memory after constructing its main policy.

The launcher reads the prepared configuration and compares the complete current
source, host, catalog and configuration identity before creating training. A
width-256 prepared campaign or checkpoint cannot be reused as a width-512
campaign. Final preparation must occur after every source and host edit. Final
evaluation reconstructs `PolicyConfig` from the trained checkpoint, preserving
width 512, and verifies the pinned source/host/catalog and frozen incumbent.
The prepared 2,048 seat-swapped pairs are **4,096 games** against the existing
deployed AI with its tactical search enabled.

Both launcher and trainer reject grants outside 1–43,200 seconds, including
NaN and infinity. The ledger refuses to change a campaign's original allocation,
does not refund charged sessions, clips resumed session grants to the remaining
allocation, and exclusively locks campaign ownership. Resume therefore uses the
same `--seconds 43200` argument plus `--resume`; it does not grant a second twelve
hours. The supervisor reads the active trainer's hard deadline, requests graceful
shutdown before it, checks heartbeats, and never automatically retries failure.

For persistence after the calling session, use a direct Python argument list,
absolute campaign path, `stdin=DEVNULL`, file-backed combined stdout/stderr,
`start_new_session=True`, `close_fds=True`, and inherited environment plus
`PYTHONUNBUFFERED=1`. This gives the launch process its own POSIX session and
avoids dependence on the caller's terminal or output pipe. Keep its PID and log
path. [Python Popen options](https://docs.python.org/3.12/library/subprocess.html#subprocess.Popen).

The supervisor starts the trainer in a separate owned process group. It first
sends SIGTERM to that trainer alone, allowing the C# host to remain alive during
checkpoint shutdown, then kills only that owned group if necessary. An actual
CPU process test spawned an owned descendant and an unrelated process: the hard
deadline killed the owned trainer/descendant, while the unrelated process stayed
alive. No process-name or GPU-wide cleanup is used.

Found and fixed one demonstrated lifecycle bug: supervision installed SIGTERM
and SIGINT handlers but left them installed after returning. Those stale handlers
could swallow stop requests during automatic evaluation. `launch.py` now restores
the preceding handlers in `finally`, including when supervision raises. Two
tests failed before correction and passed afterward.
[Python signal handlers](https://docs.python.org/3.12/library/signal.html#signal.signal).

Monitoring must distinguish the trainer PID from the launch PID. After the trainer
finishes successfully, the launch process continues running the final evaluation;
`post-training-evaluation-*.plan.json` and its matching result report carry that
progress. A requested training stop skips automatic evaluation. Reports never
claim stronger performance merely because training or a partial evaluation ended.
Stop only the recorded owned process, and leave unrelated Unity/CUDA applications
alone.

Validated by [five lifecycle regressions](../../ZeroDepthTraining/tests/test_launch_lifecycle.py):
signal restoration before evaluation and after exceptions, width-512 resume and
4,096-game plan preservation, duration validation, and actual owned-group-only
deadline cleanup. No real campaign, learned checkpoint or GPU workload was
created by these tests.

Final audit hashes:

- `launch.py`: `a75b7c1039d734a3ef2b86965100893c200d3929b6f13da3ecb1998bd054bed6`
- `test_launch_lifecycle.py`: `c546a4fa7a37bd53f4ab8a7d13e23ca853b0fbaeba9ac076ed7eeb07f5212188`

## Independent statistics and GPU-smoke review

The rolling statistics integration adds only finalized cohorts after PPO returns
and the finite policy/Adam guard passes, while terminal host buffers are still
available and before resetting them. Its compact checkpoint records are owned
immutable bytes; resume rebuilds totals from those records before RNG restoration.
Unknown observation schemas produce outcome-only statistics, and administrative
censors stay separate from natural games and draws. The hero order and public
scalar/collection offsets were checked against the actual host encoder. Eight
statistics tests independently passed, including actual two-lane terminal
extraction, wrapped-ring continuation, and safe loading of empty/nonempty state.
The module owner fixed the identified unavailable-feature lookup issue before
this final pass.

A CPU-only probe filled the complete 100,000-game ring and verified identical
summaries after restoration. It used retained compatible public rows and explicitly
fabricated finalized outcomes, without invoking a learner, trainer or GPU. The
ring owns **8.2 MB**. Insertion and eviction for a 128-game cohort took a **5.64 ms
median**; rendering a snapshot took 0.049 ms, snapshot plus JSON 0.159 ms, and
copying checkpoint bytes 0.378 ms. Rebuilding the full ring on resume took 536 ms
once. These measurements use the recorded module revision, before the owner's
small unavailable-feature/empty-ring guards; they establish the bounded work
and approximate overhead, not a full training-rate claim.
[Raw CPU scopes and source hash](../../ZeroDepthTraining/results/launch-rolling-stats-cpu-validation.json).

The [width-512 CUDA validation](../../ZeroDepthTraining/results/launch-512-frozen-gpu-validation.json)
uses the actual 24,576 / 64-by-48 input shape, packed adaptive CUDA actors, and a
128-row disposable optimizer step. Its report explicitly marks fabricated
utilities, an unchanged frozen original, finite parameters/moments, zero behavior
error, and no training or strength evidence. The code generates alternating
zero-sum utilities for unfinished inputs and discards the copied model's update.
The subsequent frozen collection resolved 384 games with zero censors.

Reported peak allocated CUDA memory is **851,240,448 bytes**, with 973,078,528
reserved. That includes the actor/small disposable smoke scope; it **excludes
the production 131,072-row rollout and 1,024-row learner minibatch**. It must not
be described as the full campaign's GPU peak. The separate 14.507 GB rollout
estimate, live memory allocation guard and final running process observation
provide the production allocation checks.
