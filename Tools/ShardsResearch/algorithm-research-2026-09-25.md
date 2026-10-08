# Fresh algorithm investigation: Shards of Infinity, all DLCs, 1v1

Date: 2026-09-25. Scope: strongest practical agents and credible balance evidence using this computer's RTX 5090, with 12 hours for training. The user confirmed that final evaluation and balance analysis may run longer and do not consume that training allocation. This is a research/design recommendation, not a trained result or a throughput benchmark. No previous game AI, checkpoints, training implementation, deleted files, or repository history were used. Repository facts below come from current rules-engine and observation contracts; algorithm evidence comes from linked primary papers and author implementations.

## Recommendation

Build a compact, legal-action-masked, candidate-scoring PPO policy on a headless simulator, with terminal win/loss rewards and a small opponent archive. Preserve strategic choices, but compress observations into card counts, public board entities, current choices, and turn-state features. Do not begin with full-game tree search, a large Transformer, or a GPU rewrite of every rule.

Run a short, equal-wall-time challenger using DouZero-style Monte Carlo action-value learning if the common simulator/encoder infrastructure already makes this cheap. Choose using held-out matchup performance and learning speed, not updates/second alone. PPO is the recommended default if the pilot is inconclusive. A later compact recurrent policy or local search audit can test what the count-based policy misses.

The strongest policy found is not necessarily a faithful picture of the whole meta. Preserve multiple strategically different policies, challenge the leading policy, and collect controlled balance experiments. Twelve hours cannot establish perfect play, an exhaustive meta, or exact exploitability for this game. The useful deliverable is a demonstrably improving policy population with measured limitations and reproducible data.

## What primary research actually supports

| Approach | Evidence | Implication for this budget — a proposal, not a published Shards result |
|---|---|---|
| Masked PPO / policy gradient | The final ICLR 2026 comparison tested five imperfect-information games over more than 7,000 runs. Its fictitious-play, double-oracle and CFR-based deep approaches did not outperform generic policy-gradient methods. Its games were Liar's Dice and dark/phantom board games, not Shards. [Paper](https://www.mit.edu/~gfarina/2026/iclr26_reevaluating/iclr26_reevaluating.pdf), [author code](https://github.com/nathanlct/IIG-RL-Benchmark). | Strong practical first choice. A game-theoretically sophisticated algorithm is not automatically more effective at finite compute. Tune entropy and evaluate a population; do not infer convergence from PPO's name. |
| Deep Monte Carlo Q, DouZero style | DouZero combined terminal-return regression, action encoding and parallel actors. Its reported success used four GPUs and days of training. [ICML paper](https://proceedings.mlr.press/v139/zha21a.html), [author implementation](https://github.com/kwai/DouZero). | Credible compact challenger with simple targets and no bootstrapping. Its timing is not evidence that a 5090 reaches comparable strength in 12 hours, and greedy value policies can lack useful strategic randomization. |
| Neural fictitious self-play (NFSP) | NFSP learns an approximate best response plus an average strategy; it approached equilibrium in Leduc and strong performance in limit hold'em. [Original paper](https://arxiv.org/abs/1603.01121). | Additional networks, supervised average-policy learning, buffers and slow response iteration are a substantial integration cost. Keep as a later comparison if clear cycling survives simple remedies. |
| CFR / Deep CFR | Deep CFR approximates counterfactual regrets with neural networks and samples game-tree traversals. Its published strong results concern poker. [Paper](https://arxiv.org/abs/1811.00164). | Large private deck configurations, long order-sensitive turns and many contingent choices make a new full-game solver expensive. Tiny subgames can still be excellent validation environments. |
| ReBeL | ReBeL combines search with learned values/policies over public belief states; it has a two-player zero-sum convergence result under its theoretical conditions. The released implementation covers Liar's Dice. [Paper](https://arxiv.org/abs/2007.13544), [author repository](https://github.com/facebookresearch/rebel). | Maintaining beliefs over unknown hands/deck orders and solving subgames is a large new project. Do not transfer a poker convergence claim to an approximate Shards implementation. |
| R-NaD / DeepNash | DeepNash achieved expert Stratego performance with model-free self-play, using regularized Nash dynamics to address cycling. [Paper](https://arxiv.org/abs/2206.15378). | Worth knowing about; not a justified first-run replacement for a measured compact PPO implementation. Neural finite-budget behavior does not inherit a blanket exact-equilibrium guarantee. |
| ISMCTS / determinization | ISMCTS searches information sets and was developed to address hidden-information games. [Original paper](https://eprints.whiterose.ac.uk/id/eprint/75048/). | Potential audit opponent or late inference improvement. Perfect-information rollouts on the true hidden state would cheat; sampled-world search can suffer strategy fusion and belief/model errors. |
| VRPO / Q-boosting | A May 2026 preprint replaces sampled multi-step backups with action-value expectations to reduce stochastic-action variance and reports results including Dou Dizhu and poker. [Preprint](https://arxiv.org/abs/2605.19235). | Interesting follow-up if critic noise dominates. A centralized action-value critic and expectations over candidates add implementation and compute demands. Do not make this recent result the first run's dependency. |

A separate May 2026 Big 2 preprint compares PPO with Monte Carlo Q, SARSA and Q-learning under a shared interface and finds PPO strongest in its limited-compute experiment. It also finds current-policy self-play stronger than its checkpoint curriculum. This is useful counterevidence to both “Monte Carlo is always faster” and “a league always helps”; it is not decisive for a different game. [Author preprint](https://arxiv.org/abs/2605.28863).

Invalid-action masking has direct policy-gradient justification and empirical support. Store the rollout's mask/candidate ordering and use the same masked distribution to compute old/new action probabilities. Merely rejecting invalid actions wastes samples and can make training targets inconsistent. [Masking paper](https://arxiv.org/abs/2006.14171).

## Current engine facts that constrain the algorithm

These are properties of the implemented fan game; official tabletop equivalence is a separate rules audit.

- The engine is pure C# with one pending input. Normal actions and decision answers enter through `Submit`. Training should drive that contract headlessly, without the Unity presentation/network loop. [Engine](../../Assets/Scripts/Shards/Engine/ShardsEngine.cs), [project map](../../.claude/skills/project-map/SKILL.md).
- `LegalActions` includes individual card plays, buy versus fast-play, focus, hero ability, ready champion/destiny activations, Ingeminex attacks, destiny/relic acquisition, row rerolls and end turn. Champions are attacked through the end-turn damage split, not the similarly named mid-turn action class. [Legal action generator](../../Assets/Scripts/Shards/Engine/ShardsEngine.cs).
- The decision owner can differ from the turn player: shield reveals and other effects request input from the defender. Several consecutive actions can belong to the same player; extra turns also exist. [Engine](../../Assets/Scripts/Shards/Engine/ShardsEngine.cs), [Duel effects](../../Assets/Scripts/Shards/Engine/ShardsDuelEffects.cs).
- The viewer snapshot includes the viewer's hand and a zone-blind `FullDeck` composition, sorted without exposing deck order. Opponent hand arrays are redacted. Public zones include discards, champions, played cards, destinies, market and banished cards. However, redaction of these arrays does not establish that every field is safe: the original investigation reproduced a hidden-information leak through opponent `ConditionGlowIds`. Conditional probes now run only for the viewer. [Snapshot contract](../../Assets/Scripts/Shards/Engine/ShardsSnapshot.cs), [regression fixture](EngineProbe/ObservationAudit.cs), [original pre-fix audit result](observation-audit-2026-09-25.json), [corrected audit result](observation-audit-fixed-2026-09-25.json).
- Damage assignment is encoded as one repeated option ID per power point; selection decisions ordinarily forbid duplicate IDs. `Disabled` options are observable but cannot be selected. An AI adapter cannot treat all decisions as “choose one option.” [Validation and split flow](../../Assets/Scripts/Shards/Engine/ShardsEngine.cs), [decision types](../../Assets/Scripts/Core/DecisionRequest.cs).
- Testudo protection subtracts revealed shields from each champion hit. Shield-reduced damage can leave a taunt champion alive, blocking other hits and face damage. Over-assigning above a champion's defense is therefore strategically necessary. [ResolveDefenderDamage](../../Assets/Scripts/Shards/Engine/ShardsEngine.cs).

The renderer snapshot is not a complete training observation, and each field needs a visibility audit. In the original pre-fix audit, swapping an opponent's private hand/deck changed their ready Aegis Archivist's glow from false to true while all other serialized snapshot fields remained identical. The user confirmed this is forbidden information leakage. The synthetic boundary fixture is a regression check, not a natural-play frequency estimate. Derive actor predicates from authorized inputs and run metamorphic hidden-state permutation tests after the fix as well. Selected verified snapshot fields can still serve as regression references.

A compact encoder should also explicitly supply observable state missing from presentation snapshots: fast-play status, relevant turn flags/counters, effect source and pending context. `FullDeck` excludes a fast-played loan while Allegiance includes it; the user confirmed this is intended behavior, not a bug. Own-deck count alone therefore does not reproduce the predicate. Combine observable play-zone status and rules semantics; do not silently substitute a tabletop interpretation. Publicly inferable features may be maintained from filtered events. Raw state access must not reveal the opponent's hand, shuffle order, future market, RNG state or true hidden shield total. [Audit code](EngineProbe/ObservationAudit.cs), [original audit result](observation-audit-2026-09-25.json).

## Information reduction: what to omit first

Reducing input size and reducing strategy space are different operations. A bag of card IDs compresses an unordered hand without losing its composition. Dropping focus, reroll or damage choices removes strategies. Begin with the former.

### Proposed observation v1

1. Normalize seats as self/opponent, while retaining starting seat, turn owner, input owner, draft order and round. Include current health, mastery, gems, power and applicable counters/flags.
2. Encode self hand and own total deck composition as per-definition counts; include public self/opponent discard and play counts. Keep ready/exhausted champion and destiny entities separately because identity and readiness affect actions.
3. Encode row slots, their effective prices, buy/fast-play availability, reroll price, available destinies, available relics, active Ingeminex and their relevant public attributes.
4. Include mastery threshold indicators and remaining distance to important thresholds, faction counts relevant to Unify/Dominion/Allegiance, play/banish counts, once-per-turn/game usage and pending monster/extra-turn information wherever observable. Include the source and context of the pending choice.
5. Maintain compact public memory: observed purchases/fast-plays/removals, known opponent cards, recently revealed shields and last observed opponent hand information with an uncertainty/age tag. Exact hidden hand/deck separation cannot be inferred merely because overall ownership is known.
6. Encode pending options with card definition, source/target, numeric amount, owner, disabled/required status and min/max/ordering constraints. Preserve visible but disabled options as observations.

For stable card definitions, use a learned ID embedding plus static numeric attributes and mechanic flags, not natural-language card text. Include cost/defense/faction/type and designed semantic features. ID embeddings distinguish unique behavior; numeric/mechanic features help transfer learning across cards. These features are inputs, not rewards. Do not let raw globally allocated instance IDs become predictive inputs: they identify engine objects, can leak creation chronology, and do not describe card strategy. Map local references to ephemeral candidate positions; use definition IDs and explicit public history instead.

Start with a 2–3 layer MLP or pooled entity encoder, width 256 or 512, small candidate encoder and shared scalar value head. These sizes are initial experiment settings, not capacity findings. A count matrix is cheap enough that omitting entire public zones may save little compute while damaging purchase valuation. Bucket/pad variable candidate batches and score state once per decision.

### Reasonable first omissions

- Full ordered event history, exact posterior beliefs over private hands, and an opponent-style recurrent model.
- Card artwork, descriptions, cosmetic UI fields and arbitrary object identifiers.
- Long remembered sequences of returned market cards or historical revealed deck order, provided known current top-deck information from effects such as Scry remains represented when relevant.

These omissions create a reactive approximate-information policy. State this explicitly; do not call the summary sufficient without proof. Compare it later with a small GRU that consumes each player's permitted events, including events while the opponent acts. A GRU updated only when its owner is asked for input misses intervening information. Burn-in, episode reset and per-seat hidden-state ownership become additional correctness requirements.

### Keep choices even when they seem routine

- Do not force all hand cards before buying, focus, hero abilities or mastery gains. Threshold timing, faction effects, draws, banishes and top-deck effects make order matter.
- Do not force all champion activations immediately. Costs, doubling effects, healing conversion and thresholds can change their value.
- Do not force relic/destiny acquisition as soon as available, eliminate fast-play, or fix the hero draft. The resulting data would describe a restricted game.
- Do not hardcode “reveal every shield.” More prevention can alter damage-trigger effects; public disclosure itself may have information value. At minimum, validate any compressed defense menu against unrestricted subset choices.

Safe initial reductions are forced single-option choices and grouping genuinely equivalent duplicate instances. Any broader reduction needs an equivalence argument or an unrestricted comparison, especially in all-DLC combinations. Measure distinct strategic decisions per game, not just raw `Submit` count.

## Action representation and candidate explosion

For ordinary actions, score legal candidates with `logit(o,a) = f(encode(o), encode(a))`, then a masked categorical policy. Descriptors distinguish buy/fast-play/reroll, target/source, card ID, effective cost and context. Never compute descriptors by simulating the actual hidden next card or reading future RNG outcomes.

For multi-selection and ordered decisions, use autoregressive subchoices with a partial-answer observation. A canonical option order can remove permutation duplicates for unordered subsets. Offer “finish” only after minimum requirements are met; retain selected count and remaining capacity. Ordered choices retain sequence. The wrapper holds the partial answer and submits exactly one engine decision once complete. These are zero-time policy substeps with discount 1, not extra game turns.

For the damage split, avoid constructing every integer partition. Iterate target entities and select an amount `0..remainingPower`; submit repeated IDs only at the boundary. In 1v1, the leftover can be assigned to the opponent when allowed; preserve the engine's taunt/waste semantics. An amount head conditioned on target and remaining budget avoids a full target × amount Cartesian scorer. It must be able to assign more than defense under Testudo. The engine itself bypasses its normal split for overwhelming power, but the wrapper should follow actual decisions, not invent a cap.

A smaller list of strategically meaningful amount breakpoints may eventually help, but “exact lethal only” is invalid here. Any breakpoint policy is a declared action abstraction. Test it against full amounts on sampled late-game states and compare card/matchup rankings. Do not silently truncate crowded hands, champions, Ingeminex, choices or card counts to make a fixed tensor fit; provide overflow buckets or a tested slow path and measure their frequency.

Preserve valid end turn even when attractive actions remain. Excluding voluntary concession is acceptable for training/evaluation if games are played to rule termination and administrative aborts are recorded separately; concession can otherwise contaminate game-length and card-use statistics. Do not disguise timeouts as losses or draws.

## Learning contract and initial parameters

Use outcome utility +1 win, −1 loss, 0 genuine rule-defined draw, from each player's perspective. Begin with `gamma = 1` for the episodic outcome objective. Do not assume every arbitrary policy necessarily terminates: repeated weak/end-turn behavior needs administrative step/turn guards, separately reported truncations and correctly handled bootstraps, not fabricated draws. Arbitrarily discounting every micro-action rewards short action sequences and changes the strategic objective. GAE's trace parameter can still be below 1; it is not the discount factor.

Maintain a trajectory per learning seat. Its transition goes from that seat's decision to its next decision (or terminal), including intervening environment/opponent actions. Accumulate that seat's rewards correctly and bootstrap only its next value. A defender's shield choice belongs to the defender, not `TurnPlayerIndex`. Never negate every step's return as if players strictly alternate. A fixed-seat two-value critic is another valid design, but keep one perspective convention throughout. Game-over resets and administrative rollout cuts need separate masks.

When both seats use the current policy, both seats can contribute training examples. A frozen opponent's decisions do not become on-policy learner examples. Store acting seat, behavior policy version, old log probability, candidate encoding/mask, reward, value and termination status. Pin frozen opponents for an entire match. With recurrent policies, store required hidden states/burn-in boundaries too.

Proposed starting configuration, to be adjusted by a timed pilot:

| Item | Initial setting / measurement |
|---|---|
| Optimizer | Adam, learning rate around `3e-4`; compare lower rate if KL spikes |
| PPO clipping / GAE | clip 0.1–0.2, lambda 0.95; terminal MC target as an ablation |
| Update volume | 2–4 epochs, minibatches initially 2,048–8,192; choose by whole-loop throughput and stability |
| Entropy | Preserve meaningful stochasticity; small sweep e.g. 0.01 versus 0.05 on reward scale ±1; report entropy by decision family and action count |
| Batch size | Start around 65k learning decisions per rollout only if this yields frequent updates; otherwise reduce |
| Precision | Mixed-precision dense layers; logits, masking, log probabilities, returns and reductions in FP32 |
| Stop/diagnose | NaN, invalid answers, growing KL, collapsed entropy, high abort rate, broken seat symmetry, non-improving held-out play |

These ranges are engineering starting points, not settings established for Shards. The original PPO contribution supports repeated minibatch updates under a clipped surrogate, not these particular values. [PPO paper](https://arxiv.org/abs/1707.06347).

Do not initially reward health gained, mastery, gems, acquisitions, kills or card price. Such rewards can make the learner prefer the designer's assumed strategy and then “discover” that same strategy in the balance report. Auxiliary prediction losses for public next-turn resources or terminal outcome can be tested without changing outcome utility. If shaping is later necessary, potential-based shaping with correct terminal handling has a principled basis, but its assumptions and implementation still require care. [Reward shaping paper](https://ai.stanford.edu/~ang/papers/shaping-icml99.pdf).

Start with an observation-based critic. A privileged critic may improve training without exposing hidden information to the deployed actor, but a plain state-only critic under partial observability can produce biased estimates; a history-and-state critic is the principled direction. Keep actor and critic input plumbing isolated, and verify logits are invariant to hidden-state permutations. [Asymmetric actor-critic analysis](https://arxiv.org/abs/2105.11674).

For the Monte Carlo challenger, regress each selected `Q(o,a)` toward that seat's terminal outcome and sample exploration actions from legal candidates. Reuse the same observation/action family and common seeds. Terminal targets simplify ownership and avoid bootstrapping bugs but have high variance and delayed feedback. A stale giant replay buffer mixes old opponent distributions; constrain age or explicitly model it. Compare terminal-regression training against PPO at equal wall time and also report completed games/decisions. The actor for a full autoregressive answer needs consistent joint action-value design; a naive Q score on unfinished answer fragments is not a drop-in equivalent.

## Self-play population and deployment

Begin largely with current-policy self-play, retain snapshots, and introduce historical opponents only as useful diversity. A provisional mixture after initial learning is 70% current, 20% recent/diverse snapshots and 10% currently difficult archived opponents. Those numbers are hypotheses, not literature constants. Keep approximately 8–16 active archive members and benchmark the impact of opponent batching on inference cost. Exclude demonstrably obsolete opponents from most training while retaining them for regression tests.

Use one main learner rather than many equal-size independent learners competing for a single GPU. Later, allocate brief challenger training against a frozen leader or archive mixture. Preserve a challenger that beats the leader even if its average league score is lower. Snapshot diversity from one lineage is not independent-seed replication.

This is a lightweight population heuristic, not full PSRO. PSRO's key motivation is that independent learners can overfit training partners and that approximate responses to policy mixtures can reveal missing strategies. [Original PSRO paper](https://arxiv.org/abs/1711.00832).

For deployment/evaluation, compare stochastic sampling, reduced-temperature sampling and greedy play; do not assume argmax is strongest against an adversary. If a policy mixture is chosen, sample one policy at the beginning of a game and keep it for that game. Averaging neural weights or randomly swapping policies each action does not implement the same mixture. Keep hero draft inside a deployed policy's behavior.

## The 12-hour training allocation

The target is this computer. Complete implementation, rule verification, CPU/GPU profiling, compilation and numerical/visibility preflight before the training clock. The 12 hours cover learning pilots, the main continuation and targeted challenger learning. Preserve usable pilot checkpoints rather than discarding their learning. Final frozen evaluation and balance experiments run afterward, for as long as useful. Any preflight optimization that actually trains reusable policy weights must be counted as training; correctness checks are not an extra unreported training budget.

| Elapsed | Work | Decision / output |
|---|---|---|
| 0:00–1:00 | At most two short, matched learning pilots, e.g. PPO versus already implemented DMC, or small versus richer PPO encoder | Retain usable checkpoints; a short pilot rejects clear failures but does not prove long-run superiority |
| 1:00–11:00 | Continue the best viable learner; occasional brief online evaluation and archive snapshots | Main 10-hour continuation; record strategy and coverage evolution |
| 11:00–12:00 | One or two targeted challenger/exploiter continuations against frozen opponents | Test an obvious blind spot and retain useful challenger policies |

The training blocks sum to 12 hours. Count brief online evaluations, checkpoint writes and scheduling pauses conservatively within these timed blocks when they share the same training wall clock. Keep large frozen tournaments, strategic coverage audits and decision/rule interventions outside the training clock. Their elapsed time and compute should still be reported separately.

If building DMC would delay a sound PPO run, use the pilot for PPO entropy/representation choices instead. If the simulator cannot feed the accelerator, fix measured data-path bottlenecks before expanding the model merely to increase GPU utilization. Report preparation, training and final evaluation elapsed time separately; additional evaluation must not become unreported gradient updates or opponent-training time.

Planning arithmetic only: at rate `R` actual training decisions/second averaged across the complete main block, including its online-evaluation/checkpoint pauses, the main 10 hours supply `36,000 × R` decisions. If `R` is measured only while learning is active, multiply instead by measured active training seconds. Divide by measured decisions per completed game to estimate games. Do not replace `R` with a neural-network-only benchmark. Report learner decisions, all engine decisions, games, and gradient examples separately. Reusing a batch for four epochs quadruples gradient examples, not independent game experience.

## Minimal ablations with highest value

The training allocation cannot afford a broad learning grid. Implement toggles ahead of time and prioritize; frozen-policy evaluation ablations can run longer afterward:

1. **Input compression:** own deck + opponent public counts versus the same policy without those counts. Compare equal time and learning curves; do not assume smaller wins.
2. **Action preservation:** unrestricted ordering/split amounts versus any proposed automatic play or breakpoint rules. Audit not only win rate but which cards/heroes change rank.
3. **Opponent sampling:** current-heavy versus a larger archive share, especially if learning stalls or cyclic matchups emerge.
4. **Entropy:** moderate versus near-zero; check gameplay competence and policy exploitability probes, not entropy alone.
5. **Memory or search:** only after a competent feedforward model exists, compare a small GRU or public-information sampled search on hard decisions.

A search-assisted agent should be evaluated against the policy at matched inference latency as well as unrestricted analysis cost. Use information-consistent hidden-world samples and a policy that sees only that world's legal observations; do not expose the actual world to leaf decisions. Search benefits must survive fresh seeds. Cloning/replay at a pending decision must preserve the effect continuation, not just visible state; the current iterator-based engine makes this an engineering issue to verify before relying on branching.

## Evaluation: demonstrate strength before interpreting balance

Use separate seed sets for training, online model selection and final audit. Track exact engine/content hashes, DLC/rules flags, policy weights, observation/action versions and seeds. Freeze policies during final data collection. Retain event/action replays for anomalous games and a representative sample of normal games; use compact aggregate columns for large-scale analysis.

Build a payoff matrix over several recent/diverse checkpoints and challengers. Include freshly constructed simple sanity opponents where helpful; no old game AI is required. Report seat-swapped results and confidence intervals. Elo can summarize trends but cannot represent all cyclic matchups. A restricted-policy Nash mixture or AlphaRank can help describe the observed population, not prove a game-wide equilibrium. [AlphaRank paper](https://arxiv.org/abs/1903.01373).

A learned best response that successfully exploits the leader demonstrates a weakness. Failure to find an exploit only shows that this search/training procedure failed; it is not an upper bound on true exploitability. Exact Shards exploitability is not available merely because an evaluator outputs a number with that name.

Measure these separately:

- Overall outcome score, win/draw/loss/abort rates, starting-seat advantage and legal hero-pair outcomes.
- Natural hero draft distribution and conditional outcomes, versus controlled legal hero assignments. Draft picks depend on observed board and pick order, so their raw win rates are selected samples.
- Game lengths, mastery victory frequency, damage versus healing patterns, champion survival/exhaustion, reroll spending, destiny/relic choice, Ingeminex exposure and rewards.
- Tactical fixture performance: obvious lethal, preventing lethal, Testudo/taunt over-assignment, focus-before-threshold-card, paid activation affordability, meaningful banish, buy versus fast-play, extra-turn and forced-discard ownership.
- Coverage: how often each mechanic/card is actually seen, affordable, selected, played and decisive. A never-explored combo is unknown, not weak.

Use paired seeds and seat swaps to reduce nuisance variation, but different policies consume RNG differently, so the same starting seed does not mean identical future draws. Treat a paired block as the resampling unit. In a clone/intervention study, record exactly which randomness is shared. Do not modify canonical game randomness casually to make comparisons look cleaner.

For scale, independent Bernoulli outcomes near 50% have approximate 95% half-width `0.98 / sqrt(n)`: about 2 percentage points at 2,401 games and 1 point at 9,604 games, per comparison. These are rough precision calculations, not detection-power guarantees or valid sample counts for correlated/paired data. For win/draw/loss scores and pairs, bootstrap independent seed blocks; for simple independent win proportions, use an appropriate binomial interval. [NIST interval reference](https://www.itl.nist.gov/div898/handbook/prc/section2/prc241.htm).

Training-seed uncertainty is different from match-seed uncertainty: millions of evaluation games do not show that one training run is reproducibly good. State the single-run limitation and use independent short restarts where affordable. RL benchmark research documents how point estimates from few runs can mislead. [Statistical evaluation paper](https://arxiv.org/abs/2108.13264).

## Balance data: descriptive signals, interventions and adaptation

Raw “win rate when a card is bought” is confounded: rich or already-winning players buy expensive cards; long losing games may expose more cards; a late-game finisher is selected in different states from an early economy card. The distinction between association and intervention is fundamental, even with unlimited self-play data. [Hernán and Robins, causal inference text](https://www.hsph.harvard.edu/miguel-hernan/wp-content/uploads/sites/1268/2024/04/hernanrobins_WhatIf_26apr24.pdf).

Recommended descriptive records, all conditioned on frozen policy version and rules version:

| Unit | Fields / useful denominator |
|---|---|
| Market exposure | Card appears, player can afford it, available alternatives, hand/resources/deck summary, hero, turn/mastery, current win-value estimate |
| Choice | Buy/fast-play/reroll/pass, policy probability, all candidate IDs and legal availability; measure pick rate per eligible opportunity |
| Card lifecycle | Time acquired, first played, number of draws/plays/exhaustions, kept/banished, turns remaining, match result |
| Interaction | Faction/deck composition, relic/destiny, mastery band, ready champion context, conditional effect activation |
| Outcome | Winner/draw/abort reason, starting seat, draft order, game length and termination mechanism |

Do not treat millions of turns from thousands of games as millions of independent outcomes. Cluster uncertainty at least by game/seed block; stratify rare cards and low-frequency combinations before ranking them. Apply held-out confirmation or false-discovery control when testing many cards. Report sample counts and uncertainty beside every ranking.

For promising hypotheses, use three distinct experiments:

1. **Decision intervention:** At eligible information states, randomize between buying a candidate and a specified legal alternative, then continue with frozen policies. This estimates the value of that choice under those continuation policies and sampled states. Do not force a purchase without paying cost or respecting legality. In offline branching, resample hidden states consistently with the player's information rather than selecting branches because of private future knowledge.
2. **Rules intervention:** Compare the current rules with one explicit card/hero parameter change using matched seed blocks, seats and a policy population. This estimates immediate impact under frozen behavior. Keep each revision separately versioned and preserve legal deck composition and replacement rules.
3. **Adaptation:** Fine-tune or retrain agents on the candidate change, then compare populations. A fixed agent can make a nerf look harmless because it has not learned a replacement strategy; a buff can look weak because it has not learned to exploit it.

The last experiment costs additional training compute. Large frozen-policy tournaments, coverage analysis and many local interventions may run after the 12-hour training allocation; extra match volume does not make their policies adapted to a new rule revision. Produce a prioritized hypothesis list and verified intervention results, not a claim that all cards are causally balanced. A subsequent adaptation run, potentially another 12-hour training cycle on a selected revision, must be budgeted separately from evaluation. Longer evaluation can reduce match-sampling uncertainty, while focused retraining and adversarial follow-up address strategic blind spots.

## Acceptance gates before a production run

- Every submitted action/answer is valid; all decision contexts are covered, including uncommon all-DLC chains and high entity counts.
- Deterministic replay reproduces outcomes/state checks where the engine supports them; no administrative abort is relabeled as a normal result.
- Holding the player's legal information fixed while permuting hidden cards leaves actor inputs, masks and logits unchanged. Include raw IDs, candidate order, glow-derived fields and cached feature paths in this audit.
- Seat exchange and input-owner tests verify rewards, values and trajectories, including multiple consecutive same-player actions, opponent decisions and extra turns.
- Any reduced action interface has a documented restriction and unrestricted audit; Testudo over-assignment is explicitly covered.
- A short run improves against fresh sanity opponents and passes tactical fixtures before consuming the full compute reservation.
- End-to-end profiles establish the bottleneck and sustainable rate. GPU occupancy is diagnostic; strongest verified policy per hour is the actual optimization target.

The resulting research direction is deliberately compact: preserve the game's strategic actions and cheap useful information, spend compute on diverse experience, and validate both playing strength and statistical interpretation before making balance changes.
