# Hero, relic and destiny balance review — 8 October 2026

This is a retrospective review of 3,780 completed frozen-policy self-play games. No simulation or training was run, and no rules were changed. The data contains exactly 189 games in each of the 20 ordered matchups of distinct heroes: every hero has 1,512 games, 756 in each seat, and 378 against each opponent. There are no hero mirrors. Individual hero confidence intervals use games as observations. The pooled hero regression has one row per game, not two falsely independent player outcomes.

## Results that are reasonably well supported

| Hero | Wins / games | Win rate | Wilson 95% interval |
|---|---:|---:|---:|
| Tetra | 876 / 1,512 | 57.94% | 55.43–60.40% |
| Decima | 758 / 1,512 | 50.13% | 47.61–52.65% |
| Rez | 745 / 1,512 | 49.27% | 46.76–51.79% |
| Ko Syn Wu | 720 / 1,512 | 47.62% | 45.11–50.14% |
| Volos | 681 / 1,512 | 45.04% | 42.55–47.56% |

Tetra beats every other hero: Decima 52.91%, Volos 59.52%, Ko 61.64%, Rez 57.67%. Volos loses to Decima 45.77%, Tetra 40.48%, and Ko 42.33%, but beats Rez 51.59%. Matchup scheduling cannot explain Tetra's result because all heroes face the same number of every available opponent in both seats. These results describe this search policy's play, not optimal or human play.

Seat 0 wins 1,955/3,780 = 51.72%, Wilson 95% 50.13–53.31%. This is a modest edge, not evidence for a major starting compensation change. The one-row-per-game additive hero model retains a Tetra-versus-Rez odds ratio of 1.324, 95% 1.164–1.505, controlling seat and opposing hero. It is an association under the fixed policy.

Rez is near even in this cohort. His ability is used 6.39 times/game, with 5.21 rerolls/game compared with 2.02–2.61 for the other heroes. There is no justification here for a broad Rez buff based on the old training's low score. His weakness is largely against Tetra, 42.33%. Ko uses his ability 5.84 times/game and banishes 6.08 starters/game versus 1.54–2.04 for other heroes; the activation and thinning mechanics are being used. Decima's zero recorded activations is correct because Recruiting is passive.

## Relic interpretation: a critical contamination issue

`StrategyTrace.Player.relic` stores only the normal once-per-game free choice at M10. It does not identify the first acquired relic, nor every relic used. Ingeminex Corruption grants an additional relic straight to hand, bypassing the normal limit and threshold. There are **859 player records with multiple different relics acquired**, and **101 with `relic=null` despite acquiring a relic**. A no-normal-choice group is therefore not a no-relic control.

This especially contaminates attractive-looking rare choices:

| Normal choice | Games | Wins | Games with another relic | Only-this-relic games / wins |
|---|---:|---:|---:|---:|
| Tetra: Terminal Crescents | 72 | 50 | 67 | 5 / 1 |
| Ko: World Piercer | 28 | 21 | 22 | 6 / 4 |
| Volos: Unknown God | 85 | 51 | 56 | 29 / 15 |
| Tetra: Multitask Brain | 1,190 | 734 | 80 | 1,110 / 668 |
| Volos: Entropic Talons | 998 | 485 | 101 | 897 / 408 |
| Volos: Panconscious Crown | 299 | 95 | 22 | 277 / 78 |

Another relic was acquired in a strictly earlier round than the normal choice in 44 Crescents games, 21 World Piercer games and 46 Unknown God games. Their high win rates frequently describe an already enriched deck choosing its remaining relic; they cannot be used to rank standalone relic strength.

Within-hero logistic models adjust seat, opponent hero, own M5 round, own M10 round and delay from M10 to recruitment. They do not control future game duration or final deck composition. Adjusted associations still cannot remove unrecorded deck/health/offer/strategy selection. Sparse choices with multiple-relic contamination remain particularly unreliable.

- Decima's Praetorian-03 raw advantage (56.2% versus Praetorian-01 48.7%) disappears under measured timing adjustment: standardized 52.7% versus 53.4%. Praetorian-03 is selected with mean M10 round 6.81 versus 7.59 for Praetorian-01. Do not nerf Praetorian-03 based on the crude ranking.
- Tetra: Multitask Brain 734/1,190 = 61.68%, selected in 90.08% of her normal relic choices. Measured-timing standardized association is 61.44%. Robes 25/59 = 42.37% is too small and situational to diagnose weakness conclusively. Crescents' 69.44% should not drive a nerf because of the contamination above.
- Volos: Panconscious Crown 95/299 = 31.77% versus Entropic Talons 485/998 = 48.60%. This is not merely later Crown recruitment: Crown mean M10 is 6.89 versus Talons 7.11. Timing-standardized associations are 30.46% versus 48.26%; Crown-vs-Talons adjusted odds ratio 0.440, 95% 0.331–0.584. With no additional relic acquired the observations are Crown 78/277 = 28.16% and Talons 408/897 = 45.48%. These restricted groups still have survival/choice bias but retain a large warning sign.

Crown players reach M20 in 59.5% of games versus Talons 35.1%, yet their games last longer on average (10.96 vs 10.30 rounds) and they win less. Of Crown's 95 wins, 74 are mastery and 20 ordinary damage; Talons has 65 mastery and 402 ordinary-damage wins among 485. Crown appears to commit this AI to a slow mastery route that frequently fails before conversion. The data cannot distinguish an intrinsically weak relic from a bad policy/strategy choice.

## Is Tetra's hero or her relic responsible?

The trace cannot identify that decomposition causally. Tetra's hero ability costs 3 gems to draw 2 at M5, used 4.36 times/game; the direct ability contributes 8.73 card draws/game. Tetra draws 77.21 total cards/game compared with 63.79–68.56 for Ko/Rez/Volos and 65.68 for Decima. Multitask Brain adds another repeatable faction-dependent draw-and-power engine. Of Tetra's 876 wins, 681 (77.74%) are ordinary damage and 152 are mastery. This supports testing a targeted power reduction before crippling the draw identity.

Among games where neither player ever played **any** relic, Tetra wins 52/90, Decima 59/87, Volos 42/59, Ko 31/101 and Rez 28/87. These short-game subsets are strongly selected by how the game ended and the strategies used. They must not be advertised as isolated hero-power win rates. The seemingly strong Volos early-game subset illustrates the problem with causal conclusions from such conditioning.

## Destiny synergy

Mechanical synergy exists between Multitask Brain and Agony of Choice: both reward playing multiple factions. Their observed normal-choice combination wins 76/103 = 73.79%. Other striking combinations include Slipstream Shard plus Deadly Recruits (79/108 = 73.15%) and Praetorian-03 plus Deadly Recruits (76/105 = 72.38%). Those are selected combos, not randomized comparisons. Their choice depends on the deck and early M5 timing.

Initial destiny rows were reconstructed from the frozen host setup without playing games. Every hero–destiny initial-offer association was examined, controlling seat and opposing hero. None of the 150 comparisons survives Benjamini–Hochberg correction; the smallest q is 0.803. Initial offers are shared by both sides, so this asks whether availability favors one hero relative to its opponents, not the causal benefit of taking that destiny. The large selected-combo win rates are not independently confirmed as large hero-specific effects by this initial-offer analysis.

## Doom Gate and opponent reward leakage

Current Doom Gate adds 35 Ingeminex, cycling five types equally: seven new Corruptions. It shuffles the center deck; attacks occur when those monsters are later revealed. Its immunity lasts while it is in play, and its exhaust defeat grants the controller the reward. Code confirms Corruption can therefore benefit either player if they defeat it.

In 272 Ko games where Doom Gate was played, Ko won 161 (59.19%), defeated 2.36 monsters/game versus the opponent's 1.11, and had multiple relics in 128 games versus 55 for the opponent. A reward relic was first acquired in a strictly later round than the first Doom Gate play in 59 Ko cases and 40 opponent cases; equal-round chronology is unknown. Compared with games where Ko never acquired Doom Gate, both sides have greater reward exposure, but this is not a controlled effect estimate. There is no evidence that the flood systematically rewards opponents more than Ko.

Normal-choice Doom Gate with no additional relic acquired wins only 72/186 = 38.71%. Its reward engines may be important to making it work; this condition is still post-choice and outcome-related. Do not increase monster count further from these data. More monsters would change both players' reward flow and mastery attrition.

## Suggested next experiments, separately

1. **Tetra / Multitask Brain: reduce power per faction from 2 to 1 below M20; keep the draw per faction and the M20 power tier unchanged for the first experiment.** Current rule is 2 power + 1 draw per distinct played faction, becoming 4 power + 1 draw at M20. This targets the most selected relic and Tetra's damage route without simultaneously changing her hero power. Confidence: high that Tetra is strong here, moderate that this is the best knob, unproven effect size. Watch late mastery conversion and whether Tetra simply switches to Crescents. A universal draw cap would affect many more engine interactions and is a larger first step.
2. **Volos / Panconscious Crown: add Draw 1 to its existing 2 mastery + 5 health play effect; retain its M20 Unify heal-50 tier initially.** This supports deck cycling and the mastery route rather than adding even more life to a slow losing strategy. Confidence: substantial observed weakness for the selected route, uncertain rules-versus-policy cause. Test separately from Brain; do not simultaneously buff First Aid or Talons. Monitor M30 wins and long-tail duration because the M20 healing tier can prolong games.
3. **Keep Rez and Decima unchanged initially.** Their balanced performance and functioning usage counters do not support an emergency buff/nerf. Avoid nerfing Praetorian-03 on its unadjusted rank.
4. **Defer changes to rare relics and Doom Gate pending a controlled diagnostic.** The apparent high-performing choices are mainly multi-relic decks. A test that forces a normal relic choice while logging every reward relic would be more informative than changing their numbers from this sample.

Every suggested rule change needs a paired, seat-swapped fixed-seed experiment with one knob altered, the same policy and search settings, followed by a policy-adaptation check. The present data supplies hypotheses, not guaranteed patch effects. Do not ship all suggested changes together without isolating them.

## Reproduction and metric limits

Run `Tools/BalanceReview/hero_analysis.py` with the frozen trace, catalog and optional `--openings` file. Full JSON contains all hero/matchup/seat counts, all relics, timing-stratified tables, adjusted terms, all selected destiny combinations with n≥20, Doom Gate diagnostics and initial-offer tests. The frozen catalog and current C# agree on the cited mechanics.

Counters have material limits: `healed` is actual HP restored, so it excludes at-cap healing that still converts to power through Talons. Shield totals do not equal effective damage prevented. Damage counters include ordinary overkill. First mastery/acquisition/play timestamps have round granularity. Extra turns prevent interpreting turns minus threshold-round as exact activation opportunities. No action-by-action attribution exists for card gains, relic draws or damage conversion. Additional-relic timing inside one round cannot be ordered.

## Independent mechanism audit and no-relic split

Brain's real play is added to `PlayedThisTurn` before its effect resolves (`ShardsEngine.cs:501`). Thus Brain supplies its own Order faction on a normal play. `DistinctFactionsPlayed` caps at five factions, ignores starters/None, and honors Prism and Project Yggdrasil (`ShardsDuelEffects.cs:20`). `PerCount` evaluates this count once before drawing (`ShardsEffects.cs:589`): cards just drawn do not increase the same resolution's payout. Prism can supply all five factions by itself; Brain can therefore produce 10 power + 5 draws below M20, or 20 power + 5 draws at M20.

Crown's effects resolve sequentially: gain 2 mastery and 5 health, then test M20 and Unify. It can cross M18→M20 and immediately heal50. Unify requires another Undergrowth card played or available to reveal from hand; Crown cannot satisfy itself.

**The Talons+Crown combination needs special caution.** Acquiring both occurred in 75 Volos games, with 60 wins (80%); both cards were played sometime during 70 games, with 60 wins (85.7%). This does not establish same-turn use or causal strength; obtaining two relics is a major selected advantage. Mechanically, however, Talons played before Crown supplies Unify and converts the requested5+50 healing into55power at M20, even at fullHP. A Crown consistency buff could strengthen this rare combo. Track additional-relic acquisitions and combo activations in any Crown experiment.

| Hero | No normal relic selection | No relic acquired by any route | At least one relic acquired |
|---|---:|---:|---:|
| Decima | 56/178 = 31.46% | 45/162 = 27.78% | 713/1,350 = 52.81% |
| Tetra | 67/191 = 35.08% | 44/161 = 27.33% | 832/1,351 = 61.58% |
| Volos | 50/130 = 38.46% | 35/111 = 31.53% | 646/1,401 = 46.11% |
| Ko Syn Wu | 41/222 = 18.47% | 27/200 = 13.50% | 693/1,312 = 52.82% |
| Rez | 31/150 = 20.67% | 22/136 = 16.18% | 723/1,376 = 52.54% |

These are each player's own relic status; the opponent may have relics. The no-relic groups preferentially include losses before reaching M10. They cannot isolate hero power or estimate the benefit of acquiring a relic. Earlier tables restricted to **neither player's** relic use answer a different descriptive question and remain selected.
