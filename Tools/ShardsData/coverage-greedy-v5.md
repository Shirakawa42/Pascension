# SoI Action-Space Coverage

- Bots: **bench:greedy-v5** · games 4000 (4000 finished) · DLC mask 15 · 1.7s
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

## 2b. Hero abilities, per character

A total activation count hides a single hero's ability being dead. Decima's
"Recruiting" is PASSIVE (a first-buy discount inside EffectiveCost), so it is
correctly never an action.

| Character | Games drafted | Ability used | Per drafted game |
|---|---:|---:|---:|
| (passive) decima | 1,574 | 0 | 0.00 |
| 🚨 kosynwu | 1,622 | 0 | 0.00 |
| 🚨 rez | 1,583 | 0 | 0.00 |
| tetra | 1,654 | 546 | 0.33 |
| volos | 1,567 | 6,774 | 4.32 |

## 2c. Optional decisions — ever taken, ever declined?

A `Min=0` decision the policy ALWAYS declines is an action it can never take —
the reroll bug's shape one level down, invisible to an action-type histogram.
Always-takes is equally suspicious: the choice is not being made.

| Decision | Took | Declined | Verdict |
|---|---:|---:|---|
| soi.banish | 13,488 | 11,945 | both |
| soi.confirm | 1,439 | 0 | ⚠ never declined |
| soi.copy | 1,588 | 0 | ⚠ never declined |
| soi.destroy | 1,127 | 0 | ⚠ never declined |
| soi.keepfast | 1,629 | 0 | ⚠ never declined |
| soi.maglev | 27 | 0 | ⚠ never declined |
| soi.removeshop | 0 | 7,432 | 🚨 NEVER taken |
| soi.reset | 0 | 1,716 | 🚨 NEVER taken |
| soi.return | 5,970 | 0 | ⚠ never declined |
| soi.reveal | 20,093 | 0 | ⚠ never declined |
| soi.shields | 28,984 | 0 | ⚠ never declined |
| soi.split | 368 | 0 | ⚠ never declined |
| soi.warp | 24,087 | 0 | ⚠ never declined |

Multi-option decisions — is the choice actually being made, or is it always
the first option (the `ChooseAnswer` default's signature)?

| Decision | Picked option 0 | Picked another | Verdict |
|---|---:|---:|---|
| soi.banish | 196 | 12,639 | chooses |
| soi.copy | 2,649 | 2,067 | chooses |
| soi.defiant | 5,847 | 0 | 🚨 ALWAYS the first option |
| soi.destiny | 691 | 1,957 | chooses |
| soi.destroy | 558 | 815 | chooses |
| soi.handpick | 178 | 1,303 | chooses |
| soi.herodraft | 1,771 | 6,229 | chooses |
| soi.mode | 5,664 | 0 | 🚨 ALWAYS the first option |
| soi.recruit | 21 | 97 | chooses |
| soi.relic | 221 | 783 | chooses |
| soi.return | 2,018 | 2,857 | chooses |
| soi.reveal | 5,976 | 0 | 🚨 ALWAYS the first option |
| soi.split | 3,151 | 22 | chooses |
| soi.tutor | 475 | 3,119 | chooses |
| soi.warp | 5,264 | 12,979 | chooses |

| soi.split targeting | 26,491 hit a champion | 15,123 face only | both

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
- 🚨 **2 hero ability NEVER activated**: kosynwu, rez
- 🚨 2 optional decision(s) never taken: soi.removeshop, soi.reset
- 🚨 3 decision(s) ALWAYS pick the first option (unhandled by ChooseAnswer): soi.defiant, soi.mode, soi.reveal
