# SoI Balance Report

## 1. Reproducibility

- Generated: 2026-07-21 15:16 UTC · git `99a543c` · schema 1
- Bots: **greedy-V2** · DLC mask 7 · seed base 1 · tag `greedy-v2-mass`
- Config hash: `sha256:8b9c9c50c9b47c86b335bc7250239cfe93e8242570721369f297203b9e5d604b`
- Games: **30000** (29995 decisive, 5 ties, 0 failures)

## 2. Game health

- Rounds p10/p50/p90: **10 / 13 / 18** · avg submits/game 316
- Tie rate: 0.0% [0.0%–0.0%] · failures (guard/stall/error): **0**
- Win type: 27902 kill / 2093 Infinity-Shard overwhelm (7.0% [6.7%–7.3%] of wins — mastery-race viability)
- Comeback wins (winner behind on health at midpoint): 40.2% [39.7%–40.8%]
- Shields prevented 869317 of 27009488 incoming damage (3.2%)

## 3. Seat advantage (staggered start: P0 M0, P1 M1)

- P0 win rate, all decisive games: **58.6% [58.0%–59.1%]** (n=29995)
- P0 win rate, mirror matches only (no character confound): **58.7% [57.8%–59.7%]** (n=9999)

## 4. Characters

| Character | Player-games | Win score |
|---|---|---|
| volos | 12000 | 53.8% [52.9%–54.7%] |
| decima | 12000 | 51.4% [50.5%–52.3%] |
| tetra | 12000 | 49.5% [48.7%–50.4%] |
| kosynwu | 12000 | 49.4% [48.5%–50.3%] |
| rez | 12000 | 45.9% [45.0%–46.8%] |

Matchups (win score of the alphabetically-first character; mirrors show seat-0 score):

| Matchup | Games | First's score |
|---|---|---|
| decima:decima | 2000 | 59.9% |
| decima:kosynwu | 2000 | 50.8% |
| decima:rez | 2000 | 56.5% |
| decima:tetra | 2000 | 52.4% |
| decima:volos | 2000 | 48.8% |
| kosynwu:kosynwu | 2000 | 57.5% |
| kosynwu:rez | 2000 | 53.5% |
| kosynwu:tetra | 2000 | 49.9% |
| kosynwu:volos | 2000 | 43.6% |
| rez:rez | 2000 | 58.2% |
| rez:tetra | 2000 | 45.4% |
| rez:volos | 2000 | 39.9% |
| tetra:tetra | 2000 | 59.8% |
| tetra:volos | 2000 | 45.0% |
| volos:volos | 2000 | 58.2% |

## 5. Playstyles (observational!)

Win rate by feature quartile (Q1 lowest → Q4 highest), plus logistic odds ratio per +1 SD:

| Feature | Q1 | Q2 | Q3 | Q4 | OR/SD | p |
|---|---|---|---|---|---|---|
| factionConcentration | 54.4% | 51.4% | 47.9% | 46.3% | 1.10 | 0.000 |
| avgBuyCost | 42.1% | 47.3% | 52.7% | 57.9% | 1.29 | 0.000 |
| championShare | 45.9% | 50.9% | 52.4% | 50.8% | 1.01 | 0.440 |
| focusCount | 43.6% | 49.1% | 52.3% | 55.0% | 1.03 | 0.002 |
| masteryAtRound8 | 44.2% | 47.8% | 50.7% | 57.2% | 1.32 | 0.000 |
| totalAcquisitions | 40.3% | 46.1% | 51.1% | 62.5% | 1.65 | 0.000 |
| earlyAggression | 41.6% | 45.4% | 50.7% | 62.2% | 1.67 | 0.000 |

Win rate by dominant purchase faction:

| Faction | Player-games | Win rate |
|---|---|---|
| Undergrowth | 17125 | 54.0% [53.3%–54.7%] |
| Order | 13169 | 49.2% [48.4%–50.1%] |
| Wraethe | 16893 | 48.9% [48.1%–49.6%] |
| Homodeus | 12587 | 47.0% [46.1%–47.9%] |
| Aion | 216 | 44.0% [37.5%–50.6%] |

## 6. Cards

### Flagged (BH FDR 10%, |Δ| ≥ 5 pts, ≥ 100 acquisitions)

| Card | Cost | Impact Δ | p | Buy rate | Co-acquired (lift) |
|---|---|---|---|---|---|
| Ojas, Genesis Druid (Undergrowth) | 4 | **+30.8 pts** | 0.0000 | 4.4% | general_decurion ×4.4, zetta_encryptor ×4.1, breaker ×3.7, scion_of_nothingness ×3.5, j_chord ×3.0 |
| Axia (Homodeus) | 7 | **+26.8 pts** | 0.0000 | 82.5% | general_decurion ×1.5, portal_monk ×1.5, anomaly_cleric ×1.3, zetta_encryptor ×1.3, orm_madu ×1.3 |
| General Decurion (Homodeus) | 7 | **+25.1 pts** | 0.0000 | 32.4% | grand_architect ×2.5, anomaly_cleric ×2.5, zetta_encryptor ×2.3, scion_of_nothingness ×2.3, taur_arachpriest ×2.1 |
| Breaker (Aion) | 6 | **+24.7 pts** | 0.0000 | 92.2% | scion_of_nothingness ×2.8, grand_architect ×2.8, anomaly_cleric ×2.6, root_of_the_forest ×2.3, cryptofist_monk ×2.1 |
| Root of the Forest (Undergrowth) | 7 | **+23.0 pts** | 0.0000 | 71.3% | breaker ×2.3, general_decurion ×1.9, grand_architect ×1.7, anomaly_cleric ×1.7, portal_monk ×1.6 |
| The Grand Architect (Order) | 7 | **+21.0 pts** | 0.0000 | 61.9% | breaker ×2.8, general_decurion ×2.5, anomaly_cleric ×1.9, scion_of_nothingness ×1.7, root_of_the_forest ×1.7 |
| The Rotten (Undergrowth) | 3 | **+19.5 pts** | 0.0000 | 97.6% | general_decurion ×1.3, breaker ×1.2, anomaly_cleric ×1.1, portal_monk ×1.1, lucky ×1.1 |
| Duplication Fabricator (Order) | 1 | **+18.5 pts** | 0.0000 | 99.1% | general_decurion ×1.5, scion_of_nothingness ×1.3, breaker ×1.3, grand_architect ×1.3, portal_monk ×1.2 |
| Orm Madu (Undergrowth) | 7 | **+15.7 pts** | 0.0000 | 68.0% | general_decurion ×2.1, anomaly_cleric ×1.7, zetta_encryptor ×1.7, portal_monk ×1.6, grand_architect ×1.5 |
| Giga, Source Adept (Order) | 2 | **+14.6 pts** | 0.0000 | 98.2% | general_decurion ×1.6, portal_monk ×1.3, anomaly_cleric ×1.3, breaker ×1.3, omnius ×1.2 |
| Omnius, The All-Knowing (Order) | 6 | **+14.0 pts** | 0.0000 | 78.4% | breaker ×2.0, general_decurion ×1.9, portal_monk ×1.8, scion_of_nothingness ×1.5, grand_architect ×1.5 |
| Zen Chi Set, Godkiller (Wraethe) | 7 | **+13.6 pts** | 0.0000 | 61.6% | general_decurion ×2.1, anomaly_cleric ×1.6, portal_monk ×1.6, orm_madu ×1.5, grand_architect ×1.5 |
| J-Chord (Aion) | 3 | **+13.4 pts** | 0.0000 | 97.4% | portal_monk ×3.0, reactor_drone ×1.8, scion_of_nothingness ×1.6, umbral_scourge ×1.6, anomaly_cleric ×1.6 |
| Zara Ra, Soulflayer (Wraethe) | 5 | **+13.3 pts** | 0.0000 | 75.4% | breaker ×1.8, general_decurion ×1.7, portal_monk ×1.6, anomaly_cleric ×1.5, scion_of_nothingness ×1.5 |
| Anomaly Cleric (Order) | 5 | **+13.2 pts** | 0.0000 | 50.7% | breaker ×2.6, general_decurion ×2.5, scion_of_nothingness ×2.2, grand_architect ×1.9, taur_arachpriest ×1.9 |
| Aetherbreaker (Wraethe) | 4 | **+12.1 pts** | 0.0000 | 91.0% | general_decurion ×1.5, breaker ×1.3, grand_architect ×1.3, portal_monk ×1.3, anomaly_cleric ×1.2 |
| Fungal Hermit (Undergrowth) | 3 | **+12.1 pts** | 0.0000 | 90.9% | general_decurion ×1.6, j_chord ×1.4, zetta_encryptor ×1.4, anomaly_cleric ×1.3, scion_of_nothingness ×1.3 |
| Ru Bo Vai, The Transcendant (Wraethe) | 5 | **+11.5 pts** | 0.0000 | 94.7% | portal_monk ×1.3, general_decurion ×1.2, reactor_drone ×1.2, anomaly_cleric ×1.2, lucky ×1.1 |
| Portal Monk (Order) | 3 | **+11.1 pts** | 0.0000 | 17.7% | j_chord ×3.0, lucky ×2.9, general_decurion ×2.1, reactor_drone ×2.1, anomaly_cleric ×1.8 |
| Undergrowth Aspirant (Undergrowth) | 1 | **+10.7 pts** | 0.0000 | 99.2% | general_decurion ×1.3, portal_monk ×1.3, scion_of_nothingness ×1.2, anomaly_cleric ×1.2, j_chord ×1.2 |
| Data Heretic (Order) | 3 | **+10.6 pts** | 0.0000 | 96.8% | general_decurion ×1.4, anomaly_cleric ×1.3, scion_of_nothingness ×1.2, root_of_the_forest ×1.2, portal_monk ×1.2 |
| Systema A.I. (Order) | 3 | **+9.9 pts** | 0.0000 | 97.3% | general_decurion ×1.7, anomaly_cleric ×1.3, portal_monk ×1.3, zen_chi_set ×1.2, scion_of_nothingness ×1.2 |
| Shardwood Guardian (Undergrowth) | 4 | **+9.8 pts** | 0.0000 | 95.2% | general_decurion ×1.3, breaker ×1.3, portal_monk ×1.2, scion_of_nothingness ×1.2, grand_architect ×1.2 |
| Scion of Nothingness (Wraethe) | 5 | **+9.8 pts** | 0.0000 | 32.2% | breaker ×2.8, zetta_encryptor ×2.6, general_decurion ×2.3, anomaly_cleric ×2.2, taur_arachpriest ×2.1 |
| Carnivorous Vine (Undergrowth) | 4 | **+9.1 pts** | 0.0000 | 94.1% | general_decurion ×1.3, portal_monk ×1.3, breaker ×1.3, zetta_encryptor ×1.3, scion_of_nothingness ×1.2 |
| Furrowing Elemental (Undergrowth) | 5 | **+9.1 pts** | 0.0000 | 84.0% | breaker ×1.6, general_decurion ×1.6, portal_monk ×1.4, scion_of_nothingness ×1.4, anomaly_cleric ×1.4 |
| Lucky (Aion) | 4 | **+8.8 pts** | 0.0000 | 86.7% | portal_monk ×2.9, reactor_drone ×1.8, general_decurion ×1.6, umbral_scourge ×1.5, breaker ×1.5 |
| Oblivion Gatekeeper (Wraethe) | 4 | **+8.7 pts** | 0.0000 | 93.4% | general_decurion ×1.5, portal_monk ×1.4, grand_architect ×1.3, anomaly_cleric ×1.3, taur_arachpriest ×1.2 |
| Thorn Zealot (Undergrowth) | 3 | **+8.3 pts** | 0.0000 | 97.7% | general_decurion ×1.3, portal_monk ×1.3, zetta_encryptor ×1.3, anomaly_cleric ×1.2, scion_of_nothingness ×1.2 |
| Numeri Drones (Homodeus) | 3 | **+8.0 pts** | 0.0000 | 96.4% | general_decurion ×1.4, zetta_encryptor ×1.3, scion_of_nothingness ×1.3, grand_architect ×1.3, axia ×1.2 |
| Ghostwillow Avenger (Undergrowth) | 4 | **+7.8 pts** | 0.0000 | 84.0% | general_decurion ×1.6, portal_monk ×1.4, grand_architect ×1.4, breaker ×1.4, zetta_encryptor ×1.3 |
| Torian Commandos (Homodeus) | 3 | **+7.4 pts** | 0.0000 | 96.7% | general_decurion ×1.4, scion_of_nothingness ×1.3, root_of_the_forest ×1.3, zetta_encryptor ×1.3, orm_madu ×1.3 |
| Drakonarius (Homodeus) | 6 | **+7.1 pts** | 0.0000 | 88.9% | portal_monk ×1.5, general_decurion ×1.4, anomaly_cleric ×1.3, reactor_drone ×1.2, orm_madu ×1.2 |
| The Dispossessed (Wraethe) | 1 | **+7.1 pts** | 0.0000 | 99.2% | portal_monk ×1.3, general_decurion ×1.2, zetta_encryptor ×1.2, j_chord ×1.2, anomaly_cleric ×1.1 |
| Ferrata Guard (Homodeus) | 4 | **+6.9 pts** | 0.0756 | 0.1% | portal_monk ×3.5, zetta_encryptor ×3.3, general_decurion ×2.7, scion_of_nothingness ×2.6, anomaly_cleric ×2.0 |
| The Lost (Wraethe) | 4 | **+6.8 pts** | 0.0000 | 93.0% | breaker ×1.2, general_decurion ×1.2, portal_monk ×1.2, reactor_drone ×1.1, scion_of_nothingness ×1.1 |
| Swyft (Aion) | 5 | **+6.6 pts** | 0.0000 | 92.4% | general_decurion ×1.5, grand_architect ×1.4, zen_chi_set ×1.4, portal_monk ×1.4, orm_madu ×1.3 |
| Nil Assassin (Wraethe) | 2 | **+6.6 pts** | 0.0000 | 98.4% | general_decurion ×1.2, portal_monk ×1.1, lucky ×1.1, breaker ×1.1, j_chord ×1.1 |
| Raidian, Cloud Master (Order) | 5 | **+6.4 pts** | 0.0000 | 88.7% | general_decurion ×1.6, portal_monk ×1.4, zen_chi_set ×1.4, orm_madu ×1.3, grand_architect ×1.3 |
| Cryptofist Monk (Order) | 5 | **+5.9 pts** | 0.0000 | 66.1% | breaker ×2.1, portal_monk ×1.7, scion_of_nothingness ×1.7, general_decurion ×1.7, anomaly_cleric ×1.7 |
| Optio Crusher (Homodeus) | 5 | **+5.7 pts** | 0.0000 | 93.7% | portal_monk ×1.3, general_decurion ×1.3, reactor_drone ×1.2, grand_architect ×1.2, orm_madu ×1.2 |
| Brute (Aion) | 3 | **+5.5 pts** | 0.0000 | 97.6% | portal_monk ×1.2, general_decurion ×1.2, j_chord ×1.2, anomaly_cleric ×1.2, breaker ×1.2 |
| Additri, Gaiamancer (Undergrowth) | 5 | **+5.0 pts** | 0.0000 | 93.6% | portal_monk ×1.4, general_decurion ×1.4, anomaly_cleric ×1.2, reactor_drone ×1.2, scion_of_nothingness ×1.2 |
| G-48 (Homodeus) | 4 | **-8.6 pts** | 0.0000 | 87.1% | portal_monk ×1.3, zetta_encryptor ×1.3, anomaly_cleric ×1.3, general_decurion ×1.3, taur_arachpriest ×1.2 |

Positive Δ + healthy buy rate ⇒ nerf candidate; negative Δ or rock-bottom buy rate ⇒ buff candidate. Cross-check the co-acquisition column before blaming a single card.

### Buy-rate outliers by cost band (full table in sim-summary.csv)

- Cost 1–3: least bought — Portal Monk 17.7%, Reactor Drone 44.0%, Umbral Scourge 62.1% · most bought — Undergrowth Aspirant 99.2%, The Dispossessed 99.2%, Duplication Fabricator 99.1%
- Cost 4–6: least bought — Ferrata Guard 0.1%, Ojas, Genesis Druid 4.4%, Zetta, The Encryptor 12.9% · most bought — Shardwood Guardian 95.2%, Ru Bo Vai, The Transcendant 94.7%, Fao Cu'tul, The Formless 94.7%
- Cost 7+: least bought — General Decurion 32.4%, Zen Chi Set, Godkiller 61.6%, The Grand Architect 61.9% · most bought — Axia 82.5%, Root of the Forest 71.3%, Orm Madu 68.0%

## 7. Relics, destinies, monsters

| Relic | Recruits | WR when recruited |
|---|---|---|
| Datic Robes | 9770 | 53.4% [52.4%–54.4%] |
| Entropic Talons | 9308 | 58.7% [57.7%–59.7%] |
| The Heart of Nothing | 9468 | 52.9% [51.9%–53.9%] |
| Panconscious Crown | 1292 | 74.6% [72.2%–76.9%] |
| Praetorian-01 | 9393 | 56.7% [55.7%–57.7%] |
| Praetorian-02 | 1210 | 72.1% [69.5%–74.5%] |
| Slipstream Shard | 1289 | 64.9% [62.2%–67.4%] |
| Terminal Crescents | 1424 | 70.6% [68.2%–72.9%] |
| Warpquartz | 9364 | 49.3% [48.3%–50.3%] |
| The World Piercer | 1221 | 63.1% [60.4%–65.8%] |

| Destiny | In initial row | Taken | Avg round | WR taken | WR not taken |
|---|---|---|---|---|---|
| Whatever it Takes | 12162 | 6075 (50.0%) | 5.7 | 52.1% [50.9%–53.4%] | 47.9% [46.6%–49.1%] |
| Power Struggle | 11944 | 5928 (49.6%) | 6.1 | 51.7% [50.5%–53.0%] | 48.3% [47.0%–49.6%] |
| True Leader | 11940 | 5798 (48.6%) | 6.3 | 50.9% [49.7%–52.2%] | 49.1% [47.9%–50.4%] |
| Unconditional Conscription | 11906 | 5377 (45.2%) | 6.9 | 62.0% [60.7%–63.3%] | 40.1% [38.9%–41.3%] |
| The Agony of Choice | 12164 | 5488 (45.1%) | 6.8 | 61.9% [60.6%–63.2%] | 40.2% [39.1%–41.4%] |
| War Bound | 12172 | 5484 (45.1%) | 6.9 | 49.3% [48.0%–50.6%] | 50.6% [49.4%–51.8%] |
| Absorption Grid | 11928 | 4784 (40.1%) | 7.3 | 52.9% [51.5%–54.4%] | 48.0% [46.9%–49.2%] |
| Advanced Weapons | 11752 | 4438 (37.8%) | 7.5 | 58.4% [56.9%–59.8%] | 44.9% [43.8%–46.0%] |
| Strategic Mastermind | 11846 | 3970 (33.5%) | 7.9 | 50.8% [49.2%–52.3%] | 49.6% [48.5%–50.7%] |
| Biotech Enhancements | 11830 | 3937 (33.3%) | 7.9 | 53.2% [51.7%–54.8%] | 48.4% [47.3%–49.5%] |
| The Shard Defiant | 11910 | 3425 (28.8%) | 8.1 | 46.2% [44.6%–47.9%] | 51.5% [50.5%–52.6%] |
| Soul Syphon | 12260 | 3202 (26.1%) | 8.3 | 56.4% [54.7%–58.1%] | 47.7% [46.7%–48.8%] |
| Nature Dominance | 12130 | 2797 (23.1%) | 8.7 | 54.3% [52.5%–56.1%] | 48.7% [47.7%–49.7%] |
| Deadly Recruits | 11962 | 2443 (20.4%) | 9.0 | 47.2% [45.2%–49.1%] | 50.7% [49.7%–51.7%] |
| Paradigm Shift | 11874 | 2126 (17.9%) | 9.2 | 47.8% [45.7%–49.9%] | 50.5% [49.5%–51.5%] |
| Advanced Medicine | 12048 | 1659 (13.8%) | 9.3 | 53.8% [51.4%–56.2%] | 49.4% [48.4%–50.4%] |
| Healing Hands | 12052 | 1610 (13.4%) | 9.4 | 54.2% [51.7%–56.6%] | 49.4% [48.4%–50.3%] |
| Stolen Futures | 11844 | 1369 (11.6%) | 11.8 | 74.2% [71.8%–76.5%] | 46.8% [45.9%–47.8%] |
| The Price of Power | 12068 | 1259 (10.4%) | 9.8 | 51.6% [48.9%–54.4%] | 49.8% [48.9%–50.8%] |
| Datic Secrets | 12116 | 925 (7.6%) | 10.1 | 48.3% [45.1%–51.5%] | 50.1% [49.2%–51.1%] |
| The Last City | 12166 | 917 (7.5%) | 10.0 | 55.0% [51.7%–58.2%] | 49.6% [48.7%–50.5%] |
| Synthesis | 11866 | 802 (6.8%) | 12.6 | 76.3% [73.2%–79.1%] | 48.1% [47.2%–49.0%] |
| Blood for Blood | 12196 | 326 (2.7%) | 11.2 | 48.5% [43.1%–53.9%] | 50.0% [49.1%–50.9%] |
| Phasic Technology | 12178 | 293 (2.4%) | 10.9 | 58.0% [52.3%–63.5%] | 49.8% [48.9%–50.7%] |
| One Mind One Army | 12068 | 280 (2.3%) | 10.8 | 60.7% [54.9%–66.3%] | 49.7% [48.8%–50.6%] |
| Project Yggdrasil | 11792 | 268 (2.3%) | 11.3 | 60.4% [54.5%–66.1%] | 49.8% [48.8%–50.7%] |
| Maglev Tunnels | 11920 | 270 (2.3%) | 10.7 | 51.5% [45.5%–57.4%] | 50.0% [49.1%–50.9%] |
| Forged in Flame | 12048 | 10 (0.1%) | 13.0 | 60.0% [31.3%–83.2%] | 50.0% [49.1%–50.9%] |
| The Crystal Gate | 12006 | 0 (0.0%) | 0.0 | – | 50.0% [49.1%–50.9%] |
| Bound for Life | 11852 | 0 (0.0%) | 0.0 | – | 50.0% [49.1%–50.9%] |

| Monster | Revealed | Defeated | Avg defeat round | Defeater WR |
|---|---|---|---|---|
| Ingeminex: Agony | 7989 | 7904 (98.9%) | 10.0 | 67.8% [66.7%–68.8%] |
| Ingeminex: Brutality | 7785 | 7587 (97.5%) | 9.4 | 65.8% [64.7%–66.8%] |
| Ingeminex: Corruption | 8100 | 7957 (98.2%) | 10.0 | 67.3% [66.2%–68.3%] |
| Ingeminex: Malice | 7910 | 7812 (98.8%) | 10.0 | 63.7% [62.6%–64.8%] |
| Ingeminex: Torment | 8056 | 7982 (99.1%) | 9.8 | 64.0% [63.0%–65.1%] |

Monster attacks landed: 30265

## 8. Methodology & caveats

- Every proportion carries a Wilson 95% interval; per-card deltas are stratified by matchup×seat, inverse-variance pooled, and Benjamini-Hochberg corrected (FDR 10%) with an effect floor.
- **These are correlations between THESE bots' policies, not causal card effects.** A card bought when already ahead will look like a winner. Treat findings as directional input; re-test surprising ones with a targeted A/B (forced-strategy bot variant) before patching.
- Seat counts are balanced exactly 50/50 per matchup by construction; seeds are sequential and reproducible (`soisim run --seed-base 1`).
