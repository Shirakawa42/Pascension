# Balance review: accepted AI, 1,500-game snapshot

No game balance was changed. The user accepted the current validated AI and closed the improvement campaign. Further balance edits require individual approval. No retraining or additional statistical samples were added during finalization.

## Cohort integrity

- First 1,500 completions from the original shuffled 2,500-game schedule. Twelve additional published completions and all in-flight games were excluded.
- Retained games: 1,499 decisive outcomes and one draw at round 9. No censored games or round-limit draws.
- Exactly 1,500 distinct original outcome seeds. Acquisition rankings cover the first 1,400 retained games; strategies and detailed balance cover the first 1,393 retained games. These subset sizes are labelled in the UI.
- The hard stop lost buffered detail records. Recovery replays failed exact equivalence (27 of 107 recovered outcomes differed), so none was used. The intact 1,400-game acquisition publication and original 1,393 complete traces were retained; the 1,501-game publication was excluded. Original artifacts remain archived. This is a telemetry/replay limitation, not evidence that the 1,500 original outcomes are wrong.
- The identical frozen AI/search ran on both seats, with random distinct heroes, GPU inference, no Unity and at most eight CPU cores.
- Early stopping leaves 62–82 games per ordered hero pair and can favor shorter completed games. Equal-weight estimates below correct matchup proportions, not completion-time selection or AI-specific preferences.

## Heroes and first-player advantage

Win score counts the one draw as half a win. Intervals are descriptive approximate 95% Wilson intervals on raw game scores; they do not cover uncertainty from AI skill, selection, or multiple comparisons.

| Hero | Games | Wins | Score | Approx. 95% interval | Seat 0 | Seat 1 | Equal seat/opponent weight |
|---|---:|---:|---:|---:|---:|---:|---:|
| Decima | 598 | 237 | 39.7% | 35.9%–43.7% | 46.4% | 32.8% | 39.3% |
| Tetra | 605 | 368 | 60.8% | 56.9%–64.6% | 65.7% | 55.5% | 60.7% |
| Volos | 604 | 315 | 52.2% | 48.2%–56.1% | 59.0% | 45.6% | 52.9% |
| Ko Syn Wu | 602 | 297 | 49.3% | 45.4%–53.3% | 51.4% | 47.5% | 49.0% |
| Rez | 591 | 282 | 47.8% | 43.8%–51.8% | 46.5% | 49.1% | 48.1% |

Seat 0: **808 wins / 1,500 games (53.9% wins; 53.9% including half the draw)**, approximate interval 51.4%–56.4%; equal pair weights: **53.8%**. Seat 1 has 691 wins and the shared draw. Current compensation is six opening cards and one starting mastery for seat 1.

Tetra is the clearest above-target hero; Decima is the clearest below-target hero. Rez and Ko Syn Wu are close to 50%, and Volos has a smaller excess. Adjusting every hero at once would make subsequent effects difficult to attribute.

### Matchups with equal seat weights

| Hero | Decima | Tetra | Volos | Ko Syn Wu | Rez |
|---|---:|---:|---:|---:|---:|
| Decima | — | 32.4% | 39.0% | 42.9% | 43.1% |
| Tetra | 67.6% | — | 51.2% | 62.0% | 61.8% |
| Volos | 61.0% | 48.8% | — | 47.2% | 54.6% |
| Ko Syn Wu | 57.1% | 38.0% | 52.8% | — | 48.2% |
| Rez | 56.9% | 38.2% | 45.4% | 51.8% | — |

## Normal relic picks (1,393-game detail subset)

These counts use the normal mastery-unlocked pick, excluding bonus acquisitions. They are conditional associations, not randomized relic trials. Reaching mastery 10 earlier, deck composition, and already winning positions all affect which relic is chosen.

| Hero | Relic | Picks | Wins | Score | Mean pick round |
|---|---|---:|---:|---:|---:|
| Decima | Praetorian-03 | 261 | 142 | 54.4% | 7.05 |
| Decima | Praetorian-01 | 221 | 74 | 33.5% | 7.80 |
| Decima | Praetorian-02 | 3 | 0 | 0.0% | 8.33 |
| Tetra | Terminal Crescents | 248 | 198 | 79.8% | 6.70 |
| Tetra | Multitask Brain | 248 | 125 | 50.4% | 7.66 |
| Tetra | Datic Robes | 13 | 2 | 15.4% | 7.62 |
| Volos | Entropic Talons | 479 | 257 | 53.7% | 7.07 |
| Volos | Panconscious Crown | 29 | 17 | 58.6% | 7.55 |
| Volos | Unknown God | 6 | 3 | 50.0% | 9.17 |
| Ko Syn Wu | The Heart of Nothing | 321 | 189 | 58.9% | 7.31 |
| Ko Syn Wu | Doom Gate | 140 | 51 | 36.4% | 7.78 |
| Ko Syn Wu | The World Piercer | 40 | 31 | 77.5% | 7.45 |
| Rez | Slipstream Shard | 444 | 225 | 50.7% | 7.18 |
| Rez | Star Seeker | 44 | 25 | 56.8% | 7.45 |
| Rez | Warpquartz | 17 | 9 | 52.9% | 8.00 |

### Tetra: Terminal Crescents versus Multitask Brain

Current Crescents effect: gain 2 mastery, then power equal to half the resulting mastery rounded up; at mastery 20, power equals full mastery. This combines mastery growth, earlier scaling thresholds, and direct damage.

| Relic | Seat 0 score (n) | Seat 1 score (n) | Reached M10 by round 7 score (n) | Reached M10 later score (n) |
|---|---:|---:|---:|---:|
| Terminal Crescents | 83.2% (137) | 75.7% (111) | 82.0% (189) | 72.9% (59) |
| Multitask Brain | 56.3% (126) | 44.3% (122) | 57.1% (119) | 44.2% (129) |

The direction persists in both seats and coarse mastery-timing groups. This strengthens the case for testing a targeted change, but does not remove all selection bias.

**First proposal, pending approval:** Terminal Crescents grants **1 mastery instead of 2**; retain its current damage formula and mastery-20 threshold. This reduces its self-accelerating mastery/damage loop without changing Tetra’s hero power or other relics. The resulting win rate cannot be predicted reliably from observational data.

## Destinies and cards to watch (1,393-game detail subset)

Normal destiny picks with at least 40 observations, ordered by score. Bonus destiny rewards are excluded here. Early acquisition and hero/deck preferences can inflate the score; do not treat this as a list of mandatory nerfs.

| Destiny | Picks | Wins | Score | Mean pick round |
|---|---:|---:|---:|---:|
| Deadly Recruits | 251 | 181 | 72.1% | 3.83 |
| Stolen Futures | 237 | 139 | 58.6% | 4.09 |
| The Agony of Choice | 293 | 165 | 56.3% | 4.01 |
| Strategic Mastermind | 199 | 111 | 55.8% | 4.19 |
| Soul Syphon | 165 | 89 | 53.9% | 4.43 |
| Unconditional Conscription | 237 | 117 | 49.4% | 4.30 |
| Biotech Enhancements | 87 | 42 | 48.3% | 5.03 |
| True Leader | 113 | 50 | 44.2% | 4.62 |
| Nature Dominance | 192 | 84 | 43.8% | 4.45 |
| Bound for Life | 103 | 44 | 42.7% | 4.67 |
| Paradigm Shift | 194 | 82 | 42.3% | 4.56 |
| Synthesis | 126 | 52 | 41.3% | 4.42 |
| Advanced Weapons | 86 | 35 | 40.7% | 4.64 |
| The Last City | 142 | 56 | 39.4% | 4.49 |
| Absorption Grid | 56 | 21 | 37.5% | 4.80 |
| Advanced Medicine | 82 | 30 | 36.6% | 4.80 |
| Project Yggdrasil | 42 | 15 | 35.7% | 4.95 |

Deadly Recruits deserves a later focused review because it combines immediate free play with optional permanent acquisition. High acquisition scores for Comet, finishing fast-plays, and powerful relics obtained as bonus rewards can instead reflect an existing advantage or the winning turn. Low-use defensive relics are not established as weak by this sample.

## Victory pathways (1,393-game detail subset)

| Ending | Games | Share |
|---|---:|---:|
| Mastery / Infinity Shard | 467 | 33.5% |
| Comet | 23 | 1.7% |
| Normal damage | 875 | 62.8% |
| Other health loss | 28 | 2.0% |
| Concession | 0 | 0.0% |
| Unattributed | 0 | 0.0% |
| Draw | 0 | 0.0% |

Mean final round: **10.246**. Game length is not a measure of AI strength.

## Next evaluation

Ask for one specific balance approval at a time. Apply only explicitly approved changes. Keep the accepted model and search fixed for the follow-up so balance effects are not mixed with AI changes. Run exactly 1,500 fresh games with equal ordered distinct-hero pairs (75 per pair), then replace the statistics view with only that cohort. Record the approved patch and compare seat/hero scores and uncertainty. A frozen model may not adapt optimally to a changed rule, so a single snapshot cannot prove optimal human balance.

Snapshot: `8bd708eaba11c7c5`. Policy SHA-256: `c9d751fc2987199ab77ef4b38facc77cad54278822b14dd8493f672d36eb3102`.

Artifacts: `/home/lva/.local/share/shards-training/2026-09-28/ten-hour-improvement/final-statistics-1500`.
