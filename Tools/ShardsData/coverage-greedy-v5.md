# SoI Action-Space Coverage

- Bots: **bench:greedy-v5** · games 4000 (4000 finished) · DLC mask 15 · 1.6s
- A **zero** below is a blind spot: a bug, a scoring hole, or a strategic
  choice that should be stated rather than assumed. Win rate cannot see any of them.

## 1. Priority actions

| Action | Times chosen | Per game |
|---|---:|---:|
| ShardsPlayCardAction | 713,344 | 178.34 |
| ShardsBuyCardAction | 145,294 | 36.32 |
| ShardsRerollRowAction | 1,956 | 0.49 |
| ShardsFocusAction | 86,862 | 21.72 |
| ShardsHeroAbilityAction | 7,320 | 1.83 |
| ShardsExhaustAction | 178,948 | 44.74 |
| ShardsAttackMonsterAction | 5,074 | 1.27 |
| ShardsTakeDestinyAction | 7,999 | 2.00 |
| ShardsRecruitRelicAction | 7,777 | 1.94 |
| ShardsEndTurnAction | 107,179 | 26.79 |
| SubmitDecisionAction | 203,118 | 50.78 |
| ↳ of which mercenary fast-play | 29,498 | 7.37 |

## 2. Decision contexts

| Context | Times reached | Per game |
|---|---:|---:|
| soi.banish | 25,444 | 6.361 |
| soi.confirm | 1,439 | 0.360 |
| soi.copy | 5,443 | 1.361 |
| soi.defiant | 5,847 | 1.462 |
| soi.destiny | 2,648 | 0.662 |
| soi.destroy | 1,980 | 0.495 |
| soi.discard | 1,442 | 0.360 |
| soi.handpick | 1,481 | 0.370 |
| soi.herodraft | 8,000 | 2.000 |
| soi.keepfast | 1,629 | 0.407 |
| soi.maglev | 27 | 0.007 |
| soi.mode | 5,664 | 1.416 |
| soi.recruit | 118 | 0.029 |
| soi.relic | 1,004 | 0.251 |
| soi.removeshop | 7,432 | 1.858 |
| soi.reorder | 4,563 | 1.141 |
| soi.reset | 1,716 | 0.429 |
| soi.return | 8,612 | 2.153 |
| soi.reveal | 20,093 | 5.023 |
| 🚨 soi.scry | 0 | 0.000 |
| soi.shields | 28,984 | 7.246 |
| soi.split | 41,614 | 10.403 |
| soi.tutor | 3,851 | 0.963 |
| soi.warp | 24,087 | 6.022 |

`soi.target` is 0 as expected — auto-resolves with one living opponent (3 sites, all guarded on Count > 1).

## 3. Cards never acquired

**0 cards were OFFERED but never once ended up owned.** These are
the policy's rejected cards — the highest-signal list in this report, because the
balance report's ≥100-acquisition floor hides them completely.

| Card | Cost | Type | Games offered in |
|---|---:|---|---:|

**52 never appeared in the row at all** (relics/destinies reach play by other routes, so some of these are expected):

`agony_of_choice`, `axia`, `bound_for_life`, `cinder_scars`, `cloud_oracles`, `command_seer`, `crystal_gate`, `dash`, `data_heretic`, `datic_robes`, `datic_robes_duel`, `datic_secrets`, `deadly_recruits`, `doom_gate`, `duplication_fabricator`, `entropic_talons`, `evokatus`, `ferrata_guard`, `furrowing_elemental`, `healing_hands`, `heart_of_nothing`, `hounds_of_volos`, `j_chord`, `korvus_legionnaire`, `legion_carrier`, `li_hin`, `mainframe_abbot`, `nil_assassin`, `order_initiate`, `orm_madu`, `paradigm_shift`, `praetorian_01`, `praetorian_02`, `primus_pilus`, `reactor_drone`, `root_of_the_forest`, `ru_bo_vai`, `shardwood_guardian`, `slipstream_shard`, `soul_syphon`

## 4. Cards never PLAYED though owned

Every owned non-destiny card was played at least once. ✅ (Destinies are exhausted from their own zone, never played, so they are excluded.)

## 5. Winner's final mastery

⚠ This is *how the winner finished*, not *how they won* — a player can reach
M30 and still win by damage. For the actual win-type split use the balance
report's `Win type` line, which reads the terminating event.

- winner reached M30: **62.7%** (2,508)
- winner below M30: **37.3%** (1,492)

## 6. Verdict

- ⚠ 1 decision context(s) never reached: soi.scry — either unreachable content, or a card that is never bought.
