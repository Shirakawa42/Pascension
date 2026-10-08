# V7 learner integrity audit — 2026-09-26

## Scope and conclusion

Independent source and CPU contract audit of the V7 learner, its frozen V6 host lineage, outcome credit, choice mixture, actor storage, PPO guards, and migration/continuation. Audited V7 source identity: `d90a6254cdba784f67d8e487b99d25f279a25e236204820b9f034a169956debb`. No live training process, pinned source, or GPU state was changed by this audit.

**No learner correctness defect was reproduced in this scope.** This is bounded evidence, not proof that all game interactions or possible numerical states are correct. There are important limitations in what the training statistics establish, listed below. Existing GPU parity evidence was inspected; this audit did not independently rerun GPU work alongside training.

## Outcome credit and episode boundaries

- Each saved decision records the host's **pre-action deciding seat**, not the current turn owner and not an assumed alternating sequence. At termination, its return is exactly `outcomes[episode, deciding_seat]`. Consecutive actions and defending-player decisions therefore receive the appropriate result. Wins/losses/draws must be `+1/-1/0` and zero sum. [Collector and terminal reference](../learning_rollout.py#L43), [pre-action ownership and append](../learning_rollout.py#L229).
- A generation freezes the learner actor. Archived-opponent actions are submitted to the host but excluded from the learner store; current self-play decisions from both seats are retained. Adaptive actor packing preserves original lane identity while removing padded copies. [Opponent/learner routing](../learning_rollout.py#L229), [adaptive actor](../adaptive_actor.py#L68), [owned contiguous copies](../experiments/rollout_fastpath.py#L59).
- Complete games receive a Monte Carlo terminal return; advantages are `return - frozen_value`, standardized over retained learner decisions. This is the stated undiscounted outcome objective, not a turnwise alternating-sign TD target. No HP payment, relic acquisition, or other local event directly invents positive or negative reward. [Store sealing](../learning_rollout.py#L142).
- Administrative caps are distinct from real draws. Entire capped episodes are excluded before outcome credit and advantage normalization. A censor ceiling fails closed. Unfinished collection at shutdown is discarded and accounted, with seeds advanced. The host checks held lanes cannot replay terminal rewards. [Lifecycle checks](../learning_rollout.py#L264), [censor accounting and ceiling](../train_campaign.py#L224).
- Censoring is still conditional-on-completion sampling and can bias training if it occurs systematically. The V7 pilot reported **0 censored games out of 19,712**; absence in that sample does not justify deleting the guard. [Pilot evidence](v7-pilot-training-summary.json).

## Choice exploration and gradients

The actual saved V7 learner distribution is

`q = (1 - alpha_relic - alpha_destiny) * softmax(scores) + alpha_relic * U(legal relics) + alpha_destiny * U(legal destinies)`.

`alpha_relic = 0.06` only when at least one normal relic action is legal; `alpha_destiny = 0.02` only when at least one normal destiny action is legal. Absent categories receive zero mixture mass. The actor samples this distribution, stores its selected log probability, and PPO evaluates the same distribution. These are menu-level probabilities, not percentages of games randomly assigned a relic. [CPU policy reference](../experiments/choice_policy_v7.py#L74).

The mixture is not an external override secretly labelled as a policy action. With base probabilities `p`, mixture probabilities `q`, and upstream log-mixture gradient `g`, the custom backward uses responsibility `r = (1-alpha_relic-alpha_destiny)*p/q` and

`dL/dscore_j = g_j*r_j - p_j*sum_i(g_i*r_i)`.

That is the correct derivative of the implemented mixture. Illegal entries are explicitly zeroed. The Volos head receives the sum of score derivatives for its corresponding legal mode. [Fused implementation](../experiments/choice_logits_v7.py#L57). A new independent double-precision CPU contract verifies this identity against autograd at score scales 1, 25, and 200; other new contracts exercise the V7 actor and learner together. [Independent integration tests](../experiments/test_learning_integrity_v7.py).

Existing GPU evidence covers 72 combinations of batch size, extreme score scale, random masks, and exploration coefficients. Its maximum reported custom-gradient error is `5.7220458984375e-06`; maximum selected log-probability error is `3.0517578125e-05`. These are finite-precision parity results, not strength measurements. [GPU numerical evidence](v7-cuda-choice-parity.json).

Volos has a separate four-output state-dependent residual head, with context and legal-mode guards. This removes the previous requirement to discriminate its four effects primarily through a tiny ordinal feature. It does **not** prove that the head has learned optimal tactical play. Old heads migrate as exact zeros. [Mode scoring](../experiments/choice_policy_v7.py#L22), [CPU mode tests](../experiments/test_choice_policy_v7.py#L28).

## PPO mutation and frozen versions

- All stored selected log probabilities and values are recomputed against the unchanged behavior policy before any update. The pilot's maximum stored/recomputed log-probability error was `7.581710815429688e-05`. [Verifier](../train_campaign.py#L93), [pilot evidence](v7-pilot-training-summary.json).
- Actor weights refresh only at generation boundaries; archived opponent weights are fixed for the cohort. The buffer copies observations, candidates, masks, actions, log probabilities, and values into owned storage before actor reuse. [Generation setup](../train_campaign.py#L216), [actor refresh](../learning_model.py#L219), [storage](../experiments/rollout_fastpath.py#L91).
- The captured graph computes speculative gradients, but does not mutate Adam. Invalid inputs, excessive log ratios, excessive approximate KL, and nonfinite derivatives prevent the optimizer update. A rejected minibatch stops the rest of that generation's epochs. Accepted state is checked for finiteness. [Captured objective](../learning_model.py#L256), [acceptance boundary](../learning_model.py#L437), [loop stop](../train_campaign.py#L271).
- The pilot accepted **9,504 optimizer steps** and rejected **2 minibatches**. This is not evidence of corrupt training: the rejection guard deliberately limits stale updates. It would be a quality/performance concern if rejection became frequent or happened before meaningful use of each cohort. [Pilot evidence](v7-pilot-training-summary.json).
- The final minibatch is filled with uniformly sampled extra real rows; no padded host rows are learned. Consequently `sample_reuse` can slightly exceed the nominal epoch count, and `optimizer_example_passes` intentionally counts repetitions. [Minibatch construction](../train_campaign.py#L263).

## Independent real-checkpoint migration verification

Loaded the actual V6 generation-7253 checkpoint and its published V7 migration with their required saved identities and checkpoint checksums. Removed only the declared V7 fields from a copy of the target state, then compared recursive digests against the complete source state.

Verified:

- Every existing learner parameter, retained initial/champion/archive policy, Adam moment/step, and continuation counter is unchanged.
- The payload RNG state and budget snapshot are unchanged.
- All 19 retained policies have zero new Volos heads; the two new optimizer states are zero.
- Only the learner has exploration `[0.06, 0.02]`. The 18 retained initial/champion/archive policies have `[0, 0]`.
- CUDA was not initialized by the audit process.

The numerical float32 representation of 0.06/0.02 differs in its final decimal digits, as expected. Evidence: [independent migration comparison](v7-independent-migration-integrity.json). Migration source: [preservation checks](../experiments/migrate_runtime_v7.py#L45).

An additional synthetic V7 CPU integration test checkpoints after one update, constructs a fresh learner, restores it, then proves the next Torch random draw, next accepted parameter update, and next Adam state match exactly. It also proves a rejected step leaves the new Volos head and Adam untouched. This checks implementation mechanics, not gameplay strength. [Tests](../experiments/test_learning_integrity_v7.py#L47).

The production resume restores global RNG only after policy/actor construction and graph warmup. The checkpoint owns its CPU snapshots, preserves the campaign allocation, and uses an atomic checksummed file. GPU bitwise continuation across all possible runtime/library changes is not established by the CPU test. [Resume order](../train_campaign.py#L171), [checkpoint format and RNG](../campaign_state.py#L514).

## Statistics that need careful interpretation

| Statistic | What the source actually measures | Limitation |
|---|---|---|
| Completed games | Real terminal games in accepted collection accounting | A real completed game may have all or part of its update skipped at a deadline; game count is not optimizer work. |
| Learning decisions | Retained current-policy rows, excluding archived-opponent and censored rows | Longer games and players making more decisions contribute more rows; this is not a count of independent matches. |
| Optimizer example passes | Accepted minibatch rows including repeated epochs and last-batch fill | Better measure of actual optimization work than generation number alone. |
| Generation | Completed collection/update-loop iteration | Increments even if the deadline interrupts remaining minibatches; it does not guarantee three full epochs. |
| Mean loss / approximate KL | Mean over reported attempted minibatches, including a rejected attempt when it provides that field | It is not strictly an accepted-update-only average. Read rejection counts and sample reuse alongside it. |
| Gradient norm | Norm before clipping, only available for relevant accepted/reporting paths | A value above 0.5 is expected because gradients are subsequently clipped. |
| Value loss | Half the mean of the larger unclipped/clipped squared error, then multiplied by the configured value coefficient in total loss | It is not plain prediction MSE, calibration, or explained variance. |
| Entropy | Mean categorical entropy over all retained decisions | Includes mandatory one-legal-action states and heterogeneous menus; does not establish exploration for every relic, destiny, hero, or mode. |
| Archive score | Current learner outcome versus the selected frozen archive/champion opponent, completed games only | Opponent version changes between cohorts. A changing score is not a fixed-opponent strength trend; use frozen paired evaluations. |
| Behavior parity | Stored vs recomputed selected-action log probability and value | Proves consistency of behavior records, not completeness of observations or correctness of every game rule. |
| Finite checks | Parameters, buffers, optimizer state, and relevant arithmetic are finite | Finite but semantically wrong features or weak strategy remain possible. |

Source: [generation aggregation](../train_campaign.py#L288), [PPO objective](../learning_model.py#L297), [archive score denominator](../learning_rollout.py#L325), [dashboard plots](../training_dashboard.html#L165).

Two useful monitoring improvements require no live learner change: label loss/KL as attempted-minibatch aggregates, and expose the already-recorded behavior **value** parity alongside log-probability parity. Coverage/competence diagnostics should stay separate from aggregate entropy.

## Tests executed in this audit

**64 CPU tests passed** with `CUDA_VISIBLE_DEVICES=''`, one Torch/OMP/MKL thread, and the preflight virtual environment:

- 26 existing tests: complete-episode credit and ownership, learner mathematical guards, adaptive actor CPU packing/refresh, V7 mode/mixture contracts.
- 34 existing tests: checkpoint/collection accounting, censored evaluation, contiguous rollout copies and host validation.
- 4 new V7 integration tests: actual frozen actor mixture likelihood, exact next-step Adam/RNG resume, immutable rejected updates, and independently derived mixture gradient.

New file: [test_learning_integrity_v7.py](../experiments/test_learning_integrity_v7.py). No pinned production files were edited. The new tests use synthetic tensors exclusively and do not run a training campaign.

Exact commands, from the repository root:

```sh
CUDA_VISIBLE_DEVICES='' OMP_NUM_THREADS=1 MKL_NUM_THREADS=1 PYTHONPATH=Tools/TrainingPreflight:Tools/TrainingPreflight/experiments /home/lva/.venvs/shards-preflight/bin/python -m unittest test_learning_rollout test_learning_model.LearningMathTests test_adaptive_actor.AdaptiveActorTests.test_cpu_subsets_padding_refresh_and_behavior_parity test_adaptive_actor.AdaptiveActorTests.test_invalid_lanes_rejected_before_packing test_adaptive_actor.AdaptiveActorTests.test_unselected_rows_are_not_consumed test_choice_policy_v7 -v

CUDA_VISIBLE_DEVICES='' OMP_NUM_THREADS=1 MKL_NUM_THREADS=1 PYTHONPATH=Tools/TrainingPreflight:Tools/TrainingPreflight/experiments /home/lva/.venvs/shards-preflight/bin/python -m unittest test_campaign_state.CheckpointTests test_campaign_state.CollectionAccountingTests test_censored_evaluation test_rollout_fastpath -v

CUDA_VISIBLE_DEVICES='' OMP_NUM_THREADS=1 MKL_NUM_THREADS=1 PYTHONPATH=Tools/TrainingPreflight:Tools/TrainingPreflight/experiments /home/lva/.venvs/shards-preflight/bin/python -m unittest test_learning_integrity_v7 -v
```

## Remaining limits

This audit supports continuing the V7 learner and its guards. It does not certify all game content, balance aggregation, hidden-information behavior, every menu branch, or optimal play. The parent audit is responsible for reconciling live counters and game/statistics behavior. No claim of statistical superiority, full game mastery, or permanent absence of bugs follows from this report.
