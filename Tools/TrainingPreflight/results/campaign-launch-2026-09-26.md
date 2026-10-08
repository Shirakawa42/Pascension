# Live training campaign — 26 September 2026

The main run is active on this computer, supervised independently of the browser and this chat. Live status is at [localhost:8768](http://localhost:8768). This file records the launch decision; the dashboard and runtime files hold the current state.

## Selected setup

Width **128**, 256 concurrent games, 8 C# workers, shared-memory transport, adaptive 32/64/128/256 actor batches, FP32 storage and losses with TF32 matrix multiplication, captured PPO backward, 2048-row minibatches, up to 3 epochs. Complete per-seat terminal returns; 25% of games use a frozen archived opponent. All DLCs including Duel, two players, observation schema v2. The partial-observation omissions remain explicit in the [contract](../training-contract.md).

The main run resumed generation 65 from `pilot128-ready/latest.soicp`. It keeps the same model, Adam state, RNG, opponent archive and next engine seed. No previous deleted AI was loaded.

## Measured pilot comparison

Both selected-source pilots received 300-second grants from the shared ledger and reserved 30 seconds for stopping/checkpointing. They spent approximately 270 seconds each. Boundary collection discarded at the time limit received no learning target. Throughput below counts fresh retained decisions over collection, verification and updates; repeated optimizer passes are separate.

| Width | Generations | Completed games | Learning decisions | Decisions/s | Capped games |
| ---: | ---: | ---: | ---: | ---: | ---: |
| 128 | 65 | 16,640 | 6,647,204 | 24,998 | 0 |
| 256 | 60 | 15,360 | 6,180,298 | 23,179 | 0 |

The frozen, seat-swapped comparisons used 1024 games each on reserved selection seeds:

| Policy A | Policy B | A wins | A score | Conservative 95% score bound |
| --- | --- | ---: | ---: | --- |
| Width128 pilot | Its untrained initial policy | 1015 | 99.12% | 93.12–100% |
| Width256 pilot | Its untrained initial policy | 993 | 96.97% | 90.97–100% |
| Width256 pilot | Width128 pilot | 382 | 37.30% | 31.30–43.31% |

There were no draws or unresolved outcomes in these 3072 evaluation games. Width128 won 642/1024 head-to-head games (62.70%; bound 56.69–68.70%) and processed more fresh experience in the matched time. It is the selected measured setup. These results establish improvement over the untrained policies and this pilot comparison; they do not establish optimal play, the final meta, or final balance conclusions.

Raw [training summary](pilot-comparison-2026-09-26/training-summary.json), [128 vs initial](pilot-comparison-2026-09-26/pilot128-vs-initial.json), [256 vs initial](pilot-comparison-2026-09-26/pilot256-vs-initial.json), [head-to-head](pilot-comparison-2026-09-26/pilot256-vs-pilot128.json), and [selection identity](pilot-comparison-2026-09-26/selection.json) preserve the evidence.

## Budget and supervision

The shared maximum remains **43,200 seconds**. Earlier pilots, failure diagnosis and stopped sessions remain charged. At the main launch, **42,303.05 seconds** (11h45m03s) remained; the independent hard deadline is **2026-09-27T02:04:59+02:00**. Training starts graceful cleanup roughly 30 seconds before that boundary, so it may leave a small unused remainder.

Wrapper PID at launch: `20791`; supervisor: `20792`; trainer: `20793`. The supervisor enforces the deadline and heartbeat checks, saves known failures, and never automatically retries. Checkpoints are written atomically about every 60 seconds and at a completed session boundary. A process restart does not reset the shared ledger.

Runtime directory: `/home/lva/.local/share/shards-training/2026-09-26`. Main checkpoint: `main/latest.soicp`; metrics: `main/metrics.jsonl`; console: `main/console.log`; status: `main/status.json`; budget: `budget.json`; resource history: `resource.jsonl`. These are local files, not uploaded artifacts. Closing the dashboard or chat does not stop the detached supervisor.

After a successful main training exit, the wrapper automatically runs two frozen **4096-game** final audits (latest learner versus champion, then initial policy) on separate final-audit seeds. These audits perform no learning, consume no training-ledger time, and record legal candidate opportunities/selections for later analysis. Results and their status appear on the same dashboard.

## Cutoff diagnosis and data integrity

The original pilot stopped on an unrecorded administrative cap. Its exact cause remains unknown. A later capped learned episode was captured and [replayed exactly](learned-cap-replay.json): 3713 legal actions, health unchanged from round42 through round400, and a resource dead end after both policies removed currency and usable damage. The later episode demonstrates a real nonterminal trajectory, not a rules draw or a conclusion about card quality.

Revision2 removes every learner row from a capped episode before target construction and advantage normalization; it retains genuine completed games. It saves the full seed/action trace, reports excluded game/row rates and preserves counters in the ledger. Four capped games in the conservative trailing 4096-attempt window stop learning before further updates. This introduces completion bias; it is explicitly monitored. Deadline-discarded incomplete cohorts remain separately logged and are outside that finalized-cohort window.

Frozen evaluation retains unknown outcomes as score intervals, with no invented draws. The [real-engine mixed-cohort check](censor-lifecycle.json) verified 802 excluded rows and three retained genuine-outcome rows. [Readiness evidence](../README.md#readiness-evidence-and-recent-optimizations) covers 91 incrementally verified tests, plus host/replay checks. Multi-hour stability is still being observed live.
