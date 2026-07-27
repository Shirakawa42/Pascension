# SoI Action-Space Coverage

- Bots: **greedy** · games 3000 (3000 finished) · DLC mask 15 · 1.3s
- A **zero** below is a blind spot: a bug, a scoring hole, or a strategic
  choice that should be stated rather than assumed. Win rate cannot see any of them.

## 1. Priority actions

| Action | Times chosen | Per game |
|---|---:|---:|
| ShardsPlayCardAction | 531,468 | 177.16 |
| ShardsBuyCardAction | 108,064 | 36.02 |
| ShardsRerollRowAction | 1,303 | 0.43 |
| ShardsFocusAction | 64,521 | 21.51 |
| ShardsHeroAbilityAction | 17,123 | 5.71 |
| ShardsExhaustAction | 133,574 | 44.52 |
| ShardsAttackMonsterAction | 3,735 | 1.25 |
| ShardsTakeDestinyAction | 5,999 | 2.00 |
| ShardsRecruitRelicAction | 5,833 | 1.94 |
| ShardsEndTurnAction | 79,735 | 26.58 |
| SubmitDecisionAction | 160,555 | 53.52 |
| ↳ of which mercenary fast-play | 21,869 | 7.29 |

## 2. Decision contexts

| Context | Times reached | Per game |
|---|---:|---:|
| soi.banish | 20,236 | 6.745 |
| soi.confirm | 1,051 | 0.350 |
| soi.copy | 4,155 | 1.385 |
| soi.defiant | 4,282 | 1.427 |
| soi.destiny | 1,923 | 0.641 |
| soi.destroy | 1,395 | 0.465 |
| soi.discard | 1,042 | 0.347 |
| soi.handpick | 1,085 | 0.362 |
| soi.herodraft | 6,000 | 2.000 |
| soi.keepfast | 1,181 | 0.394 |
| soi.maglev | 12 | 0.004 |
| soi.mode | 2,451 | 0.817 |
| soi.recruit | 39 | 0.013 |
| soi.relic | 728 | 0.243 |
| soi.removeshop | 5,517 | 1.839 |
| soi.reorder | 3,402 | 1.134 |
| soi.reset | 1,194 | 0.398 |
| soi.return | 6,345 | 2.115 |
| soi.reveal | 14,839 | 4.946 |
| soi.scry | 10,672 | 3.557 |
| soi.shields | 21,202 | 7.067 |
| soi.split | 30,936 | 10.312 |
| soi.tutor | 2,863 | 0.954 |
| soi.warp | 18,005 | 6.002 |

`soi.target` is 0 as expected — auto-resolves with one living opponent (3 sites, all guarded on Count > 1).

## 2b. Hero abilities, per character

A total activation count hides a single hero's ability being dead. Decima's
"Recruiting" is PASSIVE (a first-buy discount inside EffectiveCost), so it is
correctly never an action.

| Character | Games drafted | Ability used | Per drafted game |
|---|---:|---:|---:|
| (passive) decima | 1,183 | 0 | 0.00 |
| kosynwu | 1,232 | 1,139 | 0.92 |
| rez | 1,181 | 10,672 | 9.04 |
| tetra | 1,257 | 414 | 0.33 |
| volos | 1,147 | 4,898 | 4.27 |

## 2c. Optional decisions — ever taken, ever declined?

A `Min=0` decision the policy ALWAYS declines is an action it can never take —
the reroll bug's shape one level down, invisible to an action-type histogram.
Always-takes is equally suspicious: the choice is not being made.

| Decision | Took | Declined | Verdict |
|---|---:|---:|---|
| soi.banish | 10,609 | 9,616 | both |
| soi.confirm | 1,051 | 0 | ⚠ never declined |
| soi.copy | 1,189 | 0 | ⚠ never declined |
| soi.destroy | 774 | 0 | ⚠ never declined |
| soi.keepfast | 1,181 | 0 | ⚠ never declined |
| soi.maglev | 12 | 0 | ⚠ never declined |
| soi.removeshop | 172 | 5,345 | both |
| soi.reset | 1,194 | 0 | ⚠ never declined |
| soi.return | 4,427 | 0 | ⚠ never declined |
| soi.reveal | 14,839 | 0 | ⚠ never declined |
| soi.scry | 0 | 10,672 | 🚨 NEVER taken |
| soi.shields | 21,202 | 0 | ⚠ never declined |
| soi.split | 277 | 0 | ⚠ never declined |
| soi.warp | 18,005 | 0 | ⚠ never declined |

Multi-option decisions — is the choice actually being made, or is it always
the first option (the `ChooseAnswer` default's signature)?

| Decision | Picked option 0 | Picked another | Verdict |
|---|---:|---:|---|
| soi.banish | 179 | 9,951 | chooses |
| soi.copy | 2,069 | 1,531 | chooses |
| soi.defiant | 4,154 | 128 | chooses |
| soi.destiny | 498 | 1,425 | chooses |
| soi.destroy | 407 | 563 | chooses |
| soi.handpick | 119 | 966 | chooses |
| soi.herodraft | 1,336 | 4,664 | chooses |
| soi.mode | 533 | 1,918 | chooses |
| soi.recruit | 6 | 33 | chooses |
| soi.relic | 150 | 578 | chooses |
| soi.removeshop | 28 | 144 | chooses |
| soi.reset | 316 | 425 | chooses |
| soi.return | 1,490 | 2,071 | chooses |
| soi.reveal | 4,422 | 0 | 🚨 ALWAYS the first option |
| soi.split | 2,415 | 15 | chooses |
| soi.tutor | 363 | 2,297 | chooses |
| soi.warp | 4,003 | 9,742 | chooses |

Most-banished cards — thinning should prefer whatever sits furthest
below the deck's own average, so starters should dominate this list.

- crystal: 7,615
- shard_reactor: 2,331
- blaster: 1,154
- g_48: 1
- unknown_god: 1
- aegis_archivist: 1
- shard_seer: 1
- dash_duel: 1

| soi.split targeting | 19,644 hit a champion | 11,292 face only | both

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

- winner reached M30: **61.3%** (1,839)
- winner below M30: **38.7%** (1,161)

## 6. Verdict

- 🚨 1 optional decision(s) never taken: soi.scry
- 🚨 1 decision(s) ALWAYS pick the first option (unhandled by ChooseAnswer): soi.reveal
