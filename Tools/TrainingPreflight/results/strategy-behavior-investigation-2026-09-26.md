# Strategic coverage and suspicious behavior

**The current AI has broad card/action exposure, but it does not yet demonstrate broad strategic competence.** Fresh frozen probes found wasted combat allocations, premature conditional activations, nearly indistinguishable opposite modes, and exact information losses. Several apparently ignored purchases are instead used heavily through fast-play. The low win rates of complex cards/heroes cannot yet be treated as evidence that those strategies are intrinsically bad.

Training continued throughout this investigation. All new gameplay tests used an immutable **generation 7552** V7 snapshot, CPU FP32, one Torch thread and one host worker. No optimizer, training source, live observation contract, or campaign allocation was changed. The snapshot is retained at `/home/lva/.local/share/shards-training/2026-09-26/strategy-audit-v7/latest.soicp`. Its serialized payload SHA256 is `a5c903318acd65ae8f05929508ce4df1d6f48ebcaacea2f40387dc3aeaeb3ae2`.

## Evidence collected

- **640 frozen self-play games**, equal exposure for every distinct hero pairing with seat swaps: 1,280 player trajectories, 245,808 decisions, all terminal. Only initial heroes were assigned; subsequent actions sampled the actual saved policy. [Full probe](strategy-coverage-v7-2026-09-26.json), [implementation](../experiments/strategy_coverage_probe.py).
- **160 additional sequence-traced games** on new seeds, testing later same-turn opportunities and announced damage thresholds. [Trace evidence](strategy-sequences-v7-2026-09-26.json), [implementation](../experiments/strategy_sequence_probe.py).
- **640-game combat ablation**, testing only the narrow sole-champion damage filter, all terminal. [Result](split-timing-v7-2026-09-26.json), [predeclared plan](split-timing-v7-2026-09-26.plan.json).
- **640-game timing ablation**, with one side deferring a narrow whitelist of currently inactive exhaust effects; balanced over all 20 ordered hero pairings and both seats. [Result](conditional-timing-v7-2026-09-26.json), [predeclared plan](conditional-timing-v7-2026-09-26.plan.json).
- Source inventory of **19 strategic paths**, all 30 active destinies, action eligibility and representation. Two isolated fixtures demonstrate information aliases and were scored with the actual checkpoint. [Rules/path audit](strategy-paths-audit-2026-09-26.md), [representation audit](strategy-representation-audit-2026-09-26.md), [policy responses](strategy-alias-policy-response-v7.json).
- A **189-row card inventory** combines rules, availability, recent random-hero associations, fresh legal opportunities, acquisitions, plays, exhausts and conditional activation counts. It retains all 45 replaced definitions with explicit inactive status. [CSV inventory](strategy-card-coverage-2026-09-26.csv), [statistics capture](strategy-statistics-snapshot-2026-09-26.json).

These are sampled visited states and source checks, not exhaustive exploration of every possible deck or game state. Opportunity counts deduplicate identical card/category candidates within each menu, but repeated menus during a turn remain repeated opportunities. Exposure counts deduplicate player-games. Never treat individual decisions as independent strength samples.

## 1. Damage allocation is the strongest behavioral concern

In the 160-game sequence panel, **396 of 875** examined sole-champion splits allocated a positive amount below that champion's announced remaining defense. Examples include 2 damage into a 4-defense Fao Cutul and 2 into a 5-defense Numeri Drones when all available power was only 2.

For these checks there is one opposing champion, the player target comes first, and full assignment is mandatory. The player's final chosen amount therefore determines the sole champion's remaining allocation. The inference excludes taunt cases, where allocation is optional. Announced remaining defense is authoritative and accounts for existing marks/defense modifiers. Positive sublethal allocation does not destroy the champion and its marks do not persist into the next turn. This is **sublethal diversion**, not proof that redirecting it wins the game: face shields and subsequent responses may still prevent useful damage. [Split source audit](strategy-paths-audit-2026-09-26.md), [sample allocations](strategy-sequences-v7-2026-09-26.json).

The broader panel's 11,343 split decisions had mean normalized entropy **0.8044**, far above ordinary-priority decisions' **0.2822**. High entropy alone is not an error, but the traced allocations demonstrate a real competence gap. The current action adapter exposes interval subdivision rather than explicit plans such as “kill this champion” or “all damage to the player.” Available numeric features do not imply mastery of this procedure.

A separate evaluation-only split intervention is recorded in [split-timing-v7-2026-09-26.json](split-timing-v7-2026-09-26.json). It removes only integer intervals incompatible with either zero damage or at least the announced defense for a sole opposing champion, retaining all potentially lethal allocations and all-face damage. Taunt, multiple champions and unknown entity records remain untouched. Four interval-filter tests passed, including 361 power/defense combinations and coarse-interval boundary cases. [Filter](../experiments/split_interval_guard.py), [tests](../experiments/test_split_interval_guard.py). **Result: 56.09375% over 640 complete games, no censors, conservative paired 95% bound 48.5017–63.6858%.** This is promising but inconclusive; it is not a deployed training change. Both sides use the same saved weights, with only the declared narrow filter changing A's sampled behavior. An initial evaluator-only attempt aborted because it filtered unused opponent inference rows; a deterministic failing regression identified that issue. The corrected attempt filters only active policy-A rows and uses a new seed namespace. [Retained aborted-attempt record](split-timing-v7-2026-09-26-aborted.json).

## 2. Conditional abilities are often activated before they can work

The 640-game panel measured these exact public prerequisites at activation:

| Ability | Gate false when activated | Gate true when activated |
|---|---:|---:|
| Paradigm Shift | 758 | 301 |
| Primus Pilus | 391 | 266 |
| War Bound | 442 | 354 |
| Biotech Enhancements | 271 | 142 |
| True Leader | 246 | 121 |
| Strategic Mastermind | 218 | 120 |
| Synthesis | 137 | 109 |
| Stolen Futures | 79 | 39 |
| Datic Secrets | 22 | 15 |
| Forged in Flame | 14 | 6 |

The engine legally offers these exhaust actions even when their conditional effects do nothing. This is not a rules bug. A false prerequisite can be harmless at the end of a turn, so counts alone do not establish lost value. The separate sequence panel found **79 cases** where the same ability's prerequisite subsequently became true **before that player's end action**, while the ability remained owned and unavailable. These include 27 Paradigm Shift, 18 Biotech, 12 True Leader, 9 Primus Pilus and smaller counts elsewhere. These are concrete foregone activation opportunities; their effects and win value are not interchangeable. [Exact predicates](../experiments/strategy_coverage_probe.py), [same-turn traces](strategy-sequences-v7-2026-09-26.json).

The timing-only intervention scored **51.40625%**, with conservative paired 95% bound **43.8142–58.9983%**, in 640 complete games with no censors. That is inconclusive for strength. It does not justify hardcoding a broad “never exhaust” rule or claiming the intervention improves training. A conditional-readiness representation and targeted sequencing curriculum remain plausible improvements to test.

Do not extend this test to abilities that set useful later flags, compound effects with unconditional benefits, or healing that converts into power. Unknown God can also double effects. The whitelist deliberately avoids treating a zero immediate resource delta as a universal no-op.

## 3. Opposite modes are still nearly coin flips

The fresh panel encountered 152 Reactor Drone mode menus. Mean maximum probability was about **50.22%**, with normalized entropy effectively **1.0**. Physical Drone activations split exactly 36/36 in 72 menus. This is suspicious because preserving the card versus gaining an extra gem and banishing it has different strategic consequences.

Copying Reactor through Warpquartz or Fabricator does **not** banish the copier. In that context, three gems gives an extra gem without the self-banish cost. An isolated Reactor/Ojas fixture yields byte-identical policy inputs despite different banish consequences. The actual checkpoint assigned **49.8086%** to two gems and **50.1914%** to three gems in both cases. The fixture constructs effect contexts by reflection; it proves an information alias, not its natural-game frequency. The fresh gameplay panel also observed a Reactor choice under a Warpquartz-initiated chain selecting two gems; root-action attribution is recorded separately from the engine's internal effect source. [Copy source rules and fixture](strategy-representation-audit-2026-09-26.md), [checkpoint scoring](strategy-alias-policy-response-v7.json).

Shard Defiant had only five reveal menus: four ordinary Keep/Banish menus were approximately 50/50; the Comet menu correctly allowed **Banish only**. Five observations cannot rank this destiny. The earlier probe merged its two same-card options; the new probe includes the mode ordinal and exposes the distinction.

V7's categorical repair currently covers Volos only. For Reactor/Defiant, the shared card embedding does not distinguish the two options; the small ordinal feature carries the contrast. A useful repair needs **semantic option identity plus effect/source or revealed-card context**, not merely more training on the same ambiguous inputs.

## 4. Some legitimate information is absent

After legally playing Fabricator and copying the same opposing card, two constructed positions with different revealed personal top cards produce exactly equal 4,160-float policy inputs. The subsequent draw is different, but the feedforward policy's logits and value are identical. The existing knowledge ledger tracks limited center-deck information, not personal revealed tops. [Legal-action fixture and scope](strategy-representation-audit-2026-09-26.md).

Public entity status overflow occurred in **2,699 / 245,808 decisions (1.10%)**; ordered staging exceeded 16 entries in **273 (0.11%)**. Aggregates and legal candidates remain available, so these figures do not mean actions were dropped. No menu exceeded the paging threshold in this sample; therefore later-page competence remains untested, rather than being proven unnecessary.

## 5. What is covered, and which apparent neglect is reasonable?

All **144 active definitions** appeared through their appropriate route in the fresh panel. Of these, 139 had a direct normal purchase/recruit/destiny selection or monster attack, four are starters, and Root of the Forest was used through fast-play instead of normal buying. All **15 active relics** and **30 active destinies** had normal selections. This establishes broad entry coverage, not adequate experience or effective continuation.

| Card | Normal purchases | Paid fast-plays |
|---|---:|---:|
| Lifebloom Ritual | 2 | 81 |
| Doomstalker | 2 | 258 |
| Scion of Nothingness | 5 | 193 |
| Aetherbreaker | 6 | 332 |
| Grim Tutor | 18 | 260 |
| Root of the Forest | 0 | 76 |

These cards are not ignored. Choosing immediate effects without adding permanent deck weight is a coherent route. The figures do not prove every fast-play was correct.

Other expected absences: no Comet fast-play/free acquisition, no normal market purchase of starters, no activation for purely passive destinies, no concession in these games, no paging without oversized menus, and no opponent-target selection when 1v1 rules directly choose the sole opponent. ResetChampion offers only exhausted targets, so “resetting an already ready champion” is not an available mistake for that effect. [Rules inventory](strategy-paths-audit-2026-09-26.md), [full card inventory](strategy-card-coverage-2026-09-26.csv).

Rare combinations remain poorly sampled: Datic Secrets, Forged in Flame, Maglev Tunnels and Whatever it Takes each had **one normal selection** in this panel despite 230–271 player-games of availability. Shard Defiant's repeat branch was not encountered. Warpquartz, defensive relics, monster reward engines, multi-destiny builds, personal-top planning and threshold-dependent combos cannot be declared bad from these data.

## 6. Broad strategy evidence is mixed

Each hero had 256 player trajectories. Observed pre-action mastery reached 30 in 80 Decima, 39 Tetra, 39 Volos, 35 Ko and 48 Rez trajectories. At least three champions appeared in 104/71/110/85/138 respectively. Banish choices occurred in 201/190/213/253/185. The most frequent ordinary banish targets were Crystal (2,508) and Blaster (490). Thus mastery, champion and thinning paths are present; their quality and sequencing remain the issue. Extrema are pre-action observations and can miss changes on the terminal action.

Volos used all four modes. With all four affordable, its mean probabilities were **84.8% heal, 3.3% power, 7.3% draw, 4.6% mastery** over 804 menus. This is a substantial preference, not mode absence. Even at full health, declining gem costs can be sensible; free healing that gains no HP is not automatically worse than spending gems. Timing relative to purchases, healing conversion and imminent mastery thresholds needs conditional evaluation.

The AI ended 628 of 15,536 turns while a hand play remained legal. Some omitted plays were simple Blasters or Crystals; others may have genuine costs. Because this is stochastic sampled play, occasional inferior actions are expected. The aggregate does not distinguish exploration from bad ordering, and ending with a legal purchase is often appropriate when buying would worsen the deck.

The retained natural-draft champion was still generation 1827 during inspection. Recent V7 evaluations against it scored 51.2% and 57.4% over 256 games each, with broad intervals including 50%; frozen CPU scores against champion/anchor were also inconclusive. There is no established recent strength gain from game count or GPU utilization alone. Broader hero training can improve underrepresented matchups without improving the natural-draft matchup immediately; that hypothesis needs a balanced fixed-opponent comparison rather than pooling changing learners.

## Priorities justified by this investigation

1. **Combat plans and thresholds:** expose meaningful damage outcomes and test allocation around champion defense, taunt and shields. The measured sublethal diversions make this a higher priority than blindly increasing GPU throughput.
2. **Typed modes and effect context:** separate Keep/Banish and Reactor's resource/self-banish choices; retain enough source/revealed-card context to distinguish their consequences.
3. **Conditional sequencing:** provide explicit readiness/threshold cues and evaluate short targeted scenarios such as play prerequisite → exhaust, mastery → payoff, healing converter → healing, banish → Warpquartz.
4. **Small public-knowledge memory:** retain legitimately revealed own tops and relevant public history with strict invalidation and privacy tests. Extra network width cannot recover absent input information.
5. **Strategy-level coverage tests:** measure useful effects, successful combinations and frozen matchup performance. Randomizing a relic alone does not teach assembling or executing its supporting deck.

These are priorities supported by measured gaps, not a claim that a proposed new learner is already superior. Preserve checkpoint migration, exact behavior likelihood and the original remaining-time budget for any training change. The complete path audits and per-card inventory explicitly retain unmeasured areas instead of labelling them weak.

## Final live check

At the end of this investigation, training was active at generation **7860**, with **135,680** completed main-V7 continuation games, **0** censored games and finite parameters/optimizer state. Approximately **4.55 hours** remained under the original allocation. The successful new frozen panels totaled **2,080 games**, plus isolated constructed fixtures; the aborted evaluator attempt is retained separately and contributes no strength estimate.
