# Generic effects migration and supervised overnight continuation

User authorization: pause, add generic card effects, demonstrate approximately 50% against the prior representation, then continue through **2026-09-27 11:00 Europe/Paris (09:00 UTC)** with checks every 20 minutes. Original 12-hour ledger history remains intact; the explicit extension receipt caps this continuation at the absolute deadline.

## Representation and preservation

V10 appends public information (2816→3328 observation values) and adds shared 512-dimensional effect descriptors for all 189 catalog cards. Effects feed candidate representations, public card zones, the public opponent collection and the exact remaining own-hero relic pool. Numeric primitive quantities are read from actual effects. Custom callbacks have reviewed recipes and implementation manifests: unknown callback changes fail closed instead of silently pretending to understand changed rules.

This is a **hybrid warm start**: existing learned ID embeddings remain as residuals. The semantic branch initially contributes zero, and every existing policy, optimizer moment, RNG state and continuation counter is preserved. Thus learned strength survives the migration; generic effects begin learning during continuation. It is not an identity-free policy, lossless effect grammar, search engine, or guarantee of immediate strength after a rebalance. Phase summaries lose exact ordering and branch binding. Rebalanced definitions should be re-exported, validated, and evaluated, with fine-tuning as necessary. Changed callback code requires descriptor review before re-export.

All 19 saved policies migrated. Only duplicate internal evaluation scheduling was disabled in favor of exclusive external evaluation; game balance was not tuned. One separately reproduced engine bug was corrected in the V10 host: an explicitly banished fast-played Reactor now banishes before temporary-card return. Both sides of the representation comparison use that corrected engine. Canonical and frozen older engines remain untouched.

## Evidence before learning

- 12 descriptor tests, 2,422 public-information checks, 82 Reactor regression checks pass.
- Earlier prefix-equivalence audit: 12,342 reached states across 32 completed games. This predates the intentional Reactor correction; it establishes encoder compatibility for the same underlying state, not identical trajectories across different engine rules.
- CPU policy equivalence checked on 4,096 real decision rows; GPU log-probability error at most 0.00001526 on sampled real rows.
- **4,096 seat-swapped games: 2,039 wins / 2,057 losses, score 49.7803%, paired conservative 95% bound 46.7793–52.7813%, zero censored games.** No optimizer updates or training-budget charge. This supports preserved strength, not improvement.
- 72 GPU fused forward/backward cases pass against Torch reference, maximum gradient error 0.00000477.
- Frozen 256-game actor/collector smoke passes, all 20 forced hero pairings observed, no censors, behavior log-probability error 0.00003946, hidden/setup rows excluded, model and ledger unchanged.
- Actor-only captured forward cost is approximately 1.23–1.36× V9 depending on batch. The first actual learning pilot exposed a much larger backward bottleneck; actor-only timing is not an adoption result.

## Automated control

`experiments/overnight_controller.py` schedules bounded training/evaluation slots targeting 20-minute start-to-start cadence. CUDA evaluation runs only while training is paused. Each cycle compares 4,096 fresh, paired-seed games against the previous learner and, when different, the retained best learner. A summable error budget across repeated comparisons avoids promoting random positive noise. Inconclusive matches remain inconclusive. Checkpoints are written every 60 seconds and immutable comparison endpoints are retained every cycle.

Live audits run every 10 seconds against available publications; final audits require clean checkpoint completion, finite weights/Adam, behavior parity, generation continuity, reconciled episode/row counts, and reconciled hero/card/matchup/round statistics. Acquisition coverage and observed hero scores are descriptive, not causal balance estimates. Statistics are published in batches by the host, not per-action dashboard work. The controller stops on concrete failures or confirmed regressions and records actionable evidence. It does **not** edit arbitrary game code or silently retry after a failure.

The absolute deadline is enforced by both budget sessions and the external process controller; frozen evaluation has an independent OS timeout. Shutdown can be several minutes early if another validated slot will not fit. UI: http://localhost:8768/ and http://localhost:8768/statistics.

## Performance correction and pilot evidence

V10's first learning pilot completed 18 generations / 4,608 games, with zero censored games and exact statistics reconciliation. Its 4,096-game comparison scored 49.9756% against its migrated start (inconclusive). All three semantic parameter groups changed with finite values, proving they receive training updates, not stronger play. This pilot failed the planned throughput/minimum-generation adoption gates and was not used as the overnight execution setup.

Profiling separated collection (~3.05s) from learning (~5.50s), versus V9 learning (~0.34s). Repeated advanced-index lookups through the shared semantic table caused expensive gradient accumulation. V11 preserves all V10 state and replaces those differentiable lookups with the equivalent embedding operation. A captured forward/backward benchmark at batch2048 fell from **38.676ms to 1.480ms (26.13×)**, with matching outputs and maximum gradient difference **0.000000954**. No optimizer updates occurred in that benchmark.

The V10→V11 migration preserved all policy tensors, optimizer moments, RNG, budget and continuation counters exactly. The original V10 source and published checkpoints remain frozen. V11 still uses the same V10 descriptor catalog, observation and corrected engine. Early live V11 measurements are ~3.31s per generation, ~0.39s learning; final pilot and overnight controller artifacts are authoritative.

## Runtime outcome

V11 completed 44 generations / 11,264 games, with zero censors, finite parameters/Adam, and exact host/training statistics reconciliation. The forced-random cohort covered all 20 hero pairings, 15 relics and 30 destinies. Nevertheless, **the strength gate failed: 42.9199% over 4,096 games against the starting learner**, with sequential bound39.6491–46.1907%. The controller correctly stopped. Good throughput, healthy PPO metrics and broad action coverage did not establish strength.

V12 is a documented conservative recovery: restore the validated generation9344 policy and its Adam state, keep the generation9388 campaign counters/RNG/seed history and all spent budget, and reduce learning rate0.0003→0.0001. All recovery changes are explicitly enumerated and every unrelated payload field is digest-checked unchanged. This is a tested hypothesis about update stability, not a proven diagnosis of the regression's strategic cause. A further bounded pilot/evaluation gate precedes the overnight launch.


## Monitoring correction

The cohort cache had a separate real bug: automatic metadata selection only recognized V6/V7, so current V9+ statistics could be silently displaced by old V7 observations. The cache now selects the newest supported runtime population using exact rules/catalog/observation identity, with explicit V9 and V10 presentation catalogs. V10–V12 share the same card definitions and corrected engine, so their changing-policy cohort observations can be pooled; older incompatible populations are excluded. A new regression test proves that V12 observations replace V7 without mixing the populations. All18 pool-statistics tests pass. The live random-hero view now names `pilot-v12/segment-0001` and shows current observations.


## Adopted overnight continuation

The V12 pilot completed **42 generations / 10,752 games**, zero censors, and clean automatic audits. Its frozen4,096-game comparison scored **52.4414%** against the restored baseline; the repeated-test bound49.1706–55.7122% is **inconclusive**, not proof of improvement. Average post-warmup generation time was3.47s. Smaller update steps passed this bounded test but do not conclusively explain the strategic cause of the earlier regression.

The overnight V12 controller was launched at01:33 Paris with learning rate0.0001, 20-minute slots, fresh0xB000… evaluation seeds and the11am deadline. It resumes the latest tested learner but retains the earlier validated baseline separately; an inconclusive positive pilot does not promote the best model. State and results live at `/home/lva/.local/share/shards-training/2026-09-26/overnight-v12/overnight-controller.json`. The controller has22 passing tests; the live pilot exercised a full training/audit/evaluation cycle, including the previously reproduced automatic regression halt.

Audits investigate actual structural errors and statistically confirmed regressions. Unexpected descriptive statistics remain labeled observations; no system can certify from these checks that every strategic choice is optimal or every possible game bug is absent. On a hard failure the controller pauses for investigation and preserves evidence, rather than making unreviewed automatic code changes.
