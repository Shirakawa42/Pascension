# Whole-training audit, 27 September 2026

The current training is numerically active, but numerical health and millions of games are not proof of strategic progress. The audit found remaining public-information aliases, a limited card representation, and inadequate resolution in the old routine champion test. A separate checkpoint-retention and strength-evidence service now addresses the measurement problem. It does not silently change the learner or promise that each new checkpoint is better.

## Resources, relics and planning

Own health, gems, power and mastery are encoded and pass exact-value checks. Mastery10 readiness is explicit. Accepted engine actions correctly move from mastery9 through Focus to mastery10 and expose the hero's legal remaining relics. The legality mask prevents recruiting a wrong or unavailable relic.

However, the unrecruited relic pool is not explicitly visible before eligibility. The network has hero identity and must learn the hero-to-relic mapping, the threshold payoff and the value of investing in mastery from experience. That is substantially weaker than explicitly providing the future options. Likewise, the policy performs one feed-forward evaluation, then samples an action. It does not simulate an action, the rest of its turn or the opponent's reply. The learned value estimates eventual outcome from experience, which gives a full-game training horizon but **zero explicit search depth**. [Reward/planning source audit](v9-reward-exploration-audit-2026-09-27.md).

## Public information and card representation

V9 correctly includes the exact public opponent permanent composition while hiding its hand/draw split and draw order. That fixed a demonstrated V8 omission. It does not make the full observation sufficient for all mechanics. The new audit finds different meaningful states with identical complete policy inputs, including the remaining future relic pool, public banished identities, queued monster identities/effects, per-card temporary/cleanup associations and effect-continuation stage. These require a new version and engine-backed privacy/behavior tests; additional games cannot distinguish identical inputs. [Detailed information audit](v9-whole-information-audit-2026-09-27.md).

Cards are represented by learned ID embeddings, counts in zones, selected numeric properties (such as faction, type, cost, defense and shield), public card/entity states, choice-menu summaries, and selected hand-coded readiness predicates. The policy does not receive a complete effect program or card rules text. A constructed effect-only change from Crystal granting1gem to4gems produces identical inputs. Accordingly, no effect-level generalization guarantee exists. A numerical cost change can appear in the candidate attributes, but the policy still needs evaluation and usually fine-tuning after rebalance; an altered effect may otherwise remain invisible until it resolves. Current checkpoint identities deliberately pin source, rules and catalog instead of silently loading an old policy against changed rules.

## Exploration and reward

The policy already samples on every action, rather than always choosing its highest score. Entropy regularization is.01. When available, explicit mixtures assign6%probability mass to legal normal relic recruitments and2%to legal normal destiny choices. Initial heroes are uniform distinct pairs in75%of games;25%use natural drafts. Historical opponents supplement current-policy self-play. This is meaningful exploration, but not proof that rare multi-action strategies receive enough exposure.

An arbitrary random override every X actions must be included in the actor's actual logged distribution and PPO likelihood; otherwise the update is inconsistent. Coherent strategic exploration or targeted rare-choice coverage is a better experiment than inserting unrecorded noise. No untested exploration change was adopted in this audit.

Rewards are final win+1/loss−1/draw0 from the actual deciding player's perspective. The CPU tests found no reversed-seat sign or mixture-probability bug. Undiscounted complete-game credit is a legitimate objective, and learned value can assign statistical future value. It is also noisy: unnecessary moves in wins share positive credit, and useful moves in losses can share negative credit. Blind rewards for mastery, damage or expensive cards can change the objective and teach losing behavior. Potential-based shaping with complete Monte Carlo returns can telescope into a baseline shift, so adding dense-looking rewards alone is not an established improvement. [Tests and primary research sources](v9-reward-exploration-audit-2026-09-27.md).

## Does training improve?

A prior controlled4096game V8 comparison scored54.39%against champion1827, with a conservative51.39–57.40%bound. That demonstrates improvement against that specific older opponent under those evaluation rules. It does not prove that every subsequent update is useful. The V9 pilot scored46.09%against its starting weights with the timing prior disabled, with a38.50–53.69%bound. Passing its engineering floor of45%was not proof of improvement or noninferiority.

The old routine test used256games,128paired seeds. Its conservative95%margin was12.0percentagepoints: champion promotion needed roughly62%score. Therefore a stale champion can coexist with a modest gain, stagnation or regression. The old checks cannot distinguish those reliably.

A new direct4096game comparison freezes generation8965 against V9's starting learner8811, uses fresh seat-swapped seeds, and names both learner roles explicitly. It scored **55.0415%**, with a conservative **52.0405–58.0425%** pointwise95%bound, zero censors and4096complete games. That is evidence of recent improvement against this specific preceding learner. The comparison covers39,424additional training games. Under the progress watch’s stricter first-test alpha=.025, its bound is51.7707–58.3123%, still above50%. Generation8965 is therefore retained as the current best demonstrated checkpoint and next comparison target. The current learner can be newer; it is not automatically declared better. [Raw comparison](/home/lva/.local/share/shards-training/2026-09-26/evaluations/progress-v9-audit-vs-start-learner.json). It changes no optimizer or training state. One mistakenly selected champion-role probe was stopped, marked aborted and retained separately; it supplies no result or evidence.

The user's20human games averaging11.5rounds cannot be ranked against roughly12.4AI rounds from different opponents and conditions. Shorter games may mean better offense, weaker defense, or an opponent losing quickly. Both players getting stronger may lengthen games. The frozen statistics' round counter is also the latest pre-action observation, not necessarily a final engine-round record. Match outcomes against controlled opponents, hero/seat balance and tactical tests are appropriate strength measurements; training should not optimize short games as a substitute for wins.

## Changes deployed during this audit

- Existing crash-recovery `latest.soicp` writes remain every60seconds.
- A separate watcher retains immutable full checkpoints every300seconds, including during evaluation. It does not delete older snapshots.
- Every100,000training games, it freezes the current learner and runs4096seat-swapped games against the previous evaluated learner, plus the retained best if different. Both comparator roles are explicitly `learner`.
- A best reference changes only after its lower bound exceeds50%. Regressed and inconclusive results remain distinct. Fresh reserved seeds and a summable5%error budget over the watch's repeated comparisons guard against promoting a lucky checkpoint from repeated testing.
- Promotion affects only the evaluation best reference, not the running optimizer, training champion or policy. The current learner and the best measured candidate remain separate.
- The dashboard includes a “Checkpoint strength evidence” section with policy generations, scores, uncertainty and verdicts. Frozen champion/anchor statistics continue independently.

The sidecar uses one CPU inference thread and one host worker, nice10, no CUDA and no training-budget writes. Concurrent CPU work can still reduce training throughput; these checks are not free. Jobs have bounded timeouts, no automatic retry after failure, strict checkpoint identity/checksum loading and an explicit invalid-result state. The watch stops after the trainer is finished; it does not control training automatically.

Validation: four inference-independent evidence/role contracts, a two-real-game end-to-end retention/learner-comparison/no-unproven-promotion check,23monitor tests, and parsed dashboard JavaScript. [Integration evidence](progress-watch-integration-2026-09-27.json), [watch implementation](../experiments/progress_watch.py).

## Priority after measurement

Expose the demonstrated missing public state and future relic options before increasing training duration or adopting reward bonuses. Then test each proposed representation, exploration, credit or selective-lookahead change against the same fixed opponents and comparable compute, including balanced-hero panels. Rebalance transfer requires new rules identities and fresh evaluation. This audit does not establish optimal play or absence of every engine/training bug.

## Additional engine finding

The accepted Deadly Recruits (Mastery20) → Reactor → decline permanent keep → three-gem mode → End Turn sequence returns Reactor to the center deck with its banish-at-cleanup flag still attached. That is a reproducible persistent-state anomaly. Its cleanup precedence needs an explicit rule and regression test; this audit has **not** patched the frozen running engine. It is listed alongside the five remaining information aliases, so this report is not an “all clear.” [Reproduction and source](v9-whole-information-audit-2026-09-27.md#confirmed-reactor-cleanup-anomaly).
