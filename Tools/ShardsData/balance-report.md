# SoI Balance Report

## 1. Reproducibility

- Generated: 2026-07-21 14:17 UTC · git `7fc453b` · schema 1
- Bots: **heuristic-v1** · DLC mask 7 · seed base 1 · tag `heuristic-baseline`
- Config hash: `sha256:d2318a1b273e15df312d44f392fa8f6aa61c67b4762e041c4be5c2bc93f22b4e`
- Games: **30000** (29963 decisive, 37 ties, 0 failures)

## 2. Game health

- Rounds p10/p50/p90: **9 / 11 / 14** · avg submits/game 246
- Tie rate: 0.1% [0.1%–0.2%] · failures (guard/stall/error): **0**
- Win type: 29251 kill / 712 Infinity-Shard overwhelm (2.4% [2.2%–2.6%] of wins — mastery-race viability)
- Comeback wins (winner behind on health at midpoint): 38.6% [38.0%–39.1%]
- Shields prevented 505341 of 11135875 incoming damage (4.5%)

## 3. Seat advantage (staggered start: P0 M0, P1 M1)

- P0 win rate, all decisive games: **58.6% [58.0%–59.1%]** (n=29963)
- P0 win rate, mirror matches only (no character confound): **59.0% [58.0%–59.9%]** (n=9990)

## 4. Characters

| Character | Player-games | Win score |
|---|---|---|
| volos | 12000 | 52.5% [51.6%–53.4%] |
| decima | 12000 | 50.8% [49.9%–51.7%] |
| tetra | 12000 | 50.5% [49.6%–51.4%] |
| kosynwu | 12000 | 49.6% [48.7%–50.5%] |
| rez | 12000 | 46.5% [45.6%–47.4%] |

Matchups (win score of the alphabetically-first character; mirrors show seat-0 score):

| Matchup | Games | First's score |
|---|---|---|
| decima:decima | 2000 | 59.4% |
| decima:kosynwu | 2000 | 51.4% |
| decima:rez | 2000 | 55.0% |
| decima:tetra | 2000 | 50.1% |
| decima:volos | 2000 | 48.5% |
| kosynwu:kosynwu | 2000 | 59.4% |
| kosynwu:rez | 2000 | 54.8% |
| kosynwu:tetra | 2000 | 48.3% |
| kosynwu:volos | 2000 | 46.1% |
| rez:rez | 2000 | 58.1% |
| rez:tetra | 2000 | 46.2% |
| rez:volos | 2000 | 42.8% |
| tetra:tetra | 2000 | 57.7% |
| tetra:volos | 2000 | 47.7% |
| volos:volos | 2000 | 60.2% |

## 5. Playstyles (observational!)

Win rate by feature quartile (Q1 lowest → Q4 highest), plus logistic odds ratio per +1 SD:

| Feature | Q1 | Q2 | Q3 | Q4 | OR/SD | p |
|---|---|---|---|---|---|---|
| factionConcentration | 54.6% | 50.5% | 47.4% | 47.6% | 1.06 | 0.000 |
| avgBuyCost | 42.6% | 46.9% | 51.8% | 58.6% | 1.31 | 0.000 |
| championShare | 40.0% | 47.7% | 53.9% | 58.5% | 1.26 | 0.000 |
| focusCount | 45.5% | 48.4% | 51.4% | 54.7% | 1.22 | 0.000 |
| masteryAtRound8 | 53.9% | 48.9% | 47.5% | 49.6% | 1.12 | 0.000 |
| totalAcquisitions | 43.6% | 46.1% | 50.0% | 60.3% | 1.69 | 0.000 |
| earlyAggression | 37.8% | 43.4% | 51.7% | 67.1% | 2.11 | 0.000 |

Win rate by dominant purchase faction:

| Faction | Player-games | Win rate |
|---|---|---|
| Undergrowth | 11799 | 57.2% [56.3%–58.1%] |
| Aion | 126 | 57.1% [48.4%–65.4%] |
| Homodeus | 16603 | 51.6% [50.9%–52.4%] |
| Wraethe | 19775 | 47.5% [46.8%–48.2%] |
| Order | 11623 | 44.5% [43.6%–45.4%] |

## 6. Cards

### Flagged (BH FDR 10%, |Δ| ≥ 5 pts, ≥ 100 acquisitions)

| Card | Cost | Impact Δ | p | Buy rate | Co-acquired (lift) |
|---|---|---|---|---|---|
| Ojas, Genesis Druid (Undergrowth) | 4 | **+34.2 pts** | 0.0000 | 5.1% | breaker ×18.8, portal_monk ×9.2, j_chord ×6.7, raidian ×5.1, cryptofist_monk ×4.6 |
| Axia (Homodeus) | 7 | **+32.9 pts** | 0.0000 | 84.4% | general_decurion ×1.4, numeri_drones ×1.4, ferrata_guard ×1.4, swyft ×1.3, breaker ×1.3 |
| Drakonarius (Homodeus) | 6 | **+32.3 pts** | 0.0000 | 86.7% | portal_monk ×1.3, general_decurion ×1.2, swyft ×1.2, root_of_the_forest ×1.2, kiln_drone ×1.2 |
| Ru Bo Vai, The Transcendant (Wraethe) | 5 | **+27.3 pts** | 0.0000 | 93.0% | breaker ×1.3, portal_monk ×1.2, g_48 ×1.1, general_decurion ×1.1, swyft ×1.1 |
| Breaker (Aion) | 6 | **+20.3 pts** | 0.0000 | 11.3% | portal_monk ×14.9, cryptofist_monk ×6.5, lucky ×4.6, j_chord ×4.5, root_of_the_forest ×3.1 |
| Optio Crusher (Homodeus) | 5 | **+20.1 pts** | 0.0000 | 90.6% | breaker ×1.3, general_decurion ×1.2, portal_monk ×1.2, swyft ×1.2, axia ×1.1 |
| Root of the Forest (Undergrowth) | 7 | **+18.1 pts** | 0.0000 | 48.6% | breaker ×3.1, general_decurion ×2.2, cryptofist_monk ×1.7, portal_monk ×1.7, raidian ×1.7 |
| Orm Madu (Undergrowth) | 7 | **+17.4 pts** | 0.0000 | 1.3% | raidian ×4.9, g_48 ×4.2, breaker ×3.8, cryptofist_monk ×3.7, general_decurion ×3.6 |
| Swyft (Aion) | 5 | **+14.7 pts** | 0.0000 | 91.8% | general_decurion ×1.9, zen_chi_set ×1.7, root_of_the_forest ×1.6, grand_architect ×1.5, breaker ×1.4 |
| Taur, Arachpriest (Undergrowth) | 5 | **+14.0 pts** | 0.0000 | 4.6% | portal_monk ×13.3, breaker ×4.3, lucky ×4.0, j_chord ×3.7, cryptofist_monk ×2.9 |
| The Rotten (Undergrowth) | 3 | **+13.7 pts** | 0.0000 | 97.0% | breaker ×1.3, portal_monk ×1.2, j_chord ×1.1, cryptofist_monk ×1.1, raidian ×1.1 |
| Spore Cleric (Undergrowth) | 2 | **+13.4 pts** | 0.0000 | 38.3% | brute ×3.0, j_chord ×2.5, breaker ×2.5, portal_monk ×2.5, lucky ×2.1 |
| Zen Chi Set, Godkiller (Wraethe) | 7 | **+12.9 pts** | 0.0000 | 47.6% | general_decurion ×2.2, raidian ×1.8, swyft ×1.7, root_of_the_forest ×1.7, breaker ×1.6 |
| General Decurion (Homodeus) | 7 | **+12.3 pts** | 0.0000 | 25.9% | raidian ×2.7, cryptofist_monk ×2.3, zen_chi_set ×2.2, root_of_the_forest ×2.2, command_seer ×2.0 |
| Additri, Gaiamancer (Undergrowth) | 5 | **+12.2 pts** | 0.0000 | 93.6% | breaker ×1.3, general_decurion ×1.2, portal_monk ×1.2, raidian ×1.1, numeri_drones ×1.1 |
| Numeri Drones (Homodeus) | 3 | **+11.8 pts** | 0.0000 | 96.4% | general_decurion ×1.6, root_of_the_forest ×1.5, zen_chi_set ×1.4, breaker ×1.4, axia ×1.4 |
| Brute (Aion) | 3 | **+11.2 pts** | 0.0000 | 96.7% | spore_cleric ×3.0, breaker ×1.2, portal_monk ×1.2, j_chord ×1.1, kiln_drone ×1.1 |
| Undergrowth Aspirant (Undergrowth) | 1 | **+10.9 pts** | 0.0000 | 98.9% | breaker ×1.3, portal_monk ×1.3, spore_cleric ×1.2, cryptofist_monk ×1.2, j_chord ×1.2 |
| J-Chord (Aion) | 3 | **+10.9 pts** | 0.0000 | 72.1% | portal_monk ×4.7, breaker ×4.5, cryptofist_monk ×2.9, spore_cleric ×2.5, raidian ×2.2 |
| Duplication Fabricator (Order) | 1 | **+10.3 pts** | 0.0000 | 98.8% | breaker ×1.5, general_decurion ×1.4, portal_monk ×1.3, cryptofist_monk ×1.3, spore_cleric ×1.2 |
| Portal Monk (Order) | 3 | **+10.2 pts** | 0.0000 | 26.6% | breaker ×14.9, raidian ×5.2, j_chord ×4.7, cryptofist_monk ×4.5, lucky ×4.3 |
| Fao Cu'tul, The Formless (Wraethe) | 4 | **+9.6 pts** | 0.0000 | 89.6% | general_decurion ×1.2, zen_chi_set ×1.1, cryptofist_monk ×1.1, kiln_drone ×1.1, command_seer ×1.1 |
| Evokatus (Homodeus) | 4 | **+9.5 pts** | 0.0000 | 94.2% | breaker ×1.3, general_decurion ×1.3, portal_monk ×1.2, root_of_the_forest ×1.2, cryptofist_monk ×1.2 |
| Torian Commandos (Homodeus) | 3 | **+9.5 pts** | 0.0000 | 96.7% | general_decurion ×1.3, breaker ×1.3, portal_monk ×1.3, root_of_the_forest ×1.3, cryptofist_monk ×1.2 |
| Zetta, The Encryptor (Order) | 5 | **+9.1 pts** | 0.0002 | 4.6% | portal_monk ×13.6, breaker ×5.6, lucky ×4.5, j_chord ×4.0, raidian ×2.7 |
| Cryptofist Monk (Order) | 5 | **+9.0 pts** | 0.0000 | 25.3% | breaker ×6.5, portal_monk ×4.5, j_chord ×2.9, raidian ×2.8, oblivion_gatekeeper ×2.5 |
| Thorn Zealot (Undergrowth) | 3 | **+8.9 pts** | 0.0000 | 88.3% | breaker ×1.5, general_decurion ×1.4, portal_monk ×1.4, cryptofist_monk ×1.3, j_chord ×1.2 |
| Shardwood Guardian (Undergrowth) | 4 | **+8.5 pts** | 0.0000 | 86.0% | general_decurion ×1.3, breaker ×1.2, raidian ×1.2, portal_monk ×1.2, cryptofist_monk ×1.2 |
| The Dispossessed (Wraethe) | 1 | **+8.3 pts** | 0.0000 | 98.9% | breaker ×1.4, portal_monk ×1.3, general_decurion ×1.2, cryptofist_monk ×1.2, raidian ×1.1 |
| Carnivorous Vine (Undergrowth) | 4 | **+7.3 pts** | 0.0000 | 95.3% | breaker ×1.5, portal_monk ×1.3, general_decurion ×1.2, j_chord ×1.2, cryptofist_monk ×1.1 |
| Systema A.I. (Order) | 3 | **+6.5 pts** | 0.0000 | 96.8% | general_decurion ×1.6, breaker ×1.5, portal_monk ×1.3, raidian ×1.3, cryptofist_monk ×1.2 |
| Raidian, Cloud Master (Order) | 5 | **+6.3 pts** | 0.0000 | 15.0% | portal_monk ×5.2, cryptofist_monk ×2.8, general_decurion ×2.7, g_48 ×2.6, oblivion_gatekeeper ×2.3 |
| Furrowing Elemental (Undergrowth) | 5 | **+6.1 pts** | 0.0000 | 67.1% | breaker ×2.1, portal_monk ×1.8, general_decurion ×1.7, cryptofist_monk ×1.6, raidian ×1.5 |
| The Grand Architect (Order) | 7 | **+6.0 pts** | 0.0000 | 75.1% | breaker ×1.9, general_decurion ×1.8, swyft ×1.5, cryptofist_monk ×1.5, portal_monk ×1.4 |
| Hounds of Volos (Undergrowth) | 3 | **+5.7 pts** | 0.0000 | 95.5% | breaker ×1.5, portal_monk ×1.3, general_decurion ×1.2, cryptofist_monk ×1.2, j_chord ×1.2 |
| Nil Assassin (Wraethe) | 2 | **+5.5 pts** | 0.0000 | 97.8% | breaker ×1.3, portal_monk ×1.2, j_chord ×1.1, g_48 ×1.1, kiln_drone ×1.1 |
| The Lost (Wraethe) | 4 | **+5.2 pts** | 0.0000 | 94.7% | breaker ×1.3, portal_monk ×1.2, j_chord ×1.1, kiln_drone ×1.1, ferrata_guard ×1.1 |
| Ghostwillow Avenger (Undergrowth) | 4 | **+5.1 pts** | 0.0000 | 71.3% | general_decurion ×1.5, breaker ×1.4, raidian ×1.4, lucky ×1.3, portal_monk ×1.3 |
| Order Initiate (Order) | 1 | **-5.3 pts** | 0.0000 | 98.9% | general_decurion ×1.4, breaker ×1.4, raidian ×1.3, portal_monk ×1.3, cryptofist_monk ×1.3 |
| Legion Carrier (Homodeus) | 2 | **-5.6 pts** | 0.0000 | 97.6% | breaker ×1.4, general_decurion ×1.4, portal_monk ×1.3, root_of_the_forest ×1.3, cryptofist_monk ×1.3 |
| Reactor Drone (Homodeus) | 3 | **-5.7 pts** | 0.0000 | 83.7% | general_decurion ×1.5, breaker ×1.5, portal_monk ×1.4, cryptofist_monk ×1.4, root_of_the_forest ×1.4 |
| Cache Warden (Order) | 2 | **-6.6 pts** | 0.0000 | 98.2% | general_decurion ×1.4, breaker ×1.3, portal_monk ×1.3, cryptofist_monk ×1.2, lucky ×1.1 |
| Umbral Scourge (Wraethe) | 3 | **-7.4 pts** | 0.0000 | 95.9% | breaker ×1.3, portal_monk ×1.2, j_chord ×1.2, spore_cleric ×1.1, cryptofist_monk ×1.1 |
| Shard Abstractor (Order) | 3 | **-8.5 pts** | 0.0000 | 96.8% | breaker ×1.3, general_decurion ×1.2, portal_monk ×1.2, cryptofist_monk ×1.2, j_chord ×1.1 |

Positive Δ + healthy buy rate ⇒ nerf candidate; negative Δ or rock-bottom buy rate ⇒ buff candidate. Cross-check the co-acquisition column before blaming a single card.

### Buy-rate outliers by cost band (full table in sim-summary.csv)

- Cost 1–3: least bought — Portal Monk 26.6%, Spore Cleric 38.3%, Li Hin, The Shattered 60.8% · most bought — The Dispossessed 98.9%, Kiln Drone 98.9%, Wraethe Skirmisher 98.9%
- Cost 4–6: least bought — Zetta, The Encryptor 4.6%, Taur, Arachpriest 4.6%, Ojas, Genesis Druid 5.1% · most bought — Carnivorous Vine 95.3%, The Lost 94.7%, Evokatus 94.2%
- Cost 7+: least bought — Orm Madu 1.3%, General Decurion 25.9%, Zen Chi Set, Godkiller 47.6% · most bought — Axia 84.4%, The Grand Architect 75.1%, Root of the Forest 48.6%

## 7. Relics, destinies, monsters

| Relic | Recruits | WR when recruited |
|---|---|---|
| Datic Robes | 8094 | 53.7% [52.6%–54.8%] |
| Entropic Talons | 7655 | 55.5% [54.4%–56.6%] |
| The Heart of Nothing | 7714 | 52.0% [50.9%–53.1%] |
| Panconscious Crown | 835 | 71.4% [68.2%–74.3%] |
| Praetorian-01 | 7710 | 54.6% [53.5%–55.7%] |
| Praetorian-02 | 736 | 70.9% [67.5%–74.1%] |
| Slipstream Shard | 7634 | 48.1% [47.0%–49.2%] |
| Terminal Crescents | 864 | 73.1% [70.1%–76.0%] |
| Warpquartz | 893 | 57.4% [54.2%–60.7%] |
| The World Piercer | 721 | 67.5% [64.0%–70.9%] |

| Destiny | In initial row | Taken | Avg round | WR taken | WR not taken |
|---|---|---|---|---|---|
| War Bound | 12172 | 2508 (20.6%) | 7.0 | 55.3% [53.4%–57.3%] | 48.6% [47.6%–49.6%] |
| Absorption Grid | 11928 | 2426 (20.3%) | 7.1 | 52.7% [50.7%–54.7%] | 49.3% [48.3%–50.3%] |
| Biotech Enhancements | 11830 | 2405 (20.3%) | 7.0 | 51.8% [49.8%–53.8%] | 49.5% [48.5%–50.6%] |
| Phasic Technology | 12178 | 2475 (20.3%) | 7.0 | 58.7% [56.8%–60.7%] | 47.8% [46.8%–48.8%] |
| Advanced Medicine | 12048 | 2448 (20.3%) | 7.0 | 56.0% [54.0%–57.9%] | 48.5% [47.5%–49.5%] |
| Datic Secrets | 12116 | 2461 (20.3%) | 7.1 | 48.2% [46.2%–50.2%] | 50.5% [49.5%–51.5%] |
| The Last City | 12166 | 2468 (20.3%) | 7.0 | 48.1% [46.1%–50.0%] | 50.5% [49.5%–51.5%] |
| The Agony of Choice | 12164 | 2467 (20.3%) | 7.0 | 58.0% [56.0%–59.9%] | 48.0% [47.0%–49.0%] |
| Project Yggdrasil | 11792 | 2390 (20.3%) | 7.0 | 53.2% [51.2%–55.2%] | 49.2% [48.2%–50.2%] |
| Soul Syphon | 12260 | 2481 (20.2%) | 7.0 | 60.1% [58.2%–62.0%] | 47.4% [46.4%–48.4%] |
| The Price of Power | 12068 | 2442 (20.2%) | 7.0 | 54.5% [52.5%–56.5%] | 48.9% [47.9%–49.9%] |
| Strategic Mastermind | 11846 | 2397 (20.2%) | 7.0 | 51.6% [49.6%–53.6%] | 49.6% [48.6%–50.6%] |
| Advanced Weapons | 11752 | 2374 (20.2%) | 7.1 | 57.2% [55.2%–59.2%] | 48.2% [47.2%–49.2%] |
| Paradigm Shift | 11874 | 2398 (20.2%) | 7.0 | 49.9% [47.9%–51.9%] | 50.0% [49.0%–51.0%] |
| Blood for Blood | 12196 | 2452 (20.1%) | 7.0 | 47.3% [45.3%–49.3%] | 50.7% [49.7%–51.7%] |
| True Leader | 11940 | 2400 (20.1%) | 7.0 | 49.8% [47.8%–51.7%] | 50.1% [49.1%–51.1%] |
| Unconditional Conscription | 11906 | 2393 (20.1%) | 7.1 | 61.3% [59.4%–63.3%] | 47.1% [46.1%–48.2%] |
| Nature Dominance | 12130 | 2432 (20.0%) | 7.0 | 56.1% [54.1%–58.0%] | 48.5% [47.5%–49.5%] |
| The Crystal Gate | 12006 | 2407 (20.0%) | 7.1 | 46.3% [44.3%–48.3%] | 50.9% [49.9%–51.9%] |
| Power Struggle | 11944 | 2393 (20.0%) | 7.1 | 54.3% [52.3%–56.3%] | 48.9% [47.9%–49.9%] |
| Healing Hands | 12052 | 2399 (19.9%) | 7.0 | 52.0% [50.0%–54.0%] | 49.5% [48.5%–50.5%] |
| Maglev Tunnels | 11920 | 2372 (19.9%) | 7.1 | 48.7% [46.6%–50.7%] | 50.3% [49.3%–51.3%] |
| Deadly Recruits | 11962 | 2369 (19.8%) | 7.0 | 48.8% [46.8%–50.9%] | 50.3% [49.3%–51.3%] |
| Forged in Flame | 12048 | 2384 (19.8%) | 7.0 | 50.7% [48.7%–52.7%] | 49.8% [48.8%–50.8%] |
| Whatever it Takes | 12162 | 2398 (19.7%) | 7.1 | 61.6% [59.6%–63.5%] | 47.2% [46.2%–48.1%] |
| Bound for Life | 11852 | 2336 (19.7%) | 7.1 | 46.1% [44.1%–48.2%] | 50.9% [49.9%–51.9%] |
| The Shard Defiant | 11910 | 2341 (19.7%) | 7.0 | 53.7% [51.6%–55.7%] | 49.1% [48.1%–50.1%] |
| Synthesis | 11866 | 2320 (19.6%) | 7.0 | 47.5% [45.5%–49.5%] | 50.6% [49.6%–51.6%] |
| One Mind One Army | 12068 | 2357 (19.5%) | 7.0 | 47.4% [45.4%–49.5%] | 50.6% [49.6%–51.6%] |
| Stolen Futures | 11844 | 2222 (18.8%) | 6.9 | 52.0% [49.9%–54.1%] | 49.5% [48.5%–50.5%] |

| Monster | Revealed | Defeated | Avg defeat round | Defeater WR |
|---|---|---|---|---|
| Ingeminex: Agony | 6501 | 6283 (96.6%) | 9.0 | 66.4% [65.3%–67.6%] |
| Ingeminex: Brutality | 6416 | 6026 (93.9%) | 8.7 | 76.2% [75.1%–77.2%] |
| Ingeminex: Corruption | 6600 | 6302 (95.5%) | 8.9 | 67.4% [66.2%–68.6%] |
| Ingeminex: Malice | 6483 | 6257 (96.5%) | 9.0 | 62.3% [61.1%–63.5%] |
| Ingeminex: Torment | 6579 | 6405 (97.4%) | 8.9 | 62.2% [61.0%–63.4%] |

Monster attacks landed: 26898

## 8. Methodology & caveats

- Every proportion carries a Wilson 95% interval; per-card deltas are stratified by matchup×seat, inverse-variance pooled, and Benjamini-Hochberg corrected (FDR 10%) with an effect floor.
- **These are correlations between THESE bots' policies, not causal card effects.** A card bought when already ahead will look like a winner. Treat findings as directional input; re-test surprising ones with a targeted A/B (forced-strategy bot variant) before patching.
- Seat counts are balanced exactly 50/50 per matchup by construction; seeds are sequential and reproducible (`soisim run --seed-base 1`).
