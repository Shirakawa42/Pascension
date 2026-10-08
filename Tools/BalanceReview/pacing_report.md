# Pacing and mastery audit: stopped Nyou-vs-Nyou cohort

This is an offline analysis of **3,780 completed games**, not a new simulation. Each of the 20 ordered, distinct-hero matchups has exactly 189 games. Both seats have the same AI. It therefore describes this AI's metagame, not a proven optimal-play balance. No mirror matches are present.

Source: `/home/lva/.local/share/shards-zero-depth/2026-10-08/new-ai-search-statistics-10000-optimized/strategy-prefix.jsonl`. Reproducible script: `Tools/BalanceReview/pacing_analysis.py`; detailed results: `/home/lva/.local/share/shards-zero-depth/2026-10-08/balance-review/pacing_results.json` (contains source SHA-256).

## Pacing is already approximately ten rounds

- Mean **9.980 rounds**, median 10, 10th–90th percentile 8–12, range 5–20.
- 681 games (18.0%) end in rounds 5–8; 2,469 (65.3%) in rounds 9–11; 630 (16.7%) in round 12+.
- Victory: normal damage 2,358 (**62.4%**); Infinity/M30 effect 1,221 (**32.3%**); Comet 139 (**3.7%**); other health loss 62 (**1.6%**).
- First seat wins 1,955/3,780 = **51.72%**, Wilson 95% CI **50.13–53.31%**. This is a small advantage, not evidence for a large opening-hand change. A prespecified confirmation cohort should precede changes to compensation.
- Current source grants second seat **five cards, one mastery, one opening-turn gem**. The older shards-engine guide saying six cards is stale; `ShardsEngine.cs:103–125` and initial draw loop are authoritative.
- Round is the engine's cycle counter, not turns per player: at least 408 games have a player's turn count greater than the round due to extra turns. Same-round ordering is not captured by the aggregate telemetry.

## Does reaching M10 first predict winning?

**Yes, strongly as an association.** The most defensible answer excludes games in which the opponent never reached M10, and excludes same-round ties because their event order was not recorded.

| Milestone | Both reached, different rounds | Earlier player wins | 95% CI | Standardized for hero matchup and starting seat |
|---|---:|---:|---:|---:|
| M5 | 2,756 | 59.5% | 57.7–61.3% | 65.3% (63.2–67.5%) |
| M10 | 2,383 | **68.9%** | **67.0–70.7%** | **70.4% (68.6–72.2%)** |
| M20 | 333 | 56.8% | 51.4–62.0% | 56.1% (49.7–62.5%) |
| M30 | 16 | 18.8% | 6.6–43.0% | Too sparse to estimate |

The last row is a selection trap: games where both reach M30 necessarily exclude the many games immediately ended by the first player with Infinity Shard. It does **not** mean reaching M30 earlier is bad.

Among both-reach-M10 games, the earlier player's win rate grows with its lead:

| M10 lead | Games | Earlier player win rate |
|---|---:|---:|
| 1 round | 1,116 | 62.2% (59.3–65.0%) |
| 2 rounds | 722 | 71.7% (68.4–74.9%) |
| 3+ rounds | 545 | 78.9% (75.3–82.1%) |

There are also 632 same-round M10 games, 670 where only one reaches it, and 95 where neither reaches it. The sole reacher wins 78.1%, but this mixes mastery advantage with survival: losing early can prevent ever reaching M10.

A less terminally selected check uses a fixed landmark: **among games alive after round 6**, if exactly one player had reached M10 by then, that player won **1,126/1,491 = 75.5% (73.3–77.6%)**. Standardized for hero matchup and starting seat, **76.7% (74.6–78.8%)**. This association is visible before the game ends, but still reflects draw quality, acquisition quality, existing economy, and all preceding choices; it does not establish a causal 26.7-point benefit from the relic threshold itself.

Why the M5 adjustment matters: second seat is the earlier M5 player in 2,254 of 2,756 distinguishable races (81.8%), reflecting starting mastery compensation. Earlier first seat wins 71.7%, earlier second seat 56.8%. Raw M5 counts mix opening advantage with threshold timing.

## Milestones do not explain the hero gap by themselves

| Hero | Overall win | Mean game rounds | Reaches M10 | Mean first M10 round, among reachers | Reaches M30 |
|---|---:|---:|---:|---:|---:|
| Tetra | **57.9%** | 9.83 | 87.4% | 7.32 | 11.9% |
| Decima | 50.1% | 10.07 | 88.4% | 7.14 | 25.3% |
| Rez | 49.3% | 9.99 | 90.1% | 7.30 | **30.6%** |
| Ko Syn Wu | 47.6% | 9.76 | 85.6% | 7.40 | 10.8% |
| Volos | **45.0%** | 10.26 | **91.5%** | **7.12** | 11.1% |

Tetra reaches M5 **later** on average (4.71 rounds vs 4.36–4.47 for the others), yet is strongest. Volos reaches M10 most often and slightly earliest, yet is weakest. A blanket extra-mastery buff for Volos or higher mastery gate for everybody therefore targets the wrong observed bottleneck. Conversion of resources into winning actions, and relic/hero interactions, deserve priority.

Win routes differ:

- Tetra: 681 of 876 wins are ordinary damage (77.7%); 152 mastery.
- Ko Syn Wu: 536 of 720 wins ordinary damage (74.4%); 153 mastery.
- Volos: 517 of 681 wins ordinary damage (75.9%); 142 mastery.
- Decima: 358 of 758 ordinary damage (47.2%); 346 mastery.
- Rez: 266 of 745 ordinary damage (35.7%); **428 mastery (57.4%)**.

Rez is functioning as a mastery-oriented hero in this cohort, not broadly underpowered. Among Rez's 463 M30 games, he goes from first M20 to first M30 in the same engine round 149 times (32.2%). Extra turns and burst effects can both produce this; the aggregate trace cannot assign the burst to a specific card chain.

The mean final collection is 14.1 cards for Ko vs 18.8–21.0 for others. Ko banishes 6.08 starters per game vs 1.54–2.04 for others. Thinning is clearly used, but cannot be equated with better overall hero balance: Ko still has lower win rate and is exposed to early damage (36.2% win in games ending by round 8, a descriptive end-time-conditioned subgroup).

## M10 is not instant relic payoff

- 6,700 player-seats reach M10; 6,689 select a normal relic (99.84%).
- 6,612/6,689 choices occur in the **same round** as first M10; 75 one round later, 2 later still. This does not establish same-action or immediate selection.
- 1,285 selected relics (**19.2%**) are never played before the game ends. This is not proof they were useless: Datic Robes has a passive shield from discard at M15.
- Among the 5,404 relics eventually played, first play occurs in the selection round 797 times (14.7%), +1 round 2,095 (38.8%), +2 rounds 1,833 (33.9%), +3+ rounds 679 (12.6%).
- Source `RecruitRelic` places the card in **discard**, explaining deck-cycle dependence. Reaching M10 first can mean gaining a whole shuffle-cycle head start, not simply having an extra effect that turn.
- M5 destiny is also usually claimed in the threshold round (6,795/7,439 choices =91.3%). Hero ability and destiny access arrive together, preventing attribution to the hero alone from aggregate milestone data.

## Balance implications and small, separable hypotheses

1. **Keep the overall ten-round pacing target and initial M10/M30 rules for the first patch.** The cohort is already at 9.98 rounds, with ordinary damage and mastery both substantial. Raising the global relic threshold or weakening mastery indiscriminately risks invalidating Rez's main successful route and buffing Tetra's damage route indirectly.
2. **Prioritize Tetra conversion and Volos payoff over access to mastery.** A Volos intervention should improve an underperforming effect/relic or reduce a real opportunity cost, not just grant mastery earlier. Tetra's advantage persists despite later M5. Specific card changes require the separate relic/card analysis.
3. **Do not automatically increase second-seat compensation.** The aggregate first-seat advantage is only 1.72 points with a CI barely excluding equality. Second-seat M5 access is already much earlier; any extra gem/mastery would interact with the early economy.
4. **Only if reducing M10 snowball becomes an explicit design goal**, test one constrained catch-up mechanic separately: a modest, conditional effect for a player behind in mastery (e.g. a new low-cost defensive card gives an extra shield or one mastery only while trailing). Avoid an unconditional free relic-to-hand for the second reacher: it removes discard-cycle cost and is a major buff, not a small tuning. The current observations do not establish that catch-up is needed.
5. **Record event order before tuning extra turns or thresholds.** Add first-threshold action/turn ordinal and controller, relic first activation, resource state at landmarks, actual prevented damage, and effects attributed to source. This is a proposed future telemetry change, not implemented here.

## Interpretation and telemetry limits

- `StrategyTrace` records only **first milestone ROUND**, not per-turn snapshots, action sequence, within-round crossing order, or mastery later lost. Lower round proves earlier crossing; equal round cannot be resolved, even from seat number.
- Reaching a milestone by game end is survival-selected. Restricting to both reaching it is also selected. Landmark analysis reduces future-information use but remains observational.
- An early-mastery win association cannot separate the relic itself from M5 hero/destiny abilities, mastery-card thresholds, economy, draws, or deck composition before the milestone.
- `healed` counts actual positive HP change. Over-cap healing converted to power is absent, so it underreports healing-effect output for Entropic Talons and Nectar Alchemist.
- `shields_prevented` counts values of shields revealed from hand, even when greater than incoming damage; it excludes passive shields. It is **not actual damage prevented**.
- `damage_dealt` is ordinary post-shield face damage including overkill; Infinity attack amounts are excluded. It does not include all health-loss effects or champion damage.
- `fastplays` includes paid and free effect-driven acquisitions. Full-game totals cannot identify first-turn fast-play behavior without a matching acquisition round and mode history.
- `champions` counts deployment events, not unique acquired champions; redeployment can count repeatedly.
- `cards_drawn` includes initial and cleanup draws. Winning on one's own turn changes the number of completed cleanups; this affects per-turn summaries.
- Final-style regressions in the JSON are **descriptive checks only**. Their end-state exposure is outcome-dependent: e.g. reaching M30 can remove the incentive to Focus, so a negative focus/win association is not evidence to weaken or stop Focus.
- Wilson CIs for game-level rates; adjusted rate CIs use a logistic model with ordered earlier-hero/opponent fixed effects plus earlier-player starting seat, standardized equally over matchups and seats, and sandwich standard errors with one row per game. Sparse/separated adjusted estimates are suppressed. Exploratory intervals are not multiplicity-corrected and are not independent proof of a balance issue.

## Source points inspected

- `Tools/FinalEvaluationHost/StrategyTrace.cs`: milestone rounds, actions/effects aggregation, collections, relic/destiny normal selections.
- `Tools/FinalEvaluationHost/TrainingStatistics.cs:154`: records pre-action round then processes turn-start events in log order.
- `Tools/FinalEvaluationHost/VictoryEvidence.cs`: ordinary damage / Infinity signature / Comet / health-loss attribution.
- `Assets/Scripts/Shards/Engine/ShardsEngine.cs:103`: initial seat resources.
- `Assets/Scripts/Shards/Engine/ShardsEngine.cs:660`: current M5 hero ability costs/effects.
- `Assets/Scripts/Shards/Engine/ShardsEngine.cs:825`: relic threshold and discard destination.
- `Assets/Scripts/Shards/Engine/ShardsEngine.cs:1663`: extra turn does not advance engine round.
- `Assets/Scripts/Shards/Engine/ShardsEngine.cs:1912`: actual healing vs over-cap conversion.

## Follow-up: Conscription availability and the M5 race

The root opening-state analysis identified Unconditional Conscription availability as associated with second-seat advantage. Checking the mechanism:

- Conscription starts available in 758 games. It is selected by seat 0 in 210 (chooser wins 34.3%), seat 1 in 214 (chooser wins 49.5%), and neither in 334 (seat 0 wins 47.9%).
- Its presence **does not appreciably increase second-seat M5 priority**. Seat 0 reaches M5 earlier in 13.46% of available games versus 13.47% when absent; seat 1 earlier 58.05% vs 60.13%; the balance is same-round.
- When seat 0 selects it, selection is later than opponent's destiny in 135 games, same-round in 37, earlier in 35, with opponent never selecting in 3. It often appears to be the later chooser's fallback. This is a hypothesis, not a causal attribution.
- Average selection round is 5.13 for seat 0 vs 4.23 for seat 1. Total recorded activations are similar (2.74 vs 2.69 per game).
- Within each M5-order category, first-seat win rate is descriptively lower with Conscription available: 66.7% vs 73.2% when seat 0 earlier; 35.5% vs 45.1% when seat 1 earlier; 53.7% vs 62.3% when same-round.
- Its source effect is conditional **4 power** after playing at least two nonstarter allies costing 2 or less that turn (`ShardsHorizonSet.cs:538`). Availability fills one of six destiny slots; replacing a weak fallback can change the later chooser's options without changing opening resources.

Reproduction: `pacing_conscription.py --source <strategy-prefix.jsonl> --openings <openings.jsonl> --out <pacing_conscription.json>`. The conditional selection rates must not be interpreted as the causal effect of selecting the destiny.
