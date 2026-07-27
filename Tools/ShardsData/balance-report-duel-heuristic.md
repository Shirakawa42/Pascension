# SoI Balance Report

## 1. Reproducibility

- Generated: 2026-07-27 12:16 UTC · git `1def077` · schema 1
- Bots: **bench:heuristic** · DLC mask 15 · seed base 1 · tag `heuristic-duel`
- Config hash: `sha256:65619c1c644157eaf87df0ea8ecb012f0142b338489389d8cbb7e9c91c2b5a68`
- Games: **30000** (29976 decisive, 24 ties, 0 failures)

## 2. Game health

- Rounds p10/p50/p90: **10 / 12 / 15** · avg submits/game 283
- Tie rate: 0.1% [0.1%–0.1%] · failures (guard/stall/error): **0**
- Win type: 28261 kill / 1715 Infinity-Shard overwhelm (5.7% [5.5%–6.0%] of wins — mastery-race viability)
- Comeback wins (winner behind on health at midpoint): 41.8% [41.2%–42.4%]
- Shields prevented 674421 of 21964956 incoming damage (3.1%)

## 3. Seat advantage (staggered start: P0 M0, P1 M1)

- P0 win rate, all decisive games: **58.5% [58.0%–59.1%]** (n=29976)
- P0 win rate, mirror matches only (no character confound): **–** (n=0)

## 4. Characters

| Character | Player-games | Win score |
|---|---|---|
| decima | 18000 | 55.2% [54.5%–56.0%] |
| volos | 10000 | 52.9% [51.9%–53.9%] |
| tetra | 12000 | 48.5% [47.6%–49.4%] |
| rez | 10000 | 45.4% [44.4%–46.4%] |
| kosynwu | 10000 | 44.1% [43.1%–45.1%] |

Matchups (win score of the alphabetically-first character; mirrors show seat-0 score):

| Matchup | Games | First's score |
|---|---|---|
| decima:kosynwu | 4000 | 58.7% |
| decima:rez | 4000 | 59.6% |
| decima:tetra | 6000 | 52.4% |
| decima:volos | 4000 | 51.8% |
| kosynwu:rez | 2000 | 49.5% |
| kosynwu:tetra | 2000 | 45.5% |
| kosynwu:volos | 2000 | 42.9% |
| rez:tetra | 2000 | 50.6% |
| rez:volos | 2000 | 44.9% |
| tetra:volos | 2000 | 44.2% |

## 5. Playstyles (observational!)

Win rate by feature quartile (Q1 lowest → Q4 highest), plus logistic odds ratio per +1 SD:

| Feature | Q1 | Q2 | Q3 | Q4 | OR/SD | p |
|---|---|---|---|---|---|---|
| factionConcentration | 55.6% | 52.1% | 47.5% | 44.9% | 1.07 | 0.000 |
| avgBuyCost | 40.3% | 47.7% | 53.0% | 59.0% | 1.22 | 0.000 |
| championShare | 36.7% | 47.7% | 54.4% | 61.2% | 1.37 | 0.000 |
| focusCount | 44.6% | 47.8% | 50.6% | 57.0% | 1.17 | 0.000 |
| masteryAtRound8 | 50.3% | 47.3% | 47.7% | 54.8% | 1.26 | 0.000 |
| totalAcquisitions | 41.0% | 43.6% | 50.3% | 65.0% | 1.84 | 0.000 |
| earlyAggression | 44.1% | 44.6% | 49.4% | 61.9% | 1.79 | 0.000 |

Win rate by dominant purchase faction:

| Faction | Player-games | Win rate |
|---|---|---|
| Homodeus | 14882 | 53.6% [52.8%–54.4%] |
| Undergrowth | 12130 | 53.1% [52.2%–54.0%] |
| Aion | 172 | 51.2% [43.7%–58.5%] |
| Order | 10732 | 48.0% [47.1%–49.0%] |
| Wraethe | 22036 | 46.8% [46.2%–47.5%] |

## 6. Cards

### Flagged (BH FDR 10%, |Δ| ≥ 5 pts, ≥ 100 acquisitions)

| Card | Cost | Impact Δ | p | Buy rate | Co-acquired (lift) |
|---|---|---|---|---|---|
| Ojas, Genesis Druid (Undergrowth) | 4 | **+47.6 pts** | 0.0000 | 1.8% | portal_monk ×6.1, bleak_communion ×5.1, raidian ×4.2, grim_tutor ×3.8, reactor_drone_duel ×3.6 |
| Longshot (Aion) | 4 | **+39.0 pts** | 0.0000 | 1.7% | portal_monk ×7.7, bleak_communion ×4.4, grim_tutor ×4.2, raidian ×4.1, cryptofist_monk ×4.0 |
| Drakonarius (Homodeus) | 6 | **+37.2 pts** | 0.0000 | 90.2% | aegis_archivist ×1.3, portal_monk ×1.3, general_decurion ×1.3, reactor_drone_duel ×1.2, swyft_duel ×1.2 |
| Breaker (Aion) | 6 | **+33.1 pts** | 0.0000 | 5.6% | portal_monk ×13.6, bleak_communion ×7.0, cryptofist_monk ×6.7, grim_tutor ×6.4, root_of_the_forest_duel ×5.9 |
| Axia (Homodeus) | 8 | **+31.0 pts** | 0.0000 | 80.3% | testudo_vanguard ×1.6, aegis_archivist ×1.6, general_decurion ×1.5, portal_monk ×1.5, raidian ×1.5 |
| Lifebloom Ritual (Undergrowth) | 6 | **+27.4 pts** | 0.0000 | 4.0% | portal_monk ×14.3, grim_tutor ×6.5, j_chord_duel ×5.3, bleak_communion ×5.3, reactor_drone_duel ×3.6 |
| Orm Madu (Undergrowth) | 7 | **+25.3 pts** | 0.0000 | 1.1% | raidian ×6.4, cryptofist_monk ×5.4, oblivion_gatekeeper ×4.0, general_decurion ×3.9, bleak_communion ×3.4 |
| Optio Crusher (Homodeus) | 5 | **+22.2 pts** | 0.0000 | 92.8% | aegis_archivist ×1.2, general_decurion ×1.2, portal_monk ×1.2, grim_tutor ×1.2, axia_duel ×1.2 |
| Ru Bo Vai, The Transcendant (Wraethe) | 5 | **+21.6 pts** | 0.0000 | 91.4% | portal_monk ×1.3, general_decurion ×1.3, raidian ×1.3, aegis_archivist ×1.3, bleak_communion ×1.2 |
| Swyft (Aion) | 5 | **+20.2 pts** | 0.0000 | 93.1% | general_decurion ×1.7, root_of_the_forest_duel ×1.7, zen_chi_set ×1.5, cryptofist_monk ×1.4, omnius ×1.4 |
| Root of the Forest (Undergrowth) | 7 | **+19.2 pts** | 0.0000 | 32.9% | general_decurion ×2.9, raidian ×2.7, cryptofist_monk ×2.5, century_forge ×2.2, aegis_archivist ×2.2 |
| Additri, Gaiamancer (Undergrowth) | 5 | **+18.4 pts** | 0.0000 | 94.6% | aegis_archivist ×1.2, portal_monk ×1.2, root_of_the_forest_duel ×1.2, general_decurion ×1.2, bleak_communion ×1.2 |
| Giga, Source Adept (Order) | 2 | **+18.2 pts** | 0.0000 | 97.6% | portal_monk ×1.5, general_decurion ×1.4, reactor_drone_duel ×1.4, root_of_the_forest_duel ×1.3, cryptofist_monk ×1.3 |
| Bleak Communion (Wraethe) | 3 | **+18.1 pts** | 0.0000 | 10.9% | portal_monk ×5.7, grim_tutor ×5.6, j_chord_duel ×4.6, reactor_drone_duel ×4.3, lucky ×3.1 |
| Taur, Arachpriest (Undergrowth) | 5 | **+17.5 pts** | 0.0000 | 1.6% | portal_monk ×20.7, bleak_communion ×4.9, grim_tutor ×4.5, raidian ×3.5, reactor_drone_duel ×3.1 |
| Systema A.I. (Order) | 3 | **+17.5 pts** | 0.0000 | 97.3% | general_decurion ×1.6, raidian ×1.4, cryptofist_monk ×1.4, root_of_the_forest_duel ×1.4, portal_monk ×1.4 |
| Zen Chi Set, Godkiller (Wraethe) | 7 | **+16.4 pts** | 0.0000 | 55.0% | general_decurion ×1.9, aegis_archivist ×1.8, raidian ×1.7, root_of_the_forest_duel ×1.7, century_forge ×1.6 |
| Omnius, The All-Knowing (Order) | 6 | **+15.8 pts** | 0.0000 | 71.2% | portal_monk ×1.9, general_decurion ×1.9, root_of_the_forest_duel ×1.8, raidian ×1.7, aegis_archivist ×1.7 |
| Evokatus (Homodeus) | 4 | **+15.5 pts** | 0.0000 | 94.6% | root_of_the_forest_duel ×1.2, portal_monk ×1.2, general_decurion ×1.2, axia_duel ×1.2, raidian ×1.2 |
| General Decurion (Homodeus) | 7 | **+15.2 pts** | 0.0000 | 40.9% | root_of_the_forest_duel ×2.9, raidian ×2.8, cryptofist_monk ×2.5, century_forge ×2.4, aegis_archivist ×2.3 |
| Portal Monk (Order) | 3 | **+15.1 pts** | 0.0000 | 10.7% | raidian ×6.7, grim_tutor ×6.2, bleak_communion ×5.7, reactor_drone_duel ×4.5, j_chord_duel ×4.5 |
| J-Chord (Aion) | 3 | **+14.5 pts** | 0.0000 | 85.6% | grim_tutor ×4.7, bleak_communion ×4.6, portal_monk ×4.5, reactor_drone_duel ×3.5, cryptofist_monk ×2.6 |
| Cryptofist Monk (Order) | 5 | **+14.4 pts** | 0.0000 | 25.3% | portal_monk ×4.0, raidian ×3.1, grim_tutor ×2.8, j_chord_duel ×2.6, root_of_the_forest_duel ×2.5 |
| Raidian, Cloud Master (Order) | 5 | **+14.3 pts** | 0.0000 | 12.5% | portal_monk ×6.7, cryptofist_monk ×3.1, general_decurion ×2.8, g_48 ×2.7, root_of_the_forest_duel ×2.7 |
| Numeri Drones (Homodeus) | 3 | **+14.2 pts** | 0.0000 | 96.7% | root_of_the_forest_duel ×1.6, raidian ×1.5, general_decurion ×1.5, axia_duel ×1.5, cryptofist_monk ×1.4 |
| Zetta, The Encryptor (Order) | 5 | **+14.0 pts** | 0.0009 | 1.5% | portal_monk ×22.7, grim_tutor ×5.5, bleak_communion ×5.0, raidian ×4.6, reactor_drone_duel ×4.1 |
| Thornshell Warden (Undergrowth) | 2 | **+13.5 pts** | 0.0000 | 97.6% | portal_monk ×1.4, bleak_communion ×1.4, grim_tutor ×1.3, raidian ×1.3, general_decurion ×1.3 |
| The Grand Architect (Order) | 7 | **+12.5 pts** | 0.0000 | 81.3% | general_decurion ×1.7, aegis_archivist ×1.6, cryptofist_monk ×1.5, century_forge ×1.5, root_of_the_forest_duel ×1.4 |
| Primus Pilus (Homodeus) | 2 | **+12.3 pts** | 0.0000 | 97.7% | raidian ×1.5, general_decurion ×1.5, grim_tutor ×1.4, cryptofist_monk ×1.4, root_of_the_forest_duel ×1.3 |
| Thorn Zealot (Undergrowth) | 3 | **+12.3 pts** | 0.0000 | 92.1% | portal_monk ×1.4, root_of_the_forest_duel ×1.3, grim_tutor ×1.3, general_decurion ×1.3, cryptofist_monk ×1.3 |
| Grim Tutor (Wraethe) | 3 | **+11.7 pts** | 0.0000 | 10.6% | portal_monk ×6.2, bleak_communion ×5.6, j_chord_duel ×4.7, reactor_drone_duel ×4.0, lucky ×3.1 |
| Li Hin, The Shattered (Wraethe) | 3 | **+11.0 pts** | 0.0000 | 96.2% | bleak_communion ×1.3, grim_tutor ×1.3, raidian ×1.3, portal_monk ×1.2, general_decurion ×1.2 |
| Century Forge (Homodeus) | 5 | **+10.7 pts** | 0.0000 | 39.0% | portal_monk ×2.8, cryptofist_monk ×2.4, general_decurion ×2.4, root_of_the_forest_duel ×2.2, raidian ×2.1 |
| Fao Cu'tul, The Formless (Wraethe) | 4 | **+10.7 pts** | 0.0000 | 90.7% | aegis_archivist ×1.2, cryptofist_monk ×1.2, general_decurion ×1.2, grim_tutor ×1.2, portal_monk ×1.2 |
| The Dispossessed (Wraethe) | 1 | **+10.6 pts** | 0.0000 | 98.9% | portal_monk ×1.4, bleak_communion ×1.3, cryptofist_monk ×1.3, grim_tutor ×1.2, root_of_the_forest_duel ×1.2 |
| Aetherbreaker (Wraethe) | 4 | **+10.6 pts** | 0.0000 | 89.6% | general_decurion ×1.4, root_of_the_forest_duel ×1.3, portal_monk ×1.3, cryptofist_monk ×1.3, raidian ×1.2 |
| The Rotten (Undergrowth) | 3 | **+10.4 pts** | 0.0000 | 97.2% | portal_monk ×1.3, reactor_drone_duel ×1.2, cryptofist_monk ×1.2, bleak_communion ×1.2, grim_tutor ×1.2 |
| Furrowing Elemental (Undergrowth) | 5 | **+10.3 pts** | 0.0000 | 52.7% | cryptofist_monk ×2.0, general_decurion ×1.9, portal_monk ×1.9, root_of_the_forest_duel ×1.9, grim_tutor ×1.8 |
| Brute (Aion) | 3 | **+10.0 pts** | 0.0000 | 96.9% | grim_tutor ×1.2, portal_monk ×1.2, reactor_drone_duel ×1.2, raidian ×1.2, aegis_archivist ×1.2 |
| Undergrowth Aspirant (Undergrowth) | 1 | **+9.3 pts** | 0.0000 | 99.0% | bleak_communion ×1.4, grim_tutor ×1.3, portal_monk ×1.3, reactor_drone_duel ×1.3, cryptofist_monk ×1.3 |
| Hounds of Volos (Undergrowth) | 3 | **+9.3 pts** | 0.0000 | 85.0% | portal_monk ×1.6, grim_tutor ×1.5, bleak_communion ×1.4, reactor_drone_duel ×1.4, raidian ×1.3 |
| Aegis Archivist (Order) | 4 | **+9.2 pts** | 0.0000 | 94.9% | general_decurion ×2.3, root_of_the_forest_duel ×2.2, raidian ×2.0, zen_chi_set ×1.8, cryptofist_monk ×1.8 |
| Anomaly Cleric (Order) | 5 | **+8.2 pts** | 0.0000 | 83.9% | raidian ×1.7, root_of_the_forest_duel ×1.7, general_decurion ×1.7, cryptofist_monk ×1.6, portal_monk ×1.5 |
| Torian Commandos (Homodeus) | 3 | **+8.2 pts** | 0.0000 | 97.0% | raidian ×1.3, general_decurion ×1.3, cryptofist_monk ×1.3, root_of_the_forest_duel ×1.3, century_forge ×1.3 |
| Carnivorous Vine (Undergrowth) | 4 | **+7.8 pts** | 0.0000 | 96.2% | raidian ×1.3, portal_monk ×1.3, bleak_communion ×1.3, reactor_drone_duel ×1.2, cryptofist_monk ×1.2 |
| Shardwood Guardian (Undergrowth) | 4 | **+7.7 pts** | 0.0000 | 85.0% | general_decurion ×1.3, root_of_the_forest_duel ×1.3, raidian ×1.3, cryptofist_monk ×1.3, century_forge ×1.2 |
| Dash (Aion) | 2 | **+7.4 pts** | 0.0000 | 97.9% | portal_monk ×1.4, bleak_communion ×1.4, grim_tutor ×1.3, root_of_the_forest_duel ×1.3, raidian ×1.3 |
| Ghostwillow Avenger (Undergrowth) | 4 | **+7.4 pts** | 0.0000 | 80.2% | general_decurion ×1.6, root_of_the_forest_duel ×1.4, raidian ×1.4, grim_tutor ×1.3, cryptofist_monk ×1.3 |
| Testudo Vanguard (Homodeus) | 4 | **+7.4 pts** | 0.0000 | 92.0% | root_of_the_forest_duel ×1.8, general_decurion ×1.7, zen_chi_set ×1.6, axia_duel ×1.6, raidian ×1.5 |
| Reactor Drone (Homodeus) | 3 | **+6.8 pts** | 0.0000 | 18.0% | portal_monk ×4.5, bleak_communion ×4.3, grim_tutor ×4.0, j_chord_duel ×3.5, lucky ×2.9 |
| Data Heretic (Order) | 4 | **+6.8 pts** | 0.0000 | 45.4% | raidian ×2.1, cryptofist_monk ×1.9, century_forge ×1.8, general_decurion ×1.8, portal_monk ×1.7 |
| Prism (Aion) | 3 | **+6.8 pts** | 0.0000 | 73.3% | grim_tutor ×1.8, bleak_communion ×1.7, portal_monk ×1.7, reactor_drone_duel ×1.5, raidian ×1.5 |
| Zara Ra, Soulflayer (Wraethe) | 5 | **+6.4 pts** | 0.0000 | 91.8% | aegis_archivist ×1.3, portal_monk ×1.3, cryptofist_monk ×1.3, general_decurion ×1.3, bleak_communion ×1.3 |
| Lucky (Aion) | 4 | **+5.6 pts** | 0.0000 | 65.7% | portal_monk ×3.3, bleak_communion ×3.1, grim_tutor ×3.1, reactor_drone_duel ×2.9, raidian ×1.9 |
| Arach Devotees (Undergrowth) | 2 | **+5.5 pts** | 0.0000 | 96.6% | general_decurion ×1.3, cryptofist_monk ×1.2, root_of_the_forest_duel ×1.2, bleak_communion ×1.2, century_forge ×1.2 |

Positive Δ + healthy buy rate ⇒ nerf candidate; negative Δ or rock-bottom buy rate ⇒ buff candidate. Cross-check the co-acquisition column before blaming a single card.

### Buy-rate outliers by cost band (full table in sim-summary.csv)

- Cost 1–3: least bought — Grim Tutor 10.6%, Portal Monk 10.7%, Bleak Communion 10.9% · most bought — Undergrowth Aspirant 99.0%, Wraethe Skirmisher 98.9%, The Dispossessed 98.9%
- Cost 4–6: least bought — Zetta, The Encryptor 1.5%, Taur, Arachpriest 1.6%, Longshot 1.7% · most bought — Carnivorous Vine 96.2%, Aegis Archivist 94.9%, The Lost 94.9%
- Cost 7+: least bought — Comet 0.0%, Orm Madu 1.1%, Root of the Forest 32.9% · most bought — The Grand Architect 81.3%, Axia 80.3%, Zen Chi Set, Godkiller 55.0%

## 7. Relics, destinies, monsters

| Relic | Recruits | WR when recruited |
|---|---|---|
| Datic Robes | 7991 | 53.2% [52.1%–54.3%] |
| Doom Gate | 6760 | 47.6% [46.4%–48.8%] |
| Entropic Talons | 6919 | 57.2% [56.0%–58.3%] |
| The Heart of Nothing | 1476 | 74.5% [72.2%–76.6%] |
| Multitask Brain | 948 | 75.7% [72.9%–78.4%] |
| Panconscious Crown | 762 | 73.4% [70.1%–76.4%] |
| Praetorian-01 | 12480 | 59.5% [58.6%–60.3%] |
| Praetorian-02 | 1480 | 74.4% [72.1%–76.6%] |
| Praetorian-03 | 49 | 71.4% [57.6%–82.2%] |
| Slipstream Shard | 6815 | 49.3% [48.1%–50.5%] |
| Star Seeker | 950 | 71.4% [68.4%–74.2%] |
| Terminal Crescents | 25 | 84.0% [65.3%–93.6%] |
| Unknown God | 13 | 69.2% [42.4%–87.3%] |
| Warpquartz | 34 | 73.5% [56.9%–85.4%] |
| The World Piercer | 446 | 86.3% [82.8%–89.2%] |

| Destiny | In initial row | Taken | Avg round | WR taken | WR not taken |
|---|---|---|---|---|---|
| The Price of Power | 12258 | 2709 (22.1%) | 7.5 | 53.3% [51.4%–55.2%] | 49.1% [48.1%–50.1%] |
| Project Yggdrasil | 12030 | 2625 (21.8%) | 7.5 | 52.8% [50.9%–54.7%] | 49.2% [48.2%–50.2%] |
| Datic Secrets | 12034 | 2611 (21.7%) | 7.6 | 49.4% [47.5%–51.4%] | 50.2% [49.1%–51.2%] |
| Synthesis | 12010 | 2601 (21.7%) | 7.5 | 50.4% [48.5%–52.3%] | 49.9% [48.9%–50.9%] |
| Strategic Mastermind | 12140 | 2626 (21.6%) | 7.5 | 52.5% [50.6%–54.4%] | 49.3% [48.3%–50.3%] |
| Whatever it Takes | 12124 | 2618 (21.6%) | 7.7 | 61.3% [59.5%–63.2%] | 46.9% [45.9%–47.9%] |
| Unconditional Conscription | 12152 | 2620 (21.6%) | 7.7 | 58.9% [57.0%–60.8%] | 47.5% [46.5%–48.5%] |
| Paradigm Shift | 12018 | 2582 (21.5%) | 7.7 | 53.3% [51.4%–55.2%] | 49.1% [48.1%–50.1%] |
| Bound for Life | 11982 | 2574 (21.5%) | 7.7 | 48.6% [46.6%–50.5%] | 50.4% [49.4%–51.4%] |
| The Shard Defiant | 11984 | 2572 (21.5%) | 7.6 | 50.7% [48.8%–52.6%] | 49.8% [48.8%–50.8%] |
| The Agony of Choice | 11826 | 2537 (21.5%) | 7.5 | 63.4% [61.5%–65.2%] | 46.3% [45.3%–47.3%] |
| The Crystal Gate | 12170 | 2607 (21.4%) | 7.5 | 50.2% [48.3%–52.1%] | 50.0% [49.0%–51.0%] |
| War Bound | 12274 | 2628 (21.4%) | 7.6 | 57.0% [55.1%–58.8%] | 48.1% [47.1%–49.1%] |
| Soul Syphon | 12060 | 2572 (21.3%) | 7.5 | 61.8% [59.9%–63.6%] | 46.8% [45.8%–47.8%] |
| Biotech Enhancements | 11942 | 2540 (21.3%) | 7.6 | 53.1% [51.1%–55.0%] | 49.2% [48.2%–50.2%] |
| Absorption Grid | 11934 | 2536 (21.3%) | 7.5 | 56.3% [54.4%–58.2%] | 48.3% [47.3%–49.3%] |
| Advanced Medicine | 12072 | 2562 (21.2%) | 7.5 | 58.7% [56.8%–60.6%] | 47.6% [46.6%–48.6%] |
| Nature Dominance | 11980 | 2542 (21.2%) | 7.6 | 58.1% [56.1%–60.0%] | 47.8% [46.8%–48.8%] |
| Phasic Technology | 11980 | 2538 (21.2%) | 7.6 | 58.7% [56.8%–60.6%] | 47.7% [46.7%–48.7%] |
| Advanced Weapons | 11594 | 2454 (21.2%) | 7.6 | 57.0% [55.0%–59.0%] | 48.1% [47.1%–49.1%] |
| Forged in Flame | 11882 | 2510 (21.1%) | 7.6 | 48.1% [46.2%–50.1%] | 50.5% [49.5%–51.5%] |
| Blood for Blood | 11820 | 2484 (21.0%) | 7.5 | 48.2% [46.3%–50.2%] | 50.5% [49.5%–51.5%] |
| Maglev Tunnels | 11976 | 2510 (21.0%) | 7.7 | 51.8% [49.8%–53.7%] | 49.5% [48.5%–50.5%] |
| The Last City | 11832 | 2469 (20.9%) | 7.6 | 51.0% [49.1%–53.0%] | 49.7% [48.7%–50.7%] |
| Healing Hands | 11892 | 2480 (20.9%) | 7.5 | 54.1% [52.1%–56.1%] | 48.9% [47.9%–49.9%] |
| Power Struggle | 11972 | 2479 (20.7%) | 7.6 | 51.3% [49.3%–53.3%] | 49.7% [48.7%–50.7%] |
| True Leader | 12076 | 2492 (20.6%) | 7.5 | 51.2% [49.2%–53.2%] | 49.7% [48.7%–50.7%] |
| Deadly Recruits | 11864 | 2428 (20.5%) | 7.6 | 58.5% [56.5%–60.4%] | 47.8% [46.8%–48.8%] |
| One Mind One Army | 11992 | 2431 (20.3%) | 7.6 | 49.2% [47.3%–51.2%] | 50.2% [49.2%–51.2%] |
| Stolen Futures | 12130 | 2448 (20.2%) | 7.4 | 53.4% [51.5%–55.4%] | 49.1% [48.1%–50.1%] |

| Monster | Revealed | Defeated | Avg defeat round | Defeater WR |
|---|---|---|---|---|
| Ingeminex: Agony | 8258 | 7360 (89.1%) | 10.2 | 67.9% [66.8%–68.9%] |
| Ingeminex: Brutality | 8323 | 7205 (86.6%) | 10.0 | 73.5% [72.5%–74.5%] |
| Ingeminex: Corruption | 8419 | 7254 (86.2%) | 10.2 | 71.6% [70.6%–72.7%] |
| Ingeminex: Malice | 8334 | 7412 (88.9%) | 10.2 | 64.7% [63.6%–65.7%] |
| Ingeminex: Torment | 8378 | 7540 (90.0%) | 10.2 | 64.4% [63.3%–65.4%] |

Monster attacks landed: 30189

## 8. Methodology & caveats

- Every proportion carries a Wilson 95% interval; per-card deltas are stratified by matchup×seat, inverse-variance pooled, and Benjamini-Hochberg corrected (FDR 10%) with an effect floor.
- **These are correlations between THESE bots' policies, not causal card effects.** A card bought when already ahead will look like a winner. Treat findings as directional input; re-test surprising ones with a targeted A/B (forced-strategy bot variant) before patching.
- Seat counts are balanced exactly 50/50 per matchup by construction; seeds are sequential and reproducible (`soisim run --seed-base 1`).
