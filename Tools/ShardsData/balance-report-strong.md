# SoI Balance Report

## 1. Reproducibility

- Generated: 2026-07-21 16:38 UTC · git `3e79f62` · schema 1
- Bots: **ismcts-V2-200it** @ budget 200 · DLC mask 7 · seed base 1 · tag `strong-headline`
- Config hash: `sha256:2eac998bf6e0be9042ea566f63e740de87a28df51242c3e43dc633bc5610d3ff`
- Games: **1200** (1199 decisive, 1 ties, 0 failures)

## 2. Game health

- Rounds p10/p50/p90: **11 / 14 / 18** · avg submits/game 282
- Tie rate: 0.1% [0.0%–0.5%] · failures (guard/stall/error): **0**
- Win type: 1087 kill / 112 Infinity-Shard overwhelm (9.3% [7.8%–11.1%] of wins — mastery-race viability)
- Comeback wins (winner behind on health at midpoint): 38.9% [36.2%–41.7%]
- Shields prevented 28590 of 1429136 incoming damage (2.0%)

## 3. Seat advantage (staggered start: P0 M0, P1 M1)

- P0 win rate, all decisive games: **54.4% [51.5%–57.2%]** (n=1199)
- P0 win rate, mirror matches only (no character confound): **57.1% [52.2%–61.9%]** (n=399)

## 4. Characters

| Character | Player-games | Win score |
|---|---|---|
| decima | 480 | 52.9% [48.4%–57.3%] |
| volos | 480 | 51.9% [47.4%–56.3%] |
| tetra | 480 | 51.5% [47.0%–55.9%] |
| kosynwu | 480 | 49.4% [44.9%–53.8%] |
| rez | 480 | 44.4% [40.0%–48.8%] |

Matchups (win score of the alphabetically-first character; mirrors show seat-0 score):

| Matchup | Games | First's score |
|---|---|---|
| decima:decima | 80 | 61.3% |
| decima:kosynwu | 80 | 58.8% |
| decima:rez | 80 | 55.0% |
| decima:tetra | 80 | 41.2% |
| decima:volos | 80 | 62.5% |
| kosynwu:kosynwu | 80 | 52.5% |
| kosynwu:rez | 80 | 61.3% |
| kosynwu:tetra | 80 | 50.0% |
| kosynwu:volos | 80 | 43.8% |
| rez:rez | 80 | 50.6% |
| rez:tetra | 80 | 50.0% |
| rez:volos | 80 | 32.5% |
| tetra:tetra | 80 | 56.2% |
| tetra:volos | 80 | 50.0% |
| volos:volos | 80 | 65.0% |

## 5. Playstyles (observational!)

Win rate by feature quartile (Q1 lowest → Q4 highest), plus logistic odds ratio per +1 SD:

| Feature | Q1 | Q2 | Q3 | Q4 | OR/SD | p |
|---|---|---|---|---|---|---|
| factionConcentration | 53.3% | 52.2% | 48.9% | 45.7% | 1.22 | 0.000 |
| avgBuyCost | 41.4% | 48.5% | 51.6% | 58.5% | 1.29 | 0.000 |
| championShare | 46.6% | 48.7% | 54.6% | 50.2% | 0.98 | 0.749 |
| focusCount | 41.6% | 45.7% | 53.9% | 58.8% | 1.04 | 0.397 |
| masteryAtRound8 | 38.7% | 44.5% | 54.3% | 62.5% | 1.52 | 0.000 |
| totalAcquisitions | 35.4% | 43.0% | 51.9% | 69.7% | 2.26 | 0.000 |
| earlyAggression | 40.9% | 46.5% | 51.8% | 60.8% | 1.67 | 0.000 |

Win rate by dominant purchase faction:

| Faction | Player-games | Win rate |
|---|---|---|
| Undergrowth | 635 | 52.0% [48.1%–55.8%] |
| Homodeus | 512 | 49.6% [45.3%–53.9%] |
| Wraethe | 678 | 49.4% [45.7%–53.2%] |
| Order | 565 | 49.2% [45.1%–53.3%] |
| Aion | 8 | 25.0% [7.1%–59.1%] |

## 6. Cards

### Flagged (BH FDR 10%, |Δ| ≥ 5 pts, ≥ 100 acquisitions)

| Card | Cost | Impact Δ | p | Buy rate | Co-acquired (lift) |
|---|---|---|---|---|---|
| Root of the Forest (Undergrowth) | 7 | **+31.2 pts** | 0.0000 | 44.0% | breaker ×3.7, zen_chi_set ×1.9, omnius ×1.8, anomaly_cleric ×1.8, general_decurion ×1.8 |
| The Grand Architect (Order) | 7 | **+29.5 pts** | 0.0000 | 37.1% | breaker ×5.0, zen_chi_set ×2.4, cryptofist_monk ×2.2, reactor_drone ×1.9, raidian ×1.9 |
| Axia (Homodeus) | 7 | **+29.1 pts** | 0.0000 | 50.0% | general_decurion ×2.5, primus_pilus ×1.9, ferrata_guard ×1.8, numeri_drones ×1.8, evokatus ×1.6 |
| Additri, Gaiamancer (Undergrowth) | 5 | **+26.6 pts** | 0.0000 | 69.1% | portal_monk ×1.8, grand_architect ×1.7, g_48 ×1.6, root_of_the_forest ×1.5, breaker ×1.4 |
| Breaker (Aion) | 6 | **+24.5 pts** | 0.0000 | 72.1% | grand_architect ×5.0, root_of_the_forest ×3.7, omnius ×2.5, cryptofist_monk ×2.2, anomaly_cleric ×1.8 |
| Drakonarius (Homodeus) | 6 | **+23.9 pts** | 0.0000 | 52.9% | portal_monk ×2.1, zen_chi_set ×1.8, ru_bo_vai ×1.7, omnius ×1.6, raidian ×1.6 |
| Ru Bo Vai, The Transcendant (Wraethe) | 5 | **+23.5 pts** | 0.0000 | 75.3% | drakonarius ×1.7, grand_architect ×1.6, anomaly_cleric ×1.5, general_decurion ×1.5, ghostwillow_avenger ×1.4 |
| Li Hin, The Shattered (Wraethe) | 3 | **+23.2 pts** | 0.0000 | 82.5% | zen_chi_set ×1.8, brute ×1.5, root_of_the_forest ×1.5, cryptofist_monk ×1.5, command_seer ×1.4 |
| Furrowing Elemental (Undergrowth) | 5 | **+23.1 pts** | 0.0000 | 65.5% | breaker ×1.7, drakonarius ×1.6, root_of_the_forest ×1.5, portal_monk ×1.5, omnius ×1.4 |
| Zara Ra, Soulflayer (Wraethe) | 5 | **+20.3 pts** | 0.0000 | 70.4% | raidian ×1.6, general_decurion ×1.5, grand_architect ×1.5, anomaly_cleric ×1.5, breaker ×1.4 |
| Carnivorous Vine (Undergrowth) | 4 | **+19.9 pts** | 0.0000 | 87.0% | zen_chi_set ×1.3, root_of_the_forest ×1.3, orm_madu ×1.3, breaker ×1.3, ferrata_guard ×1.2 |
| Optio Crusher (Homodeus) | 5 | **+19.1 pts** | 0.0000 | 68.0% | general_decurion ×1.8, zen_chi_set ×1.5, g_48 ×1.5, drakonarius ×1.5, portal_monk ×1.4 |
| The Rotten (Undergrowth) | 3 | **+17.9 pts** | 0.0000 | 95.1% | orm_madu ×1.4, taur_arachpriest ×1.2, general_decurion ×1.2, breaker ×1.2, zen_chi_set ×1.2 |
| Raidian, Cloud Master (Order) | 5 | **+16.6 pts** | 0.0000 | 59.2% | general_decurion ×2.0, grand_architect ×1.9, fao_cutul ×1.7, portal_monk ×1.6, axia ×1.6 |
| Arach Devotees (Undergrowth) | 2 | **+16.6 pts** | 0.0000 | 95.5% | general_decurion ×1.5, cryptofist_monk ×1.3, zen_chi_set ×1.3, root_of_the_forest ×1.3, taur_arachpriest ×1.3 |
| Undergrowth Aspirant (Undergrowth) | 1 | **+16.1 pts** | 0.0000 | 98.1% | taur_arachpriest ×1.4, anomaly_cleric ×1.3, grand_architect ×1.2, oblivion_gatekeeper ×1.2, breaker ×1.2 |
| Anomaly Cleric (Order) | 5 | **+15.8 pts** | 0.0001 | 60.5% | general_decurion ×2.0, cryptofist_monk ×1.8, root_of_the_forest ×1.8, breaker ×1.8, zen_chi_set ×1.7 |
| Venator of the Wastes (Homodeus) | 4 | **+15.0 pts** | 0.0001 | 85.8% | zen_chi_set ×1.4, general_decurion ×1.4, command_seer ×1.3, j_chord ×1.3, furrowing_elemental ×1.3 |
| The Dispossessed (Wraethe) | 1 | **+14.4 pts** | 0.0000 | 98.0% | general_decurion ×1.3, zen_chi_set ×1.3, axia ×1.3, orm_madu ×1.2, giga_source_adept ×1.2 |
| The Lost (Wraethe) | 4 | **+14.3 pts** | 0.0000 | 85.2% | omnius ×1.2, evokatus ×1.2, breaker ×1.2, raidian ×1.2, kiln_drone ×1.2 |
| Shard Abstractor (Order) | 3 | **+13.7 pts** | 0.0000 | 89.9% | general_decurion ×1.4, axia ×1.2, orm_madu ×1.2, additri_gaiamancer ×1.2, anomaly_cleric ×1.2 |
| Duplication Fabricator (Order) | 1 | **+13.6 pts** | 0.0000 | 97.3% | zen_chi_set ×1.7, general_decurion ×1.4, grand_architect ×1.3, anomaly_cleric ×1.3, breaker ×1.3 |
| Evokatus (Homodeus) | 4 | **+13.4 pts** | 0.0000 | 77.8% | axia ×1.6, oblivion_gatekeeper ×1.3, ferrata_guard ×1.3, orm_madu ×1.3, legion_carrier ×1.3 |
| Mainframe Abbot (Order) | 3 | **+13.1 pts** | 0.0000 | 88.4% | omnius ×1.5, j_chord ×1.3, raidian ×1.3, zetta_encryptor ×1.3, grand_architect ×1.3 |
| Omnius, The All-Knowing (Order) | 6 | **+13.0 pts** | 0.0009 | 65.5% | breaker ×2.5, orm_madu ×2.2, portal_monk ×1.9, zen_chi_set ×1.8, general_decurion ×1.8 |
| Aetherbreaker (Wraethe) | 4 | **+12.7 pts** | 0.0000 | 89.2% | general_decurion ×1.9, grand_architect ×1.7, breaker ×1.3, torian_commandos ×1.3, cryptofist_monk ×1.2 |
| Taur, Arachpriest (Undergrowth) | 5 | **+12.2 pts** | 0.0049 | 45.9% | zetta_encryptor ×1.6, drakonarius ×1.6, omnius ×1.6, mining_drones ×1.5, portal_monk ×1.5 |
| Kiln Drone (Homodeus) | 1 | **+11.5 pts** | 0.0000 | 96.7% | orm_madu ×1.7, general_decurion ×1.5, omnius ×1.4, zen_chi_set ×1.3, anomaly_cleric ×1.3 |
| Oblivion Gatekeeper (Wraethe) | 4 | **+11.3 pts** | 0.0025 | 84.1% | general_decurion ×2.6, g_48 ×1.6, zen_chi_set ×1.4, ghostwillow_avenger ×1.4, orm_madu ×1.4 |
| Scion of Nothingness (Wraethe) | 5 | **+11.3 pts** | 0.0002 | 55.4% | general_decurion ×2.5, orm_madu ×1.5, j_chord ×1.5, grand_architect ×1.4, raidian ×1.3 |
| Brute (Aion) | 3 | **+10.8 pts** | 0.0004 | 94.3% | li_hin ×1.5, ojas_genesis_druid ×1.3, lucky ×1.3, omnius ×1.3, breaker ×1.3 |
| Torian Commandos (Homodeus) | 3 | **+10.7 pts** | 0.0000 | 90.6% | zen_chi_set ×1.4, drakonarius ×1.4, orm_madu ×1.3, general_decurion ×1.3, anomaly_cleric ×1.3 |
| Fungal Hermit (Undergrowth) | 3 | **+10.4 pts** | 0.0004 | 90.8% | general_decurion ×1.5, grand_architect ×1.5, j_chord ×1.3, breaker ×1.2, oblivion_gatekeeper ×1.2 |
| Nil Assassin (Wraethe) | 2 | **+9.4 pts** | 0.0002 | 97.4% | zen_chi_set ×1.2, raidian ×1.2, j_chord ×1.2, giga_source_adept ×1.1, taur_arachpriest ×1.1 |
| Thorn Zealot (Undergrowth) | 3 | **+9.1 pts** | 0.0016 | 93.7% | zen_chi_set ×1.4, orm_madu ×1.3, omnius ×1.3, anomaly_cleric ×1.3, zetta_encryptor ×1.2 |
| Wraethe Skirmisher (Wraethe) | 1 | **+8.7 pts** | 0.0008 | 98.5% | j_chord ×1.3, cryptofist_monk ×1.2, oblivion_gatekeeper ×1.2, zetta_encryptor ×1.1, fao_cutul ×1.1 |
| Shardwood Guardian (Undergrowth) | 4 | **+8.5 pts** | 0.0008 | 85.4% | general_decurion ×1.3, taur_arachpriest ×1.3, breaker ×1.3, j_chord ×1.3, orm_madu ×1.2 |
| Cache Warden (Order) | 2 | **+8.5 pts** | 0.0039 | 96.9% | general_decurion ×1.4, anomaly_cleric ×1.4, ghostwillow_avenger ×1.3, j_chord ×1.3, drakonarius ×1.3 |
| J-Chord (Aion) | 3 | **+8.3 pts** | 0.0346 | 93.5% | general_decurion ×2.0, scion_of_nothingness ×1.5, cryptofist_monk ×1.5, furrowing_elemental ×1.4, primus_pilus ×1.4 |
| Shadebound Sentry (Wraethe) | 3 | **+8.3 pts** | 0.0046 | 91.8% | drakonarius ×1.4, j_chord ×1.2, lucky ×1.2, giga_source_adept ×1.2, swyft ×1.2 |
| Mining Drones (Homodeus) | 2 | **+7.7 pts** | 0.0031 | 96.6% | taur_arachpriest ×1.5, general_decurion ×1.4, zen_chi_set ×1.3, orm_madu ×1.3, j_chord ×1.3 |
| Pall Shades (Wraethe) | 2 | **+7.4 pts** | 0.0039 | 95.0% | orm_madu ×1.4, zen_chi_set ×1.3, j_chord ×1.3, general_decurion ×1.3, taur_arachpriest ×1.2 |
| Command Seer (Order) | 4 | **+7.1 pts** | 0.0201 | 57.2% | omnius ×1.5, general_decurion ×1.5, orm_madu ×1.5, lucky ×1.4, zen_chi_set ×1.4 |
| Shadow Apostle (Wraethe) | 2 | **+6.1 pts** | 0.0225 | 95.2% | general_decurion ×1.4, lucky ×1.3, axia ×1.2, furrowing_elemental ×1.2, orm_madu ×1.2 |
| Order Initiate (Order) | 1 | **+5.8 pts** | 0.0256 | 98.0% | zen_chi_set ×1.5, general_decurion ×1.5, orm_madu ×1.3, scion_of_nothingness ×1.3, breaker ×1.3 |
| Le'shai Knight (Undergrowth) | 3 | **+5.8 pts** | 0.0298 | 94.4% | general_decurion ×1.4, zen_chi_set ×1.3, lucky ×1.2, orm_madu ×1.2, drakonarius ×1.2 |
| Dash (Aion) | 2 | **+5.6 pts** | 0.0599 | 96.0% | general_decurion ×1.8, orm_madu ×1.5, root_of_the_forest ×1.4, raidian ×1.3, scion_of_nothingness ×1.3 |
| Data Heretic (Order) | 3 | **+5.6 pts** | 0.0300 | 95.3% | general_decurion ×1.4, orm_madu ×1.3, breaker ×1.3, j_chord ×1.3, grand_architect ×1.2 |
| Korvus Legionnaire (Homodeus) | 3 | **+5.5 pts** | 0.0279 | 88.0% | anomaly_cleric ×1.3, ferrata_guard ×1.2, zen_chi_set ×1.2, general_decurion ×1.2, ghostwillow_avenger ×1.2 |

Positive Δ + healthy buy rate ⇒ nerf candidate; negative Δ or rock-bottom buy rate ⇒ buff candidate. Cross-check the co-acquisition column before blaming a single card.

### Buy-rate outliers by cost band (full table in sim-summary.csv)

- Cost 1–3: least bought — Reactor Drone 77.5%, Li Hin, The Shattered 82.5%, Portal Monk 84.0% · most bought — Wraethe Skirmisher 98.5%, Undergrowth Aspirant 98.1%, Order Initiate 98.0%
- Cost 4–6: least bought — Taur, Arachpriest 45.9%, Zetta, The Encryptor 48.3%, Drakonarius 52.9% · most bought — Aetherbreaker 89.2%, Carnivorous Vine 87.0%, Venator of the Wastes 85.8%
- Cost 7+: least bought — Zen Chi Set, Godkiller 22.1%, General Decurion 22.3%, Orm Madu 28.0% · most bought — Axia 50.0%, Root of the Forest 44.0%, The Grand Architect 37.1%

## 7. Relics, destinies, monsters

| Relic | Recruits | WR when recruited |
|---|---|---|
| Datic Robes | 226 | 55.3% [48.8%–61.6%] |
| Entropic Talons | 363 | 56.7% [51.6%–61.7%] |
| The Heart of Nothing | 319 | 56.1% [50.6%–61.5%] |
| Panconscious Crown | 82 | 59.8% [48.9%–69.7%] |
| Praetorian-01 | 338 | 63.3% [58.1%–68.3%] |
| Praetorian-02 | 110 | 47.3% [38.2%–56.5%] |
| Slipstream Shard | 273 | 50.5% [44.7%–56.4%] |
| Terminal Crescents | 231 | 58.4% [52.0%–64.6%] |
| Warpquartz | 160 | 52.5% [44.8%–60.1%] |
| The World Piercer | 122 | 59.0% [50.1%–67.3%] |

| Destiny | In initial row | Taken | Avg round | WR taken | WR not taken |
|---|---|---|---|---|---|
| Unconditional Conscription | 452 | 177 (39.2%) | 5.9 | 66.7% [59.4%–73.2%] | 39.3% [33.7%–45.2%] |
| Advanced Weapons | 472 | 178 (37.7%) | 6.3 | 56.7% [49.4%–63.8%] | 45.9% [40.3%–51.6%] |
| The Agony of Choice | 484 | 174 (36.0%) | 6.6 | 58.6% [51.2%–65.7%] | 45.2% [39.7%–50.7%] |
| Soul Syphon | 506 | 153 (30.2%) | 6.9 | 60.8% [52.9%–68.2%] | 45.3% [40.2%–50.5%] |
| Strategic Mastermind | 530 | 141 (26.6%) | 6.0 | 63.1% [54.9%–70.6%] | 45.2% [40.4%–50.2%] |
| Phasic Technology | 440 | 114 (25.9%) | 6.4 | 54.4% [45.2%–63.2%] | 48.5% [43.1%–53.9%] |
| Stolen Futures | 488 | 126 (25.8%) | 7.8 | 59.5% [50.8%–67.7%] | 46.7% [41.6%–51.8%] |
| Absorption Grid | 460 | 118 (25.7%) | 7.1 | 52.5% [43.6%–61.3%] | 49.1% [43.9%–54.4%] |
| Healing Hands | 454 | 111 (24.4%) | 7.0 | 45.9% [37.0%–55.2%] | 51.3% [46.0%–56.6%] |
| Biotech Enhancements | 480 | 117 (24.4%) | 7.2 | 50.4% [41.5%–59.3%] | 49.9% [44.7%–55.0%] |
| Power Struggle | 456 | 111 (24.3%) | 8.6 | 54.1% [44.8%–63.0%] | 48.7% [43.5%–54.0%] |
| Whatever it Takes | 478 | 114 (23.8%) | 9.2 | 61.4% [52.2%–69.8%] | 46.4% [41.4%–51.6%] |
| Advanced Medicine | 458 | 109 (23.8%) | 6.9 | 39.4% [30.8%–48.8%] | 53.3% [48.1%–58.5%] |
| Nature Dominance | 534 | 127 (23.8%) | 7.4 | 57.5% [48.8%–65.7%] | 47.7% [42.9%–52.5%] |
| The Shard Defiant | 498 | 116 (23.3%) | 6.6 | 50.9% [41.9%–59.8%] | 49.7% [44.8%–54.7%] |
| Bound for Life | 496 | 110 (22.2%) | 6.7 | 56.4% [47.0%–65.3%] | 48.2% [43.2%–53.2%] |
| War Bound | 480 | 102 (21.2%) | 8.1 | 59.8% [50.1%–68.8%] | 47.4% [42.4%–52.4%] |
| True Leader | 470 | 99 (21.1%) | 8.8 | 53.5% [43.8%–63.0%] | 49.1% [44.0%–54.1%] |
| Synthesis | 464 | 92 (19.8%) | 8.2 | 57.6% [47.4%–67.2%] | 48.1% [43.1%–53.2%] |
| Project Yggdrasil | 456 | 79 (17.3%) | 7.1 | 51.9% [41.1%–62.6%] | 49.6% [44.6%–54.6%] |
| One Mind One Army | 532 | 91 (17.1%) | 6.8 | 48.4% [38.4%–58.5%] | 50.3% [45.7%–55.0%] |
| Deadly Recruits | 424 | 61 (14.4%) | 9.2 | 52.5% [40.2%–64.5%] | 49.6% [44.5%–54.7%] |
| The Price of Power | 522 | 74 (14.2%) | 7.5 | 60.8% [49.4%–71.1%] | 48.2% [43.6%–52.8%] |
| Paradigm Shift | 480 | 56 (11.7%) | 8.0 | 55.4% [42.4%–67.6%] | 49.3% [44.6%–54.0%] |
| The Last City | 508 | 50 (9.8%) | 7.8 | 42.0% [29.4%–55.8%] | 50.9% [46.3%–55.4%] |
| Datic Secrets | 442 | 40 (9.0%) | 7.3 | 55.0% [39.8%–69.3%] | 49.5% [44.6%–54.4%] |
| Maglev Tunnels | 452 | 39 (8.6%) | 8.6 | 43.6% [29.3%–59.0%] | 50.6% [45.8%–55.4%] |
| The Crystal Gate | 534 | 44 (8.2%) | 7.7 | 36.4% [23.8%–51.1%] | 51.2% [46.8%–55.6%] |
| Forged in Flame | 502 | 36 (7.2%) | 8.3 | 41.7% [27.1%–57.8%] | 50.6% [46.1%–55.2%] |
| Blood for Blood | 448 | 22 (4.9%) | 8.5 | 40.9% [23.3%–61.3%] | 50.5% [45.7%–55.2%] |

| Monster | Revealed | Defeated | Avg defeat round | Defeater WR |
|---|---|---|---|---|
| Ingeminex: Agony | 264 | 235 (89.0%) | 11.5 | 74.5% [68.5%–79.6%] |
| Ingeminex: Brutality | 263 | 242 (92.0%) | 11.6 | 74.0% [68.1%–79.1%] |
| Ingeminex: Corruption | 286 | 253 (88.5%) | 11.2 | 70.0% [64.0%–75.3%] |
| Ingeminex: Malice | 276 | 240 (87.0%) | 11.8 | 76.7% [70.9%–81.6%] |
| Ingeminex: Torment | 280 | 244 (87.1%) | 11.7 | 70.9% [64.9%–76.2%] |

Monster attacks landed: 1133

## 8. Methodology & caveats

- Every proportion carries a Wilson 95% interval; per-card deltas are stratified by matchup×seat, inverse-variance pooled, and Benjamini-Hochberg corrected (FDR 10%) with an effect floor.
- **These are correlations between THESE bots' policies, not causal card effects.** A card bought when already ahead will look like a winner. Treat findings as directional input; re-test surprising ones with a targeted A/B (forced-strategy bot variant) before patching.
- Seat counts are balanced exactly 50/50 per matchup by construction; seeds are sequential and reproducible (`soisim run --seed-base 1`).
