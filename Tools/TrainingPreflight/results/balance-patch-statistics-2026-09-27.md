# Balance patch statistics — 27 September 2026

The live observatory has a **Balance patch** tab at http://localhost:8768/statistics?view=balance.
Only the final frozen 50,000-game population is published. No training or optimizer updates were run.

## Finishing causes

| Actual ending | Games |
|---|---:|
| Mastery / Infinity Shard | 17,971 |
| Comet | 250 |
| Normal assigned damage | 28,994 |
| Other direct health loss | 2,092 |
| Concession | 676 |
| Draw | 17 |
| Unattributed | 0 |
| Total | 50,000 |

Mastery counts lethal damage after the **resolved +9994 Infinity Shard M30 power effect**, not simply a winning player having 30 mastery. Comet counts the **resolved DestroyOpponent health-loss effect** (-1,000,000), including copies. These event signatures are specific to the frozen content and verified against actual engine executions. If either implementation changes in a balance patch, update these assertions and the attribution rules together.

Concessions are policy-selected legal endings, not normal-damage wins. Other health loss includes monster/direct-loss effects; the current log cannot reliably attribute all these to individual sources. Do not silently redistribute these into the three primary win methods.

## Useful new views

- Exclusive win-method counts with hero and seat filters; each winning game contributes once.
- End-round histogram, median, p10/p90, winner health and final mastery distributions.
- First reach of mastery 5/10/20/30 and normal relic/destiny picks: count, reach rate, mean round and subsequent observed score.
- Twenty activity/economy counters, totals, averages per player-game, per turn started and per winning player-game.
- Acquisitions versus observed play/fast-play/deployment/activation; cards have images and rules, type/search/sample filters, CSV export. Includes banish and reroll counts by definition.
- Normal destiny selections separated from extra reward destinies and their acquisition timing.
- Resolved card-mode choices and all four Volos modes, both event counts and player-game outcomes.
- Comet appearance/acquisition/play funnel and acquisition-to-play latency.
- Complete analysis JSON export with evaluation provenance.

The all-hero/all-seat group contains 100,000 correlated player outcomes from 50,000 games. Its score is necessarily 50%; it is not a strength measurement. Conditional card/milestone scores are not causal balance estimates. Passive destinies need not emit play or activation events. Card-use coverage is at definition/player-game level, not per physical copy. Acquisition includes effect grants and temporary fast-plays; no opportunity-based pick rates are claimed.

## Additional observations

Comet appeared in the market in 16,671 games. 591 players acquired it; 246 played it from hand, and 345 acquirers did not. Four additional Comet-effect wins occurred without the winner playing Comet from hand (copied effects). Among acquirers who played it, the mean acquisition-to-play interval was 1.028 rounds. This is conditional on having played it, not an unconditional latency estimate.

Volos resolved 103,717 healing choices, 20,153 power choices, 25,110 draw choices and 17,835 mastery choices. All four modes have direct recorded usage.

| Hero | Mastery wins | Comet wins | Normal damage wins |
|---|---:|---:|---:|
| Decima | 4,449 | 77 | 3,625 |
| Tetra | 2,280 | 34 | 10,950 |
| Volos | 2,337 | 42 | 6,962 |
| Ko Syn Wu | 3,331 | 36 | 5,111 |
| Rez | 5,574 | 61 | 2,346 |

These routes describe the current policy. They do not establish that a hero has exhausted its possible strategies.

## Provenance and checks

- Evaluation directory: `/home/lva/.local/share/shards-training/2026-09-26/final-50000-balance-audit`.
- Checkpoint: retained segment 0029, generation 18232.
- Policy SHA-256: `945b40d0ae793ebd93603c7d64339a2774bb88f07b9bd90763637f88bfbd70cc`.
- Evaluation-host SHA-256: `e07845d52c0e4361938466c0b6c3befeeb3a330d5742930a83deeb93b02045b9`.
- Per-game trace SHA-256: `3a6eb8032d7155c1129c462181a7ca11b8f375e167931b104d65097f10fec2e0`.
- Replay elapsed: 238.654 seconds. Every ordered distinct-hero pair has 2,500 games.
- All original aggregate outcomes, rankings, hero/seat rows, matchups, per-hero acquisitions, final-round histograms and final-state sums reproduced **exactly**.
- All 2,087,003 card/relic/destiny acquisition events reconcile to the original ranking event totals, by definition.
- All 18 hero/seat groups reconcile winner counts, round distributions and winner-health samples.
- C# verifier: 12 attribution checks, including actual Infinity M30, Infinity below M30 and Comet engine finishes, plus ownership-only and stale-turn counterexamples.
- Python: balance aggregation, strategy aggregation, monitor publication/HTTP, dashboard browser, asset and training-dashboard regression suites pass. Browser fixtures exercise desktop and mobile layouts, every new panel, filters, images, details and CSV output.
- Live API and real desktop/mobile screenshots reviewed. Enlarged the bounded final-statistics reader from 4 MiB to 16 MiB; the enriched snapshot is approximately 5.7 MiB. Invalid/oversized final files now produce an explicit monitor error instead of silently appearing as no data.

Reproduction:

```sh
/home/lva/.dotnet/dotnet run --project Tools/BalanceTraceVerify -c Release
PYTHONPATH=Tools/TrainingPreflight /home/lva/.venvs/shards-preflight/bin/python -m unittest test_balance_analysis test_strategy_statistics test_statistics_dashboard test_statistics_assets test_training_dashboard test_monitor_training -q
```

The replay is passive telemetry only. The in-game policy and one-second action pacing are unchanged.
