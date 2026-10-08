# Live learning-quality audit — 2026-09-26

**The completed generations show learning; the available evidence does not establish a plateau or an optimal game strategy.** A subsequent process/status check found that the next collection triggered the existing behavior-likelihood guard at **13:55:26 UTC**: maximum log-probability discrepancy **0.0129366**, above the unchanged **0.01** threshold. The trainer stopped before that collection's optimizer updates. Numerical diagnosis and recovery take priority over further training. The quality experiments below remain useful after that issue is resolved.

This audit is read-only. It did not signal the trainer, acquire its budget lock, change pinned sources, run GPU work, or perform optimizer updates. The CPU checkpoint reader used one Torch thread with CUDA hidden and confirmed `torch.cuda.is_initialized() == False`.

## Snapshot and reproducibility

- Metrics read time: **2026-09-26 13:55:56 UTC**, generations **66–1996**, 1,931 main-run generations. The last successful generation is timestamped13:55:24 UTC; later status inspection established that the process had stopped immediately afterward.
- Audited complete metrics prefix: 5,580,181 bytes, SHA256 `01697292d78fbdd27aa7cb4849dee5933a70ab348149895c4ae9ba02c32de9db`.
- Independently copied checkpoint: generation **1977**, champion **1827**, 248,328 cumulative optimizer steps and three cumulative rejected minibatches. Its earlier generation is expected because checkpoints are periodic.
- Checkpoint file SHA256: `fe3d7da085d93f155b52a2c4ad31119f415e11cb5c8f28b108614048a190e223`.
- Pinned training source SHA256: `03454a86354ccd5c320604511df055e3043a9c56f298c2c2087fc3972070cd42`.

The [analysis script](../analyze_live_quality.py) reads complete JSONL records, computes rolling aggregates, checks the checkpoint checksum/identity/finite state, and hashes archived policies. Derived data and the immutable audit checkpoint are local at `/home/lva/.cache/shards-preflight/live-quality-audit-2026-09-26.json` and the adjacent `.soicp`. Raw evidence comes from `/home/lva/.local/share/shards-training/2026-09-26/main/metrics.jsonl`, `main/identity.json`, and `evaluations/*.json`. Throughput below divides by **charged monotonic seconds**, including intervening evaluation/checkpoint overhead, rather than relying on UTC clock differences.

## What the measurements support

The main run added **494,336 terminal games**, **162,448,010 retained learner decisions**, and **240,690 accepted optimizer steps** in 5,251.8 charged seconds. No main-run games were capped in this snapshot. Three minibatches were rejected by existing guards; all logged parameter/optimizer finite checks passed. Sample reuse remained about 3.03 passes per retained row. These are activity and correctness measures, not independent strength samples. [Metrics aggregation](../analyze_live_quality.py), [training counters](../train_campaign.py).

| Metric | First 100 generations, 66–165 | Previous 100, 1797–1896 | Latest 100, 1897–1996 |
|---|---:|---:|---:|
| Retained decisions / charged second | 27,590 | 31,897 | 30,892 |
| Mean episode wrapper decisions | 425.7 | 349.1 | 343.4 |
| Mean policy entropy, nats | 1.114 | 0.728 | 0.698 |
| Approximate KL | 0.00225 | 0.00253 | 0.00258 |
| Clip fraction | 1.98% | 2.49% | 2.49% |
| Pre-clipping gradient norm | 0.146 | 0.240 | 0.242 |
| Logged value loss | 0.322 | 0.364 | 0.342 |
| Seat-0 win fraction | 56.32% | 43.49% | **40.73%** |
| Draw fraction | 0.027% | 0.055% | 0.094% |

The PPO metrics are means of generation-level logged summaries; they are not held-out critic losses. The value objective includes clipping and a factor of one half, so it must not be read as plain MSE or explained variance. PPO remains materially below its configured mean-KL guard of 0.03, with a minority of ratios clipped; this supports stable small updates, not a conclusion that learning rate must increase. The implementation uses actual stored behavior likelihoods and repeated minibatch passes, consistent with the [PPO objective](https://arxiv.org/abs/1707.06347). [Loss/guard implementation](../learning_model.py).

Frozen champion comparisons provide more direct evidence:

| Candidate vs previous champion | Wins / 256 | Score | Conservative per-comparison 95% bound | Promoted |
|---|---:|---:|---:|---|
| 241 vs initial | 254 | 99.22% | 87.21–100% | Yes |
| 436 vs 241 | 157 | 61.33% | 49.32–73.33% | No |
| 644 vs 241 | 182 | 71.09% | 59.09–83.10% | Yes |
| 860 vs 644 | 155 | 60.55% | 48.54–72.55% | No |
| 1083 vs 644 | 167 | 65.23% | 53.23–77.24% | Yes |
| 1325 vs 1083 | 141 | 55.08% | 43.07–67.08% | No |
| 1575 vs 1083 | 155 | 60.55% | 48.54–72.55% | No |
| 1827 vs 1083 | 171 | 66.80% | 54.79–78.80% | Yes |

All eight evaluations completed with no censored outcomes. Improvement between several frozen generations is demonstrated; a flat score near 50% against a changing, increasingly strong champion would not demonstrate a plateau. Initial-policy results are now saturated and should remain only a basic regression check. The earlier independent 1,024-game pilot comparison favored width128 over width256, 62.70% with bound56.69–68.70%; that justifies the early selection, not a claim about the eventual capacity ceiling. [Frozen evaluation and bounds](../learning_eval.py), [checkpoint evaluator](../evaluate_checkpoints.py).

## Findings that matter for learning quality

1. **Seat/hero stratification is the most useful immediate diagnostic.** Seat-0 win share is 40.30% over the latest 60 minutes, versus 56.32% in the first100 generations. This is not evidence of a sign error: labels are indexed by the recorded deciding seat, both current-policy seats contribute in self-play, frozen-opponent decisions are excluded, and terminal masks separate caps from outcomes. Duel intentionally gives seat1 the first hero draft choice and seat0 the first turn. Hero choice may therefore explain a real asymmetry, but the metrics cannot separate it from seat-specific policy behavior. Measure same-checkpoint self-play by hero pair and seat before drawing a balance conclusion. [Credit assignment](../learning_rollout.py), [Duel draft order](../../../Assets/Scripts/Shards/Engine/ShardsEngine.cs).

2. **Entropy has fallen, but legal-choice exploration is unmeasured.** The latest100 generations still use a broad set of action kinds. Fast-play share rose from2.08% to3.57%; reroll share fell from4.68% to3.04%. Concessions are rare: eight among7.68million latest-window retained decisions. Aggregate entropy mixes forced decisions, small menus, large menus, and changing decision contexts. It cannot diagnose collapse alone. Record entropy only on menus with at least two legal choices, normalized by `log(legal_count)`, plus top-action probability, kind/context and hero-draft entropy. The existing action histogram reports selected counts, not opportunity-normalized preference. [Policy prior and masking](../learning_model.py), [collector histograms](../learning_rollout.py).

3. **The archive preserves distinct history, but is one learning lineage.** The checkpoint contains16 distinct archived hashes at versions `0,128,288,416,624,784,976,1096,1216,1368,1520,1704,1904,1960,1968,1976`; the champion1827 is independently retained. No accidental archive collapse was found. In the latest100 generations, the learner scored about93.2% against128,64.9% against1096,52.8% against1827, and roughly50% against very recent snapshots. These are training-distribution diagnostics, not independent tournament estimates. One opponent version is selected per generation; all archived-opponent lanes in that cohort share it. Roughly75% of games are current self-play, while25% face the sampled archive/champion. The frozen pool lacks a separately trained strategy or search opponent. A small heterogeneous anchor panel or targeted exploiter tests this blind spot more directly than adding more nearly identical snapshots. The motivation is supported by the role of diverse opponents and exploiters in the original [AlphaStar league description](https://storage.googleapis.com/deepmind-media/research/alphastar/AlphaStar_unformatted.pdf); its scale and gains are not assumed to transfer here. [League schedule](../train_campaign.py).

4. **The next collection triggered the numerical guard.** The largest discrepancy in successful generation logs is0.0094507 versus the existing0.01 stop threshold. The following collection stopped at log-probability discrepancy0.0129366 and value discrepancy0.0008396. Adaptive actor batches32/64/128/256 and verifier batches2048/remainder can select different TF32 kernels; that is a concrete hypothesis, not an established root cause. Preserve the guard and compare identical inputs, masks, actions and weights across actor/verifier batch shapes and IEEE arithmetic. Do not widen tolerance merely to keep training running. The failed collection was checked before optimizer updates, but earlier updates since the periodic checkpoint must be discarded on recovery. [Behavior verification](../train_campaign.py), [adaptive actor shapes](../adaptive_actor.py).

5. **Important diagnostic outputs are computed but not retained in generation logs.** `value_mean`, `value_abs_max`, legal-logit extrema and maximum absolute log-ratio are returned by the learner, but aggregation keeps only loss/value loss/entropy/KL/clip/gradient norm. No current held-out value calibration, return histogram by deciding seat, forced-action fraction, hero frequencies, entity-overflow rate, or legal-menu-size histogram is available. Finite checkpoint tensors cannot answer those questions. Add such telemetry only in an explicitly versioned future change or the separate frozen evaluator; no live source edit is required for this audit. [Learner metrics](../learning_model.py), [generation aggregation](../train_campaign.py).

6. **Known observation omissions can limit the attainable strategy.** The feedforward policy has24 public entity records and16 staged-choice records, with aggregate counts outside those limits. It intentionally omits some public per-entity attack-pending state, deferred defense allocations, short Scry/reorder memory, full history, and some mode text. Exact action coverage does not imply complete decision information. Evaluate error and overflow by decision context before spending budget on a larger network; width cannot reconstruct information absent from the observation. [Observation contract](../Host/CONTRACT.md), [policy transform](../learning_model.py).

## Confidence and balance-data limits

The existing evaluator correctly treats seat-swapped seed pairs as the independent units for its conservative bound, and bounds censored outcomes without assigning draws. Individual decisions and the two games of one pair must never become independent win-rate samples. Reusing an evolving archive score as a fixed-opponent learning curve is also invalid. [Evaluation implementation](../learning_eval.py).

The repeated 95% champion tests are **per-comparison**, not a95% guarantee that every champion selected over12hours is stronger. Repeated selection and pilot model selection require a fresh final confirmation set or an explicitly multiplicity-controlled/sequential procedure. For example, reserving five final comparisons and using one-sided error0.01 per comparison gives a straightforward union-bound familywise budget. A continuously monitored alternative needs a valid confidence sequence, not repeatedly inspecting ordinary intervals; see the primary [Howard et al. confidence-sequence paper](https://arxiv.org/abs/1810.08240). No adjustment is retroactively claimed for the eight reported comparisons.

Legal-opportunity/selection counts and hero-conditioned win rates are useful balance evidence, but are not causal card-strength estimates. Draft availability, opponent composition, game phase, and policy preference confound selection. Preserve seeds, both frozen policy hashes, hero pair, market opportunity, decision context and actual terminal/censored outcome before attempting balance recommendations. [Evaluator telemetry scope](../learning_eval.py).

## Bounded next experiments and acceptance rules

| Priority | Experiment | Bound and controls | Decision criterion |
|---|---|---|---|
| 1 | Frozen CPU shadow panel: current champion against selected pilot128, pilot256 and two strong historical checkpoints | Start with16–32 games solely to validate CPU semantics/overhead. Later1,024 games per anchor; fresh disjoint paired seeds, both seat assignments, frozen payload hashes, one CPU thread/host worker | Establish whether strength is still improving against fixed opponents; no training changes based on the smoke's noisy scores |
| 2 | Same-checkpoint self-play with hero×seat diagnostics |1,024 planned games initially; report coverage, terminal/censor counts, hero-draft choices, legal-choice entropy and confidence by paired seed | Explain seat asymmetry; insufficiently sampled hero pairs remain unknown, not “balanced” |
| 3 | Plateau test using two snapshots separated by30–60 charged minutes | Evaluate both snapshots against the same frozen panel on development seeds; confirm any selected winner on an untouched seed set | Improvement in opponent-balanced score and no material worst-anchor regression; initial-policy score alone is insufficient |
| 4 | Only after a confirmed plateau: one preserved-checkpoint challenger at a time | Two matched10-minute charged branches, baseline versus a single change, at most20minutes total and inside the remaining shared budget. Preserve current model/optimizer/RNG/archives and exact parent checkpoint. Candidate changes: diversity-focused opponent mixture, or entropy coefficient0.02 versus0.01 | Keep a challenger only after fresh frozen head-to-head plus anchor-panel improvement; unchanged guards, legal action coverage and censor accounting are mandatory |

**Strength per hour:** report the change in mean score against a predeclared fixed panel divided by charged training hours, together with worst-anchor score, paired-seed uncertainty, per-seat results and censor fraction. Also show retained decisions/second, but never substitute that throughput for strength. Opponent weights and acceptance margins must be fixed before examining challenger results. Do not combine incompatible CPU FP32 and GPU TF32 series silently; validate frozen-policy score agreement on a modest paired sample and label each inference mode.

The shadow evaluator can supply the missing observations without updating the learner, but its CPU use may slow the simulator. Measure main-run collection time and retained decisions/charged-second before/during/after an authorized overlap smoke. Do not launch a permanent shadow process merely because it avoids CUDA.

## Follow-up: safe stop and CPU evaluator smoke

A read-only process check confirmed that trainer20793, host20829, supervisor20792 and wrapper20791 had exited. The supervisor recorded returncode1 and no automatic retry; the planned final evaluation was skipped after training failure. The ledger closed normally through its exception path with **6,151.354 seconds charged and 37,048.646 seconds remaining**. It separately recorded1,554,413 uncheckpointed decisions and5,120 episodes as discarded, with an unknown unreported tail. Durable finalized-collection/censor counts are preserved rather than rolled back with the model. The latest verified checkpoint remains generation1977. These are observations, not a restart or budget mutation.

The isolated [CPU shadow helper](../experiments/cpu_shadow_eval.py) reuses the production frozen match loop and outcome bounds. Seven [focused tests](../experiments/test_cpu_shadow_eval.py) passed, including exact seeded CPU action-trace parity, legal masking, held lanes, unknown censored outcomes, cleanup, same-payload dual-role snapshotting, and v3 loader-reference wiring. It forces eager CPU FP32, unpinned buffers and one Torch thread/host worker; it never opens the campaign ledger. `--variant v3` installs the explicitly identified v3 host/loader only inside the helper process; strict checkpoint identity remains mandatory. The actual v3 runtime smoke is pending migration.

One authorized **16-game smoke** compared frozen generation1977 against champion1827 using eight fresh seat-swapped seeds starting at `0x6000000000000000`. It completed in **2.375 seconds of matches**,3.050 seconds including snapshot/loading, with no caps, unchanged frozen weights, and `cuda_initialized=false`. Its12/16 score has conservative95% bound26.98–100% and establishes no strength ranking. All16 games used Decima in seat0 and Tetra in seat1; this is a coverage observation, not proof that the other heroes are weak. The helper also recorded legal-menu counts, nonforced normalized entropy and terminal-outcome value MSE, scoped to actual active decisions. Raw JSON is `/home/lva/.cache/shards-preflight/cpu-shadow-1977-vs1827-smoke.json`.

Training was already stopped throughout this smoke, so its interference with main-run throughput is **unmeasured**. No continuous CPU evaluator was launched. A later overlap check must establish whether the extra CPU load is acceptable before scheduling a fixed-anchor panel during training.

The optional [shadow watch](../experiments/shadow_watch.py) is prepared for that later launch:256 games against the checkpoint's own frozen champion and256 against one immutable historical anchor every20minutes, with one CPU child at a time, a300-second child timeout, reserved unique paired seeds and no automatic retry after failure. It stops on a finished/failed trainer, dead trainer PID or exhausted budget. The fixed anchor and each candidate snapshot are retained; it never deletes or modifies training checkpoints or writes the training budget. Helper and watcher now have15 passing CPU contract/lifecycle tests. Reports belong directly in the campaign's `evaluations/` directory because dashboard discovery is intentionally flat. This scheduler has **not** been launched by this audit.
