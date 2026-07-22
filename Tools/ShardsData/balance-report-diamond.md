# SoI Balance Report

## 1. Reproducibility

- Generated: 2026-07-22 17:40 UTC · git `unknown` · schema 1
- Bots: **rank:diamond** · DLC mask 7 · seed base 900000 · tag `dstats`
- Config hash: `sha256:673e0db753fb63ff686c6c98bf36bda6441b27c290a68720408a1717ee93bee0`
- Games: **5163** (5163 decisive, 0 ties, 0 failures)

## 2. Game health

- Rounds p10/p50/p90: **10 / 13 / 18** · avg submits/game 235
- Tie rate: 0.0% [0.0%–0.1%] · failures (guard/stall/error): **0**
- Win type: 4881 kill / 282 Infinity-Shard overwhelm (5.5% [4.9%–6.1%] of wins — mastery-race viability)
- Comeback wins (winner behind on health at midpoint): 38.3% [36.9%–39.6%]
- Shields prevented 91071 of 3697420 incoming damage (2.5%)

## 3. Seat advantage (staggered start: P0 M0, P1 M1)

- P0 win rate, all decisive games: **56.7% [55.4%–58.1%]** (n=5163)
- P0 win rate, mirror matches only (no character confound): **55.6% [53.1%–58.0%]** (n=1623)

## 4. Characters

| Character | Player-games | Win score |
|---|---|---|
| volos | 2125 | 53.6% [51.4%–55.7%] |
| decima | 2108 | 51.0% [48.9%–53.2%] |
| kosynwu | 2140 | 50.0% [47.9%–52.2%] |
| tetra | 2124 | 48.6% [46.5%–50.7%] |
| rez | 1829 | 46.3% [44.0%–48.5%] |

Matchups (win score of the alphabetically-first character; mirrors show seat-0 score):

| Matchup | Games | First's score |
|---|---|---|
| decima:decima | 343 | 56.3% |
| decima:kosynwu | 361 | 49.0% |
| decima:rez | 343 | 54.5% |
| decima:tetra | 359 | 54.3% |
| decima:volos | 359 | 48.5% |
| kosynwu:kosynwu | 357 | 56.3% |
| kosynwu:rez | 347 | 56.8% |
| kosynwu:tetra | 358 | 47.5% |
| kosynwu:volos | 360 | 45.3% |
| rez:rez | 228 | 57.5% |
| rez:tetra | 353 | 49.9% |
| rez:volos | 330 | 41.2% |
| tetra:tetra | 342 | 53.2% |
| tetra:volos | 370 | 43.5% |
| volos:volos | 353 | 55.2% |

## 5. Playstyles (observational!)

Win rate by feature quartile (Q1 lowest → Q4 highest), plus logistic odds ratio per +1 SD:

| Feature | Q1 | Q2 | Q3 | Q4 | OR/SD | p |
|---|---|---|---|---|---|---|
| factionConcentration | 54.4% | 52.0% | 46.4% | 47.1% | 1.15 | 0.000 |
| avgBuyCost | 46.6% | 48.5% | 50.8% | 54.1% | 1.26 | 0.000 |
| championShare | 53.6% | 51.1% | 51.2% | 44.1% | 0.85 | 0.000 |
| focusCount | 45.7% | 49.5% | 49.9% | 55.0% | 0.97 | 0.172 |
| masteryAtRound8 | 45.3% | 45.4% | 50.4% | 59.0% | 1.35 | 0.000 |
| totalAcquisitions | 42.4% | 45.6% | 50.1% | 62.0% | 1.72 | 0.000 |
| earlyAggression | 39.1% | 45.5% | 53.1% | 62.3% | 1.68 | 0.000 |

Win rate by dominant purchase faction:

| Faction | Player-games | Win rate |
|---|---|---|
| Wraethe | 2895 | 53.9% [52.1%–55.7%] |
| Undergrowth | 3167 | 53.5% [51.7%–55.2%] |
| Order | 2214 | 46.5% [44.4%–48.6%] |
| Homodeus | 2006 | 42.9% [40.8%–45.1%] |
| Aion | 44 | 40.9% [27.7%–55.6%] |

## 6. Cards

### Flagged (BH FDR 10%, |Δ| ≥ 5 pts, ≥ 100 acquisitions)

| Card | Cost | Impact Δ | p | Buy rate | Co-acquired (lift) |
|---|---|---|---|---|---|
| General Decurion (Homodeus) | 7 | **+24.6 pts** | 0.0000 | 8.5% | zen_chi_set ×3.4, anomaly_cleric ×3.1, zetta_encryptor ×2.7, numeri_drones ×2.7, drakonarius ×2.7 |
| Breaker (Aion) | 6 | **+23.5 pts** | 0.0000 | 83.8% | root_of_the_forest ×3.6, grand_architect ×3.4, omnius ×2.5, zen_chi_set ×2.5, anomaly_cleric ×2.3 |
| Root of the Forest (Undergrowth) | 7 | **+22.3 pts** | 0.0000 | 54.1% | breaker ×3.6, zen_chi_set ×3.0, anomaly_cleric ×2.0, general_decurion ×1.8, grand_architect ×1.8 |
| Zen Chi Set, Godkiller (Wraethe) | 7 | **+22.3 pts** | 0.0000 | 10.5% | general_decurion ×3.4, anomaly_cleric ×3.2, root_of_the_forest ×3.0, orm_madu ×2.6, breaker ×2.5 |
| Zara Ra, Soulflayer (Wraethe) | 5 | **+18.5 pts** | 0.0000 | 82.4% | breaker ×1.6, zen_chi_set ×1.5, raidian ×1.5, command_seer ×1.3, reactor_drone ×1.3 |
| The Rotten (Undergrowth) | 3 | **+16.8 pts** | 0.0000 | 96.5% | j_chord ×1.2, taur_arachpriest ×1.1, general_decurion ×1.1, raidian ×1.1, lucky ×1.1 |
| Ojas, Genesis Druid (Undergrowth) | 4 | **+15.7 pts** | 0.0000 | 75.7% | raidian ×1.7, breaker ×1.7, omnius ×1.6, anomaly_cleric ×1.5, root_of_the_forest ×1.5 |
| J-Chord (Aion) | 3 | **+15.1 pts** | 0.0000 | 89.6% | raidian ×1.8, general_decurion ×1.6, reactor_drone ×1.6, cryptofist_monk ×1.5, orm_madu ×1.5 |
| Shadow Apostle (Wraethe) | 2 | **+15.1 pts** | 0.0000 | 96.6% | zen_chi_set ×1.3, general_decurion ×1.3, raidian ×1.3, lucky ×1.2, axia ×1.2 |
| The Grand Architect (Order) | 7 | **+14.8 pts** | 0.0000 | 50.2% | breaker ×3.4, zen_chi_set ×2.3, general_decurion ×2.3, root_of_the_forest ×1.8, reactor_drone ×1.6 |
| Lucky (Aion) | 4 | **+14.7 pts** | 0.0000 | 79.5% | general_decurion ×1.9, reactor_drone ×1.7, orm_madu ×1.7, breaker ×1.7, zetta_encryptor ×1.6 |
| Aetherbreaker (Wraethe) | 4 | **+14.4 pts** | 0.0000 | 90.6% | lucky ×1.3, orm_madu ×1.3, anomaly_cleric ×1.3, raidian ×1.2, portal_monk ×1.2 |
| Undergrowth Aspirant (Undergrowth) | 1 | **+13.3 pts** | 0.0000 | 96.3% | general_decurion ×1.3, zen_chi_set ×1.2, ojas_genesis_druid ×1.2, anomaly_cleric ×1.2, j_chord ×1.2 |
| Anomaly Cleric (Order) | 5 | **+13.1 pts** | 0.0000 | 55.1% | zen_chi_set ×3.2, general_decurion ×3.1, orm_madu ×2.5, breaker ×2.3, raidian ×2.2 |
| Scion of Nothingness (Wraethe) | 5 | **+13.0 pts** | 0.0000 | 58.6% | zen_chi_set ×2.1, breaker ×1.6, general_decurion ×1.5, portal_monk ×1.5, cryptofist_monk ×1.4 |
| Axia (Homodeus) | 7 | **+12.5 pts** | 0.0000 | 51.1% | general_decurion ×2.4, g_48 ×1.7, ferrata_guard ×1.6, numeri_drones ×1.6, zen_chi_set ×1.6 |
| Omnius, The All-Knowing (Order) | 6 | **+12.2 pts** | 0.0000 | 65.9% | breaker ×2.5, general_decurion ×1.8, portal_monk ×1.8, anomaly_cleric ×1.7, cryptofist_monk ×1.6 |
| Raidian, Cloud Master (Order) | 5 | **+11.8 pts** | 0.0000 | 29.0% | portal_monk ×2.7, general_decurion ×2.6, zetta_encryptor ×2.5, orm_madu ×2.3, anomaly_cleric ×2.2 |
| Venator of the Wastes (Homodeus) | 4 | **+11.3 pts** | 0.0000 | 90.2% | general_decurion ×1.7, raidian ×1.6, orm_madu ×1.5, cryptofist_monk ×1.4, j_chord ×1.4 |
| Fungal Hermit (Undergrowth) | 3 | **+11.0 pts** | 0.0000 | 94.6% | raidian ×1.5, j_chord ×1.3, zen_chi_set ×1.3, lucky ×1.3, anomaly_cleric ×1.2 |
| Additri, Gaiamancer (Undergrowth) | 5 | **+10.3 pts** | 0.0000 | 81.2% | portal_monk ×1.4, zetta_encryptor ×1.4, venator_of_the_wastes ×1.3, oblivion_gatekeeper ×1.2, lucky ×1.2 |
| Ru Bo Vai, The Transcendant (Wraethe) | 5 | **+10.3 pts** | 0.0000 | 76.5% | zen_chi_set ×1.9, general_decurion ×1.6, portal_monk ×1.4, kiln_drone ×1.3, drakonarius ×1.2 |
| Carnivorous Vine (Undergrowth) | 4 | **+10.2 pts** | 0.0000 | 95.7% | general_decurion ×1.4, portal_monk ×1.3, root_of_the_forest ×1.2, breaker ×1.2, raidian ×1.2 |
| The Lost (Wraethe) | 4 | **+9.8 pts** | 0.0000 | 93.6% | breaker ×1.2, portal_monk ×1.1, lucky ×1.1, reactor_drone ×1.1, omnius ×1.1 |
| Brute (Aion) | 3 | **+9.6 pts** | 0.0000 | 92.4% | order_initiate ×1.5, limiter_drones ×1.3, kiln_drone ×1.2, legion_carrier ×1.2, aetherbreaker ×1.2 |
| Ghostwillow Avenger (Undergrowth) | 4 | **+9.6 pts** | 0.0000 | 90.5% | zen_chi_set ×1.5, omnius ×1.4, root_of_the_forest ×1.4, general_decurion ×1.3, breaker ×1.3 |
| Drakonarius (Homodeus) | 6 | **+9.6 pts** | 0.0001 | 42.9% | portal_monk ×3.0, general_decurion ×2.7, zen_chi_set ×2.0, orm_madu ×1.8, root_of_the_forest ×1.6 |
| Umbral Scourge (Wraethe) | 3 | **+9.3 pts** | 0.0000 | 89.1% | general_decurion ×1.5, zen_chi_set ×1.3, j_chord ×1.3, reactor_drone ×1.2, lucky ×1.2 |
| Zetta, The Encryptor (Order) | 5 | **+9.2 pts** | 0.0001 | 41.1% | general_decurion ×2.7, raidian ×2.5, portal_monk ×2.3, zen_chi_set ×1.9, cryptofist_monk ×1.8 |
| Data Heretic (Order) | 3 | **+8.8 pts** | 0.0000 | 96.6% | general_decurion ×1.5, orm_madu ×1.3, anomaly_cleric ×1.3, omnius ×1.2, root_of_the_forest ×1.2 |
| Taur, Arachpriest (Undergrowth) | 5 | **+8.7 pts** | 0.0001 | 53.2% | portal_monk ×2.0, general_decurion ×1.9, raidian ×1.9, root_of_the_forest ×1.7, anomaly_cleric ×1.7 |
| Le'shai Knight (Undergrowth) | 3 | **+8.0 pts** | 0.0000 | 95.8% | breaker ×1.2, raidian ×1.2, lucky ×1.1, j_chord ×1.1, undergrowth_aspirant ×1.1 |
| Duplication Fabricator (Order) | 1 | **+7.9 pts** | 0.0000 | 97.3% | orm_madu ×1.6, general_decurion ×1.5, breaker ×1.3, root_of_the_forest ×1.2, raidian ×1.2 |
| Optio Crusher (Homodeus) | 5 | **+7.8 pts** | 0.0000 | 63.0% | portal_monk ×1.8, general_decurion ×1.7, omnius ×1.5, anomaly_cleric ×1.4, zen_chi_set ×1.4 |
| Cache Warden (Order) | 2 | **+7.4 pts** | 0.0000 | 96.6% | general_decurion ×1.7, orm_madu ×1.5, command_seer ×1.3, raidian ×1.3, zen_chi_set ×1.3 |
| The Dispossessed (Wraethe) | 1 | **+6.4 pts** | 0.0000 | 96.7% | general_decurion ×1.3, scion_of_nothingness ×1.2, wraethe_skirmisher ×1.2, breaker ×1.1, nil_assassin ×1.1 |
| Limiter Drones (Homodeus) | 2 | **+5.5 pts** | 0.0000 | 76.5% | zen_chi_set ×1.5, general_decurion ×1.5, lucky ×1.5, breaker ×1.4, raidian ×1.4 |
| Pall Shades (Wraethe) | 2 | **+5.5 pts** | 0.0000 | 93.4% | zen_chi_set ×1.5, general_decurion ×1.5, wraethe_skirmisher ×1.3, li_hin ×1.2, scion_of_nothingness ×1.2 |
| Nil Assassin (Wraethe) | 2 | **+5.0 pts** | 0.0002 | 98.0% | breaker ×1.2, lucky ×1.2, general_decurion ×1.2, taur_arachpriest ×1.1, zen_chi_set ×1.1 |
| Ferrata Guard (Homodeus) | 4 | **-5.4 pts** | 0.0009 | 59.5% | general_decurion ×1.8, axia ×1.6, numeri_drones ×1.6, zen_chi_set ×1.6, scion_of_nothingness ×1.4 |
| Numeri Drones (Homodeus) | 3 | **-6.4 pts** | 0.0000 | 86.2% | general_decurion ×2.7, ferrata_guard ×1.6, axia ×1.6, drakonarius ×1.4, g_48 ×1.3 |
| Primus Pilus (Homodeus) | 2 | **-9.1 pts** | 0.0000 | 92.6% | general_decurion ×1.9, axia ×1.5, orm_madu ×1.5, cryptofist_monk ×1.4, lucky ×1.3 |
| G-48 (Homodeus) | 4 | **-10.7 pts** | 0.0000 | 76.1% | zen_chi_set ×2.0, orm_madu ×1.8, axia ×1.7, general_decurion ×1.6, swyft ×1.4 |

Positive Δ + healthy buy rate ⇒ nerf candidate; negative Δ or rock-bottom buy rate ⇒ buff candidate. Cross-check the co-acquisition column before blaming a single card.

### Buy-rate outliers by cost band (full table in sim-summary.csv)

- Cost 1–3: least bought — Reactor Drone 58.4%, Portal Monk 69.4%, Li Hin, The Shattered 72.2% · most bought — Nil Assassin 98.0%, Cinder Scars 98.0%, Duplication Fabricator 97.3%
- Cost 4–6: least bought — Raidian, Cloud Master 29.0%, Command Seer 38.1%, Zetta, The Encryptor 41.1% · most bought — Carnivorous Vine 95.7%, The Lost 93.6%, Shardwood Guardian 90.8%
- Cost 7+: least bought — General Decurion 8.5%, Zen Chi Set, Godkiller 10.5%, Orm Madu 23.6% · most bought — Root of the Forest 54.1%, Axia 51.1%, The Grand Architect 50.2%

## 7. Relics, destinies, monsters

| Relic | Recruits | WR when recruited |
|---|---|---|
| Datic Robes | 97 | 57.7% [47.8%–67.1%] |
| Entropic Talons | 1703 | 57.1% [54.7%–59.4%] |
| The Heart of Nothing | 1496 | 53.5% [51.0%–56.1%] |
| Panconscious Crown | 125 | 77.6% [69.5%–84.0%] |
| Praetorian-01 | 1588 | 56.0% [53.5%–58.4%] |
| Praetorian-02 | 183 | 55.7% [48.5%–62.7%] |
| Slipstream Shard | 643 | 50.2% [46.4%–54.1%] |
| Terminal Crescents | 1725 | 50.8% [48.5%–53.2%] |
| Warpquartz | 880 | 50.6% [47.3%–53.9%] |
| The World Piercer | 314 | 51.0% [45.4%–56.4%] |

| Destiny | In initial row | Taken | Avg round | WR taken | WR not taken |
|---|---|---|---|---|---|
| Soul Syphon | 1982 | 952 (48.0%) | 5.2 | 58.5% [55.4%–61.6%] | 42.1% [39.2%–45.2%] |
| Unconditional Conscription | 2044 | 911 (44.6%) | 6.0 | 55.7% [52.4%–58.8%] | 45.5% [42.6%–48.4%] |
| The Agony of Choice | 2088 | 923 (44.2%) | 6.0 | 54.4% [51.2%–57.6%] | 46.5% [43.7%–49.4%] |
| Healing Hands | 2042 | 883 (43.2%) | 5.8 | 45.6% [42.4%–48.9%] | 53.3% [50.4%–56.2%] |
| Advanced Medicine | 1974 | 831 (42.1%) | 5.5 | 49.5% [46.1%–52.9%] | 50.4% [47.5%–53.3%] |
| War Bound | 2086 | 833 (39.9%) | 6.5 | 43.5% [40.1%–46.8%] | 54.3% [51.6%–57.1%] |
| Advanced Weapons | 2050 | 780 (38.0%) | 6.5 | 55.9% [52.4%–59.3%] | 46.4% [43.7%–49.1%] |
| Strategic Mastermind | 2028 | 666 (32.8%) | 5.7 | 57.2% [53.4%–60.9%] | 46.5% [43.8%–49.1%] |
| Nature Dominance | 2026 | 603 (29.8%) | 6.7 | 53.1% [49.1%–57.0%] | 48.7% [46.1%–51.3%] |
| Absorption Grid | 2218 | 601 (27.1%) | 7.1 | 47.9% [44.0%–51.9%] | 50.8% [48.3%–53.2%] |
| Whatever it Takes | 2162 | 543 (25.1%) | 7.7 | 43.5% [39.4%–47.7%] | 52.2% [49.8%–54.6%] |
| Power Struggle | 2074 | 496 (23.9%) | 7.0 | 54.2% [49.8%–58.6%] | 48.7% [46.2%–51.1%] |
| Biotech Enhancements | 2026 | 427 (21.1%) | 7.4 | 56.0% [51.2%–60.6%] | 48.4% [46.0%–50.9%] |
| The Shard Defiant | 2018 | 362 (17.9%) | 6.4 | 49.4% [44.3%–54.6%] | 50.1% [47.7%–52.5%] |
| The Price of Power | 2098 | 276 (13.2%) | 7.7 | 45.3% [39.5%–51.2%] | 50.7% [48.4%–53.0%] |
| Deadly Recruits | 2030 | 245 (12.1%) | 7.9 | 53.5% [47.2%–59.6%] | 49.5% [47.2%–51.8%] |
| Stolen Futures | 2174 | 217 (10.0%) | 11.2 | 54.8% [48.2%–61.3%] | 49.5% [47.3%–51.7%] |
| Phasic Technology | 2114 | 211 (10.0%) | 8.0 | 45.0% [38.5%–51.8%] | 50.6% [48.3%–52.8%] |
| True Leader | 1972 | 127 (6.4%) | 10.2 | 63.8% [55.1%–71.6%] | 49.1% [46.8%–51.3%] |
| The Crystal Gate | 2056 | 130 (6.3%) | 8.1 | 44.6% [36.3%–53.2%] | 50.4% [48.1%–52.6%] |
| The Last City | 2096 | 132 (6.3%) | 9.3 | 50.0% [41.6%–58.4%] | 50.0% [47.8%–52.2%] |
| Project Yggdrasil | 2088 | 128 (6.1%) | 8.1 | 55.5% [46.8%–63.8%] | 49.6% [47.4%–51.9%] |
| Forged in Flame | 2076 | 127 (6.1%) | 9.1 | 49.6% [41.1%–58.2%] | 50.0% [47.8%–52.2%] |
| Datic Secrets | 2042 | 118 (5.8%) | 9.2 | 43.2% [34.6%–52.2%] | 50.4% [48.2%–52.6%] |
| One Mind One Army | 2072 | 101 (4.9%) | 10.1 | 51.5% [41.9%–61.0%] | 49.9% [47.7%–52.1%] |
| Bound for Life | 2040 | 92 (4.5%) | 10.1 | 75.0% [65.3%–82.7%] | 48.8% [46.6%–51.0%] |
| Synthesis | 2142 | 93 (4.3%) | 12.2 | 69.9% [59.9%–78.3%] | 49.1% [46.9%–51.3%] |
| Paradigm Shift | 1990 | 82 (4.1%) | 10.6 | 42.7% [32.5%–53.5%] | 50.3% [48.1%–52.6%] |
| Maglev Tunnels | 2030 | 65 (3.2%) | 9.5 | 53.8% [41.9%–65.4%] | 49.9% [47.7%–52.1%] |
| Blood for Blood | 2118 | 51 (2.4%) | 9.8 | 49.0% [35.9%–62.3%] | 50.0% [47.9%–52.2%] |

| Monster | Revealed | Defeated | Avg defeat round | Defeater WR |
|---|---|---|---|---|
| Ingeminex: Agony | 1021 | 884 (86.6%) | 10.6 | 63.8% [60.6%–66.9%] |
| Ingeminex: Brutality | 1043 | 937 (89.8%) | 9.8 | 62.0% [58.9%–65.1%] |
| Ingeminex: Corruption | 1013 | 764 (75.4%) | 9.7 | 63.7% [60.3%–67.1%] |
| Ingeminex: Malice | 997 | 589 (59.1%) | 12.3 | 59.8% [55.8%–63.6%] |
| Ingeminex: Torment | 1016 | 750 (73.8%) | 10.7 | 63.5% [60.0%–66.8%] |

Monster attacks landed: 4402

## 8. Methodology & caveats

- Every proportion carries a Wilson 95% interval; per-card deltas are stratified by matchup×seat, inverse-variance pooled, and Benjamini-Hochberg corrected (FDR 10%) with an effect floor.
- **These are correlations between THESE bots' policies, not causal card effects.** A card bought when already ahead will look like a winner. Treat findings as directional input; re-test surprising ones with a targeted A/B (forced-strategy bot variant) before patching.
- Seat counts are balanced exactly 50/50 per matchup by construction; seeds are sequential and reproducible (`soisim run --seed-base 900000`).
