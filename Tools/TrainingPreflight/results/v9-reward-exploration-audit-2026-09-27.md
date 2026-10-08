# V9 reward, exploration, and planning audit — 2026-09-27

The current learner has a valid terminal-outcome objective and stochastic exploration on every decision. This audit found no reversed-seat reward bug or actor/learner probability mismatch in the CPU contracts. That does **not** establish strong play: credit is noisy across long games, useful combinations may remain rare, and the policy does no explicit lookahead. There is a concrete information convenience missing before mastery 10: the future hero-specific relic offers are not explicitly encoded until recruitment becomes legal.

Scope: source inspection, a read-only main-V9 metrics snapshot, primary research sources, and single-thread CPU tests. No GPU work, live intervention, checkpoint modification, or changes to pinned executable training files. The report concerns the published 2816-input V9, not earlier V7 calibration measurements.

## What is actually rewarded

Every learning decision receives its **actual deciding seat's** final result: win +1, loss −1, draw 0. Multiple consecutive decisions and defending-player choices do not assume alternating seats. Unfinished/capped episodes are censored rather than assigned a draw. Frozen opponent actions are not included in PPO; current-policy self-play can contribute both seats. Each generation freezes behavior weights while complete episodes drain. These contracts are implemented by [`terminal_returns`, `EpisodeStore.seal`, and collection](../learning_rollout.py).

For decision t, the unnormalized advantage is `final_result − frozen_value(observation_t)`, then centered and divided by the population standard deviation across retained generation rows. There is no discount or intermediate reward; this is complete Monte Carlo credit, equivalent to undiscounted full-return estimation rather than a short GAE horizon. The value predicts a result in [−1,1]. PPO uses ratio clipping .2, value clipping .2, entropy coefficient .01, value coefficient .5, gradient norm cap .5, and a .03 approximate-KL rejection threshold. [`learning_model.py`](../learning_model.py), [`learning_rollout.py`](../learning_rollout.py).

This can learn long-term consequences, including buying a weak immediate card to improve the eventual deck or removing an opponent's desired card. However, an unnecessary reroll in a won game also shares the positive outcome. A good action in a lost game may receive negative credit. The value baseline reduces variance; it does not identify the causal contribution of each move. Longer games contribute more decision rows, and rare combinations can remain underrepresented even after millions of games. Counting complete games is insufficient evidence that such decisions are mastered.

The multiple-epoch clipped update is consistent with the PPO approach of collecting policy interaction and optimizing a surrogate over minibatches; the paper does not establish that this game's representation or reward choice is optimal. [Schulman et al., PPO](https://arxiv.org/abs/1707.06347).

## Exploration already occurs on every action

[`sample_actions`](../model.py) draws a categorical action using Gumbel-max and records the selected action's actual log probability. It is not greedy play punctuated by a random action every X moves. In addition to entropy regularization, the current learner's saved policy contains an explicit category mixture:

`q = (1 − ar − ad) × softmax(scores) + ar × uniform(legal relic actions) + ad × uniform(legal destiny actions)`

Here `ar=.06` only when a legal normal relic-recruitment action exists, otherwise zero; similarly `ad=.02` for a legal normal destiny action. These are probability masses in a particular decision menu, **not percentages of all games or independent choices from all catalog cards**. Two legal relic actions each receive at least .03 mixture mass in that menu. The actor and PPO evaluate the same q and log q. Saved historical opponents can have different saved mixture buffers. [`choice_policy_v9.py`](../experiments/choice_policy_v9.py).

Current V9 also applies a finite −1.5 logit prior to 12 source-audited false immediate exhaust conditions, before the mixture. This is a soft policy prior, not reward shaping or a legality restriction. Learning can overcome it. The tests verify legal support, exact mixture ordering, and no penalty on unrelated actions. [`test_readiness_v9.py`](../experiments/test_readiness_v9.py).

Other diversity sources are deck/market randomness, 75% uniformly sampled distinct hero pairs with 25% natural drafting, and opponent sampling. Forced initial hero selections are excluded from PPO credit. Approximately 75% of games use current-policy self-play and 25% an archived opponent; for each collection cohort the frozen opponent is champion with probability .25 and otherwise a uniform archive entry. The archive is refreshed every eight generations and limited to 16 entries spread through history. This is useful opponent diversity, not a full population of separately trained exploiters. [`variant_v9_runtime.py`](../experiments/variant_v9_runtime.py), [`train_campaign.py`](../train_campaign.py).

Adding an unrecorded random override every X actions would make stored behavior probabilities wrong. Any new exploration schedule must define and record its actual distribution in actor and learner. Blind uniform noise is also unlikely to assemble a long coherent strategy reliably. Better candidates for **controlled tests**, not established improvements, are opportunity-conditioned exploration for scarce choices and coherent episode-level strategy curricula. Coverage must report opportunities, selections, and downstream contexts; nonzero probability is not practical coverage.

Primary precedents support diversity without supplying a universal tuning recipe. OpenAI Five used a mixture of current and historical self-play and investigated environment randomization; its reported terminal-only 1v1 ablation learned more slowly than its shaped-reward setup. Those Dota results do not determine the best Shards reward. [OpenAI Five](https://openai.com/index/openai-five/). AlphaStar describes forgetting and cycles in self-play, and uses specialized exploiter agents to expose weaknesses. This motivates cross-play tests here, not reproducing AlphaStar's training budget. [AlphaStar research account](https://deepmind.google/blog/alphastar-grandmaster-level-in-starcraft-ii-using-multi-agent-reinforcement-learning/).

## What the model knows, and how far it plans

The encoder supplies own and opponent health, mastery, current gems/power, hero identity, recruitment status, and public board information. It includes explicit own mastery 5/10/15/20/30 predicate bits; mastery-10 readiness is observation 2089. V9 includes the opponent's public permanent collection while keeping the hidden hand/draw partition and draw order private. [`HostV9/Encoder.cs`](../experiments/HostV9/Encoder.cs).

The engine defines hero-specific relic offers through `RelicIdsFor`, sets them aside during setup, and makes recruitment legal only at mastery >=10 if no relic was previously recruited. The UI's hero draft can show those public relics. But V9's own collection histograms omit `SetAside`; `HeroFeatures` supplies hero identity and ability costs, not the future relic list. The IDs become candidate/menu embeddings when recruitment is currently legal. Before then the network must learn the hero-to-relic mapping and value of approaching mastery 10 from experience. No rules text or explicit future-relic effect description is fed into the policy. [Engine](../../../Assets/Scripts/Shards/Engine/ShardsEngine.cs), [encoder](../experiments/HostV9/Encoder.cs), [hero features](../experiments/HostV9/HeroFeatures.cs).

The model is a feed-forward state/candidate network with learned card-ID embeddings, numeric features, menu pooling, and categorical mode heads. `LearningActor.compute` executes one forward pass then samples an action. There is **no engine branching, MCTS, explicit opponent-response search, or fixed number of future moves simulated**. Its full-game training target can teach the statistical future value of an observation; that is different from calculating the consequences of each candidate at decision time. The value head estimates state value, not an explicit rollout for every action. [Policy](../experiments/choice_policy_v9.py), [actor](../learning_model.py).

A compact public future-relic descriptor is a plausible additional feature, especially for threshold planning, but needs a new version with zero-initialized weights, unchanged hidden-information contract, throughput checks, and controlled evaluation. Public deterministic same-turn lookahead is another plausible experiment. It must stop or correctly integrate uncertainty at hidden draws, and its extra CPU/latency cost may reduce total training quality. Neither is proven beneficial by this audit. Fixed catalog ID embeddings also do not guarantee generalization after balance changes alter a card's effects.

## Dense rewards are not an automatic repair

Giving unconditional rewards for mastery, damage, banishing, or acquiring expensive cards changes the optimization target and can reward actions that lose games. Potential-based shaping has a formal policy-invariance result under its MDP assumptions: use `F=gamma*Phi(next)−Phi(current)` with appropriate terminal conditions. [Ng, Harada, Russell](https://people.eecs.berkeley.edu/~russell/papers/icml99-shaping.pdf).

For this implementation's complete undiscounted returns and terminal potential zero, the shaping sum telescopes: `G'_t=G_t−Phi(s_t)`. With the correspondingly shifted value `V'=V−Phi`, the advantage is unchanged. This algebra is independently CPU-tested below. Potential shaping may change finite-training behavior through baseline estimation or a different bootstrapped learner, but merely inserting more reward events is not proof of better credit. Multi-agent perspective, terminal conditions, partial information, and the moving opponent population require care; the single-agent theorem is not an unconditional guarantee here.

## Current measured evidence

The [read-only snapshot](v9-reward-exploration-snapshot-2026-09-27.json) covers generations 8812–8984: 44,288 completed games, zero censored games, 21,329 accepted optimizer steps, four rejected minibatches, and approximately 3.007 passes per collected learning row. Mean complete episode length is 373.58 decisions. Minibatch diagnostic means are entropy .62316, approximate KL .003545, clip fraction .03699, and gradient norm .28596. Maximum recorded behavior discrepancies are .00011522 log probability and .000002325 value. These show active, numerically bounded updates; they do not measure playing strength. Reported value loss .36927 is the clipped objective's half-squared-error quantity, not plain MSE or a calibration score.

The [V9 pilot](v9-public-deck-pilot-summary.json) completed 12,544 games. Its [640-game balanced comparison](v9-public-deck-balanced-regression.json) scored **46.09%** versus the same original weights migrated with zero readiness prior, with no censored games. It passed the predeclared .45 point-score screen, but this is **not evidence of superiority or formal noninferiority**. The conservative interval crosses 50%. A high generation count or passing an engineering screen cannot replace an actual strength trend.

Separately, the [controlled V8 soft-prior ablation](soft-timing-v8-2026-09-26.md) reduced observed provably missed conditions from 180 to 115 in its bounded comparison, scoring 51.875% with a wide interval. The [V9 public-collection fixture](reroll-public-response-v9-2026-09-27.md) proves that added public inputs preserve the old prefix/private partition invariance and acquire nonzero influence after training. It does not prove correct reroll-denial valuation. These are different experiments with different conclusions.

The base campaign evaluates every 600 seconds against its current champion using 256 seat-swapped games. Promotion requires the lower conservative 95% bound above .5. With 128 independent seed pairs the Hoeffding margin is about .12004, so roughly 62% is needed; lack of promotion can hide smaller gains. Archive score also changes as opponents change. Preserve durable immutable checkpoints and compare a fixed anchor plus several dated opponents, with balanced hero matchups and a separate natural-draft panel. Report intervals and censor counts. Retain a final held-out panel rather than repeatedly optimizing against one evaluation seed set. [Evaluation](../learning_eval.py), [campaign](../train_campaign.py).

## CPU validation and priorities

Existing five episode-credit contracts and six V9 readiness contracts passed: **11/11**. A new independent [audit test file](../experiments/test_v9_reward_exploration_audit.py) passed **5/5** in 8.33 seconds, including runtime installation. It checks installed V9 actor mixed-likelihood parity, positive/negative PPO advantage direction, exact relic/destiny mass, 80,000 categorical draws, and potential-return telescoping. Synthetic contracts test mathematics and wiring; they do not demonstrate game strength.

Reproduction, CPU only:

```sh
CUDA_VISIBLE_DEVICES='' OMP_NUM_THREADS=1 MKL_NUM_THREADS=1 \
PYTHONPATH=Tools/TrainingPreflight:Tools/TrainingPreflight/experiments \
/home/lva/.venvs/shards-preflight/bin/python -m unittest \
test_v9_reward_exploration_audit -v
```

Recommended order: establish a durable cross-checkpoint strength series; measure opportunity-conditioned errors near important thresholds and choices; test explicit public future-relic features and selective public consequence features; only then test changes to exploration, credit estimation, or search against the same compute allowance. Keep terminal victory as the reference objective. Do not adopt an unmeasured reward bonus or claim that all strategic errors have been eliminated.
