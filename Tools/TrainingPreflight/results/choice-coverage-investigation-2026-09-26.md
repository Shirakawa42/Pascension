# Choice coverage investigation — 26 September 2026

All 15 active relics are available and have been selected and played. The eight zero-use older relic definitions are replaced in all-DLC Duel. The monitoring gallery now defaults to **Active choices**, retaining unavailable entries under **All catalog entries** with an explanation. It also filters non-purchasable starter/monster entries and the special Cloud Oracles errata replacement. Active but unseen choices remain visible.

## Evidence and limits

- [Initial pooled snapshot](choice-coverage-2026-09-26/statistics-snapshot.json): 63,604 completed random-hero games. All **90 active market definitions, 30 active destinies, and 15 active relics** have acquisition observations. This mixes changing learners and archive opponents; it is not evidence that the latest learner prefers or understands everything.
- [Natural-choice frozen probe](choice-coverage-2026-09-26/frozen-probe.json): generation **6771**, 640 complete games, all ten distinct hero matchups with both seat assignments, 256,034 sampled in-game decisions. Zero censors, no optimizer updates, no CUDA, weights unchanged. Two externally assigned legal hero draft actions per game explain the extra 1,280 wrapper actions. The learner was frozen on both seats.
- [Assigned-relic continuation panel](choice-coverage-2026-09-26/assigned-relic-probe.json): generation **6830**, 960 complete games, **64 assigned games per relic**, each against all four other heroes and both seats. Only policy A's assigned relic is imposed, at its first legal normal recruitment opportunity. Opponent and all other actions use the frozen policy. Zero censors, no optimizer updates, no CUDA, weights unchanged. All 15 relics were actually played. These are separate evaluation interventions, not added training or natural balance statistics.

Assignment is not shown to the policy before recruitment: this panel tests its continuation with the relic, not deliberate deck-building toward a known assigned relic. Some games never reach a normal recruitment opportunity. Bonus acquisitions can also grant relics. Small, conditional outcome samples cannot establish optimal relic rankings, and the two probes use different frozen generations.

## Relic coverage

Natural selections below mean **normal recruitment actions**, excluding bonus acquisition choices. Eligible games count distinct player-games where that relic was legally offered, not repeated decision menus. Assigned plays count games with an actual play, not mere recruitment.

| Relic | Natural eligible player-games | Natural normal selections | Assigned normal recruits / 64 | Assigned games with a play |
|---|---:|---:|---:|---:|
| Praetorian-01 | 223 | 90 | 53 | 50 |
| Praetorian-02 | 238 | 5 | 59 | 47 |
| Praetorian-03 | 237 | 142 | 60 | 47 |
| Datic Robes | 234 | 3 | 55 | 49 |
| Multitask Brain | 222 | 118 | 56 | 51 |
| Terminal Crescents | 233 | 112 | 61 | 53 |
| Entropic Talons | 214 | 170 | 58 | 49 |
| Panconscious Crown | 223 | 6 | 58 | 46 |
| Unknown God | 221 | 47 | 53 | 42 |
| Doom Gate | 219 | 37 | 52 | 38 |
| The Heart of Nothing | 213 | 175 | 57 | 47 |
| The World Piercer | 224 | 12 | 51 | 32 |
| Slipstream Shard | 239 | 41 | 53 | 48 |
| Star Seeker | 228 | 198 | 52 | 37 |
| Warpquartz | 243 | 3 | 52 | 39 |

All four champion relics also had actual exhausts in both probes. Zero exhausts for the other relics are normal: they are played effects. The pool's relic ranking measures acquisition/outcome association, not actual use or causal strength.

## Volos uses every mode, but has weak learned discrimination

| Mode | Legal menus | Selections | Mean probability when all four affordable |
|---|---:|---:|---:|
| Free: gain 3 health | 2066 | 1032 | 25.17% |
| Pay 1 gem: gain 2 power | 1510 | 499 | 25.05% |
| Pay 2 gems: draw 1 | 1123 | 308 | 24.94% |
| Pay 3 gems: gain 1 mastery | 830 | 227 | 24.83% |

There were **830 menus with all four modes affordable**, selecting them 189/210/204/227 times. Mean within-menu logit range was only **0.02243**. This is close to random selection on average, not evidence of sophisticated mode choice. The source audit found that the candidates differ only by ordinal/128, with no explicit categorical mode or cost/reward encoding. More forced randomness is therefore not the priority for Volos. Explicit mode representation is a candidate improvement that still needs a versioned migration and learning-quality trial.

Healing at full health is not automatically waste: Talons converts even capped healing into power. The new test executes the actual Volos heal choice with Talons and confirms this interaction.

## Other weak or unmeasured areas

- **Forged in Flame and Whatever It Takes:** zero normal destiny selections despite legal exposure in 269 and 308 player-games. They were nevertheless selected through bonus destiny choices 16 and 12 times and exhausted 52 and 1 times respectively. They are underexplored as normal choices, not entirely untested or structurally unavailable.
- **Lifebloom Ritual:** zero normal purchases, but 56 fast-plays in the same sample. Purchase and temporary-use coverage must be separated.
- **Late mastery:** Infinity Shard was played at M30+ 166 times; Slipstream at M20+ 38 times; Crown at M20+ only four times. The mastery counters are pre-action observations, not direct records of every triggered effect or successful combo. Mastery gained during the play can cross a threshold too.
- **Comet:** 26 normal purchases across 28 player-games with an affordable opportunity. Missing fast-play/free-acquisition observations are correct under its rules.
- **Large menus:** no page action was offered in the frozen natural sample. The host selftest establishes reachability of all 117 enabled options in its large-menu fixture, but this does not establish that the learned policy will search later pages well.
- **Combinations/history:** coverage of individual cards does not cover every deck combination, timing, defensive choice, opponent matchup, or information-dependent strategy. The [source audit](choice-coverage-source-audit-2026-09-26.md) records remaining representation and scenario gaps.

## Training recommendation and what changed

A **limited randomized-relic training cohort** is a worthwhile next learning experiment because normal selection concentrates on a few relics. Keep natural-choice games as well, assign only a real legal hero relic, and retain separate statistics. A candidate fraction of 10–25% is a starting range to benchmark, not an established optimum. The assigned panel implemented here supplies an executable controlled evaluation, not proof that such a curriculum improves a trained policy.

A training implementation must account for the true intervention distribution: forced actions cannot be recorded as if sampled from the unchanged learner. Keep opponent/seat balance, preserve the remaining shared 12-hour budget, and validate continuation quality and natural selection after a pilot. Destiny exploration should use legal offered options and distinguish normal from bonus acquisitions. Do not randomize every card play or every Volos mode indiscriminately.

**The live V6 training curriculum was not changed or interrupted.** Changes in this investigation are the coverage probes, the monitoring availability correction, and additional scenario tests. No optimal-meta claim follows from these results.

## Verification

- Existing source audit: 32 targeted engine cases passed; frozen V6 selftest passed replay/privacy, pagination, exhaustive small allocations and interval coverage through 1000.
- New [ChoiceCoverageTests](../../EngineVerify/ChoiceCoverageTests.cs): **15 cases passed**, covering actual Infinity Shard M29/M30, Duel Slipstream and Terminal Crescents crossing M20, Unknown God doubled own/other exhausts, Volos/Talons at full health, and empty/single/multiple Corruption relic rewards with preserved normal recruitment allowance.
- Statistics browser regression checks passed at desktop and narrow mobile widths, including replacement filtering, retained zero-coverage active entries, images, details, special Cloud Oracles replacement and non-purchasable entries. Updated HTML verified served by the live monitor.
- Both diagnostic probes reconcile complete game/seat/hero accounting and preserve frozen weights. Main training remained active with the same PID and advancing generations.
