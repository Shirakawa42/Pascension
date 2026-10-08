# Overnight training: analysis audit, 27 September 2026

The previous response overstated the depth of the ongoing investigation. The overnight controller ran integrity checks, descriptive statistics checks and checkpoint comparisons. It did **not** continuously investigate strategic mistakes, attribute suspicious outcomes to specific policies, or diagnose and repair arbitrary game problems. The analysis below was performed afterward, prompted by the user.

## What actually happened overnight

Training finished within the authorized deadline. There were29 finalized audit cycles,2,253,312 trained games,2,006 explicitly discarded completed games at collection boundaries, zero censored games, and55 frozen4,096-game comparisons. Four comparisons demonstrated improvement under the declared repeated-test bounds. These include61.36% against the starting accepted reference and, later,55.08% against the retained previous best. The final checkpoint scored54.54% against the retained best, but its repeated-test bound included50%; it was not promoted.

The dashboard's latest-result text could obscure earlier demonstrated improvement. Its summary now reports the historical improved comparisons separately from the latest inconclusive result.

## A real blind spot in automatic analysis

The old warning threshold required at least1,000 observations **within one segment**. Rare natural hero choices never met it, even when their cumulative results were extreme. Across the night:

| Hero | Natural-draft player-games | Natural-draft score | Random-assignment score |
|---|---:|---:|---:|
| Ko Syn Wu | 1,607 | 0.311% | 45.715% |
| Rez | 3,564 | 17.859% | 42.067% |
| Tetra | 563,315 | 57.513% | 65.689% |

Scores include half credit for draws. These are changing-policy player-game associations, not equal-skill hero balance estimates.

Added cumulative disjoint-segment aggregation, a duplicate-segment guard and a regression test reproducing20 sparse segments that should trigger a combined warning. All23 controller tests pass. Reprocessing the completed night now flags both Ko Syn Wu and Rez. This fix is prospective; historical files are not rewritten to pretend the warnings ran overnight.

## Why the natural-draft rankings can be misleading

The opponent archive deliberately retains an untrained initial policy. The logs record25,355 completed games against version0 during397 collections. The low-overhead hero statistics do not attribute each player's outcome to its policy/version.

Two fresh frozen diagnostics support a concrete confounding mechanism:

- The final learner selected Ko Syn Wu zero times across32,768 natural starts.
- In256 seat-swapped games against the retained initial policy, the final learner won255. The initial policy drafted Ko Syn Wu in50 of those games and lost all50; it drafted Rez in58 games and lost all58.

Thus a very weak archived policy can dominate the outcomes for heroes rarely chosen by the strong learner. This reproduces the mechanism, but the historical aggregates cannot prove that it explains every one of the1,607 Ko Syn Wu observations. The natural-draft statistics now explicitly warn about this missing policy attribution. Use frozen policies with controlled hero assignments for balance conclusions.

All29 random-assignment segments recorded all15 relics and all30 destinies being acquired/selected. This establishes acquisition coverage; it does not establish that each was drawn, played, exhausted or used well.

## Fresh tactical checks

Ran80 frozen self-play games each for the starting reference and final learner, using the same predeclared seeds and balanced hero/seat assignments. Only initial hero choices were externally assigned. These diagnostic games do not estimate head-to-head improvement.

| Observation | Starting reference | Final learner |
|---|---:|---:|
| Recorded decisions | 30,995 | 28,706 |
| End-turn selections | 2,030 | 1,924 |
| Ended with a legal play available | 129 | 155 |
| Exhaust selected while the tracked public prerequisite was false | 515 / 6,018 exposed menus | 411 / 4,634 exposed menus |
| Volos modes0 /1 /2 /3, all four available | 42 /3 /13 /4 | 23 /3 /37 /28 |

All four Volos modes were used. Timing remains a concern: these probes do not show that early activations or turn sequencing have been solved. A false prerequisite does not by itself prove a harmful action; some activations retain an unconditional benefit or have no remaining chance to meet the prerequisite. Likewise, keeping shield cards or declining a purchase can be correct. Counterfactual replay is needed before calling each flagged action a mistake or making it automatic.

## Still unproven

The earlier V11 pilot's42.92% regression was real under the declared comparison, and the lower-rate continuation later improved. That intervention does not establish the precise strategic cause of the regression. Nor do these tests prove optimal play, complete semantic coverage, or absence of every game bug. There was no overnight autonomous code-investigation-and-repair loop.

Evidence: `v12-overnight-analysis-2026-09-27.json`, `v12-start-strategy-audit-2026-09-27.json`, `v12-final-strategy-audit-2026-09-27.json`, `v12-natural-hero-anomaly-2026-09-27.json`, and `v12-archive-hero-confounding-2026-09-27.json` in this directory. All new probes performed zero optimizer updates; their frozen policy hashes were checked, and they initialized no CUDA context.
