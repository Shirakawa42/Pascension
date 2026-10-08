# Manual game review — 2026-09-27

The AI still makes obvious mistakes. The clearest new finding is that the **eight-decision lookahead can override a sensible policy move with a premature end turn**. Extending the same diagnostic branches to 64 decisions corrected all five confirmed examples of this failure. Other mistakes come from stochastic action selection and the fixed 0.04 value-improvement threshold; greater depth did not fix those.

The user shortened the evaluation to **1,400 games**. The stats window contains exactly those completed games and their matching card, strategy and victory data. No training was run and no policy/search setting was changed for the retained statistics or the installed game.

## Scope and provenance

- Five complete games were selected to cover all five heroes in both seats: 1283, 1313, 1338, 1402 and 1418. I reviewed their chronological decisions, acquisitions, hero modes, threshold sequencing and endings.
- Three additional games were selected specifically to investigate concessions: 298, 377 and 1104.
- Total: **8 complete replays / 2,592 decisions**. This is a diagnostic sample, not an estimate of the population blunder rate.
- Replay winners, rounds, decision counts and search-override counts exactly matched the original records. Alternative lines were applied only to copied states and did not alter the replays or the statistics.
- The original run published 1,409 completions at its next progress checkpoint. Nine extras and all in-flight games were excluded. The raw acquisition snapshot already contained exactly 1,400 outcomes. Thirty-two terminal strategy records still buffered at interruption were recovered by replaying their original seeds, hero assignments and per-seat action RNG streams; none were added as new statistical samples.
- The capped cohort is the first 1,400 completions of the original randomized schedule. Pair counts are no longer exactly balanced; early-completion selection is another limitation when interpreting close rankings.

## Confirmed missed plays

All resources/cards below were visible to the acting player; none of these alternatives depends on knowing a hidden future card.

| Game / round / hero | Actual choice | Engine-checked alternative | Diagnosis |
|---|---|---|---|
| 1313 / 9 / Rez | Ended with Shard Reactor and two Crystals, holding 1 gem | Play them: **6 gems**, enabling several purchases and retaining Scry | Lookahead overrode the policy's Scry choice |
| 1338 / 7 / Tetra | Ended with three Crystals, Blaster, The Dispossessed and a ready Thornshell Warden | Play those cards and exhaust Warden: **+3 gems, +4 power, +2 health** | Lookahead overrode playing a Crystal |
| 1338 / 8 / Tetra | Ended with three Crystals and Order Initiate | Crystals alone provide **3 gems**; Order Initiate then opens its legal reroll decision | Lookahead overrode playing a Crystal; the optional Focus continuation requires resolving that menu first |
| 1313 / 13 / Volos | Ended with three Crystals, Nil Assassin and Wraethe Skirmisher at mastery 19 | Play Crystals, Focus, then both power cards: **mastery 20, 6 power, 2 gems remaining** | Lookahead overrode Nil Assassin |
| 1283 / 13 / Ko Syn Wu | Ended with two Shardwood Guardians | Playing both gives **+4 power, two draws and healing from 45 to 50 HP** in the replay | Lookahead overrode a Guardian; draw identities are not needed to justify the guaranteed resource gains |
| 1402 / 8 / Ko Syn Wu | Ended with J-Chord in hand | Deploy J-Chord and retain an available Warp-6 activation | The sampled end turn survived the value threshold |
| 1402 / 9 / Ko Syn Wu | Repeatedly left J-Chord unplayed | Same available deployment/activation | Policy preferred ending; value estimates were saturated near +1 |
| 1418 / 7 / Tetra | Ended with Blaster, 1 gem and unused Focus at mastery 14 | Play Blaster and Focus: **+1 power and mastery 15** | Sampling chose end turn; the small estimated improvement could not override it |

One additional suspicious position is Tetra's round 12 in game 1338: it ended at 46 HP with a ready Thornshell Warden and a Crystal. I did not include this in the eight counterfactual fixtures above.

## Why the lookahead causes five of these errors

The horizon counts **wrapper decisions**, including selections and Scry/reorder menus. Eight decisions often do not finish a productive turn. An immediate end-turn branch reaches the opponent's next turn quickly, while a play-card branch is evaluated mid-turn with unspent resources, pending choices or cards still to play. These endpoints receive poorly comparable estimates from the learned value head.

The discrepancy is directly visible in the captured scores (value scale −1 to +1, not calibrated win probabilities):

| Position | Depth-8 productive branch | Immediate end turn | Depth-64 productive branch |
|---|---:|---:|---:|
| Rez, game 1313 step 218 | Scry **−0.383** | **+0.037** | Scry **+0.202** |
| Tetra, game 1338 step 151 | Crystal **+0.194** | **+0.408** | Crystal **+0.605** |
| Tetra, game 1338 step 179 | Crystal **−0.308** | **−0.088** | Crystal **+0.260** |
| Volos, game 1313 step 334 | Nil Assassin **+0.157** | **+0.431** | Nil Assassin **+0.600** |
| Ko Syn Wu, game 1283 step 436 | Guardian **+0.931** | **+0.973** | Guardian **+0.991** |

In all five positions the longer diagnostic selected a productive move instead of ending. This is strong evidence for a horizon/evaluation problem in these cases, rather than missing access to the player's resources. It does not establish that a universal depth of 64 is optimal or fast enough: the better implementation direction is to compare branches at a consistent turn boundary and use an adaptive budget.

The inference profile used for the 1,400 games remains unchanged. These were diagnostic alternative searches, not a silent replacement of the evaluated AI.

## Sampling and the override threshold cause a separate failure

The planner starts from a sampled policy move and keeps it unless the selected alternative improves its raw value by more than **0.04**, or has a validated winning line. Near +1 or −1, useful differences can be smaller than that threshold.

For Tetra's missed Blaster + Focus, end turn scored **0.9883**, Focus **0.9901**, and playing Blaster **0.9904**. Focus had policy probability **58.1%**, but end turn was sampled from its **28.5%** probability and remained selected. More depth produced the same outcome. Ko's two J-Chord omissions show the same mechanism.

The 1,400-game cohort contains **14 concessions**. These are actual Concede actions, not misclassified damage or truncations. Three reviewed examples:

- Game 298: Rez conceded at **30 HP / mastery 6**, against Tetra at 44 HP / mastery 15. The policy assigned concession **26.4%** probability. Ending normally scored about **−0.9605**, just below the improvement needed to replace a sampled −1 concession.
- Game 1104: Rez conceded at **33 HP / mastery 8**, against Volos at 43 HP / mastery 13. Concession had **57.2%** policy probability; ending normally scored about **−0.9607**.
- Game 377: Volos conceded at **50 HP / mastery 14**, against Decima at 34 HP / mastery 27. It was behind, but the policy's **37.6%** concession probability is not a proof that continued play cannot win.

For balance evaluation, an AI resignation action should be disabled. A pessimistic critic should not decide which games are allowed to continue to a rules-based conclusion.

## Correct play and rejected false alarms

The review also found competent sequences:

- **Mastery sequencing:** Tetra in game 1338, round 12, used Shard Reactor, its draw ability, then Focus to reach mastery 20 **before** exhausting Oblivion Gatekeeper. This correctly moved the health loss onto the opponent.
- **Mastery win:** Ko in game 1283, round 14, used World Piercer and Focus to reach mastery 30 before playing Infinity Shard and ending with lethal power.
- **Volos synergy:** In game 1313's final turn, Entropic Talons preceded healing from the hero, Soul Syphon and Additri, building **42 power** from a turn that started at 9 HP. The subsequent champion/shield sequence won.
- **Mode coverage:** The reviewed Volos games used all four modes: healing, power, draw and mastery.
- **Inactive abilities:** Leaving Strategic Mastermind unused below 40 HP is legitimate. Leaving Stolen Futures unused below mastery 10 is also legitimate. A merely available exhaust action is not automatically a missed benefit.
- **Finishing hands:** Several end turns with cards remaining were already part of a winning champion/shield-resolution sequence. Those were excluded from the mistake list. The bounded deeper finishing check did not establish a missed lethal at the reviewed end-turn positions; that is not a proof that none exists elsewhere.

## What to fix next

1. Evaluate competing branches at comparable turn boundaries, extending only unfinished branches as needed. Add these five real premature-end positions to the tactical gate.
2. Disable AI concessions in evaluation and play. Preserve the human engine action separately.
3. Replace the fixed-margin reliance on sampled fallbacks for clearly wasteful end turns. Use narrowly verified rules for positive resource plays/unused Focus; do not indiscriminately activate HP-cost effects or inactive destinies.
4. Re-evaluate strength with paired seats **and** full-game behavioral checks before trusting a new balance cohort. A higher aggregate win rate and passing prepared tactical puzzles did not catch these midgame failures.

## Finalized statistics

[Statistics window](http://localhost:8768/statistics)

- Exactly **1,400 games**, no drawn or censored outcomes in the retained cohort.
- Seat 0: **749 wins (53.5%)**; seat 1: **651 wins (46.5%)**.
- Mean final round: **10.8614**.
- Wins: **482 mastery**, **7 Comet**, **850 normal damage**, **47 other health loss**, **14 concessions**, **0 unattributed**.

The data is useful for describing this policy and finding problems. I would not use small hero/card win-rate differences for fine balance tuning until these decision failures are corrected.

Artifacts:

- `manual-game-review-2026-09-27/verified-positions.json`: concrete states, alternatives and depth comparisons.
- `manual-game-review-2026-09-27/chronological-transcripts.md`: all decisions in the five hero-coverage games.
- Full snapshots and branch diagnostics: `/home/lva/.local/share/shards-training/2026-09-27/manual-review-depth64/reviews/`.
- Final frozen cohort: `/home/lva/.local/share/shards-training/2026-09-27/lookahead-statistics-1400/`.
