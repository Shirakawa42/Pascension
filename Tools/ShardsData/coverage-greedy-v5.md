# SoI Action-Space Coverage

- Bots: **bench:greedy-v5** · games 4000 (4000 finished) · DLC mask 15 · 1.8s
- A **zero** below is a blind spot: a bug, a scoring hole, or a strategic
  choice that should be stated rather than assumed. Win rate cannot see any of them.

## 1. Priority actions

| Action | Times chosen | Per game |
|---|---:|---:|
| ShardsPlayCardAction | 709,188 | 177.30 |
| ShardsBuyCardAction | 144,558 | 36.14 |
| ShardsRerollRowAction | 1,766 | 0.44 |
| ShardsFocusAction | 86,224 | 21.56 |
| ShardsHeroAbilityAction | 7,289 | 1.82 |
| ShardsExhaustAction | 178,484 | 44.62 |
| ShardsAttackMonsterAction | 5,058 | 1.26 |
| ShardsTakeDestinyAction | 7,998 | 2.00 |
| ShardsRecruitRelicAction | 7,771 | 1.94 |
| ShardsEndTurnAction | 106,476 | 26.62 |
| SubmitDecisionAction | 198,739 | 49.68 |
| ↳ of which mercenary fast-play | 29,139 | 7.28 |

## 2. Decision contexts

| Context | Times reached | Per game |
|---|---:|---:|
| soi.banish | 25,413 | 6.353 |
| soi.confirm | 1,417 | 0.354 |
| soi.copy | 5,467 | 1.367 |
| soi.defiant | 5,807 | 1.452 |
| soi.destiny | 2,647 | 0.662 |
| soi.destroy | 1,934 | 0.483 |
| soi.discard | 1,424 | 0.356 |
| soi.handpick | 1,439 | 0.360 |
| soi.herodraft | 8,000 | 2.000 |
| soi.keepfast | 1,601 | 0.400 |
| soi.maglev | 24 | 0.006 |
| soi.mode | 3,276 | 0.819 |
| soi.recruit | 46 | 0.011 |
| soi.relic | 990 | 0.247 |
| soi.removeshop | 7,401 | 1.850 |
| soi.reorder | 4,551 | 1.138 |
| soi.reset | 1,569 | 0.392 |
| soi.return | 8,478 | 2.119 |
| soi.reveal | 19,788 | 4.947 |
| 🚨 soi.scry | 0 | 0.000 |
| soi.shields | 28,124 | 7.031 |
| soi.split | 41,233 | 10.308 |
| soi.tutor | 3,843 | 0.961 |
| soi.warp | 24,267 | 6.067 |

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
| tetra | 1,654 | 533 | 0.32 |
| volos | 1,567 | 6,756 | 4.31 |

## 2c. Optional decisions — ever taken, ever declined?

A `Min=0` decision the policy ALWAYS declines is an action it can never take —
the reroll bug's shape one level down, invisible to an action-type histogram.
Always-takes is equally suspicious: the choice is not being made.

| Decision | Took | Declined | Verdict |
|---|---:|---:|---|
| soi.banish | 13,284 | 12,118 | both |
| soi.confirm | 1,417 | 0 | ⚠ never declined |
| soi.copy | 1,574 | 0 | ⚠ never declined |
| soi.destroy | 1,084 | 0 | ⚠ never declined |
| soi.keepfast | 1,601 | 0 | ⚠ never declined |
| soi.maglev | 24 | 0 | ⚠ never declined |
| soi.removeshop | 234 | 7,167 | both |
| soi.reset | 1,569 | 0 | ⚠ never declined |
| soi.return | 5,867 | 0 | ⚠ never declined |
| soi.reveal | 19,788 | 0 | ⚠ never declined |
| soi.shields | 28,124 | 0 | ⚠ never declined |
| soi.split | 358 | 0 | ⚠ never declined |
| soi.warp | 24,267 | 0 | ⚠ never declined |

Multi-option decisions — is the choice actually being made, or is it always
the first option (the `ChooseAnswer` default's signature)?

| Decision | Picked option 0 | Picked another | Verdict |
|---|---:|---:|---|
| soi.banish | 194 | 12,442 | chooses |
| soi.copy | 2,680 | 2,056 | chooses |
| soi.defiant | 5,648 | 159 | chooses |
| soi.destiny | 679 | 1,968 | chooses |
| soi.destroy | 553 | 792 | chooses |
| soi.handpick | 164 | 1,275 | chooses |
| soi.herodraft | 1,771 | 6,229 | chooses |
| soi.mode | 710 | 2,566 | chooses |
| soi.recruit | 7 | 39 | chooses |
| soi.relic | 198 | 792 | chooses |
| soi.removeshop | 39 | 195 | chooses |
| soi.reset | 410 | 548 | chooses |
| soi.return | 2,001 | 2,792 | chooses |
| soi.reveal | 5,907 | 0 | 🚨 ALWAYS the first option |
| soi.split | 3,141 | 21 | chooses |
| soi.tutor | 490 | 3,089 | chooses |
| soi.warp | 5,407 | 13,019 | chooses |

| soi.split targeting | 26,260 hit a champion | 14,973 face only | both

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

- winner reached M30: **62.0%** (2,478)
- winner below M30: **38.0%** (1,522)

## 6. Verdict

- ⚠ 1 decision context(s) never reached: soi.scry — either unreachable content, or a card that is never bought.
- 🚨 **2 hero ability NEVER activated**: kosynwu, rez
- 🚨 1 decision(s) ALWAYS pick the first option (unhandled by ChooseAnswer): soi.reveal
