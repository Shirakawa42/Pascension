# One-second game pacing and winning-strategy statistics

The local player waits one second between policy decisions, including before its initial decision. Engine effect resolution remains atomic. Unity compilation and Windows build succeeded. The installed `E:\Bureau\pascension-windows-v1.0.4` player matches the newly built Game assembly hash; the previous complete install is retained in `pascension-windows-v1.0.4-before-paced-ai`.

## Evaluation evidence

The original generation-18232 frozen policy was replayed for exactly 50,000 games with the same seeds, sampling seed, batch size, split configuration and balanced hero routing. No optimizer or training ran. The complete replay took 235.79 seconds. All original totals, rankings, hero-seat rows, hero matchups, hero-choice rows, terminal-round histogram and final-state sums matched exactly. The published dataset still contains 50,000 games, not 100,000.

The new evaluation-only trace records each completed game's seed, winner, round and both players' normal relic/destiny selections, extra destinies, public action counters, mastery milestones and zone-blind permanent final collection. Strategy aggregation validates unique game seeds, exact outcomes and mean rounds before publication. Normal build groups partition exactly 100,000 player outcomes, 49,983 wins and 34 drawn player outcomes.

667 strategy rows were published. These consist of normal hero/relic/destiny combinations, explicitly defined action patterns, and hero-specific patterns. Counts include losing and drawn games; sorting defaults to total wins with a 100-outcome minimum. Patterns overlap. Final-collection supporting cards are observed associations and do not establish causation. Higher threshold achievement can be affected by game length and surviving longer.

The most frequent winning build is Tetra + Terminal Crescents + Stolen Futures: 1,040 wins in 1,270 player outcomes (81.9% observed score). The complete rows are available in the dashboard's raw snapshot.

## Validation and interface

- 200-game capture smoke test: complete trace, exact outcome reconciliation, all player outcomes partitioned.
- 11 automated tests covering aggregation, duplicate/different-cohort rejection, draws, thresholds, supporting-card counts, browser filters, images, details, source isolation and responsive layouts.
- Real-data browser screenshots at 1440px and 430px reviewed; card artwork loaded.
- Unity reports no compile errors; loaded action delay constant is 1.0 seconds.
- Installed assembly SHA256: `86f58721614a8a7a0796610373fd51e6334b1120bddf4fbee6e44d389730de9c`.

Open http://localhost:8768/statistics?view=strategies. Filters support build/pattern/hero-pattern, hero, sample minimum and sorting by wins/score/observations. Images open the existing card-detail modal; expandable supporting-card lists show their prevalence in winning final collections.

## Reproduction

Enable `SHARDS_STRATEGY_TRACE=1` when running `final_evaluation.py` against the extended FinalEvaluationHost. Keep its output separate until complete. Run `publish_strategy_statistics.py --campaign <campaign> --evaluation <completed-output>` to validate and publish. If a replay's base aggregates ever differ, the publisher uses that complete new cohort rather than combining mismatched strategy and card results. Original artifacts remain archived.
