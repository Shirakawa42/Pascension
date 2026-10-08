# Public hand memory and fresh-game audit

The latest experimental profile still makes unnecessary moves on winning turns. A separate, reproduced information-memory defect is fixed and validated. No installed model or published balance cohort was replaced during this audit.

## Confirmed memory defect and correction

The search previously forgot shield/Unify/Dominion hand reveals and cards drawn from an already public personal-deck top. Its hidden-state sampler could put those known hand cards back in the opponent's deck. Six of the original eight focused cases failed before repair.

`SupplementKnowledge` now retains a lower-bound public hand multiset. Repeated reveals take maximum multiplicity, accepted plays/discards/hand banishes remove a copy, and cleanup forgets the previous hand while preserving publicly inferred identities in the redraw. Draw-event private identities are never consumed. Small public event metadata distinguishes hand reveals, hand banishes and cleanup redraw count. Three already-public Horizon movements into hand now emit the existing returned-card event.

Both ordinary and defensive search worlds reserve known hand cards before allocating unknown cards. The model feature layout and weights are unchanged; this improves search memory, not a new direct neural input. Public-count inconsistencies are detected without using hidden identities to repair memory. Offline replay separately compares remembered facts against actual hands and rejects false facts or unexpected invalidation.

Validation:

- 30 focused lifecycle/privacy checks pass, including duplicate reveals, both copy implementations, actual play/banish/discard/cleanup, Gatekeeper and Legion Carrier, feasible private-zone swaps and poisoned private draw identity.
- 131 headless engine/serialization/Rez-memory tests pass.
- Public-top, public-identity, defensive-world, no-effect and horizon audits pass.
- Original and semantic models each pass 512 tactical cases.
- 80 prior complete transcripts (24,672 actions), plus 20 fresh transcripts (5,982 actions), replay exactly with zero false hand facts and zero memory invalidations. These are 100 transcripts across several cohorts, not 100 independent seed pairs.

## Fresh natural games

Twenty new seeds cover all 20 ordered distinct hero pairs using one-turn hybrid search, semantic weights, guards/menu/setup/Scry plans, capped priors and eight-world verification. There are 5,982 decisions and 414 end turns. This small diagnostic cohort is separate from published balance statistics.

- 43 Ko activations; zero cancellations.
- Zero certified inactive destiny activations.
- All four Volos modes occur (46 / 6 / 12 / 3); this establishes coverage, not optimal use.
- No recorder flag for an alternative sampled winning line. That bounded screen is not proof of no missed wins.
- 21 nonwinning end alternatives were inspected. Nature Dominance and Absorption Grid cases yielded no resources; six Deadly Recruits cases had no eligible target. Tetra's skipped Focus at mastery 30 only spent a gem. Nine other alternatives were optional HP-cost Ko activations, not free gains.
- One unresolved end-turn choice: Volos delays its remaining relic choice at game 6 / step 146, mastery 12. Both candidate acquisitions are legal. Volos already has Entropic Talons in its collection and wins on round 9; neither fact proves the delay optimal.

The replay evidence includes 39 initial alternative paths across 16 public worlds, followed by two skip-banish probes and one corrected semantic-menu probe. Counterfactual failures are retained, not counted as successes.

## Confirmed unnecessary sacrifice

Game 1 / step 252: Ko at 34 HP and mastery 27 banishes Brute, losing 1 HP, then reaches mastery 30 and wins with Infinity Shard. Removing the banish changes the remaining reveal menu, so simply deleting its two recorded action keys does not replay. After explicitly completing the still-open reveal menu, the alternative reaches mastery 30 and wins with **34 HP in all 16 sampled worlds**. The played line wins with 33 HP.

This demonstrates a redundant sacrifice on a winning line, not a measured lost game. Existing terminal-prefix cleanup removes empty activations and canceled Ko menus, but does not minimize a winning line containing a real banish. That is the next concrete correction target; do not hardcode a prohibition on banishing Brute or learn a general banish rule from this one position. Preserve validation against public-possible shields and hidden outcomes.

Game 14 / step 298 also banishes Doom Gate during a winning turn. Removing it changes the sampled subsequent draw sequence; the exact recorded suffix fails in all 16 probes. This remains suspicious, not a certified unnecessary banish. The game later wins by mastery. Several winning turns also continue taking actions after Infinity Shard has supplied overwhelming power; shorter validated finishes deserve attention.

## Experiment selection and ongoing learning

The two-turn reply-horizon pilot completed 69–91 / 160 (43.125%, paired bootstrap interval 35.625–50.625%). It remains experimental and is not promoted. Follow-up Talons and Ferrata counterfactuals provide no clear win-rate benefit from the alternative play. See `conditional-and-reply-horizon-2026-09-28.md`.

Public-hand GPU validation completed and resumed the verified expert-experience collector (supervisor 626198, Python 626696, native 626712). At the last check it had completed 1,511 / 2,000 games. Its frozen runtime predates this memory repair; preserve that provenance for any subsequent learning comparison. The ten-hour goal remains active until its deadline, 09:46:50 UTC.

Artifacts: `/home/lva/.local/share/shards-training/2026-09-28/ten-hour-improvement/hand-*`, especially `hand-fresh-review/manual-evidence.json`, `winning-cleanup-evidence.json`, and `brute-retained-evidence.json`. No Unity tests were used; all jobs retain the eight-core affinity.
