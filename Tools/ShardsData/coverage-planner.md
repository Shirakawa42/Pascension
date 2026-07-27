# SoI Action-Space Coverage

- Bots: **planner** · games 200 (200 finished) · DLC mask 15 · 7.1s
- A **zero** below is a blind spot: a bug, a scoring hole, or a strategic
  choice that should be stated rather than assumed. Win rate cannot see any of them.

## 1. Priority actions

| Action | Times chosen | Per game |
|---|---:|---:|
| ShardsPlayCardAction | 30,142 | 150.71 |
| ShardsBuyCardAction | 4,751 | 23.75 |
| ShardsRerollRowAction | 4,835 | 24.18 |
| ShardsFocusAction | 1,984 | 9.92 |
| ShardsHeroAbilityAction | 659 | 3.29 |
| ShardsExhaustAction | 2,895 | 14.47 |
| ShardsAttackMonsterAction | 185 | 0.93 |
| ShardsTakeDestinyAction | 315 | 1.57 |
| ShardsRecruitRelicAction | 172 | 0.86 |
| ShardsEndTurnAction | 6,557 | 32.78 |
| SubmitDecisionAction | 6,907 | 34.53 |
| ↳ of which mercenary fast-play | 728 | 3.64 |

## 2. Decision contexts

| Context | Times reached | Per game |
|---|---:|---:|
| soi.banish | 880 | 4.400 |
| soi.confirm | 1 | 0.005 |
| soi.copy | 111 | 0.555 |
| soi.defiant | 17 | 0.085 |
| soi.destiny | 83 | 0.415 |
| soi.destroy | 34 | 0.170 |
| soi.discard | 100 | 0.500 |
| soi.handpick | 173 | 0.865 |
| soi.herodraft | 400 | 2.000 |
| 🚨 soi.keepfast | 0 | 0.000 |
| soi.maglev | 4 | 0.020 |
| soi.mode | 97 | 0.485 |
| soi.recruit | 108 | 0.540 |
| soi.relic | 47 | 0.235 |
| soi.removeshop | 292 | 1.460 |
| soi.reorder | 133 | 0.665 |
| soi.reset | 7 | 0.035 |
| soi.return | 356 | 1.780 |
| soi.reveal | 700 | 3.500 |
| soi.scry | 317 | 1.585 |
| soi.shields | 1,475 | 7.375 |
| soi.split | 949 | 4.745 |
| soi.tutor | 120 | 0.600 |
| soi.warp | 503 | 2.515 |

`soi.target` is 0 as expected — auto-resolves with one living opponent (3 sites, all guarded on Count > 1).

## 2b. Hero abilities, per character

A total activation count hides a single hero's ability being dead. Decima's
"Recruiting" is PASSIVE (a first-buy discount inside EffectiveCost), so it is
correctly never an action.

| Character | Games drafted | Ability used | Per drafted game |
|---|---:|---:|---:|
| (passive) decima | 85 | 0 | 0.00 |
| kosynwu | 73 | 33 | 0.45 |
| rez | 72 | 317 | 4.40 |
| tetra | 95 | 84 | 0.88 |
| volos | 75 | 225 | 3.00 |

## 2c. Optional decisions — ever taken, ever declined?

A `Min=0` decision the policy ALWAYS declines is an action it can never take —
the reroll bug's shape one level down, invisible to an action-type histogram.
Always-takes is equally suspicious: the choice is not being made.

| Decision | Took | Declined | Verdict |
|---|---:|---:|---|
| soi.banish | 549 | 319 | both |
| soi.confirm | 1 | 0 | ⚠ never declined |
| soi.copy | 11 | 0 | ⚠ never declined |
| soi.destroy | 28 | 0 | ⚠ never declined |
| soi.maglev | 4 | 0 | ⚠ never declined |
| soi.removeshop | 27 | 265 | both |
| soi.reset | 7 | 0 | ⚠ never declined |
| soi.return | 304 | 0 | ⚠ never declined |
| soi.reveal | 700 | 0 | ⚠ never declined |
| soi.scry | 0 | 317 | 🚨 NEVER taken |
| soi.shields | 1,475 | 0 | ⚠ never declined |
| soi.split | 13 | 0 | ⚠ never declined |
| soi.warp | 503 | 0 | ⚠ never declined |

Multi-option decisions — is the choice actually being made, or is it always
the first option (the `ChooseAnswer` default's signature)?

| Decision | Picked option 0 | Picked another | Verdict |
|---|---:|---:|---|
| soi.banish | 44 | 509 | chooses |
| soi.copy | 42 | 63 | chooses |
| soi.defiant | 16 | 1 | chooses |
| soi.destiny | 15 | 67 | chooses |
| soi.destroy | 3 | 5 | chooses |
| soi.handpick | 28 | 145 | chooses |
| soi.herodraft | 95 | 305 | chooses |
| soi.mode | 44 | 53 | chooses |
| soi.recruit | 32 | 76 | chooses |
| soi.relic | 8 | 39 | chooses |
| soi.removeshop | 7 | 20 | chooses |
| soi.reset | 0 | 1 | chooses |
| soi.return | 35 | 75 | chooses |
| soi.reveal | 146 | 0 | 🚨 ALWAYS the first option |
| soi.split | 49 | 1 | chooses |
| soi.tutor | 28 | 89 | chooses |
| soi.warp | 132 | 188 | chooses |

Most-banished cards — thinning should prefer whatever sits furthest
below the deck's own average, so starters should dominate this list.

- crystal: 435
- shard_reactor: 70
- blaster: 57
- wraethe_skirmisher_duel: 2
- spore_cleric_duel: 1
- taur_arachpriest: 1
- shadebound_sentry: 1
- optio_crusher: 1

| soi.split targeting | 661 hit a champion | 288 face only | both

## 3. Cards never acquired

**5 cards were OFFERED but never once ended up owned.** These are
the policy's rejected cards — the highest-signal list in this report, because the
balance report's ≥100-acquisition floor hides them completely.

| Card | Cost | Type | Games offered in |
|---|---:|---|---:|
| root_of_the_forest_duel | 7 | Mercenary | 66 |
| lifebloom_ritual | 6 | Mercenary | 65 |
| general_decurion | 7 | Champion | 61 |
| zen_chi_set | 7 | Champion | 58 |
| axia_duel | 8 | Champion | 55 |

**44 never appeared in the row at all** (relics/destinies reach play by other routes, so some of these are expected):

`agony_of_choice`, `axia`, `cinder_scars`, `cloud_oracles`, `command_seer`, `dash`, `data_heretic`, `datic_robes`, `datic_secrets`, `deadly_recruits`, `duplication_fabricator`, `evokatus`, `ferrata_guard`, `furrowing_elemental`, `healing_hands`, `heart_of_nothing`, `hounds_of_volos`, `j_chord`, `korvus_legionnaire`, `legion_carrier`, `li_hin`, `mainframe_abbot`, `nil_assassin`, `order_initiate`, `orm_madu`, `paradigm_shift`, `praetorian_02`, `primus_pilus`, `reactor_drone`, `root_of_the_forest`, `ru_bo_vai`, `shardwood_guardian`, `slipstream_shard`, `soul_syphon`, `spore_cleric`, `swyft`, `terminal_crescents`, `the_last_city`, `the_lost`, `the_rotten`

## 4. Cards never PLAYED though owned

Every owned non-destiny card was played at least once. ✅ (Destinies are exhausted from their own zone, never played, so they are excluded.)

## 5. Winner's final mastery

⚠ This is *how the winner finished*, not *how they won* — a player can reach
M30 and still win by damage. For the actual win-type split use the balance
report's `Win type` line, which reads the terminating event.

- winner below M30: **81.5%** (163)
- winner reached M30: **18.5%** (37)

## 6. Verdict

- ⚠ 1 decision context(s) never reached: soi.keepfast — either unreachable content, or a card that is never bought.
- ⚠ 5 card(s) offered but never acquired.
- 🚨 1 optional decision(s) never taken: soi.scry
- 🚨 1 decision(s) ALWAYS pick the first option (unhandled by ChooseAnswer): soi.reveal
