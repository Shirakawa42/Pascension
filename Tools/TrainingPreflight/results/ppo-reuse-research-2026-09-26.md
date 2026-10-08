# PPO reuse decision — 2026-09-26

**A predeclared 3-versus-6 epoch continuation experiment is worthwhile; low mean KL alone does not justify adopting 6.** Six offers a clearer test than 4 for a modest estimated compute cost. Test one challenger and keep 3 if inconclusive. This research performed no updates, GPU work or production edits.

## Current evidence and cost

At 14:24:49 UTC, completed `pilot-v3/metrics.jsonl` contained 110 generations, 1978–2087; SHA256 `886758faf9740ca76a6133352a664d6682b0e4dfb72aeba3d8af35ab57b1568e`. Latest 20 averages: 2.371s/generation, 0.263s learning, 79,619 retained rows, 3.037 passes/row, KL 0.00249 and clip fraction 2.428%. One minibatch was rejected across the pilot, none in the latest 20. Maximum behavior-log-probability discrepancy was 0.0000634. CPU shadow contention means this is not a clean epoch benchmark. Raw evidence: `/home/lva/.local/share/shards-training/2026-09-26/pilot-v3/metrics.jsonl`.

Holding game lengths, collection cost and per-pass learner cost fixed gives `T(E)=2.371+0.263×(E/3−1)`:

| Epochs | Estimated seconds/generation | Fresh rows/second relative to3 | Optimizer passes/second relative to3 |
|---|---:|---:|---:|
|3|2.371|1.000|1.000|
|4|2.458|0.964|1.286|
|6|2.634|0.900|1.800|

These are arithmetic projections, not measured speed or strength gains. Checkpoint/evaluation overhead, early rejection and changed gameplay can alter them.

The PPO paper supports multiple minibatch epochs but used 3 for Atari, 10 for MuJoCo and 15 for Roboschool. Those experiments do not establish this game's optimum. [Original paper, algorithm 1 and appendix A](https://arxiv.org/pdf/1707.06347).

The learner rejects approximate KL above 0.03 or absolute log-ratio above 10 **before Adam**, then stops remaining passes. Preserve these guards, clipping, entropy and finite-state checks. [Learner](../learning_model.py), [epoch loop](../train_campaign.py). Official SB3 also combines repeated epochs with a pre-update approximate-KL stop; its default 10 epochs is not transferable evidence. [SB3 source](https://stable-baselines3.readthedocs.io/en/master/_modules/stable_baselines3/ppo/ppo.html).

Three cautions make an experiment necessary:

- Logged KL averages **pre-update minibatches across all epochs**. It is neither final-policy KL nor a safety margin that permits twelve times more updates. A later single update can overshoot; clipping alone is not a hard trust-region constraint. [Generation aggregation](../train_campaign.py), [Spinning Up explanation](https://spinningup.openai.com/en/latest/algorithms/ppo.html).
- Roughly 80,000 rows come from only 256 games, with repeated terminal labels. More passes can fit correlated Monte Carlo noise and the shared critic; advantages remain fixed. Lower training loss does not prove stronger play. [Targets](../learning_rollout.py), [objective](../learning_model.py).
- Six epochs refreshes the self-play opponent distribution less often per hour and may favor responses to the previous policy. Its benefit here is unproven. [League schedule](../train_campaign.py).

## Proposed experiment and decision rule

1. Reuse the completed three-epoch pilot. Start the six-epoch challenger from the **same migrated generation 1977 checkpoint**, preserving weights, Adam, RNG, league, counters and guards. Change only `TrainConfig.epochs` through an audited configuration-only fork with a parent hash; checkpoint identity otherwise rejects the change. [Restore contract](../learning_model.py), [identity](../experiments/variant_runtime.py).
2. Use **300-second grants per arm**, including identical stop buffers: approximately 270 charged seconds each, about 9 minutes total and **270 additional seconds** beyond the completed baseline. Both count inside the 43,200-second campaign. Baseline-first order is fixed. Its CPU shadow interval near elapsed 124–152s coincided with 32,398→26,714 rows/s, then 33,278 afterward. Attributing the full 17.5% decline over 30s to contention implies approximately 5.3 baseline-equivalent seconds, or 1.9% of 270; it does not measure lost learning directly. Repeat a comparable **unthrottled** 256-game CPU evaluation near challenger elapsed 124s: latest challenger versus fixed1977, batch32, cooperative limit150s, engine seed `0x6000000000002000`, sampling97114. Test the 3ms/reply throttle separately during main training. Record actual overlap durations and throughput; differing frozen policies, seeds and timing prevent perfect load replication. Common initial RNG/seeds likewise cannot keep training trajectories identical after the policies and update RNG consumption diverge.
3. Compare frozen endpoints at matched charged time. Record fresh rows and accepted passes per charged second, rejections, caps, and per-epoch/final-rollout KL if the experiment harness exposes them. Correctness failures disqualify a branch; ordinary guarded early stops simply reduce accepted reuse. Do not choose from loss, GPU utilization or changing training-opponent scores.
4. Primary: **4,096 games, A=six epochs, B=three**, engine seed `0x5300000000000000`, sampling seed 77117, both seats and one inference mode. Adopt 6 only if the existing paired-seed lower bound exceeds 0.5. With 2,048 pairs and no caps, the margin is 3.001 percentage points, requiring score above 53.001%. Preserve worst-case censor bounds. No sample extension, intermediate decision or four-epoch fallback. [Existing bound](../learning_eval.py).
5. **Operational veto:** 6 must not score more than 5 percentage points below 3 on either fixed anchor. Each endpoint plays 1,024 games against migrated-1977 learner (engine `0x5400000000000000`, sampling 66117) and its 1827 champion (engine `0x5400000000100000`, sampling 66118). Baseline anchors may run first; challenger anchors run only if primary passes. Match each endpoint's seeds and sampling; with caps compare challenger worst case to baseline best case. This is **not a 95% noninferiority claim**: individual anchor bounds span approximately ±6 percentage points before censoring. Total evaluation is at most 8,192 games, outside charged training sessions.

One paired continuation estimates the better **current continuation**, not a universally superior PPO setting. Retain both endpoints. If 6 passes, use fresh main-run shadow comparisons to check subsequent progress; otherwise continue 3. Strength per charged hour decides, with update throughput serving only as an explanation.
