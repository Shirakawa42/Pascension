# Natural-game review during the AI improvement campaign

The experimental hybrid still makes avoidable plays. The first three repaired examples were not enough: new complete games expose conditional gains, spending continuations, and repeated canceled menus that the prepared tactical suite did not catch.

## Sample and method

Screened **20 complete games, 5,940 actions and 399 end turns**, covering every ordered pair of distinct heroes. This is the three-style candidate with tactical guards and mixed resource plans, using the unchanged generation-19062 model. These are diagnostic seeds outside the strength pilot, reused from the preceding candidate's review for comparison. They are not the published 2,500-game statistics cohort and are not a representative blunder-rate estimate.

Read full chronological sequences for games 0, 5 and 18, and inspected flagged states across the sample. Replayed all 20 transcripts by exact action key and checked outcomes. **29 alternative paths at 27 flagged positions** were tested in **16 public-information worlds each**. Source-state fingerprints remained unchanged. Positions below use zero-based game/action indices and displayed rounds.

## Confirmed missed gains

| Position | Actual action | Available legal gain in all 16 worlds |
|---|---|---|
| Game 0, round 7, Ko Syn Wu, step 155 | End turn | Active Datic Secrets: **+1 mastery, +1 gem**. This also enables Focus to mastery 10 and relic recruitment. |
| Game 1, round 10, Ko Syn Wu, step 280 | End at 22 HP | Advanced Medicine: **22 → 26 HP**. General Decurion is also ready and gives **3 gems** before its other effects. |
| Game 18, round 9, Rez, step 260 | End at 50 HP, 3 power | Thornshell Warden: **3 → 5 power**, because its full-health bonus is active. |

These are verified immediate gains. Their effect on eventual winning probability is a separate question; a won position can still contain an unnecessary omission.

Additional suspicious opportunities:

- Game 13, round 6, Volos, step 123: Bulwark Chanter remains in hand; playing it gives **2 gems**, increasing the pool from 1 to 3 and enabling useful spending.
- Game 18, round 10, Rez, step 303: Kiln Drone remains in hand despite Inspire; playing it gives **4 gems**, increasing the pool from 1 to 5. The current continuation still fails to spend them.
- Game 18, round 10, Rez, step 325: Shard Seer remains in hand at mastery 28. Its free draw is available. This is a draw/sequence opportunity, not a guaranteed lethal: sampled hidden cards and subsequent choices matter.
- Game 6, round 12, Decima, step 315: Numeri Drones remains ready; **1 → 2 gems**, enabling Bulwark Chanter's purchase. Whether that acquisition helps soon enough is uncertain.
- Game 12, round 9, Rez, step 222: Ferrata Guard's active counter produces **one gem**. The immediate gain is verified; useful spending still needs evaluation.

## False alarms and sensible play

Most Datic Secrets flags produce no gain because its condition is unmet. Strategic Mastermind is inactive at the flagged HP totals below 40. Synthesis at mastery 11 and Stolen Futures at mastery 6 do nothing. Volos healing at 50 HP also produces no gain in the checked position. Ko's two unused hero powers would offer only strong remaining cards (Duplication Fabricator or Raidian) for banishing; declining is reasonable.

All four Volos modes occur: healing **39**, power **3**, draw **4**, mastery **6**. Game 0's finishing turn uses Entropic Talons before healing effects, including the hero power. Game 5's Rez correctly activates Strategic Mastermind while above its threshold; the flagged later turns are below it. No Rez–Longshot play occurred, so this sample does not validate that interaction.

Ko opens and cancels his banish preview **29 times**, including four times in game 2's round 9. HP is unchanged across all 29 cancellations. The old pay-HP-without-banishing bug is absent here, but the repeated menus remain wasted decisions.

No alternative sampled winning line was flagged by the bounded end-turn checker. That is not exhaustive proof that no lethal was missed.

## Why search still fails

Seven positions were independently re-evaluated with the current profile, depth 64, zero prior, and eight worlds. All current-profile choices matched the recorded End Turn. Depth 64 changed none. Zero prior corrected Thornshell Warden; eight worlds corrected Shard Seer. The other omissions remained.

Some mistakes originate in search overrides, not the policy's first preference:

- Advanced Medicine has policy probability **86.1%**, versus **5.3%** for End Turn. Search scores the healing continuation about **0.9258**, versus **0.9331** for ending, and rejects healing.
- Numeri Drones has policy probability **95.4%**, yet its continuation scores worse than End Turn.
- Bulwark Chanter has policy probability **64.4%**, versus **0.4%** for ending. The search again favors ending.
- Thornshell Warden has a slightly higher raw value than ending, but the prior penalty reverses the choice.

These branches reach the turn boundary; this is not simply an insufficient primitive-depth limit. Continuation choices and endpoint evaluation both need scrutiny. More imitation of the current search could reinforce the mistakes.

## Complete-game rollout diagnostic

Added an offline diagnostic using all distinct legal root action groups, **64 paired public worlds per action**, and the same fixed cheap guarded policy for both seats until terminal. It preserves the real source state and marks unfinished simulations as censored instead of inventing outcomes. All **1,152 continuations** completed, **148,458 simulated transitions** across seven positions, with no censored games. The measured internal rollout loops totaled approximately **2.60 seconds**; this excludes startup and transcript reconstruction.

The results show why terminal wins alone cannot replace tactical reasoning:

- Advanced Medicine, Thornshell Warden, Kiln Drone, and Shard Seer positions produce **64/64 wins for every tested action**, including End Turn. They cannot distinguish clean play from waste in already favorable positions.
- Datic Secrets produces **2/64 wins**, versus **7/64** after End Turn, under this particular continuation policy. That does not make gaining mastery bad: the extra resources change subsequent choices, including Focus and relic recruitment, and the base policy can exploit them poorly.
- Bulwark Chanter produces **62/64**, versus **60/64** after End Turn. Numeri produces **15/64**, versus **16/64** after ending. These small samples/differences do not establish reliable strategic rankings.

The rollouts estimate this fixed policy's outcomes, not optimal play or the stronger hybrid. Candidates share sampled initial worlds; different actions can consume later RNG differently.

## Follow-up: allow a simple stopping continuation

A fourth experimental continuation evaluates resolving the candidate action and ending immediately, so every free-gain branch need not inherit the same poor follow-up spending. This is a simulated option, not a mandatory live action. On the same seven reconstructed positions, it corrects **Datic Secrets, Advanced Medicine, Bulwark Chanter, and Numeri Drones** under all four diagnostic profiles. Thornshell Warden, Kiln Drone, and Shard Seer still end under the default profile.

Datic Secrets followed by ending scores **−0.5589**, versus **−0.6340** for ending directly and **−0.7035** for its previous longer continuation. Advanced Medicine followed by ending scores **0.9462**, versus **0.9331** for ending directly and **0.9258** for the previous longer continuation. This directly localizes these failures to the available continuations, rather than proving the critic incapable of valuing their immediate gains. The fourth style passes **1,024/1,024 tactical/privacy checks**; its 320-game paired-seat pilot finished **158–162**, without demonstrated strength improvement. It has not been deployed.

Subsequent work adds visible-condition gain guards and reviews another 20 complete games. Progress and strategic-rollout confirmation results are recorded in the [campaign report](ten-hour-improvement-2026-09-28.md).

## Status and artifacts

### Further review: visible menu plans

The next candidate explicitly evaluates an activation plus a visible menu choice. It passes six focused menu/privacy/cache-transfer cases and 1,024 prepared tactical checks, but its 320-game exploratory paired-seat result is **157–163 (49.06%; paired-seed bootstrap 95% interval 44.69–53.44%)**. It is not a proven strength improvement and has not been deployed.

Screened another **20 complete games, 6,175 actions and 411 end turns**; read the chronological sequences of games 0, 8 and 15. The diagnostic seeds are reused for controlled comparison, not a new independent strength sample. Reconstructed every transcript and checked **22 flagged alternatives × 16 public worlds**, followed by four sequencing alternatives × 16 worlds. Of those 22 end-turn flags, 20 are inactive effects, one is the optional four-HP cost of Bound for Life, and one is an unused active Primus Pilus draw. No bounded alternative-winning-line flag occurred; this is not exhaustive lethal coverage.

| Position | Finding | Verification/status |
|---|---|---|
| Game 15, round 10, step 274, Rez | Exhausts Star Seeker at mastery 18 with Slipstream Shard already in hand; warps Lifebloom Ritual, then later plays Slipstream | **Confirmed sequencing loss.** Slipstream → Star Seeker → the same Lifebloom target reaches the second warp menu in all 16 worlds. Star Seeker → Lifebloom → Slipstream does not. Deeper search, zero prior and eight worlds all retain the mistake. |
| Game 15, round 11, step 373, Rez | Ends at mastery 28 with active Primus Pilus ready | Exhausting draws two cards in all 16 worlds. Suspicious lost opportunity, not proof that drawing now always beats preserving next turn's draw. All four search profiles still end. |
| Game 15, round 12, step 424, Rez | Spends a gem on Focus at mastery 30, with Infinity Shard in hand | **Confirmed waste.** Infinity Shard → End wins in all 16 worlds. All four profiles preferred useless Focus before the fix. Experimental tactical guards now reject capped Focus at root, in continuations, and as a terminal-search override; all four profiles now play Infinity Shard. |
| Game 8, from step 186 | Newly acquired Strategic Mastermind appears as `exhaust:197` | **Confirmed engine lookup defect.** The cached instance index omitted the destiny deck; Stolen Futures moves existing cards without incrementing its cache-invalidation counter. Fixed by indexing the destiny deck. Both-seat regressions fail before and pass after the fix. |

The Star Seeker diagnostic localizes another continuation defect: the Slipstream-first branches reach mastery 20 but their greedy continuations **decline both warps**. Their best value is approximately 0.99156, versus 0.99521 for the explicitly explored Star Seeker → Lifebloom branch. The planner compares a productive menu choice on one side with poor menu continuations on the other. More primitive depth does not repair that asymmetry. This is the next substantive search problem, rather than justification for training the model to imitate this choice.

The destiny-index defect affects live tactical effect lookup and replay naming. The policy encoder uses `FindVisibleCard`, which separately traverses authorized visible zones and already recognizes the acquired destiny; it does **not** use the defective index. No hidden-state information was added to the encoder. After the fix, the complete replay validates **15,824 public action-target lookups** across all 6,175 actions. The headless engine suite passes **260/260**. The updated gain/privacy/capped-Focus audit passes **23/23**, and the final tactical/privacy suite passes **1,024/1,024**. No card rules or model weights changed.

Additional observations:

- Ko cancels 39 banish menus, with no HP paid on those canceled previews. Repeated opening/canceling remains wasted work.
- All four Volos choices occur: 40 heals, 3 power, 5 draw and 10 mastery selections.
- Rez changes the center row or activates Star Seeker before using his available Scry in 50 decisions. This is a screen, not 50 proven mistakes: saving Scry until after some refills can expose additional future cards. This sample contains no Rez–Longshot play.
- The original Star Seeker omission at visible-gain game 8 / step 324 is corrected by menu plans. Execution commits the first Wraethe Skirmisher choice, then replans to Aetherbreaker, reaching 11 power. It does not blindly execute the original simulated second-choice decline after new information arrives.

Evidence: `menu-plans-fresh-review/{reviews,chronological-transcripts.md,evidence.json,sequencing-evidence.json,behavior-counts.json}`, `menu-plans-sequencing-diagnosis/games.json`, `capped-focus-diagnosis/games.json`, `menu-plans-pilot/assessment.json`, `capped-focus-{red,green}.json`, and `index-focus-tactics/games.json`, under the campaign artifact root below. The 320-game pilot predates the index and capped-Focus fixes; its runtime is frozen. Compilation/tests shared the same eight-core affinity during part of that run, so its 270.91-second timing is only indicative.

The campaign remains active. The installed game is unchanged; the engine fix is in source and the planning changes remain experimental. The statistics view still serves the original 2,500 games, snapshot `39c5f2e31bab5d13`.

No new model or experimental setting was deployed. The published statistics remain the original 2,500-game cohort. The three-style candidate's exploratory paired-seat result is **163–157 / 320**, insufficient to demonstrate a strength improvement. The ten-hour improvement campaign remains active.

Raw evidence root: `/home/lva/.local/share/shards-training/2026-09-28/ten-hour-improvement`:

- `styles3-fresh-review/reviews/`: complete action transcripts.
- `styles3-fresh-review/evidence.json`: all 29 immediate-effect counterfactuals and complete replay validation.
- `styles3-fresh-review/behavior-counts.json`: Volos modes, Ko cancellations, Rez–Longshot coverage.
- `styles3-diagnosis/games.json`: seven positions × four search profiles, with paths and values.
- `styles3-outcome-diagnosis/games.json`: paired terminal outcomes and censorship accounting.
- `styles3-pilot/assessment.json`: paired-seat strength assessment.


## Latest audit follow-up

The latest 20-game review confirms additional sequencing failures, including a missing Numeri setup action before a Primus purchase, and an incorrect search override of a strongly preferred Focus action. See [the latest game audit](latest-game-audit-2026-09-28.md) for 49 counterfactual paths, source-preserving replay evidence, and the distinction between confirmed errors and false alarms. No candidate has been promoted. The separate nested-menu depth-2 pilot finished 165–155/320; its paired confidence interval includes 50%.
