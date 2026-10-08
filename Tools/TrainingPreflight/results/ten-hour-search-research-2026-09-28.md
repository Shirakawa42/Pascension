# Improving Shards search within the ten-hour session

Research completed 2026-09-28. This is a ranked engineering recommendation, not evidence that any proposed variant is stronger. No model, experiment, or game installation was changed for this research.

## Diagnosis and recommendation

The strongest immediate opportunity is **better continuation decisions and useful-action coverage**, followed by a critic trained on fresh outcomes from that improved player. More hidden worlds and a longer copy of the same greedy continuation address neither defect. Keep the exact headless engine, batched GPU inference, public-information observations, and eight-core limit. A full replacement by AlphaZero, ReBeL, or a learned simulator is a poor use of this session's remaining time.

The local evidence is unusually clear: [the natural-game review](hybrid-game-review-2026-09-28.md) identified free healing and resource omissions; depth 64 did not repair them. In the inspected `HybridLookahead.cs`, only root options branch. Every subsequent decision uses `Greedy`, ending with the existing critic. `Options` normally selects leading policy groups, so actions the old policy dislikes can remain absent. The current worktree adds guards, a mixed resource plan, and fixed rollout styles; those address the observed mechanisms directly but still need fresh games and strength tests.

Suggested sequence: validate the current repair; add one bounded search improvement at a time; collect complete fresh games with the best validated player; fit an independently evaluated critic and then a policy; reserve substantial time for whole-game review and fresh paired confirmation.

**Follow-up evidence changes the immediate priority:** the main investigator reports approximately 166 games/s for pure policy play, versus 1–2 games/s for current search. That makes a bounded **terminal-rollout diagnostic** unusually attractive before investing in a general beam. Guards plus mixed plans repaired visible omissions but scored 158–162 in a 320-game pilot; a new review still found productive cards unused. Those results suggest probing both continuation quality and leaf-value bias rather than merely increasing root width.

## Selective complete-game rollouts: recommended next experiment

Rollout policy improvement evaluates candidate actions by following a base policy afterward. The original work gives conditions under which rollout improves the base heuristic; finite samples, approximate beliefs, and an adapting adversary mean those guarantees cannot simply be asserted here. [Bertsekas, Tsitsiklis and Wu](https://www.mit.edu/~jnt/Papers/J066-97-rollout.pdf). The author's later treatment discusses Monte Carlo implementation and reducing comparison variance by estimating action advantages. [Book, Sections 2.7.3–2.7.4](https://web.mit.edu/dimitrib/www/RLCOURSECOMPLETE.pdf).

1. Select 40–80 recorded visible positions: purchases, relics, destinies, saturated critic predictions, and the known resource/end-turn failures. Include both seats and every hero, rather than only positions where a desired answer is obvious.
2. Compare 2–4 materially distinct candidates, always retaining the current choice. For each candidate run 16–32 independent sampled public worlds to terminal with the **same frozen cheap, guarded behavioral policy for both seats**. No recursive planner calls. Start with 32 when diagnosing rankings; this can expose large discrepancies but cannot resolve a few percentage points reliably.
3. Couple candidates by using the same sampled initial world and random stream per replicate. Record paired outcome differences, not just unrelated win-rate estimates. Different actions can consume random events differently, so common streams do not guarantee strong correlation; measure the achieved variance reduction.
4. Compare terminal-return rankings with the critic's leaf rankings and current chosen action. Store state inputs, world seeds, complete rollout outcomes, censored counts, and estimated cost. A truncated run must remain censored or separately bootstrapped, never recorded as an invented loss/draw.
5. If terminal rollouts repair substantial mistakes at acceptable cost, test a **selective** live variant on relic/destiny menus and consequential purchase/fast-play/reroll decisions. Use a total per-decision budget; sample all candidates cheaply, then allocate 8→16→32 to close finalists. Do not require rollouts to relearn a proven free heal or a legal forced win. Do not stop by an uncorrected significance test after each sample and claim a reliable confidence level.

At the reported throughput, 4 candidates × 32 worlds × 40 positions is 5,120 terminal games, or roughly 31 seconds of idealized game throughput. This is **only an arithmetic lower-level estimate**, not a latency prediction: cloning, heterogeneous remaining lengths, shrinking batches, protocol overhead, and guarded rollout cost must be measured in the actual harness. The same 128-rollout budget at every ordinary action would be expensive and often pointless.

Purchases/relics/destinies are good initial targets because their value persists across future turns; a one-turn leaf may poorly distinguish them. But terminal evaluation still estimates the chosen base policies, **not** optimal or search-strength continuation. A base policy that cannot exploit a newly acquired combo may underrate that card. Use a second frozen competent rollout style as a sensitivity check on diagnostic positions, and promote only through actual search-versus-search games. Avoid independently taking the best policy in every hidden world.

A small beam and full rollouts solve different failures: the beam discovers an immediately useful sequence the rollout policy misses; terminal rollouts reduce dependence on a bad endpoint critic. The strongest practical candidate may use a short meaningful-choice beam to form viable complete-turn plans, then terminal-rollout only its two finalists at strategic decisions.

## Ranked implementable candidates

### 1. A small beam over consequential decisions

**Proposal:** preserve ordinary actions plus resource macros; at a few consequential own-turn choices, branch to two or three alternatives instead of accepting the greedy continuation. Start with beam width 3–4 and a global budget of roughly 32–64 expanded decision states per live root, then benchmark. These are experimental starting values, not established optimal settings.

Useful branching points include mastery thresholds before scaling effects, draw/scry before reveal effects, buying versus fast play, using an exhaust effect now versus after another effect, and the last purchase/reroll/end-turn choice. Forced menu answers and verified equivalent neutral-resource steps need not consume a strategic branch budget. Do not treat every card play as interchangeable: faction triggers, mastery thresholds, copy effects, health conversion, and future draw order can distinguish sequences.

Keep the legacy continuation among candidates, plus the existing resource-first and mastery/draw-aware styles. Finish retained candidates to a comparable turn boundary before ranking; scoring partially played hands against completed turns invites horizon artifacts. Keep a hard primitive-step cap for termination, report unfinished leaves, and fall back safely rather than inventing a terminal value.

This is a local design inference. A relevant precedent is portfolio search: Churchill and colleagues studied selecting scripted continuations under tight game-search budgets, and explicitly examined errors introduced by the simulator. Their RTS results do not establish Shards performance, but support testing a small, diverse continuation portfolio rather than assuming a neural rollout is inherently superior. [Author-hosted paper](https://www.cs.mun.ca/~dchurchill/publications/pdf/aiide17_churchill_facebook.pdf).

**Information constraint:** branch selection must use the player's information set. Before new cards become observable, the same visible history must prescribe the same action across sampled worlds. After a legitimate draw or reveal, different actions are allowed. Do not independently optimize a full sequence in each hidden world and average those winning sequences: that values a player with unavailable information. Exact state deduplication is safe within a world; merging across worlds needs an observation/history key, not a full hidden-state fingerprint.

**Compute:** width alone is misleading; record cloned states, inference rows, engine steps, and total seconds per decision/game. Expand a batch of beam states across games together, rather than issuing one GPU request per node. Comparing beams at an equal expansion budget and at equal wall time distinguishes search quality from merely spending more compute.

### 2. Allocate extra worlds to close root choices

Gumbel AlphaZero samples candidate actions without replacement and uses Sequential Halving to focus a small simulation budget. Its paper specifically addresses poor policy improvement when few root actions receive visits. It does **not** guarantee improvement with wrong action-value estimates, and its benchmark settings do not establish a solution to Shards' hidden information. [Paper, especially Algorithms 1–2](https://davidstarsilver.wordpress.com/wp-content/uploads/2025/04/gumbel-alphazero.pdf).

**Smallest useful adaptation:** evaluate a broader root shortlist cheaply on the same two worlds, retain the best half, and spend additional worlds/continuations on survivors. Include the old choice and tactically distinct actions even when policy probability is low. Reuse each survivor's earlier samples. Retain an explicit total expansion budget. This is a Sequential-Halving-inspired root scheduler, not a faithful full Gumbel AlphaZero implementation.

Use common sampled worlds for competing options. Aggregate a fixed style over worlds **before** choosing its score: `max_style mean_world(value)`, not `mean_world max_style(value)`. Selection on a tiny sample still creates winner's-curse bias; re-evaluate finalists on fresh worlds for diagnostics. Two or four samples cannot justify a statistical claim about a rare hidden-card outcome.

The official Mctx implementation is a useful reference for root allocation, completed Q-values, and policy targets. It is JAX-native and operates on batched recurrent models; wrapping the .NET engine inside it would not make engine stepping run on the GPU. Borrow the allocation logic instead of porting the simulator in this session. [Official source](https://github.com/google-deepmind/mctx/blob/main/mctx/_src/policies.py), [project documentation](https://github.com/google-deepmind/mctx).

### 3. Train a better endpoint critic, separately from the action policy

**Proposal:** collect complete games from the best repaired player, with both seats and all hero matchups represented. Store every actual decision or at least every turn boundary with actor identity and terminal outcome. Train a value candidate with whole-game-held-out validation; report calibration by hero, seat, round, and predicted-probability bin, alongside Brier score. Evaluate the old policy with the new critic as its own match candidate, so a policy regression cannot hide a value improvement or vice versa.

A turn-boundary head is attractive because those are the states the planner actually ranks. Sampling many correlated actions from the same game cannot substitute for independent outcomes. Oversample decision types that search encounters, with deliberate weighting and a held-out distribution representative of actual play. Keep terminal win/loss as the primary objective; add resource or damage prediction only as auxiliary targets, never assume their heuristic sum equals game value.

Multi-step/TD targets can propagate information faster, but bootstrapping from a saturated wrong critic can preserve its error. Start with terminal-return calibration; compare a mixed terminal/turn-step target only as a separate experiment. Count horizons in turns or consistent decision intervals, and handle perspective explicitly when actor changes. Off-policy trajectories require an appropriate estimator; searched actions are not samples from the old network's action distribution. These distinctions follow the on-policy/off-policy and n-step treatment in Sutton and Barto, Chapters 5–7. [Authors' book](https://www.incompleteideas.net/book/bookdraft2018mar21.pdf).

**Additional diagnostic:** on a small fixed set of suspicious positions, roll candidate actions through the opponent's next complete turn using a public-information policy, then evaluate. This costs more but can expose values that ignore champion exposure or immediate retaliation. Use it to label failures before making every live decision pay that cost.

### 4. Expert iteration only after the teacher improves

Expert Iteration separates search from a network that generalizes its decisions, then uses the improved network to guide subsequent search. The original work compares selected-action targets with softer tree-policy targets and collects positions visited by the apprentice to reduce distribution mismatch. Its successful domain was Hex, not an imperfect-information deckbuilder. [Original paper](https://arxiv.org/pdf/1705.08439).

**Proposal:** distill the repaired, match-validated search using action-group targets, with extra weight for verified obvious corrections and broadly sampled normal states. Keep the original policy as a candidate, not an immutable strong KL anchor everywhere: a strong anchor around a known EndTurn mistake teaches the mistake back. Conversely, replacing every old decision with the winner of a noisy two-world search can amplify noise. Separate exact tactical labels from uncertain search preferences, and use soft targets where action values are close.

Measure action-only performance and searched performance separately. A faster policy that maintains searched strength may permit a larger useful search budget. Lower imitation loss alone is not a promotion criterion; the prior session already supplies a local counterexample.

### 5. A transparent algorithmic comparator

Implement a modest no-policy or policy-light player as a comparator if the preceding candidates stall: legal guaranteed wins first; verified safe resource completion; explicit mastery-threshold opportunities; bounded purchase/fast-play combinations; simple deck/faction/mastery-aware endpoint scoring. Treat all non-proven preferences as hypotheses and retain alternatives. In particular, do not force all draws, all hero activations, all purchases, or all attacks before considering costs and timing.

The main value is diagnostic diversity. A script that beats the current hybrid or repeatedly makes better visible decisions reveals a model/search bottleneck; a script losing overall despite cleaner turns reveals inadequate long-term evaluation. Do not discard the model solely because the script is easier to explain. Evaluate scripts as rollout policies as well as complete players.

## Why not a full information-set solver now?

ISMCTS shares statistics across determinizations and addresses forms of strategy fusion. Its authors also report domains where a large information-set branching factor removes the advantage; the paper does not prove universal improvement or general Nash convergence. Shards combines private hands, remembered reveals, and variable action menus, so an information-set tree is a serious engineering change rather than a root-scoring patch. [Original paper, Sections III–V and IX–X](https://eprints.whiterose.ac.uk/id/eprint/75048/1/CowlingPowleyWhitehouse2012.pdf).

ReBeL obtains its imperfect-information guarantees using public belief states and equilibrium-solving machinery. A scalar value of a sampled hidden state is not an interchangeable substitute. Rebuilding these beliefs, targets, and solver around Shards within the current window is unlikely to leave enough time to validate strength. This is an engineering estimate, not a limitation of the method. [Original ReBeL paper](https://papers.nips.cc/paper/2020/file/c61f571dbd2fb949d3fe5ae1608dd48b-Paper.pdf).

## Evidence required before promotion

- Reproduce each known omission and verify the replacement on multiple independently sampled worlds. Record the visible position, candidate sequences, immediate effects, endpoint values, and prior terms. A high game win rate does not excuse a persistent free-heal omission.
- Repeat complete natural-game reviews after every meaningful change, including all heroes in both seats and both wins and losses. Search for wasted free gains, unspent productive resources, repeated cancel cycles, missed threshold sequencing, harmful reveals, and lethal opportunities. Include negative controls where declining an action is correct.
- Run identical-player controls with identical batch sizes and frozen inference settings. Store complete action transcripts for new cohorts. The known small-batch replay divergence makes summaries alone insufficient for exact reproducibility.
- Use pilots for rejection, then a fixed fresh paired confirmation for the chosen candidate. Swap treatment seats on each seed and preserve hero/deck assignments. Bootstrap by seed pair, not individual game. Do not pick the best of many pilots and quote its unadjusted interval as confirmation.
- Report both equal-budget strength and actual latency/throughput. Freeze code, settings, and model hashes for each contender. Reserve the last substantial portion of the session for confirmation, natural-game review, installation checks, and regression repair.

No feasible ten-hour experiment can prove globally perfect play. The practical completion evidence is a stronger independently tested player, elimination of reproducible obvious errors, repeated fresh-game audits, and a candid list of residual uncertain choices.
