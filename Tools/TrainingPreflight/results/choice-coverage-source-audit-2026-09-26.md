# Choice coverage source audit, 2026-09-26

This audit reads the current game rules and the frozen V6 training adapter/model. It makes no live runtime changes and does not measure current policy frequencies; the accompanying live investigation supplies those measurements. All citations are primary repository sources. A zero acquisition count is not enough to distinguish an unavailable definition, a legal option never selected, or a selected card whose useful combinations were never learned.

## Relic availability

There are **23 registered relic definitions, but only 15 active relic definitions in all-DLC Duel: three per hero**. Eight older definitions are deliberately replaced. Duel enables the other DLCs; `RelicIdsFor` excludes every ID replaced by a Duel definition and then selects the drafted hero's shipped relics. These excluded entries should be marked unavailable in the monitoring catalog, not interpreted as failed exploration. [DLC normalization and replacement](../../../Assets/Scripts/Shards/Engine/ShardsEngine.cs#L315), [active relic selection](../../../Assets/Scripts/Shards/Engine/ShardsEngine.cs#L226).

| Hero | Three active relic definition IDs |
|---|---|
| Decima | `praetorian_01`, `praetorian_02_duel`, `praetorian_03` |
| Tetra | `datic_robes_duel`, `multitask_brain`, `terminal_crescents_duel` |
| Volos | `entropic_talons`, `panconscious_crown_duel`, `unknown_god` |
| Ko Syn Wu | `doom_gate`, `heart_of_nothing_duel`, `world_piercer_duel` |
| Rez | `slipstream_shard_duel`, `star_seeker`, `warpquartz_duel` |

The eight inactive original IDs are `praetorian_02`, `datic_robes`, `terminal_crescents`, `panconscious_crown`, `heart_of_nothing`, `world_piercer`, `slipstream_shard`, and `warpquartz`. Sources: [original relics](../../../Assets/Scripts/Shards/Content/ShardsRelicsSet.cs#L126), [Rez originals](../../../Assets/Scripts/Shards/Content/ShardsShadowSet.cs#L59), [five new Duel relics](../../../Assets/Scripts/Shards/Content/ShardsDuelSet.cs#L30), [first replacement group](../../../Assets/Scripts/Shards/Content/ShardsDuelSet.cs#L304), [second replacement group](../../../Assets/Scripts/Shards/Content/ShardsDuelSet.cs#L398).

Normal recruitment becomes available at **mastery 10**, costs no gems, grants one relic to **discard**, and consumes the player's once-per-game normal recruitment. All three set-aside relics are offered; there is no hardcoded restriction to a preferred relic. Having recruited a relic does not guarantee drawing or playing it before the game ends. The current passive statistics count `ShardsRelicRecruitedEvent`, not actual relic plays, exhausts, or turns of operation. [Legal menu](../../../Assets/Scripts/Shards/Engine/ShardsEngine.cs#L1843), [recruitment transition](../../../Assets/Scripts/Shards/Engine/ShardsEngine.cs#L818), [statistics event handling](../experiments/HostV4/TrainingStatistics.cs#L151).

An Ingeminex Corruption reward can grant another remaining relic directly to hand, even before normal recruitment or after its allowance is consumed. It does not set `RelicRecruited`; with three initial relics, multi-relic games are legal and must not be treated as telemetry errors. [Corruption reward](../../../Assets/Scripts/Shards/Content/ShardsHorizonSet.cs#L328).

Relic identities themselves are distinguishable to the policy. Candidates carry the stable card index and use a categorical card embedding. However, the policy receives no parsed relic effect description; it has to learn the consequences from experience. [Candidate encoding](../experiments/HostV6/Encoder.cs#L253), [card embeddings](../model.py#L55).

## Volos: all modes exist, but representation is weak

Volos can activate once per turn from mastery 5. Opening the choice costs nothing. The four original option indices are:

| Index | Cost | Result |
|---|---:|---|
| 0 | 0 gems | Gain 3 health |
| 1 | 1 gem | Gain 2 power |
| 2 | 2 gems | Draw 1 card |
| 3 | 3 gems | Gain 1 mastery |

Unaffordable modes are disabled. The adapter preserves each option's **original ordinal**, including when another is disabled; no mode is silently remapped or deleted when affordable. [Volos effect](../../../Assets/Scripts/Shards/Engine/ShardsDuelEffects.cs#L55), [hero activation gates](../../../Assets/Scripts/Shards/Engine/ShardsEngine.cs#L651), [adapter option loop](../experiments/HostV6/Adapter.cs#L272).

**For a given Volos choice state, the four candidate vectors differ only at candidate column 25: 0, 1/128, 2/128, 3/128.** Synthetic `soivolos:N` IDs are recognized solely to avoid an unknown-definition exception; they are neither card embeddings nor hero identities. Option labels are not encoded. `Amount`, `Required`, owner and card features are the same/default for all four. Thus the representation distinguishes them numerically, but supplies no explicit gem cost, effect amount, draw/mastery meaning, or categorical mode identity. V6's ability cost features are for the outer Volos ability, which is free, and do not fill this gap. [Encoder](../experiments/HostV6/Encoder.cs#L275), [V6 hero features](../experiments/HostV6/HeroFeatures.cs#L37).

The input transform preserves that small ordinal difference. The shared candidate key is a linear layer followed by SiLU, with a separate linear candidate bias. Over this tiny interval, learned scores can be nearly linear in the ordinal, favoring an endpoint over both interior modes. **This is a mechanistic hypothesis, not a mathematical impossibility or a measured claim about current weights.** Inspecting logits/curvature at real Volos decisions and measuring each mode conditional on affordability will distinguish it from ordinary preference for free healing. [Numeric transform](../learning_model.py#L82), [candidate scorer](../model.py#L45).

Free healing is not necessarily wasted at full health: Entropic Talons makes gained health also grant power even at the health cap. Volos healing-mode evaluation must account for this flag, healing doubling, and overflow conversion, rather than classifying every full-health heal as a mistake. The observation already includes those public flags. [Talons](../../../Assets/Scripts/Shards/Content/ShardsRelicsSet.cs#L165), [health conversion](../../../Assets/Scripts/Shards/Engine/ShardsEngine.cs#L1894), [observed flags](../experiments/HostV6/Encoder.cs#L122).

## Other choices and coverage holes

- **Destinies:** normal taking requires mastery 5, the row is shared and shrinks without refill, and each player normally takes one. A missing destiny may never have been offered, may have been taken by the opponent first, or may have been rejected. Ingeminex and Stolen Futures provide additional acquisition paths. Track offered/available/selected separately. [Destiny transition](../../../Assets/Scripts/Shards/Engine/ShardsEngine.cs#L789), [additional reward](../../../Assets/Scripts/Shards/Content/ShardsHorizonSet.cs#L361), [Stolen Futures](../../../Assets/Scripts/Shards/Content/ShardsHorizonSet.cs#L724).
- **Binary modes:** Reactor Drone's normal versus self-banish gem gain is encoded by context/title plus a small ordinal, as are Shard Defiant's keep/banish alternatives. They are distinguishable, but lack explicit option semantics. A binary preference can be represented by a slope; Volos's four alternatives particularly expose the disadvantage of a single ordered coordinate. [Reactor Drone](../../../Assets/Scripts/Shards/Content/ShardsDuelSet.cs#L520), [Shard Defiant](../../../Assets/Scripts/Shards/Content/ShardsHorizonSet.cs#L657), [title/context hashing](../experiments/HostV6/Encoder.cs#L147).
- **Mastery thresholds:** acquisitions alone do not demonstrate using mastery-15, mastery-20, or Infinity Shard mastery-30 effects. Record relic/card plays with threshold status, and whether the condition actually fires. Examples include Unknown God's double exhaust, Slipstream Shard's extra turn, Crown's mastery-20 Unify, and Star Seeker's second warp. [Relic upgrades](../../../Assets/Scripts/Shards/Content/ShardsDuelSet.cs#L30), [Infinity Shard](../../../Assets/Scripts/Shards/Content/ShardsBaseSet.cs#L45).
- **Doom Gate changes the environment:** playing it adds 25 Ingeminex once per player and its exhaust claims a monster reward. Recruiting it without deploying/exhausting it does not test the monster strategy. [Doom Gate implementation](../../../Assets/Scripts/Shards/Content/ShardsDuelSet.cs#L864).
- **Comet:** normal gem purchase is deliberately its only acquisition path; it costs 13, cannot be rerolled, and cannot be obtained by free-play effects. A zero fast-play count is correct. Track affordable shop exposures, normal purchases, and subsequent plays separately. [Comet definition](../../../Assets/Scripts/Shards/Content/ShardsDuelSet.cs#L162).
- **Damage assignment:** all integer allocations are represented through staged intervals rather than a truncated fixed list. The default binary split has no structural loss of integer choices, but exploration may still favor an endpoint repeatedly. Probe lethal, taunt, efficient champion kills and zero/waste cases as scenarios. [Split adapter](../experiments/HostV6/Adapter.cs#L201), [engine damage menu](../../../Assets/Scripts/Shards/Engine/ShardsEngine.cs#L1065).
- **Large menus:** the adapter pages 63 real candidates plus a page action when more than 64 actions exist. Options beyond page one remain reachable, but a negative page-action prior may make them rarely examined. Record page transitions and the fraction of selectable cards hidden on later pages in tutor/warp/banish states. [Pagination](../experiments/HostV6/Adapter.cs#L78), [action prior](../learning_model.py#L24).
- **Revealed and historical information:** V6 preserves the owner's Scry/reorder center-top knowledge, but explicitly does not model all revealed-card history, center-bottom order, or every Defiant/Longshot sequence. Also, the value trunk does not consume candidate-only information; candidate logits compare each candidate to the common state independently. These are residual limitations, not proof that current missing acquisitions are caused by them. [V6 knowledge contract](../experiments/HostV6/README.md), [model state/candidate separation](../model.py#L55), [earlier source audit](representation-quality-research-2026-09-26.md).

## What should be tested before relic balance decisions

1. Measure **legal opportunity counts** and chosen probabilities for each active relic, destiny, Volos mode and special effect; keep those denominators distinct from game appearances and actual uses. Count a relic opportunity once per eligible player-game as well as decision-level exposures, so long priority sequences do not inflate coverage.
2. Use a **separate randomized-relic cohort** or a bounded mixture of exploratory games to learn continuations after the alternatives. Choose only real legal relics at legal opportunities; do not grant extra relics, cross-hero relics, or early mastery access in the ordinary training cohort. If externally overriding a policy action, do not present it to PPO as though sampled from the unchanged behavior distribution. Forced choices must be excluded appropriately or their actual mixture probability must be used consistently.
3. Keep a **natural-choice evaluation cohort**: forcing every relic forever would prevent evaluating learned selection. Forced-relic scores answer how well this policy continues from that assigned relic, not which relic is intrinsically best, and can be biased by training experience with that relic.
4. For controlled evidence, use matched hero/opponent/seat assignments and compare relic alternatives on disjoint seeds, reporting both matchup and mastery/round timing. Do not pool these interventions into natural purchase associations.
5. Consider a small **explicit mode encoding** if the measured Volos logits show interior-choice collapse. Add categorical modes or typed costs/rewards in a versioned representation, migrate retained policies deliberately, and compare policy behavior before and after. Do not mutate the frozen V6 source under a live identity.
6. Add a **scenario coverage suite** for late thresholds, multi-relic Corruption, Doom Gate monsters, Talons-healing combinations, Scry/reroll, tutor/warp paging and lethal assignment. Exhaustive game-state coverage is combinatorial; these targeted probes can verify availability and basic competence without pretending every strategy has been mastered.

## Existing scenario tests: verified versus missing

Follow-up verification rebuilt the independent `Tools/EngineVerify/Engine.Verify.csproj` with `--no-restore -m:1 -p:UseSharedCompilation=false`, then ran a name-filtered subset under `nice -n 15`, `DOTNET_PROCESSOR_COUNT=1`, and a 55-second outer timeout. **32 targeted NUnit cases passed, zero failed or skipped**; reported test execution was 69 ms. The TRX evidence is `/tmp/shards-choice-coverage-tests/choice-coverage-existing-tests.trx`. This build does not rebuild the frozen training host. Separately, the **existing frozen V6 DLL `selftest` passed** in approximately 0.5 seconds under a 45-second outer timeout and the same low-priority/single-processor environment. These establish engine/adapter behavior, not whether the trained policy makes good decisions.

Exact passing NUnit cases, grouped only where the method is parameterized:

| Scenario | Exact test method and cases | What was actually asserted |
|---|---|---|
| All four Volos modes | `Duel_Volos_ChoicePaysOnlySelectedCost(0)`, `(1)`, `(2)`, `(3)` | Opening is free; all four options remain in the engine request; unaffordable modes disabled/rejected; each selected mode pays exactly its gem cost and gains the correct health/power/draw/mastery; repeat activation rejected. |
| Volos timing | `Duel_HeroAbility_IsSeparateFromFocus_OncePerTurn` | Free health mode resolves, Focus remains independently legal, second ability use rejected. |
| Crown replacement | `SeptemberBalance_CrownReplacementIsDuelOnly(False)`, `(True)` | Correct active set-aside definition and normal low-mastery heal/mastery; not M20 Unify. |
| Praetorian-03 | `SeptemberBalance_Praetorian03_UsesNewMasteryThreshold(19,2)`, `(20,3)` | M19/M20 mastery and draw gains. |
| Heart of Nothing | `SeptemberBalance_HeartOfNothingPower(19,7)`, `(20,14)` | M19/M20 power and extra-draw flag. |
| Multitask Brain | `SeptemberBalance_MultitaskBrain_NoLongerGrantsDominionMastery` | Four-faction base power/draw, no old mastery bonus; not M20 doubling. |
| World Piercer | `SeptemberBalance_WorldPiercer_ReturnsUpToTwoWithoutRevealingDeckOrder(0)`, `(1)`, `(2)` | Optional zero/one/two returns, choices sorted rather than revealing hidden deck order. |
| World Piercer upgrade | `SeptemberBalance_WorldPiercer_M20StillReturnsAll` | Starting M18 crosses to M20 through its own gain and returns all three available mercenaries. |
| Praetorian-02 | `SeptemberBalance_Praetorian02_ActivationCostsTwo`; `Duel_Praetorian02_DoubledShields_ExpireAtOwnersNextTurn` | Cost rejection/acceptance; shield doubling and expiry timing. The expiry test sets the flag directly, so it is not itself a full card activation test. |
| Praetorian-01 | `Praetorian01_ReturnsFromDiscardWhenChampionPlayed` | Relic returns from discard on champion play. |
| Talons | `EntropicTalons_HealthGainsConvertToPower_EvenAtTheCap` | Generic health gain converts to power at full health; does not invoke Volos's specific heal choice or M20 auto-heal. |
| Slipstream | `SlipstreamShard_M20_ExtraTurn_OncePerGame` | Extra-turn scheduling and once-per-game restriction for **the non-Duel original** `slipstream_shard`. |
| Doom Gate | `Duel_DoomGate_FloodsOnlyOncePerGame`; `Duel_DoomGate_DestroyingAnIngeminex_GrantsItsReward` | Two plays add only 25 monsters total; destroying a lone Torment awards its four mastery. |
| Corruption availability | `Setup_ItHWithoutRotF_RemovesCorruption` | Expansion gating only; does not execute the extra-relic reward. |
| Midturn versus endturn attack | `ChampionAttack_MidTurnIsIllegal_ChampionsDieInTheEndTurnSplit` | Champion attacks illegal midturn; explicit split kills champion and hits face. |
| Taunt and assignment | `Split_LethalOnZetta_UnlocksOwnerInSameAnswer`; `Split_NoLethalOnZetta_OwnerAssignmentsDropped`; `SplitDecision_FullPowerAssignmentIsMandatory` | Lethal taunt assignment unlocks face; nonlethal taunt still protects; all power must be assigned where applicable. |
| Overwhelming power | `Split_OverwhelmingPower_KillsAllOpponentsInstantly` | Injected power 5000 causes instant elimination. This **does not execute Infinity Shard at M30**. |
| Testudo shields | `Duel_Testudo_OverAssignment_PaysThroughShields`; `Duel_Testudo_ExactLethal_IsSavedByAnyShield`; `Duel_Testudo_TauntHeld_ZeroesEverythingBehindIt` | Over-assignment, exact lethal versus shields, and held-taunt protection. |

Primary test sources: [content tests](../../../Assets/Tests/EngineTests/ShardsContentTests.cs#L100), [Volos tests](../../../Assets/Tests/EngineTests/ShardsContentTests.cs#L438), [Doom Gate tests](../../../Assets/Tests/EngineTests/ShardsContentTests.cs#L1102), [rulings tests](../../../Assets/Tests/EngineTests/ShardsRulingsTests.cs#L279), [Talons](../../../Assets/Tests/EngineTests/ShardsRulingsTests.cs#L442), [original Slipstream](../../../Assets/Tests/EngineTests/ShardsRulingsTests.cs#L737), [engine split fixture](../../../Assets/Tests/EngineTests/ShardsEngineTests.cs#L219).

The frozen V6 `selftest` verified:

- 32 seeded replays agree between sequential encoding and the parallel engine path; 14,503 wrapper steps and 13,223 engine submissions, no replay caps.
- A 130-option fixture disables every tenth option: all **117 enabled choices** remain reachable over two pages, disabled choices are masked and forged selection rejected, and a selected option cannot be selected twice.
- Every legal completion in the small ordered optional fixture is reachable exactly once (10 answers).
- Every nonnegative integer allocation of total 4 across three targets is reachable exactly once (15 allocations).
- Every optional allocation up to total 3 across two targets is reachable exactly once (10 allocations).
- Recursive split intervals have no gaps or overlap through upper bound 1000 for branch limits 2, 8, 32 and 64. The live default is two branches.
- Existing privacy/public-entity/candidate-field and mapped-buffer checks also passed.

Source and executable: [selftest implementation](../Host/SelfTest.cs#L14), [paging fixture](../Host/SelfTest.cs#L194), [exhaustive decision coverage](../Host/SelfTest.cs#L222), `Tools/TrainingPreflight/experiments/HostV6/bin/Release/net8.0/TrainingHostV6.dll selftest`.

**Missing explicit targeted scenarios in the searched current engine tests and host selftests** (broad seeded games may incidentally encounter them; that is not an assertion of the specific behavior):

- Corruption recruiting a second or third relic, before M10 and after normal recruitment; one versus several remaining relics; an empty set-aside pool; ensuring normal recruitment remains independently available after an early bonus relic.
- Unknown God's M20 doubling of its own and other champions' exhaust effects.
- Star Seeker's unlimited Warp and second M20 Warp resolving through the full relic exhaust path. The Comet exclusion tests exercise a generic `WarpUpTo(-1)` effect, not this complete relic sequence.
- Crown's M20 Unify success and failure; Talons' M20 ten-health bonus plus actual Volos healing at full health.
- Duel Slipstream's own +2 mastery crossing into M20 and once-per-game extra turn; the existing original-Slipstream test is helpful but does not assert this replacement's threshold interaction.
- Warpquartz Duel's up-to-three banish/copy path, gains per cards banished this turn, and optional decline; Multitask Brain M20 doubling; Terminal Crescents threshold crossing; Datic Robes M20 discard shield.
- Executing the actual Infinity Shard definition at M29 and M30. The overwhelming-power test injects a large power total instead.
- Doom Gate immunity to Ingeminex attacks, multi-monster target selection, and Corruption reward interaction; the two existing Doom Gate tests cover flood-once and a single Torment reward.
- Full learned-policy scenario assertions: selecting a valuable interior Volos mode, exploiting Talons, choosing lethal damage, defending with the right shield, choosing a card on a later page, or selecting a relic suited to its deck. Existing engine legality tests cannot establish these competencies.

Correction to the initial draft: **Infinity Shard**, not Shard Reactor, has the M30 effect. Shard Reactor uses M5/M15 gem thresholds. [Actual starter definitions](../../../Assets/Scripts/Shards/Content/ShardsBaseSet.cs#L39).

No randomized curriculum, model changes, GPU work, or training interruption was performed for this source/test audit.


## Additional independent scenario tests added during this investigation

The new headless [ChoiceCoverageTests](../../EngineVerify/ChoiceCoverageTests.cs) passed all **15 cases** (54 ms test execution). They cover actual Infinity Shard play at M29/M30; Duel Slipstream crossing M20 with its own mastery and its once-per-game restriction; Terminal Crescents crossing M20; Unknown God doubling its own and another champion's exhaust; the actual Volos healing decision with Talons at full health; and six Corruption reward combinations with empty/single/multiple relic pools and an unused/consumed normal recruitment allowance. These close those portions of the missing-test inventory above. Other listed gaps remain; this is not exhaustive strategy coverage.

An additional catalog exception is `cloud_oracles`: with Shadow of Salvation enabled, the engine explicitly removes it in favor of `cloud_oracles_sos`, without using `ReplacesId`. The statistics filter handles this exception. After applying this rule and the declared replacements, the configured market contains 90 active buyable definitions, alongside 15 active relics and 30 active destinies. [Initial center pool](../../../Assets/Scripts/Shards/Engine/ShardsEngine.cs#L189).
