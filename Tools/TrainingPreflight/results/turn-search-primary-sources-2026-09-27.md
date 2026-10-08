# Turn sequencing and learned strategy: primary-source research

Date: 2026-09-27. Scope: algorithmic evidence for the proposed Shards of Infinity turn planner. This note contains no new game-throughput or playing-strength measurements. The accompanying local investigation must establish those separately.

## Conclusion

A hybrid is well motivated: enumerate or solve small deterministic tactical segments, use a learned evaluator for strategic tradeoffs, and train the policy from verified stronger search decisions. This is a proposed engineering direction, not evidence that an entire Shards turn can always be exhaustively solved cheaply. A fixed five-card permutation problem and a turn containing draws, purchases, rerolls, target selections, triggered effects, and new information are different search problems.

## What the literature establishes

### Equivalent orders can be removed, with explicit conditions

Wehrle and Helmert formalize action dependencies through enabling, disabling, and conflicting effects. For independent actions, both orders remain applicable and yield the same state. Their partial-order reduction methods preserve completeness; strong stubborn sets preserve optimality when combined with an optimal search algorithm. Their analysis also identifies defects in earlier reduction techniques, illustrating that an intuitively harmless pruning rule can be unsound. These results concern their planning formalism, not arbitrary game-engine objects. [About Partial Order Reduction in Planning and Computer Aided Verification, ICAPS 2012, definitions 2–4](https://cdn.aaai.org/ojs/13526/13526-40-17044-1-2-20201228.pdf).

**Application proposed here:** reduce only action pairs whose legality, information, and future effects are preserved. Matching current health, gems, and power is insufficient. Effects may depend on faction counts, played cards, pending continuations, available banish targets, thresholds, or future callbacks. Distinguish instance symmetry from order independence: two ordinary identical Crystals may be interchangeable in one position without being safe to play before every other card. A useful first implementation is a small, explicit set of verified reductions, backed by differential transition tests.

### A tactical sequence can be represented as one policy decision

Sutton, Precup, and Singh define an option through an initiation set, an internal policy, and a termination condition. Options are temporally extended actions; primitive actions and options can coexist. Their induced decision process is semi-Markov, accommodating variable durations. Their framework also allows interrupting an option during execution. [Between MDPs and semi-MDPs, Artificial Intelligence 1999](https://people.eecs.berkeley.edu/~russell/classes/cs294/f05/papers/sutton%2Bal-1999.pdf).

**Application proposed here:** expose meaningful candidate plans such as reaching a mastery threshold before a dependent play, collecting resources toward an acquisition, or resolving a known lethal line. Keep a primitive-action fallback. Terminate or replan when new information appears or an unanticipated decision opens. If training uses per-action discounting, changing how many actions a plan represents must preserve the intended return; silently applying the old discount once per macro changes the objective. With terminal rewards and discount one, this particular duration issue disappears, but exploration and credit assignment still change.

### Search must respect what the player can know

Cowling, Powley, and Whitehouse explain strategy fusion: solving sampled complete worlds independently may assume the player can make different future choices in states that are observationally indistinguishable. Information-set tree search shares decisions appropriately, although benefits vary by game and increased branching can offset them. Their experiments do not justify a universal claim that information-set MCTS is always faster or stronger than determinization. [Information Set Monte Carlo Tree Search, IEEE TCIAIG 2012, sections III-B and IX](https://eprints.whiterose.ac.uk/id/eprint/75048/1/CowlingPowleyWhitehouse2012.pdf).

**Application proposed here:** public deck composition supports a distribution over unknown orders, not knowledge of the real order. A scry result already revealed to the acting player may be used exactly. A future unknown draw requires a chance boundary or sampled continuation with actions coupled until observations distinguish the worlds. The player may adapt after seeing the drawn card, but cannot choose the earlier action using its hidden identity. Counting full-state simulator paths measures computational growth; it does not automatically produce a fair playable policy.

ReBeL combines search and learning through public belief states and an equilibrium-finding procedure. Its theoretical results are for its specific algorithm and assumptions in two-player zero-sum games. A public belief state includes a belief distribution, not merely the public board. [Combining Deep Reinforcement Learning and Search for Imperfect-Information Games, NeurIPS 2020, sections 4–6](https://proceedings.neurips.cc/paper_files/paper/2020/file/c61f571dbd2fb949d3fe5ae1608dd48b-Paper.pdf).

**Application proposed here:** ReBeL is a useful correctness reference, not a drop-in replacement for the current short-budget trainer. Averaging two sampled-world value predictions is not equivalent to ReBeL and inherits none of its equilibrium guarantees.

### A better tactical planner can teach the network

Anthony, Tian, and Barber separate planning from generalization in Expert Iteration: search produces stronger decisions; a neural policy learns from them and subsequently guides better search. Their online variant aggregates expert datasets. They explicitly discuss the cost of generating search labels and report results in Hex, a perfect-information game. [Thinking Fast and Slow with Deep Learning and Tree Search, NeurIPS 2017, sections 3.1–3.3](https://proceedings.neurips.cc/paper/2017/file/d8e1344e27a5b08cdfd5d027d9b8d6de-Paper.pdf).

Kitchen and Benedetti explore a related feedback loop for imperfect-information games using Online Outcome Sampling in place of standard MCTS. This supports investigating a search-teacher architecture without assuming a perfect-information search algorithm transfers unchanged. [ExIt-OOS: Towards Learning from Planning in Imperfect Information Games, 2018](https://arxiv.org/abs/1808.10120).

**Application proposed here:** retain the current generic-effect encoder and warm-start compatible weights. Gather teacher decisions from actual self-play states, include different heroes and tactical families, and mix tactical imitation with outcome-based learning. Search targets must first pass correctness checks: imitating a flawed search can reinforce its errors. There is no source-backed reason here to require training from scratch, and no guarantee that a warm start wins; compare it experimentally against the frozen current checkpoint. Full replacement of the action interface may require a new head or compatibility layer while preserving the encoder.

## Recommended experiment boundaries

These are proposed acceptance criteria for this project, not claims from the papers:

1. Measure distinct initial card multisets, legal decisions per step, meaningful decision depth, and search node counts separately. Report median and tail positions; a cheap opening does not characterize a draw-heavy late turn.
2. Compare unrestricted enumeration, duplicate-instance reduction, and verified order reduction on the same public states. Small fully enumerated cases provide an oracle for checking pruning.
3. Stop exact deterministic segments at unknown draws/reveals or explicitly introduce chance branches. Report whether each result is exhaustive, bounded, or heuristic.
4. Evaluate candidates at comparable turn boundaries. Do not automatically prefer ending now because its value estimate is from a different phase than an unfinished productive line.
5. Preserve strategic alternatives. More current gems, health, or damage is not a universal dominance proof when card locations, acquisition opportunities, or long-term resources differ.
6. Measure search time, neural inference time, copies, transitions, and batch occupancy. Lower GPU utilization can accompany a faster solver if it removes unnecessary inference calls; utilization alone is not the objective.
7. Require held-out tactical correctness, information-leak invariance, seat-swapped strength comparisons, and complete-game review before claiming improvement. Keep the published statistics cohort separate from exploratory games.

The near-term target is a bounded, information-safe planner with exact solutions where affordable, verified reductions where possible, and learned evaluation where genuine tradeoffs remain. Exhaustive full-turn optimality should be claimed only for positions actually completed under a correct information model.
