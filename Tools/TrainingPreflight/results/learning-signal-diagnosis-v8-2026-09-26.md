# Learning signal diagnosis for V8 — 2026-09-26

The learner is updating correctly in the audited path, but its representation makes several elementary decisions difficult or impossible to learn. More games alone cannot resolve an information alias. The immediate priorities are semantic mode discrimination, legitimate source/memory information, explicit public activation conditions, and eliminating mechanically invalid damage choices. The evidence does not support replacing the value head or reducing exploration blindly.

This report uses the repository's implementation as primary sources. All new work ran on CPU with one Torch thread; live training, checkpoints, and the GPU were untouched. The frozen checkpoint was generation 7552, payload `a5c903318acd65ae8f05929508ce4df1d6f48ebcaacea2f40387dc3aeaeb3ae2`. New artifacts: [reproducible diagnostic](../experiments/learning_signal_diagnosis_v8.py), [raw results](learning-signal-diagnosis-v8-2026-09-26.json).

## 1. Mode decisions have a small gradient channel

The two Reactor options have no card identity and differ in exactly one candidate feature: ordinal slot 25 is `0` versus `1/128`. After the signed-log transform their feature distance is **0.00778214**. Both pass through the same candidate projection, and their identical card embeddings cancel exactly in the score difference. A contrast-gradient measurement on the actual saved checkpoint confirms that the card-embedding gradient is zero. The Volos residual head is correctly inactive in this other context. This is poor conditioning, not a categorical-softmax bug. Sources: [candidate scorer](../model.py#L49), [input transform](../learning_model.py#L88), [V7 context guard](../experiments/choice_policy_v7.py#L61), [raw contrast gradients](learning-signal-diagnosis-v8-2026-09-26.json).

A deliberately favorable, artificial supervised experiment repeatedly presents the same frozen Reactor input, labels mode 1 as desired, and uses AdamW at `3e-4`, no weight decay, and the production 0.5 gradient clipping threshold. This is a trainability experiment, **not gameplay training or evidence that this label is optimal for a physical Reactor**.

| Model trained on the repeated fixture | Probability after one update | After 100 updates | Updates to probability ≥95% |
|---|---:|---:|---:|
| Existing complete V7 policy | 50.2111% | 55.3090% | 288 |
| Zero-initialized independent two-mode residual head; old trunk frozen | 50.9212% | 94.9179% | 101 |

Both start at 50.1914%. The new head's first-step probability change is about **37 times** larger. This is a controlled result for one fixture and optimizer; it does not predict a campaign speedup or win-rate gain. No trained fixture weights were saved or deployed. The categorical head fixes the attenuated contrast without requiring large changes to the pretrained global trunk. Source: [complete procedure and output](../experiments/learning_signal_diagnosis_v8.py).

Recommendation: add zero-initialized categorical residual scores for actual mode/keep-banish contexts, guarded by context and original option identity. Do not treat the current visible slot as a globally meaningful mode: paging, unavailable options, and separate contexts can change its meaning. Supply the public effect source and revealed choice card to this branch. Keep the preexisting logits as an exact residual baseline during migration.

## 2. Some choices are information-theoretically unlearnable

The previously constructed physical-Reactor and Ojas-copy fixtures encode **all 4160 input floats identically**. The physical source can be banished by the 3-gem mode, while a copy cannot banish the copier. The engine explicitly tests the resolving source's definition and play-zone membership. A deterministic policy function of identical inputs cannot condition on this consequence, regardless of training duration. A balanced artificial classification task demanding opposite labels on these two inputs has a minimum cross-entropy of `ln(2)` and maximum accuracy of 50%; those bounds describe representation capacity, not the optimal actions in the fixture. Sources: [engine source test](../../../Assets/Scripts/Shards/Content/ShardsDuelSet.cs#L520), [fixture](strategy-reactor-source-inputs.json), [alias construction](strategy-alias-probe/Program.cs), [diagnostic](learning-signal-diagnosis-v8-2026-09-26.json).

The same previous investigation reproduced an own-top-deck memory alias after legal Fabricator actions. The acting player had learned a different next card, but the encoded inputs were identical. That requires memory derived from actual authorized reveals and subsequent movements; reading the private deck order would introduce a leak. Source: [own-top fixture and scope](strategy-own-top-alias.json).

The value branch is `value(trunk(obs))` and never reads candidates. In 256 sampled public gameplay rows, artificially zeroing every candidate card code changed its predicted value by **exactly zero**. This differential is an architecture dependency test, not a set of alternate legal positions. When a revealed card appears only in candidates, its identity cannot influence the baseline, and in same-card opposite choices its embedding contribution cancels from their contrast. Sources: [value computation](../experiments/choice_policy_v7.py#L32), [measured dependency](learning-signal-diagnosis-v8-2026-09-26.json).

Recommendation: put the legitimately known source/revealed-card summary into the shared state representation, or use a small masked candidate pooling branch for the policy/value residual. A mode head alone does not repair information absent from every input.

## 3. Premature activation is a representation and credit problem, not a reason to remove every action

The preceding action-sequence probe found **79** gated abilities activated while false that became true later in the same player turn while the same ability was still owned but unavailable. These are stronger evidence of missed timing than a raw false-gate count. Some other exhaust abilities install a flag used by later plays, so a blanket rule against early activation would destroy valid sequencing. The prior intervention blocking a narrow false-gate whitelist scored 51.406% in 640 paired games, with a wide interval crossing 50%; it did not establish superiority. Sources: [sequence evidence](strategy-sequences-v7-2026-09-26.json), [conditional intervention](conditional-timing-v7-2026-09-26.json), [predicate implementation](../experiments/strategy_coverage_probe.py#L23), [strategy audit](strategy-paths-audit-2026-09-26.md).

Many exact gate ingredients are already in the observation: faction plays, mastery, champion count, and health. The network must discover the correct threshold or conjunction separately for rare card actions from a single terminal result. A public `condition_known`/`condition_satisfied` candidate feature, with a learned interaction against card/context, directly exposes the rule without denying the choice. Unknown conditions must remain explicitly unknown, never mapped to false. The feature must use only the acting player's authorized state; evaluating an opponent's hidden-hand condition would leak.

Recommendation: preserve legal activation, expose exact source-owned readiness for audited predicates, and track successful gated effects as well as selections. A modest learned readiness branch preserves alternative timing strategies. Avoid a blanket HP penalty, resource-shaped win target, or arbitrary action prohibition as a substitute for these corrections.

## 4. Terminal credit is correct but noisy

The collector records the **pre-action deciding seat**, not turn owner, and assigns `outcomes[episode, deciding_seat]` at termination. Archived-opponent rows are excluded; current self-play rows from both seats remain. Complete-game advantages are `terminal_return - frozen_value`, standardized across retained rows. This audited path contains no alternating-sign or seat inversion bug. Administrative censors are excluded before sealing. Sources: [credit reference](../learning_rollout.py#L43), [collection ownership](../learning_rollout.py#L229), [sealing](../learning_rollout.py#L142).

In the recent live 200-generation snapshot, episodes average **379.19 decisions**. Every retained decision in a winning player trajectory receives the same +1 return, including a wasted activation. Across many games a useful action can still learn through correlations, but rare-mode and early-sequencing effects are diluted by many later decisions and random draws. The earlier coverage panel encountered only 152 Reactor menus in 245,808 decisions (about 0.062%). Repeatedly sampled common actions dominate optimizer rows; two million games is not two million learning opportunities for each mechanic. Sources: [snapshot in raw diagnosis](learning-signal-diagnosis-v8-2026-09-26.json), [coverage panel](strategy-coverage-v7-2026-09-26.json).

This is a high-variance Monte Carlo objective, not a broken objective. More aggressive bootstrapping, short-horizon auxiliary prediction, or balanced rare-context minibatches would need their own signed-seat, off-policy, weighting, and strength validation. For the remaining run, fix the proven representational defects first. Exact dominated damage removal also shortens unnecessary decisions and makes the outcome signal less noisy without inventing reward.

## 5. The value network is useful and is not generally saturated

A separate **64-game** frozen self-match, natural hero draft, both seats, seed namespace `0x7C00000000000000`, yielded **23,198 acting decisions**, all resolved. These are correlated decision-weighted measurements, not 23,198 independent games or a strength comparison.

| Slice | Decisions | Value MSE against final ±1 result | Fraction with absolute value >0.95 |
|---|---:|---:|---:|
| All | 23,198 | 0.70733 | 4.565% |
| Rounds 1–5 | 6,774 | 0.96131 | 0% |
| Rounds 6–10 | 10,909 | 0.70349 | 2.035% |
| Round 11 onward | 5,515 | 0.40294 | 15.177% |

A constant-zero value has MSE 1.0 in this no-draw sample. Only **two** decisions had an absolute value >0.95 with the wrong outcome sign. Mean tanh derivative `1-v²` was **0.69281**. High-confidence positive/negative bins had corresponding empirical outcomes with the correct direction. The 0.998 value in a constructed alias fixture is not evidence of global value collapse. Early prediction is relatively weak, which agrees with difficult long-horizon credit assignment. Do not remove tanh or reset the value head based on the isolated fixture. Source: [raw calibration and bin definitions](learning-signal-diagnosis-v8-2026-09-26.json).

## 6. PPO is making updates; entropy is not the missing root cause

The live snapshot covers generations **8052–8251**, **51,200 completed games**, zero censors, **25,007 accepted optimizer steps**, and **6 rejected minibatches**. Mean approximate KL is **0.002959**, clip fraction **0.029331**, gradient norm **0.246282**, and entropy **0.647121**. The training loop is neither globally refusing updates nor clipping essentially every sample. Metrics average attempted minibatches, including reported rejected attempts. Source: [snapshot](learning-signal-diagnosis-v8-2026-09-26.json), [PPO acceptance and objective](../learning_model.py#L256), [aggregation](../train_campaign.py#L271).

Maximum observed behavior-log-probability parity error in that snapshot was **0.000112772**, below the production 0.01 guard; it exceeds the older pilot's tighter 0.0001 acceptance criterion. That stricter pilot criterion is not the runtime guard. No numerical corruption follows from this small discrepancy. Source: [parity checker](../train_campaign.py#L93).

V7 adds explicit 6% relic and 2% destiny menu mixtures only when these legal categories exist. These probabilities are part of both actor and PPO distributions; they do not explain a Reactor mode remaining at 50/50 in a pure mode menu, where both mixture components are absent. Entropy regularization affects all menus, but cannot restore an absent source or amplify a categorical contrast that the representation reduces to a tiny ordinal. Source: [exact mixture](../experiments/choice_policy_v7.py#L74).

## Recommended adoption boundary

Implement and test damage feasibility as an adapter invariant. Preserve the full old policy and Adam state, append zero-initialized learned semantic branches, distinguish all reproduced legitimate-information aliases, and test hidden-zone perturbation invariance. Before adoption, verify exact migration, actor/PPO likelihood consistency, GPU derivative parity, allocation/memory and end-to-end throughput, no censor increase, and a bounded strength panel. This report establishes root causes and a promising minimal direction; it does not certify an as-yet-unimplemented V8 policy as stronger.
