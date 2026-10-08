# Can an algorithm play the hand while the network chooses strategy?

## Recommendation

Yes: build a hybrid tactical planner. Small deterministic hand segments are cheap enough to search; many repeated micro-decisions can be grouped. Do **not** implement an unconditional “play the hand, then let the AI decide” phase. Buying, banishing, mastery thresholds, drawing, copying, and champion activations interact with the hand. Full-turn enumeration is much larger than five-card permutations suggest.

Use the real headless engine to generate legal tactical plans, reduce only verified equivalent orders, and use the learned evaluator for genuine strategic tradeoffs. Replan when information changes. Warm-start the existing generic-effect model after the planner passes correctness and strength tests; this investigation does not establish a need to train from scratch.

This change is **not deployed**. Only offline research instrumentation and more precise replay logging were added. No training ran; policy weights and the published 1,400-game statistics are unchanged.

## Measured evidence

Reconstructed eight complete previously reviewed games: 2,592 decisions, all five heroes, both seats. Five games were selected for hero coverage; three additional games were selected to investigate concessions. This is a diagnostic sample, **not** a random estimate of all games. Exact seeds, heroes, winners, rounds, decisions and override counts matched the frozen evaluation cohort.

| Quantity | Median | 90th percentile | Maximum |
|---|---:|---:|---:|
| Initial hand size, first normal-action position per player/round | 5 | 5 | 8 |
| Permutations of those hand instances | 120 | 120 | 40,320 |
| Permutations treating identical definitions as interchangeable | 30 | 120 | 10,080 |
| Legal choices at a recorded decision | 10 | 17 | 23 |
| Observed wrapper decisions per turn, including resolution inputs | 13 | 22 | 31 |

The five coverage games alone had 1,829 decisions across 121 player-turns; their median multiset hand permutations were **60**, and median decisions per turn **15**. The broader eight-game numbers should not hide this difference.

### Actual engine-tree enumeration

Tested all 181 first normal-action positions plus eight previously identified tactical mistakes: **189 positions**. Serial depth-first enumeration used the optimized C# copier, no Unity, a maximum of eight permitted CPU cores, a depth ceiling of 32, and a 5,000-node ceiling per position/method. No policy inference was needed to count branches. Each root was sanitized with `PublicWorld`; tests did not plan against the live hidden order. Different paths were never merged through the engine hash.

| Search scope | Median nodes | Median time | Node-capped positions |
|---|---:|---:|---:|
| Initial-hand plays, distinct instances | 130 | 5.60 ms | 0 / 189 |
| Same, collapsing identical copies | 48 | 1.74 ms | 0 / 189 |
| Initial hand + focus + hero power + champion activations | 2,620 | 100.21 ms | 70 / 189 |
| Same tactical actions, collapsing identical hand copies | 555 | 26.07 ms | 39 / 189 |
| Whole turn, including purchases, rerolls and effect choices | ≥5,000 | 178.90 ms to cap | 186 / 189 |
| Whole turn, collapsing identical hand copies | ≥5,000 | 180.26 ms to cap | 186 / 189 |

**Important boundaries:** hand-only searches stop at a pending effect choice, a draw/new hand card, or a changed market. They do not solve the rest of the turn. In **102 / 189** positions, at least one hand branch reached a draw or effect-choice boundary. The collapsed hand searches visited 11,561 nodes total versus 50,030 without collapse, a **4.33× node reduction**. Their 90th-percentile time was 5.62 ms and maximum 14.16 ms. These are timings of the enumeration work, excluding root-world construction, learned leaf evaluation, and planning beyond the boundary.

Collapsing copies here is an **optimistic experimental symmetry reduction**, not a production proof that every instance can always be exchanged. It compares definition, ownership and mutable card flags, but does not prove that every paused continuation or future target is invariant under renaming. A capped DFS is a lower bound and is sensitive to traversal order. Four whole-turn positions also reached the depth ceiling. Whole-turn measurements cover **one sampled world**, not all possible unknown draws or opponent responses; opponent-input boundaries stop enumeration. Concede and paging cycles are excluded. None of these results claim exhaustive optimal game play.

The collapsed tactical-action row was measured in a separate pass over the identical 189 reconstructed positions. It completed its restricted search in 150 / 189 positions, while 39 still exceeded the node budget. This is encouraging for an adaptive solver, but neither that completion rate nor its timing establishes optimal full-turn play. Raw results and summary are retained separately below.

### Larger opening-position stress test

Raised the node limit to 100,000 for the first three positions of game 298:

| Position | Initial hand | Collapsed whole-turn nodes | Time |
|---|---|---:|---:|
| Round 1, Tetra, seat 0 | 3 Crystals, Infinity Shard, Blaster | **91,634**, completed within sampled-world scope | 2.94 s |
| Round 1, Rez, seat 1 | 4 Crystals, Infinity Shard, Blaster | **≥100,000**, capped | 3.84 s |
| Round 2, Tetra, seat 0 | 4 Crystals, Shard Reactor | **≥100,000**, capped | 3.52 s |

For the first position, ordering the hand is only 20 multiset permutations; including the rest of the turn produces over 91,000 tree nodes. Nodes include prefixes, so they are not the same unit as complete permutations. The contrast illustrates the scope expansion, not a mathematical branching-factor estimate. More aggressive proven reductions could make this much smaller; these results reject naive brute-force enumeration, not the possibility of a substantially better solver.

## Where the present search wastes work or makes mistakes

1. **Duplicate root choices consume search slots.** Across 2,580 searched positions, 709 of 16,777 root candidates repeated a hand-play name. There were 486 searches with this repetition; 332 also had more than eight legal actions. This can spend the eight-candidate budget on copies while excluding distinct alternatives. Aggregate probability over a proven equivalence class and search a representative; retain its concrete legal instance for execution. Do not merely delete duplicate probabilities without preserving their mass.
2. **The horizon measures button presses, not meaningful progress.** In 1,278 / 2,580 searches, leaves had different active-turn players. The earlier manual review demonstrated five harmful early EndTurns that disappeared when continuation depth increased from eight to 64. This is direct evidence that short rollout truncation contributes to bad sequencing; it is not proof that all mixed-phase comparisons are wrong. A tactical segment should not consume the same budget as a strategic branch merely because it contains several starter plays or menu clicks.
3. **A fixed value margin preserves some obviously wasteful fallback actions.** Three other reviewed EndTurns survived depth 64 because estimates near +1 differed by less than the 0.04 replacement margin. More sequence enumeration alone does not fix this selection rule. The previous review also found actual concessions, which should be excluded from balance evaluation. This investigation filters concession only from its enumeration; it does not silently change the live policy.
4. **Many micro-actions are repetitive.** There were 504 Crystal plays, 19.4% of all recorded decisions. Consecutive Crystal runs contained 195 extra individual steps beyond one step per run. This is a possible macro-action opportunity, **not** a measured games/s gain or permission to always play all Crystals. The amount to play can itself be strategically significant.

See [the manual replay review](manual-game-review-2026-09-27.md) for the exact bad decisions and successful sequencing examples.

## What to offload, and what to preserve

| Decision family | Proposed algorithmic role | Strategic choice that must remain |
|---|---|---|
| Equivalent copies and independent resource plays | Group equivalent branches; execute a chosen resource-collection sequence without re-running the network for every copy | How many cards to retain as banish/copy targets; when to spend crystals |
| Mastery-dependent plays | Enumerate resource/focus/hero sequences around actual effect thresholds | Whether buying something is worth more than reaching the threshold now |
| Champion damage allocation | Enumerate feasible kill subsets; calculate exact required damage and mandatory taunts; existing kill-or-nothing allocation remains useful | Which champion is worth killing versus damaging the hero; uncertain opposing shields |
| Healing and damage conversion | Test orderings such as Entropic Talons before healing, or a healing multiplier before a heal | Current safety versus power, costs, caps and triggered consequences |
| Known scry/reveal sequences | Solve the currently known prefix and card compatibility with actual effects | Which future market/deck plan to pursue; unknown cards beyond the revealed prefix |
| Purchase/fast-play/activation combinations | Generate legal plans that collect resources, reach discounts or unlock the intended action | Deck composition, denial, long-term value and whether fast-play is worth losing the card |
| EndTurn | Challenge it with cheap tactical alternatives and concrete lethal verification | Avoid a blanket rule that every remaining card/activation must be used |

Examples already present in the reviewed games:

- Ko Syn Wu used World Piercer and Focus to reach mastery 30 **before** Infinity Shard. A threshold-aware scheduler should discover that line without requiring the policy to memorize every intermediate ordering.
- Volos correctly played Entropic Talons before several healing effects. This is a local ordering interaction, but “always heal first” would destroy it.
- Tetra correctly delayed Oblivion Gatekeeper until mastery 20 and Strategic Mastermind until 40 HP. Automatic champion exhaustion at turn start would be harmful.
- Rez's abandoned Shard Reactor + two Crystals at game 1313, step 218 was a nine-node collapsed hand search in the prototype. The alternative is computationally tiny; the prior search's ranking/horizon, rather than the size of the hand, was the important problem.
- Playing every Crystal immediately can remove all useful hand targets for a banish effect. A macro should be able to play **k of n**, leaving the remaining copies available. The long-term value of banishing versus spending is still a learned decision.

No general “maximize this turn's gems + mastery + damage” objective is correct. Outcomes with different card locations, deck quality, hero safety or market opportunities are not equivalent. Pareto filtering can retain alternatives, but dominance must account for all relevant future consequences, not just visible resource totals.

## Concrete architecture to test next

1. **Build conservative effect summaries from the generic effect tree.** Record which resources/thresholds/zones an effect reads and changes; whether it draws, reveals, chooses targets, pays a cost, or triggers additional effects. Use actual numeric effect values so balance changes carry through. Unknown `Custom`/`Do` logic and static callbacks must fail closed to ordinary engine search. Effect summaries guide search; the existing engine remains the rules authority.
2. **Generate short, interruptible tactical plans.** Keep primitive actions available. Start with verified instance symmetry and resource bundles with all relevant stopping points. Add mastery thresholds and known-prefix combinations. Include stopping/retaining cards as alternatives; exhaustive ordering of an obligatory “play all” hand would miss those choices.
3. **Search cheap segments exactly; budget expensive branches.** Use partial-order reductions only after proving that both orders remain legal and preserve future effects and information. Do not reuse the old incomplete engine hash as a transposition key. If a full state/continuation equivalence key is not available, cache exact prefixes and conservative commuting blocks instead.
4. **Batch learned evaluations at comparable boundaries.** Prefer consistent end-of-turn afterstates for strategic comparisons, with explicit handling when budget expires or new information arrives. Use several public-information samples for uncertain continuations; couple decisions while observations are identical. Execute only the current known segment, then replan after a real draw/reveal. Knowing the public deck composition never permits reading its hidden order.
5. **Keep the CPU/GPU division appropriate to the work.** Run cheap real-engine transitions on the permitted CPU workers and batch the remaining neural evaluations across games/frontiers on the GPU. Eliminating unnecessary neural calls may lower GPU utilization while improving speed. This prototype has **not** measured an end-to-end hybrid games/s result or strength improvement.

The primary-source basis and its limits are recorded separately in [turn-search-primary-sources-2026-09-27.md](turn-search-primary-sources-2026-09-27.md): partial-order reduction, temporally extended actions, information-set search and learning from search.

## Retraining strategy

First fix and validate the planner with frozen weights. The generic effect encoder and value model can be reused; no observed result requires restarting them from zero. If the first implementation preserves the current primitive-action interface and merely returns the first step of a better plan, it need not immediately change the model shape. A later explicit plan-scoring head can reuse the encoder while learning plan features.

After validation, use search decisions from **real self-play positions** as teaching data, spanning all heroes, early/late turns, resource shortages, known/unknown information and withheld tactical combinations. A large hand-authored scenario set is useful for regression coverage, but should not become the entire training distribution. Mix teacher supervision with outcome-based strategic learning and retain comparison opponents.

The current training contract already uses `gamma = lambda = 1`, so compressing micro-actions does not introduce an additional discount-per-step discrepancy. It still changes state visitation, entropy, example weighting and credit assignment. Deterministic solver steps should not be mislabeled as samples from the policy's old stochastic distribution. Train actual strategic selections on their correct behavior probabilities; teach solver improvements through an explicit imitation/search-target objective.

Acceptance gates before a long run: exact small-case oracle comparisons; duplicate/order reductions checked against unreduced trees; hidden-zone swap invariance; no live-state or memory mutation; no resignations in balance evaluation; held-out hero tactics; complete-game manual review; and seat-swapped, seed-paired strength plus speed tests against the frozen current search. Compare both fixed settings and a matched compute budget. There is no basis yet for promising that an arbitrary ten-minute retraining or 100,000 generated puzzles will be sufficient.

## Reproduction and artifacts

- Instrument: `Tools/GpuSearchHost/TurnBranchAudit.cs`, mode `turn-branch-audit`.
- Precise replay fields: `selectedIndex` and `selectedKey` in `ReviewRecorder.cs`; this matters because terminal proof can override the earlier rollout proposal and different allocations can share a display name.
- Raw measurements: [189-position audit](turn-branch-audit-2026-09-27.json), [100,000-node opening stress test](turn-branch-opening-stress-2026-09-27.json), [summary](turn-branch-summary-2026-09-27.json).
- Additional fair comparison with duplicate reduction also enabled for hero/champion tactics: [raw](turn-branch-tactics-collapsed-2026-09-27.json), [summary](turn-branch-tactics-collapsed-summary-2026-09-27.json). Reproduce only this scope with `--audit-scope hand-plus-tactics-collapsed`.
- Frozen exact-key replays: `/home/lva/.local/share/shards-training/2026-09-27/turn-branch-replays/`.
- GPU replay: eight outcomes/steps/overrides identical to the frozen cohort, maximum native/GPU policy difference `1.1026859e-6`, 109 parity cases. Diagnostic overhead means its 15.97-second duration is not a new throughput benchmark.
- Build: `dotnet build Tools/GpuSearchHost -c Release --nologo`, zero warnings/errors.
- Repeated the first three 5,000-node probes: node, leaf, branch, depth and boundary counts matched exactly. Python compilation and whitespace checks passed. Current policy SHA-256 equals the frozen cohort policy, and the monitor still serves only the original 1,400-game cohort without errors.

```sh
taskset -c 0,2,4,6,8,10,12,14 env DOTNET_PROCESSOR_COUNT=8 DOTNET_gcServer=1 \
  /home/lva/.dotnet/dotnet Tools/GpuSearchHost/bin/Release/net8.0/GpuSearchHost.dll \
  --mode turn-branch-audit \
  --replay /home/lva/.local/share/shards-training/2026-09-27/manual-review-games.json \
  --reviews /home/lva/.local/share/shards-training/2026-09-27/turn-branch-replays/reviews \
  --node-cap 5000 --output /tmp/turn-branch-audit.json

python3 Tools/TrainingPreflight/experiments/turn_branch_summary.py \
  --audit /tmp/turn-branch-audit.json \
  --reviews /home/lva/.local/share/shards-training/2026-09-27/turn-branch-replays/reviews \
  --output /tmp/turn-branch-summary.json
```
