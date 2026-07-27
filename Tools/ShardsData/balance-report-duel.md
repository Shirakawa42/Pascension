# SoI Balance Report

## 1. Reproducibility

- Generated: 2026-07-27 12:15 UTC · git `1def077` · schema 1
- Bots: **bench:greedy-v5** · DLC mask 15 · seed base 1 · tag `greedy-v5-duel`
- Config hash: `sha256:e3d562a107de1222a2b56446bd4e6573213c621a4bd249e8b5379c69a4ee4243`
- Games: **30000** (30000 decisive, 0 ties, 0 failures)

## 2. Game health

- Rounds p10/p50/p90: **11 / 14 / 17** · avg submits/game 366
- Tie rate: 0.0% [0.0%–0.0%] · failures (guard/stall/error): **0**
- Win type: 14667 kill / 15333 Infinity-Shard overwhelm (51.1% [50.5%–51.7%] of wins — mastery-race viability)
- Comeback wins (winner behind on health at midpoint): 46.8% [46.3%–47.4%]
- Shields prevented 795087 of 174649608 incoming damage (0.5%)

## 3. Seat advantage (staggered start: P0 M0, P1 M1)

- P0 win rate, all decisive games: **59.4% [58.9%–60.0%]** (n=30000)
- P0 win rate, mirror matches only (no character confound): **–** (n=0)

## 4. Characters

| Character | Player-games | Win score |
|---|---|---|
| decima | 18000 | 57.9% [57.2%–58.6%] |
| rez | 10000 | 52.9% [52.0%–53.9%] |
| tetra | 12000 | 49.0% [48.1%–49.9%] |
| volos | 10000 | 44.2% [43.3%–45.2%] |
| kosynwu | 10000 | 39.8% [38.9%–40.8%] |

Matchups (win score of the alphabetically-first character; mirrors show seat-0 score):

| Matchup | Games | First's score |
|---|---|---|
| decima:kosynwu | 4000 | 64.6% |
| decima:rez | 4000 | 53.8% |
| decima:tetra | 6000 | 53.8% |
| decima:volos | 4000 | 61.3% |
| kosynwu:rez | 2000 | 38.6% |
| kosynwu:tetra | 2000 | 41.2% |
| kosynwu:volos | 2000 | 48.5% |
| rez:tetra | 2000 | 53.9% |
| rez:volos | 2000 | 57.1% |
| tetra:volos | 2000 | 50.6% |

## 5. Playstyles (observational!)

Win rate by feature quartile (Q1 lowest → Q4 highest), plus logistic odds ratio per +1 SD:

| Feature | Q1 | Q2 | Q3 | Q4 | OR/SD | p |
|---|---|---|---|---|---|---|
| factionConcentration | 57.8% | 55.6% | 48.6% | 38.0% | 1.07 | 0.000 |
| avgBuyCost | 33.1% | 47.8% | 56.8% | 62.3% | 1.43 | 0.000 |
| championShare | 44.0% | 51.6% | 54.2% | 50.2% | 1.01 | 0.405 |
| focusCount | 43.0% | 49.0% | 51.8% | 56.2% | 0.97 | 0.002 |
| masteryAtRound8 | 40.8% | 41.9% | 50.1% | 67.2% | 1.84 | 0.000 |
| totalAcquisitions | 28.7% | 40.1% | 54.8% | 76.4% | 2.67 | 0.000 |
| earlyAggression | 50.4% | 47.4% | 48.9% | 53.4% | 1.29 | 0.000 |

Win rate by dominant purchase faction:

| Faction | Player-games | Win rate |
|---|---|---|
| Order | 14720 | 57.0% [56.2%–57.8%] |
| Undergrowth | 12970 | 48.8% [48.0%–49.7%] |
| Homodeus | 15758 | 48.6% [47.8%–49.4%] |
| Wraethe | 16318 | 46.2% [45.5%–47.0%] |
| Aion | 234 | 30.8% [25.2%–37.0%] |

## 6. Cards

### Flagged (BH FDR 10%, |Δ| ≥ 5 pts, ≥ 100 acquisitions)

| Card | Cost | Impact Δ | p | Buy rate | Co-acquired (lift) |
|---|---|---|---|---|---|
| Comet (Aion) | 14 | **+49.1 pts** | 0.0000 | 2.2% | portal_monk ×4.5, lifebloom_ritual ×3.7, scion_of_nothingness ×3.1, zetta_encryptor ×3.0, doomstalker ×2.8 |
| Ghostwillow Avenger (Undergrowth) | 4 | **+43.0 pts** | 0.0000 | 89.2% | scion_of_nothingness ×1.6, doomstalker ×1.5, century_forge ×1.5, lifebloom_ritual ×1.5, root_of_the_forest_duel ×1.5 |
| The Grand Architect (Order) | 7 | **+39.8 pts** | 0.0000 | 93.4% | lifebloom_ritual ×1.5, scion_of_nothingness ×1.5, breaker ×1.5, ojas_genesis_druid ×1.4, general_decurion ×1.4 |
| Omnius, The All-Knowing (Order) | 6 | **+34.4 pts** | 0.0000 | 95.1% | portal_monk ×1.5, lifebloom_ritual ×1.4, century_forge ×1.4, breaker ×1.4, doomstalker ×1.4 |
| Axia (Homodeus) | 8 | **+30.2 pts** | 0.0000 | 75.6% | general_decurion ×1.8, testudo_vanguard ×1.6, lifebloom_ritual ×1.6, century_forge ×1.5, doomstalker ×1.5 |
| Giga, Source Adept (Order) | 2 | **+27.5 pts** | 0.0000 | 98.4% | scion_of_nothingness ×1.4, ojas_genesis_druid ×1.4, doomstalker ×1.3, portal_monk ×1.3, century_forge ×1.3 |
| Ojas, Genesis Druid (Undergrowth) | 4 | **+27.1 pts** | 0.0000 | 22.8% | lifebloom_ritual ×4.5, scion_of_nothingness ×3.1, longshot ×2.7, doomstalker ×2.5, the_lost_duel ×2.2 |
| Root of the Forest (Undergrowth) | 7 | **+24.7 pts** | 0.0000 | 58.9% | breaker ×2.5, lifebloom_ritual ×2.4, scion_of_nothingness ×2.3, doomstalker ×2.1, century_forge ×2.1 |
| Breaker (Aion) | 6 | **+23.7 pts** | 0.0000 | 95.4% | root_of_the_forest_duel ×2.5, lifebloom_ritual ×2.3, scion_of_nothingness ×2.2, doomstalker ×2.1, century_forge ×2.0 |
| Systema A.I. (Order) | 3 | **+23.3 pts** | 0.0000 | 97.9% | lifebloom_ritual ×1.6, ojas_genesis_druid ×1.4, scion_of_nothingness ×1.4, century_forge ×1.3, doomstalker ×1.3 |
| General Decurion (Homodeus) | 7 | **+22.7 pts** | 0.0000 | 88.1% | axia_duel ×1.8, lifebloom_ritual ×1.8, scion_of_nothingness ×1.7, zetta_encryptor ×1.7, century_forge ×1.6 |
| Shard Abstractor (Order) | 3 | **+22.3 pts** | 0.0000 | 97.9% | lifebloom_ritual ×1.4, ojas_genesis_druid ×1.3, scion_of_nothingness ×1.3, ghostwillow_avenger ×1.3, doomstalker ×1.2 |
| Anomaly Cleric (Order) | 5 | **+22.0 pts** | 0.0000 | 95.7% | century_forge ×1.5, scion_of_nothingness ×1.4, root_of_the_forest_duel ×1.4, doomstalker ×1.4, general_decurion ×1.4 |
| Aetherbreaker (Wraethe) | 4 | **+21.2 pts** | 0.0000 | 62.4% | scion_of_nothingness ×2.0, lifebloom_ritual ×2.0, doomstalker ×1.9, ojas_genesis_druid ×1.7, century_forge ×1.7 |
| Testudo Vanguard (Homodeus) | 4 | **+21.1 pts** | 0.0000 | 96.5% | axia_duel ×1.6, zetta_encryptor ×1.5, scion_of_nothingness ×1.5, century_forge ×1.4, lifebloom_ritual ×1.4 |
| Scion of Nothingness (Wraethe) | 5 | **+20.9 pts** | 0.0000 | 20.6% | lifebloom_ritual ×4.8, doomstalker ×3.4, ojas_genesis_druid ×3.1, the_lost_duel ×2.4, root_of_the_forest_duel ×2.3 |
| Doomstalker (Wraethe) | 5 | **+20.6 pts** | 0.0000 | 31.8% | scion_of_nothingness ×3.4, lifebloom_ritual ×3.3, ojas_genesis_druid ×2.5, century_forge ×2.3, root_of_the_forest_duel ×2.1 |
| Zara Ra, Soulflayer (Wraethe) | 5 | **+20.6 pts** | 0.0000 | 95.3% | lifebloom_ritual ×1.5, breaker ×1.3, scion_of_nothingness ×1.3, doomstalker ×1.3, century_forge ×1.3 |
| Fao Cu'tul, The Formless (Wraethe) | 4 | **+20.1 pts** | 0.0000 | 93.4% | portal_monk ×1.4, lifebloom_ritual ×1.3, scion_of_nothingness ×1.3, century_forge ×1.3, general_decurion ×1.3 |
| Century Forge (Homodeus) | 5 | **+20.0 pts** | 0.0000 | 53.7% | doomstalker ×2.3, scion_of_nothingness ×2.3, lifebloom_ritual ×2.1, root_of_the_forest_duel ×2.1, breaker ×2.0 |
| Lifebloom Ritual (Undergrowth) | 6 | **+19.4 pts** | 0.0000 | 6.2% | scion_of_nothingness ×4.8, ojas_genesis_druid ×4.5, portal_monk ×4.3, doomstalker ×3.3, longshot ×3.0 |
| Optio Crusher (Homodeus) | 5 | **+18.9 pts** | 0.0000 | 91.3% | general_decurion ×1.3, scion_of_nothingness ×1.2, century_forge ×1.2, axia_duel ×1.2, aegis_archivist ×1.2 |
| Zen Chi Set, Godkiller (Wraethe) | 7 | **+18.2 pts** | 0.0000 | 88.8% | lifebloom_ritual ×1.5, portal_monk ×1.4, general_decurion ×1.4, scion_of_nothingness ×1.4, century_forge ×1.4 |
| Cache Warden (Order) | 2 | **+18.1 pts** | 0.0000 | 98.3% | portal_monk ×1.3, doomstalker ×1.3, ojas_genesis_druid ×1.3, scion_of_nothingness ×1.3, lifebloom_ritual ×1.3 |
| The Rotten (Undergrowth) | 3 | **+18.1 pts** | 0.0000 | 97.8% | scion_of_nothingness ×1.3, lifebloom_ritual ×1.3, ojas_genesis_druid ×1.2, portal_monk ×1.2, century_forge ×1.2 |
| Venator of the Wastes (Homodeus) | 4 | **+17.7 pts** | 0.0000 | 92.0% | lifebloom_ritual ×1.5, root_of_the_forest_duel ×1.4, scion_of_nothingness ×1.4, century_forge ×1.4, doomstalker ×1.4 |
| Orm Madu (Undergrowth) | 7 | **+17.3 pts** | 0.0000 | 91.8% | portal_monk ×1.5, lifebloom_ritual ×1.5, ojas_genesis_druid ×1.4, scion_of_nothingness ×1.4, general_decurion ×1.4 |
| Cryptofist Monk (Order) | 5 | **+17.2 pts** | 0.0000 | 76.1% | breaker ×1.7, root_of_the_forest_duel ×1.7, scion_of_nothingness ×1.7, doomstalker ×1.7, century_forge ×1.6 |
| Drakonarius (Homodeus) | 6 | **+17.2 pts** | 0.0000 | 79.7% | portal_monk ×1.6, general_decurion ×1.5, axia_duel ×1.4, lifebloom_ritual ×1.4, aegis_archivist ×1.4 |
| Zetta, The Encryptor (Order) | 5 | **+16.7 pts** | 0.0000 | 48.0% | century_forge ×1.9, lifebloom_ritual ×1.9, doomstalker ×1.7, scion_of_nothingness ×1.7, ojas_genesis_druid ×1.7 |
| Duplication Fabricator (Order) | 3 | **+16.7 pts** | 0.0000 | 97.7% | portal_monk ×1.4, scion_of_nothingness ×1.4, lifebloom_ritual ×1.3, century_forge ×1.3, ojas_genesis_druid ×1.3 |
| Dash (Aion) | 2 | **+16.4 pts** | 0.0000 | 98.5% | lifebloom_ritual ×1.4, portal_monk ×1.4, scion_of_nothingness ×1.4, doomstalker ×1.3, ojas_genesis_druid ×1.3 |
| Furrowing Elemental (Undergrowth) | 5 | **+16.1 pts** | 0.0000 | 90.9% | lifebloom_ritual ×1.5, breaker ×1.5, scion_of_nothingness ×1.4, portal_monk ×1.4, root_of_the_forest_duel ×1.4 |
| J-Chord (Aion) | 3 | **+15.7 pts** | 0.0000 | 98.0% | portal_monk ×2.1, scion_of_nothingness ×2.0, lifebloom_ritual ×2.0, doomstalker ×1.8, century_forge ×1.7 |
| Shard Seer (Order) | 2 | **+15.1 pts** | 0.0000 | 98.4% | portal_monk ×1.3, scion_of_nothingness ×1.3, doomstalker ×1.3, lifebloom_ritual ×1.3, ojas_genesis_druid ×1.3 |
| Shardwood Guardian (Undergrowth) | 4 | **+14.8 pts** | 0.0000 | 93.8% | doomstalker ×1.3, root_of_the_forest_duel ×1.3, century_forge ×1.3, breaker ×1.3, scion_of_nothingness ×1.3 |
| Data Heretic (Order) | 4 | **+14.8 pts** | 0.0000 | 97.0% | lifebloom_ritual ×1.5, portal_monk ×1.3, doomstalker ×1.3, scion_of_nothingness ×1.3, root_of_the_forest_duel ×1.3 |
| Ru Bo Vai, The Transcendant (Wraethe) | 5 | **+14.7 pts** | 0.0000 | 85.0% | portal_monk ×1.5, general_decurion ×1.4, century_forge ×1.4, zetta_encryptor ×1.3, lifebloom_ritual ×1.3 |
| Bleak Communion (Wraethe) | 3 | **+14.5 pts** | 0.0000 | 97.6% | lifebloom_ritual ×1.4, scion_of_nothingness ×1.3, century_forge ×1.3, doomstalker ×1.3, breaker ×1.2 |
| Korvus Legionnaire (Homodeus) | 3 | **+14.3 pts** | 0.0000 | 94.6% | lifebloom_ritual ×1.4, scion_of_nothingness ×1.3, doomstalker ×1.3, century_forge ×1.3, zetta_encryptor ×1.3 |
| Prism (Aion) | 3 | **+14.1 pts** | 0.0000 | 96.5% | doomstalker ×1.4, scion_of_nothingness ×1.4, lifebloom_ritual ×1.3, ojas_genesis_druid ×1.3, century_forge ×1.3 |
| Aegis Archivist (Order) | 4 | **+14.0 pts** | 0.0000 | 96.8% | lifebloom_ritual ×1.6, zetta_encryptor ×1.6, scion_of_nothingness ×1.6, doomstalker ×1.6, root_of_the_forest_duel ×1.5 |
| Carnivorous Vine (Undergrowth) | 4 | **+13.8 pts** | 0.0000 | 92.9% | portal_monk ×1.4, lifebloom_ritual ×1.3, breaker ×1.3, doomstalker ×1.3, century_forge ×1.3 |
| Order Initiate (Order) | 1 | **+13.7 pts** | 0.0000 | 99.0% | scion_of_nothingness ×1.4, lifebloom_ritual ×1.3, portal_monk ×1.3, doomstalker ×1.3, century_forge ×1.3 |
| Portal Monk (Order) | 3 | **+13.5 pts** | 0.0000 | 2.2% | lifebloom_ritual ×4.3, longshot ×3.0, scion_of_nothingness ×2.2, lucky ×2.1, j_chord_duel ×2.1 |
| Command Seer (Order) | 4 | **+13.5 pts** | 0.0000 | 74.5% | scion_of_nothingness ×1.7, doomstalker ×1.7, lifebloom_ritual ×1.7, century_forge ×1.7, zetta_encryptor ×1.6 |
| Taur, Arachpriest (Undergrowth) | 5 | **+13.3 pts** | 0.0000 | 69.7% | portal_monk ×1.6, zetta_encryptor ×1.5, lifebloom_ritual ×1.5, scion_of_nothingness ×1.5, general_decurion ×1.5 |
| The Lost (Wraethe) | 4 | **+12.9 pts** | 0.0000 | 42.4% | lifebloom_ritual ×2.5, scion_of_nothingness ×2.4, ojas_genesis_druid ×2.2, doomstalker ×2.0, longshot ×1.8 |
| Umbral Scourge (Wraethe) | 3 | **+12.9 pts** | 0.0000 | 97.7% | lifebloom_ritual ×1.4, ojas_genesis_druid ×1.3, portal_monk ×1.3, scion_of_nothingness ×1.2, doomstalker ×1.2 |
| Primus Pilus (Homodeus) | 2 | **+12.6 pts** | 0.0000 | 98.5% | portal_monk ×1.4, scion_of_nothingness ×1.4, lifebloom_ritual ×1.4, axia_duel ×1.4, doomstalker ×1.3 |
| Oblivion Gatekeeper (Wraethe) | 4 | **+12.6 pts** | 0.0000 | 97.2% | lifebloom_ritual ×1.5, portal_monk ×1.4, scion_of_nothingness ×1.4, doomstalker ×1.3, root_of_the_forest_duel ×1.3 |
| Lucky (Aion) | 4 | **+12.0 pts** | 0.0000 | 97.1% | portal_monk ×2.1, whisper_extractor ×1.7, leshai_knight ×1.4, scion_of_nothingness ×1.4, lifebloom_ritual ×1.4 |
| Swyft (Aion) | 5 | **+11.9 pts** | 0.0000 | 95.0% | portal_monk ×1.4, lifebloom_ritual ×1.4, scion_of_nothingness ×1.3, doomstalker ×1.3, axia_duel ×1.3 |
| Bulwark Chanter (Order) | 4 | **+11.5 pts** | 0.0000 | 96.7% | portal_monk ×1.4, scion_of_nothingness ×1.4, lifebloom_ritual ×1.3, century_forge ×1.3, doomstalker ×1.3 |
| Fungal Hermit (Undergrowth) | 3 | **+11.5 pts** | 0.0000 | 97.9% | lifebloom_ritual ×1.3, ojas_genesis_druid ×1.3, scion_of_nothingness ×1.3, doomstalker ×1.3, portal_monk ×1.3 |
| Cinder Scars (Wraethe) | 2 | **+11.5 pts** | 0.0000 | 98.3% | lifebloom_ritual ×1.3, scion_of_nothingness ×1.2, doomstalker ×1.2, root_of_the_forest_duel ×1.2, century_forge ×1.2 |
| Thorn Zealot (Undergrowth) | 3 | **+10.6 pts** | 0.0000 | 97.7% | lifebloom_ritual ×1.5, scion_of_nothingness ×1.3, doomstalker ×1.3, century_forge ×1.3, ojas_genesis_druid ×1.3 |
| Mining Drones (Homodeus) | 2 | **+10.1 pts** | 0.0000 | 98.3% | scion_of_nothingness ×1.4, lifebloom_ritual ×1.3, doomstalker ×1.3, century_forge ×1.3, portal_monk ×1.3 |
| Li Hin, The Shattered (Wraethe) | 3 | **+9.5 pts** | 0.0000 | 91.8% | lifebloom_ritual ×1.4, scion_of_nothingness ×1.3, portal_monk ×1.3, taur_arachpriest ×1.3, century_forge ×1.2 |
| Torian Commandos (Homodeus) | 3 | **+9.2 pts** | 0.0000 | 89.8% | lifebloom_ritual ×1.4, scion_of_nothingness ×1.4, doomstalker ×1.4, zetta_encryptor ×1.3, century_forge ×1.3 |
| Evokatus (Homodeus) | 4 | **+9.1 pts** | 0.0000 | 95.4% | lifebloom_ritual ×1.3, axia_duel ×1.3, scion_of_nothingness ×1.3, doomstalker ×1.2, general_decurion ×1.2 |
| Mainframe Abbot (Order) | 3 | **+9.0 pts** | 0.0000 | 97.6% | lifebloom_ritual ×1.4, scion_of_nothingness ×1.3, ojas_genesis_druid ×1.3, doomstalker ×1.3, century_forge ×1.2 |
| Arach Devotees (Undergrowth) | 2 | **+8.9 pts** | 0.0000 | 98.3% | lifebloom_ritual ×1.3, portal_monk ×1.3, scion_of_nothingness ×1.3, ojas_genesis_druid ×1.2, doomstalker ×1.2 |
| Longshot (Aion) | 4 | **+8.8 pts** | 0.0000 | 12.8% | lifebloom_ritual ×3.0, portal_monk ×3.0, ojas_genesis_druid ×2.7, scion_of_nothingness ×2.1, doomstalker ×1.9 |
| Kiln Drone (Homodeus) | 1 | **+8.7 pts** | 0.0000 | 98.4% | doomstalker ×1.4, scion_of_nothingness ×1.4, zetta_encryptor ×1.4, root_of_the_forest_duel ×1.3, century_forge ×1.3 |
| Numeri Drones (Homodeus) | 3 | **+8.5 pts** | 0.0000 | 97.2% | lifebloom_ritual ×1.4, axia_duel ×1.3, doomstalker ×1.3, scion_of_nothingness ×1.3, ojas_genesis_druid ×1.3 |
| Limiter Drones (Homodeus) | 2 | **+8.0 pts** | 0.0000 | 98.2% | lifebloom_ritual ×1.4, scion_of_nothingness ×1.3, portal_monk ×1.3, doomstalker ×1.2, ojas_genesis_druid ×1.2 |
| Pall Shades (Wraethe) | 2 | **+8.0 pts** | 0.0000 | 98.3% | lifebloom_ritual ×1.3, scion_of_nothingness ×1.3, doomstalker ×1.2, ojas_genesis_druid ×1.2, century_forge ×1.2 |
| Raidian, Cloud Master (Order) | 5 | **+7.7 pts** | 0.0000 | 96.2% | lifebloom_ritual ×1.7, portal_monk ×1.5, doomstalker ×1.4, root_of_the_forest_duel ×1.3, century_forge ×1.3 |
| Shadebound Sentry (Wraethe) | 3 | **+7.3 pts** | 0.0000 | 95.2% | lifebloom_ritual ×1.3, scion_of_nothingness ×1.3, ojas_genesis_druid ×1.3, century_forge ×1.2, doomstalker ×1.2 |
| Cloud Oracles (Order) | 2 | **+7.3 pts** | 0.0000 | 98.5% | lifebloom_ritual ×1.3, portal_monk ×1.3, scion_of_nothingness ×1.3, doomstalker ×1.3, century_forge ×1.3 |
| Nectar Alchemist (Undergrowth) | 3 | **+7.2 pts** | 0.0000 | 92.2% | lifebloom_ritual ×1.4, scion_of_nothingness ×1.4, portal_monk ×1.3, doomstalker ×1.3, century_forge ×1.3 |
| Riposte Doctrine (Homodeus) | 2 | **+7.2 pts** | 0.0000 | 93.5% | lifebloom_ritual ×1.4, scion_of_nothingness ×1.3, ojas_genesis_druid ×1.3, doomstalker ×1.2, longshot ×1.2 |
| Thornshell Warden (Undergrowth) | 2 | **+7.1 pts** | 0.0000 | 98.4% | lifebloom_ritual ×1.5, portal_monk ×1.4, scion_of_nothingness ×1.3, doomstalker ×1.3, ojas_genesis_druid ×1.3 |
| Hounds of Volos (Undergrowth) | 3 | **+6.9 pts** | 0.0000 | 95.3% | scion_of_nothingness ×1.4, portal_monk ×1.3, doomstalker ×1.3, lifebloom_ritual ×1.3, zetta_encryptor ×1.3 |
| Additri, Gaiamancer (Undergrowth) | 5 | **+6.5 pts** | 0.0000 | 90.6% | lifebloom_ritual ×1.4, portal_monk ×1.3, aegis_archivist ×1.3, scion_of_nothingness ×1.2, century_forge ×1.2 |
| Nil Assassin (Wraethe) | 2 | **+6.4 pts** | 0.0000 | 92.2% | scion_of_nothingness ×1.3, portal_monk ×1.2, lifebloom_ritual ×1.2, ojas_genesis_druid ×1.2, doomstalker ×1.2 |
| Legion Carrier (Homodeus) | 2 | **+6.2 pts** | 0.0000 | 97.6% | scion_of_nothingness ×1.3, doomstalker ×1.3, root_of_the_forest_duel ×1.3, century_forge ×1.3, ojas_genesis_druid ×1.3 |
| Grim Tutor (Wraethe) | 3 | **+6.0 pts** | 0.0000 | 96.5% | lifebloom_ritual ×1.3, scion_of_nothingness ×1.3, ojas_genesis_druid ×1.3, doomstalker ×1.3, century_forge ×1.2 |
| Le'shai Knight (Undergrowth) | 3 | **+6.0 pts** | 0.0000 | 65.4% | lifebloom_ritual ×1.6, lucky ×1.4, portal_monk ×1.4, ojas_genesis_druid ×1.4, longshot ×1.4 |
| Brute (Aion) | 3 | **+5.8 pts** | 0.0000 | 97.5% | portal_monk ×1.4, lifebloom_ritual ×1.3, scion_of_nothingness ×1.3, longshot ×1.2, doomstalker ×1.2 |
| Ferrata Guard (Homodeus) | 4 | **+5.6 pts** | 0.0000 | 94.5% | axia_duel ×1.4, scion_of_nothingness ×1.4, lifebloom_ritual ×1.4, zetta_encryptor ×1.3, century_forge ×1.3 |
| Index of Futures (Order) | 3 | **+5.6 pts** | 0.0000 | 96.7% | lifebloom_ritual ×1.5, scion_of_nothingness ×1.4, doomstalker ×1.4, ojas_genesis_druid ×1.3, century_forge ×1.3 |
| Whisper Extractor (Wraethe) | 3 | **+5.3 pts** | 0.0000 | 39.2% | lucky ×1.7, longshot ×1.5, portal_monk ×1.5, j_chord_duel ×1.4, scion_of_nothingness ×1.2 |

Positive Δ + healthy buy rate ⇒ nerf candidate; negative Δ or rock-bottom buy rate ⇒ buff candidate. Cross-check the co-acquisition column before blaming a single card.

### Buy-rate outliers by cost band (full table in sim-summary.csv)

- Cost 1–3: least bought — Portal Monk 2.2%, Whisper Extractor 39.2%, Le'shai Knight 65.4% · most bought — Order Initiate 99.0%, Dash 98.5%, Primus Pilus 98.5%
- Cost 4–6: least bought — Lifebloom Ritual 6.2%, Longshot 12.8%, Scion of Nothingness 20.6% · most bought — Oblivion Gatekeeper 97.2%, Lucky 97.1%, Data Heretic 97.0%
- Cost 7+: least bought — Comet 2.2%, Root of the Forest 58.9%, Axia 75.6% · most bought — The Grand Architect 93.4%, Orm Madu 91.8%, Zen Chi Set, Godkiller 88.8%

## 7. Relics, destinies, monsters

| Relic | Recruits | WR when recruited |
|---|---|---|
| The Heart of Nothing | 1062 | 63.8% [60.9%–66.7%] |
| Multitask Brain | 1456 | 77.9% [75.7%–79.9%] |
| Panconscious Crown | 1069 | 66.6% [63.7%–69.4%] |
| Praetorian-02 | 2421 | 72.1% [70.3%–73.9%] |
| Praetorian-03 | 17542 | 58.7% [58.0%–59.4%] |
| Slipstream Shard | 1565 | 87.0% [85.2%–88.5%] |
| Star Seeker | 9730 | 54.0% [53.0%–55.0%] |
| Terminal Crescents | 11676 | 49.9% [49.0%–50.8%] |
| Unknown God | 9735 | 44.9% [43.9%–45.9%] |
| The World Piercer | 9688 | 40.7% [39.7%–41.6%] |

| Destiny | In initial row | Taken | Avg round | WR taken | WR not taken |
|---|---|---|---|---|---|
| Deadly Recruits | 11864 | 5932 (50.0%) | 4.5 | 64.5% [63.3%–65.7%] | 35.5% [34.3%–36.7%] |
| True Leader | 12076 | 6038 (50.0%) | 4.8 | 51.5% [50.3%–52.8%] | 48.5% [47.2%–49.7%] |
| Datic Secrets | 12034 | 5870 (48.8%) | 5.2 | 46.7% [45.5%–48.0%] | 53.1% [51.9%–54.3%] |
| Paradigm Shift | 12018 | 5837 (48.6%) | 5.1 | 53.8% [52.6%–55.1%] | 46.4% [45.1%–47.6%] |
| Whatever it Takes | 12124 | 5601 (46.2%) | 5.5 | 42.8% [41.5%–44.1%] | 56.2% [55.0%–57.4%] |
| Biotech Enhancements | 11942 | 5102 (42.7%) | 5.8 | 50.9% [49.6%–52.3%] | 49.3% [48.1%–50.5%] |
| Strategic Mastermind | 12140 | 5111 (42.1%) | 5.9 | 57.9% [56.5%–59.2%] | 44.3% [43.1%–45.4%] |
| Soul Syphon | 12060 | 4602 (38.2%) | 6.2 | 50.3% [48.8%–51.7%] | 49.8% [48.7%–51.0%] |
| The Shard Defiant | 11984 | 4234 (35.3%) | 6.4 | 54.1% [52.6%–55.6%] | 47.7% [46.6%–48.9%] |
| Nature Dominance | 11980 | 3852 (32.2%) | 6.8 | 51.7% [50.2%–53.3%] | 49.2% [48.1%–50.3%] |
| Healing Hands | 11892 | 3416 (28.7%) | 7.1 | 51.9% [50.2%–53.5%] | 49.2% [48.2%–50.3%] |
| Advanced Medicine | 12072 | 3161 (26.2%) | 7.3 | 49.9% [48.1%–51.6%] | 50.0% [49.0%–51.1%] |
| The Last City | 11832 | 2728 (23.1%) | 7.5 | 55.5% [53.6%–57.3%] | 48.4% [47.3%–49.4%] |
| Absorption Grid | 11934 | 2424 (20.3%) | 7.8 | 55.3% [53.3%–57.2%] | 48.7% [47.7%–49.7%] |
| Stolen Futures | 12130 | 2236 (18.4%) | 11.3 | 75.6% [73.8%–77.3%] | 44.2% [43.2%–45.2%] |
| Power Struggle | 11972 | 2051 (17.1%) | 8.2 | 49.6% [47.5%–51.8%] | 50.1% [49.1%–51.1%] |
| The Agony of Choice | 11826 | 1704 (14.4%) | 8.3 | 63.4% [61.1%–65.6%] | 47.7% [46.8%–48.7%] |
| Synthesis | 12010 | 1706 (14.2%) | 12.0 | 83.4% [81.6%–85.1%] | 44.5% [43.5%–45.4%] |
| The Price of Power | 12258 | 1437 (11.7%) | 8.7 | 53.7% [51.1%–56.3%] | 49.5% [48.6%–50.4%] |
| Unconditional Conscription | 12152 | 1042 (8.6%) | 8.9 | 61.1% [58.1%–64.0%] | 49.0% [48.0%–49.9%] |
| War Bound | 12274 | 1031 (8.4%) | 8.8 | 58.8% [55.7%–61.7%] | 49.2% [48.3%–50.1%] |
| Advanced Weapons | 11594 | 681 (5.9%) | 9.0 | 64.0% [60.3%–67.5%] | 49.1% [48.2%–50.1%] |
| Project Yggdrasil | 12030 | 285 (2.4%) | 10.2 | 63.2% [57.4%–68.5%] | 49.7% [48.8%–50.6%] |
| Phasic Technology | 11980 | 254 (2.1%) | 10.1 | 66.9% [60.9%–72.4%] | 49.6% [48.7%–50.5%] |
| Blood for Blood | 11820 | 245 (2.1%) | 10.5 | 62.9% [56.7%–68.7%] | 49.7% [48.8%–50.6%] |
| Maglev Tunnels | 11976 | 232 (1.9%) | 10.2 | 62.5% [56.1%–68.5%] | 49.8% [48.8%–50.7%] |
| One Mind One Army | 11992 | 221 (1.8%) | 10.6 | 62.0% [55.4%–68.1%] | 49.8% [48.9%–50.7%] |
| Forged in Flame | 11882 | 25 (0.2%) | 12.2 | 80.0% [60.9%–91.1%] | 49.9% [49.0%–50.8%] |
| The Crystal Gate | 12170 | 0 (0.0%) | 0.0 | – | 50.0% [49.1%–50.9%] |
| Bound for Life | 11982 | 0 (0.0%) | 0.0 | – | 50.0% [49.1%–50.9%] |

| Monster | Revealed | Defeated | Avg defeat round | Defeater WR |
|---|---|---|---|---|
| Ingeminex: Agony | 7658 | 7606 (99.3%) | 10.9 | 70.8% [69.7%–71.8%] |
| Ingeminex: Brutality | 7687 | 7581 (98.6%) | 10.7 | 64.3% [63.2%–65.4%] |
| Ingeminex: Corruption | 7715 | 7624 (98.8%) | 11.0 | 74.1% [73.1%–75.1%] |
| Ingeminex: Malice | 7647 | 7598 (99.4%) | 11.0 | 68.8% [67.7%–69.8%] |
| Ingeminex: Torment | 7770 | 7714 (99.3%) | 10.7 | 70.1% [69.0%–71.1%] |

Monster attacks landed: 27661

## 8. Methodology & caveats

- Every proportion carries a Wilson 95% interval; per-card deltas are stratified by matchup×seat, inverse-variance pooled, and Benjamini-Hochberg corrected (FDR 10%) with an effect floor.
- **These are correlations between THESE bots' policies, not causal card effects.** A card bought when already ahead will look like a winner. Treat findings as directional input; re-test surprising ones with a targeted A/B (forced-strategy bot variant) before patching.
- Seat counts are balanced exactly 50/50 per matchup by construction; seeds are sequential and reproducible (`soisim run --seed-base 1`).
