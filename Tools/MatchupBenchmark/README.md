# Fixed hero benchmark against the in-game AI

This isolated host evaluates one frozen zero-depth learner against the packaged
`PolicyEngine`, preserving its tactical search settings. The learner plays
Decima, Tetra, Volos and Ko Syn Wu. Each faces every different opponent hero,
including Rez: 16 matchups, 150 independent seeds per matchup, both learner
seats per seed, 4,800 games total. Initial heroes are selected through actual
legal draft submissions; no gameplay decisions or rewards are changed.

`Host` is a pinned copy of the training host. Its only functional change is
`FixedMatchups.Apply` before the opponent's first turn. Production engine,
content, observation encoding and opponent inference sources remain identical.
A fresh assembly renumbers metadata tokens inside IL coverage hashes; the
catalog verifier requires identical covered method names and every other
catalog field, including all numeric card features. Both binary provenance
and the source difference are retained in the benchmark plan.

`start.py` arms an independent `guardian.py` before gracefully checkpointing
training. It holds the exact existing watchdog, freezes the learner and current
packaged opponent, and starts six benchmark workers. `run.py` also limits its
process tree to six distinct physical cores (one logical thread each). Automatic reviews are paused during this
exclusive workload. The guardian resumes training only if the original fixed
deadline permits it and no durable training stop exists. Benchmark duration
never extends the training authorization.

`server.py` serves the live page on localhost:8767. Every completed game is
journaled; atomic JSON reports supply the matchup matrix, seat outcomes,
throughput, ETA and active rounds. Capped games are unknown outcomes. Intervals
use seed pairs and simultaneous correction across 16 rows. No interim or final
result automatically deploys a policy. This filtered benchmark does not measure
the learner playing Rez.

Current artifacts:
`/home/lva/.local/share/shards-zero-depth/2026-10-06/current-ai-matchups`.
`STOP` in that directory requests benchmark cancellation; training resumes only
under the original authorization. A training watchdog/campaign `STOP` also
cancels the benchmark and prevents the guardian from resuming training.

Validation: `fixed-matchups-selftest` checks 32 actual hero drafts and the entire
4,800-game assignment. `test_benchmark.py` checks seat-relative scoring,
censors, duplicate rejection, complete coverage, and semantic catalog changes.
Actual learner observations are checked against the planned heroes throughout
every live game.

Performance upgrade: `../MatchupOptimizedHost` builds a drop-in host with exact
sparse inference and one-lane worker scheduling. `../MatchupPerfHost` verifies
bitwise probability/value parity and full search action/state/branch parity.
The live October 6 run switched binaries only between complete 32-game cohorts;
`performance.json` records both hashes, the activation boundary and validation
files. Retain that sidecar with `plan.json` and results: the original immutable
plan records the initial binary. Existing games are never restarted.
The dashboard reports a recent five-minute rate after at least 120 seconds and
32 completions, excluding time before the optimized host activated.
