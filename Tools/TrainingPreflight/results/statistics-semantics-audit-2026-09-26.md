# Statistics semantics audit, 2026-09-26

## Result

The captured live statistics reconcile exactly with their raw inputs. I found **no duplicated completed outcomes, censored games counted as draws, lost hero-seat totals, or V7 routing failure** in the checked sources. I did reproduce and fix three presentation problems: a claim that frozen fast-play entries are temporary ownership, an unclear frozen round metric, and per-hero filters hiding unobserved choices. These were monitoring defects, not changes to game outcomes or PPO training.

This audit covers passive C# acquisition/terminal accounting, Python aggregation, and both monitoring views. It does not certify the entire training algorithm or demonstrate optimal policy play. No training process was signalled, no GPU workload was launched, and no pinned trainer or host source was edited.

Evidence: [read-only reconciliation probe](../experiments/statistics_semantics_probe.py), [captured probe results](statistics-semantics-probe-2026-09-26.json). Capture time: **2026-09-26 20:40:26 UTC**, before the main V7 continuation. All counts below describe that capture rather than a moving current total.

## Live accounting checks

| Source | Completed real games | Hero outcomes | Terminal draws | Censored games | Raw reconciliation |
|---|---:|---:|---:|---:|---|
| Earlier training pool | 87,404 | 174,808 | 64 | 0 | Exact |
| Random heroes | 104,947 | 209,894 | 25 | 0 | Exact |
| Natural drafts | 62,869 | 125,738 | 25 | 0 | Exact |
| Frozen evaluations | 1,024 | 1,024 | 1 | 0 | Exact |

Training records both players' outcomes from each real game; frozen evaluation rankings track only learner A. Two hero outcomes per training game are intentional, and their shared game is one confidence cluster. Each player-card-acquisition-mode outcome is counted once per game even if the player obtains that card repeatedly; `pick_count` counts the actual repeated events. Both players acquiring the same card creates two correlated player outcomes in one real-game cluster. [C# terminal accounting](../experiments/HostV5/TrainingStatistics.cs), [pool aggregation](../balance_host_statistics.py), [frozen aggregation](../balance_statistics.py).

The independent probe rebuilt each captured training window from its raw latest cumulative file minus its exact historical anchor. It compared `totals`, every ranking row, per-hero choice rows, hero seats, matchups and final-state sums with the API output. It separately rebuilt frozen outcomes from the four accepted raw reports and matched their totals/rankings/hero seats/matchups. The checked V6/V7 sessions' finished-seed ranges did not overlap across runs. The two hero cohorts share an overall seed range but partition games by setup mode; overlapping ranges between those disjoint cohorts are not duplicate games.

## Cohorts, windows and V7

At capture, the random-hero window contained **90,029 V6 games plus 14,918 V7 pilot games**. The natural-draft window contained **1,260 V6 pilot + 56,815 V6 main + 4,794 V7 pilot games**. These are mixed changing-policy observations, not pure post-V7 estimates. The backend exposes contributing run files and `choice_learning_by_run`; the V7 exploration declaration appears in the scope notes and insights. V7 deliberately retains V6's exact host, rules and observation catalog, so this routing is consistent with its identity. [V7 identity](../experiments/variant_v7_runtime.py), [pool routing](../balance_host_statistics.py).

Raw publication occurs every 10,000 **completed games per hero cohort**, with initial/final partial publications. The 100,000-game number is a recent-window target, not the threshold before anything becomes visible. Windows round to available publication boundaries; the captured random window's 104,947 games are the actual displayed sample, not a claim of exactly 100,000. Cumulative history is subtracted, not appended as new games. Retired sessions contribute to lifetime totals but not automatically to the recent window. [Publication cadence/history](../experiments/HostV5/TrainingStatistics.cs), [anchor selection and subtraction](../balance_host_statistics.py), [regression coverage](../test_balance_host_statistics.py).

**Rolling-window recheck:** after main V7 published its first 10,000 random-hero games, the displayed total remained 104,947 but draws changed from 25 to 21. This is expected: the V6 anchor advanced from 80,000 to 90,000, removing a 10,000-game block with six draws and adding the new V7 block with two draws. The new window contains 80,029 V6 games (18 draws), 14,918 V7 pilot games (one draw), and 10,000 V7 main games (two draws). A matching total count does not mean the sample is unchanged; compare snapshot IDs and session/anchor bounds. The [independent recheck](statistics-semantics-probe-recheck-2026-09-26.json) passed.

Frozen evaluation considered 16 recent report files, accepted four matching the current observation identity, and excluded 12 older incompatible files. Its **1,024** outcomes come from accepted reports only. `window.reports=16` denotes reports considered; `source_reports` lists four accepted reports. The displayed evaluated-game total is correct. Forced/intervention reports and reused seeds from different frozen policy cohorts are excluded; duplicate copies of the same policy/seed/seat game are deduplicated. [Frozen selection/deduplication](../balance_statistics.py), [frozen tests](../test_balance_statistics.py).

## Meaning of each statistic

| Statistic | Actual denominator or meaning |
|---|---|
| Hero score | Wins plus half of terminal draws divided by completed appearances for the selected source. Training includes both evolving learners and archive policies. |
| Hero-seat score | Same outcome calculation restricted to a hero and physical seat. Both seats sum to the hero total. |
| Hero matchup | Oriented hero-versus-opponent outcomes, aggregated across seats in the displayed matchup. It is not automatically seat-standardized. |
| Card/relic/destiny score | Completed player-games in which that player recorded that acquisition mode at least once; an association conditional on acquiring, not a causal treatment effect. |
| Acquisition events | Repeated actual acquisitions in completed games for training pools. Frozen `selections` count direct selected actions. These counts can exceed qualifying games. |
| Training pick rate | Unknown: passive host telemetry collects no opportunity denominator. The UI must not divide acquisitions by all games and call it a pick rate. |
| Frozen pick rate | Selected direct actions per eligible visible decision menu. Repeated menus during a turn count repeatedly; this is not picks per unique shop appearance. |
| Mean acquisition/selection round | Event-weighted round for the recorded acquisition or direct selection, not the first acquisition per game or the turn it was played. |
| Training mean final rounds | Exact terminal engine round from the completed-game histogram. Overflow is tracked explicitly. |
| Frozen mean observed rounds | Latest pre-action observation, which need not be the exact terminal engine round. |
| Winner/loser mastery and winner health | Exact terminal training state for decisive games only. Draws do not enter these means. |
| Final permanent collection | Completed training state, both players, excluding temporary fast-play cards in the play zone; distinct from current hand size or deck draw-pile size. |
| Unfinished discarded games | No fabricated outcome; not included in completed scores and not silently turned into a draw. |
| Censored games | Unknown result, separate from terminal draws; if a ranking row contains one, its main point score is withheld and identification bounds remain available. |
| 95% bounds | Supplied conservative pointwise game-cluster bounds for the observed policy mixture. They do not establish causal strength, simultaneous ranking certainty, or an equilibrium meta. |

Sources: [raw counter contract](../experiments/HostV5/TrainingStatistics.cs), [training score/row construction](../balance_host_statistics.py), [frozen telemetry](../experiments/balance_telemetry.py), [frozen interval/selection aggregation](../balance_statistics.py).

Training mode labels distinguish accepted direct purchases/fast-play actions from effect-driven acquisition events. Relic and destiny events also include effect-granted acquisitions such as Corruption. **Relic recruitment does not imply drawing, playing, or exhausting the relic.** No current acquisition score can answer whether the policy used the relic's high-mastery effect well.

## Reproduced defects and fixes

1. **Incorrect temporary-ownership claim.** Both views called frozen fast-play selections “temporary use.” The engine can subsequently keep fast-played cards through Swyft and related choices, while this telemetry records entry paths. Both views now say **“Fast-play entry”** and explain that later effects can make the card permanent. [Retention rules](../../../Assets/Scripts/Shards/Engine/ShardsEngine.cs#L1408).
2. **Frozen round estimate unclear.** Both views now explicitly distinguish **“Mean final rounds”** for terminal training data from **“Mean observed rounds”** for frozen observations. Frozen captions/interpretation state that the value comes from the latest pre-action observation rather than exact terminal state.
3. **Unobserved per-hero choices disappeared.** Global rankings scaffold catalog rows, but the per-hero filter previously used only sparse observed records. A concrete captured example was natural-draft Rez: Comet had **2,015** global buyer outcomes and zero recorded Rez acquisitions, yet filtering Rez removed Comet entirely, even under all samples. The captured natural source had **259 absent hero/card-mode combinations** with global observations; the random source had four. Both views now scaffold eligible missing combinations as **zero acquisitions and unknown score**, preserving card identity without copying global wins, losses, events, costs, intervals or rounds. Foreign-hero relics are excluded. Exact existing hero records remain authoritative.

Files changed: [dedicated statistics view](../statistics_dashboard.html), [embedded balance view](../training_dashboard.html), [dedicated browser regressions](../test_statistics_dashboard.py), [embedded browser regressions](../test_training_dashboard.py). Filters that explicitly require a minimum completed sample still hide zero rows; all-samples/zero-sample settings expose them.

The dedicated statistics view's existing availability filter correctly hides the eight replaced relic originals. All **15 active relics** had acquisition outcomes in every captured training source. This proves some acquisition exposure, not adequate equal experience or competent continuation play. [Active relic source audit](choice-coverage-source-audit-2026-09-26.md).

## Validation

- Existing aggregation, dedicated dashboard and artwork suites: **34 tests passed** in 1.211 seconds before these display fixes. These include cumulative subtraction, duplicate reports, censors, paired clusters, exact hero partitions, identity/cohort rejection and recent-window boundaries.
- Independent live raw-to-API reconciliation probe: **passed**, approximately 0.44 seconds, stdlib only and read-only against the campaign.
- After the fixes, both browser regression tests passed in approximately **1.22 seconds**, exercising desktop/mobile standalone rendering and the embedded table. Added assertions verify neutral fast-play wording, frozen timing caveats, preserved zero rows, no copied pooled counts, and exclusion of foreign-hero relics.

The corrected monitoring text and coverage rows do not change training samples, rewards, policy weights, gameplay or the training budget.
