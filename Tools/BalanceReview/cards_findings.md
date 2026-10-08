# Card and destiny review — 3,780 completed frozen-search self-play games

This is observational evidence for choosing balance experiments, not proof of a
card's causal contribution. The cohort has 7,560 player records; the two outcomes
within each game are complementary. All adjusted confidence intervals below use
standard errors clustered by game. No games or training were run for this review.

## Method and evidence files

`cards_analysis.py` consumes `strategy-prefix.jsonl` and the frozen `card-catalog.json`.
It produces raw ownership/acquisition tables and linear-probability estimates
adjusted for the full interaction of own hero, opposing hero, and seat. Destiny
comparisons are restricted to players choosing a normal destiny and additionally
adjust for selection round. `cards_sensitivity.py` jointly includes common early
acquisitions, and adds the first-round card profile to the destiny model.

The output files are under
`/home/lva/.local/share/shards-zero-depth/2026-10-08/balance-review/`:

- `cards_analysis.json`: full card, destiny, hero-interaction and card-pair results.
- `cards_sensitivity.json`: joint acquisition models and mechanism summaries.
- `cards_tables.csv`: sortable complete tables, including uncertainty and sample size.

Multiple-comparison correction uses Benjamini–Hochberg within each exploratory
family. This does not account for all researcher choices or make a result causal.
Only card-pair/hero-interaction samples of at least 30 are tested; card-pair candidates
must each appear in at least 150 openings. Some final-collection and lifetime
acquisition results are strongly biased by game state, game length and buying a
finisher on the winning turn. They are kept in the appendix, not treated as nerf lists.

## Strongest early-card signals

The table measures acquisition by the end of round 2, not merely owning the card at
game end. Adjusted differences compare acquiring the named card with not acquiring
it, within the same hero matchup and seat.

| Card | Players acquiring by round 2 | Win rate | Adjusted association, percentage points | 95% interval |
|---|---:|---:|---:|---:|
| Giga, Source Adept | 206 | 70.4% | +20.0 | +13.5 to +26.5 |
| Shard Abstractor | 613 | 57.1% | +8.3 | +4.2 to +12.5 |
| Shard Seer | 631 | 57.4% | +7.7 | +3.6 to +11.8 |
| Order Initiate | 720 | 57.1% | +7.2 | +3.4 to +11.0 |
| Breaker | 83 | 67.5% | +18.8 | +8.7 to +28.9 |
| Duplication Fabricator, Duel | 426 | 57.3% | +7.3 | +2.4 to +12.2 |
| J-Chord, Duel | 187 | 61.0% | +10.1 | +2.9 to +17.3 |

All these by-round-2 associations pass the within-family exploratory correction.
In a simultaneous model of the 54 common early acquisitions, all seven remain
positive and pass that model's correction. Giga remains +20.6 points, suggesting its
signal is not explained merely by which other common early cards accompanied it.

Round-1-only results also identify Giga (154 players, 70.1% wins, +19.4 points),
Order Initiate (564 players, 59.2%, +8.9 points), and Shard Seer (487 players, 57.1%,
+7.1 points). All three pass the round-1 correction. Giga opening owners win at
least 60% with each hero in this sample, but the individual hero samples are small
(34–50 by round 2); no specific hero interaction is established.

Giga's current cost is 2, defense 4; it draws one when **played**, and its exhaust
gives 3 mastery under Dominion. Buying it normally puts it in the discard. It does
not draw immediately on acquisition, and the champion recruit-directly-to-play
route also does not execute its play effect. It is an early investment in a repeatable
mastery engine, not an immediate purchase cantrip. Of the 145 wins with early Giga,
77 were mastery victories and 63 ordinary damage victories; it supports more than
one route to winning.

Acquisition estimates alone cannot separate the card from initial market quality,
opening resources or skillful acquisition decisions. The parent review separately
reconstructed the exact initial setups from the frozen host. Giga appeared in 117
initial markets; seat 0 acquired it in round 1 in all 117, and won 82/117 (70.1%),
versus 51.1% when it was absent. Adjusting ordered matchup and both players' opening
gems gives +18.1 percentage points [9.4, 26.8], BH q=.004 across 90 market definitions.
That offered-card check avoids conditioning solely on a player's decision to buy.
It still describes this AI policy and the same cohort, not an independent test set.
Initial J-Chord and Shard Abstractor offers also have positive corrected associations
in `openings_analysis.json`.

## Destinies

| Normal destiny chosen | Players | Raw win rate | Adjusted association, pp | 95% interval |
|---|---:|---:|---:|---:|
| Deadly Recruits, Duel | 752 | 64.5% | +11.0 | +7.2 to +14.8 |
| Unconditional Conscription | 424 | 42.0% | −7.9 | −12.8 to −3.0 |
| Soul Syphon, Duel | 539 | 44.5% | −7.1 | −11.5 to −2.6 |
| Strategic Mastermind | 615 | 58.4% | +4.8 | +0.6 to +9.0 |
| Nature Dominance | 338 | 42.6% | −6.8 | −12.2 to −1.3 |

The first three pass the within-destiny correction; Strategic Mastermind and Nature
Dominance do not. Deadly Recruits remains +10.7 points after including the common
round-1 card profile. Its raw win rate is elevated for all heroes: Decima 66.0%,
Ko Syn Wu 62.6%, Rez 69.6%, Tetra 65.6%, Volos 58.9%. This is evidence of a general
destiny advantage rather than one isolated hero combination, though acquisition
priority and deck quality can still confound it.

Deadly Recruits is chosen in mean round 3.96. For its 752 normal owners the trace
contains 5,013 exhausts, but only 3,043 explicit card-mode choices: 2,554 fast-plays
and 489 recruits. These are not identical denominators: an exhaust can have no
eligible row target. The fast-play branch supplies about 84% of recorded resolved
choices, so a nerf limited to keeping/recruiting cards would miss most observed use.

Soul Syphon's actual Duel text gives **7 health**, not the base card's 5. A raw
activation count does not prove that its three-faction condition succeeded, and
the trace does not attribute healing to each source. Its weakness could partly be
the value of healing against mastery wins, the restriction, or AI choice of this
destiny when better suited alternatives existed. Those are different hypotheses.

## First-round fast-play: what is identifiable

The trace's `first_acquired_round` combines normal and fast acquisition. Its
`fast_acquired` counts are lifetime totals. `first_played_round` does **not** resolve
the ambiguity: the engine's hand-play path emits `ShardsCardPlayedEvent`, while the
immediate fast-play path records the buy and resolves its effects without that
event. Treating `first_acquired_round == 1` plus any later fast-play as an opening
fast-play would introduce false positives.

A guaranteed subset exists when a card was first acquired in round 1 and **all**
its acquisitions in that game were fast. The upper bound adds mixed-mode games
that first acquired it in round 1 and fast-played at least one copy at any time.

| Card | Guaranteed first-round fast-play players | Possible upper count | Wins in guaranteed subset |
|---|---:|---:|---:|
| Shadow Apostle | 245 | 275 | 50.2% |
| Umbral Scourge | 68 | 104 | 39.7% |
| Cinder Scars, Duel | 53 | 95 | 52.8% |
| Shard Abstractor | 32 | 94 | 53.1% |

None of these four adjusted associations is significant; smaller samples are even
less informative. Conditioning on all later acquisitions being fast also selects a
special subset of playstyles. These observations do not justify a blanket early
fast-play nerf or establish that it is harmless. There is no exact treatment-time
exposure denominator in this trace. Record an acquisition event's round and mode
directly in future cohorts.

## Synergies, weak openings and survival bias

There were 221 tested early card-by-hero interactions and 119 supported early
card-pair interactions. **None survived multiple-testing correction.** Some raw
pair rates look dramatic, but it would be easy to turn sampling noise into a
supposed broken combo. Decklist examples can suggest mechanisms but cannot supply
the missing sample size.

The early-purchase low end includes Fao Cu'tul (11 wins in 43, 25.6%), Furrowing
Elemental, Duel (19/61, 31.1%), and The Dispossessed (55/146, 37.7%). Their adjusted
associations are negative and pass the single-card exploratory correction, but
they are lower priority than Giga: Fao is explicitly a mastery-20 payoff, Furrowing
requires high health and costs 5, and buying an aggressive low-cost card early can
be an AI strategy error rather than a universally weak card. Do not infer that
these cards should be stronger at every game stage.

Lifetime acquisition is especially misleading for finishers: Comet owners win
78.7% (202), Scion of Nothingness acquirers 70.5% (1,111), Shadebound Sentry 72.1%
(818). A player already about to win can acquire a fast-play finisher and win before
the opponent acts. These figures cannot establish that any of those cards caused
an unfair advantage or show their win rate when offered in comparable positions.

## Sequential tuning candidates

1. **Giga: Dominion exhaust mastery 3 → 2.** Strongest early card signal, across
   heroes and after adjusting other early acquisitions. Preserve its cheap champion,
   draw and Dominion identity. Test this alone first; do not simultaneously raise
   its cost, lower defense and cut mastery. Cost 2 → 3 is an alternative tempo test,
   but not an access fix: every starting seat-0 hand in this cohort can generate at
   least 3 gems, so the higher cost would chiefly compete with Focus/other purchases.
   Giga currently has no Duel replacement; a Duel-only balance patch would need one
   to preserve the original base-set card outside this ruleset.
2. **Deadly Recruits: activation 0 → 1 gem**, preserve fast-play/recruit choice and
   the cost-2/cost-4 thresholds. Targets its repeatable free value, including the
   dominant fast-play use, without removing the mechanic. It is a provisional
   experiment, not a measured prediction of the resulting win-rate shift.
3. **Unconditional Conscription: required cheap non-starter allies 2 → 1; power
   4 → 2.** This is a two-number redesign of one conditional reward, not a straight
   damage buff: make its small aggressive payoff reliable and keep it below Power
   Struggle's 6. Lower-confidence option, after checking how often the existing
   condition actually succeeds.
4. **Soul Syphon: require 2 factions instead of 3 and heal 5 instead of 7.** Another
   optional reliability redesign. It reduces the exceptional heal while making
   the ability useful on ordinary turns. First establish effective trigger frequency;
   if losses predominantly come from mastery, more reliable healing alone may fail.
5. **Furrowing Elemental: cost 5 → 4** is a lower-priority test if weakness persists
   outside bad openings. Preserve current Duel heal4/draw1/full-health power4. The
   61-opening sample and costly-card acquisition bias are insufficient for immediate
   deployment. Fao and The Dispossessed should remain unchanged pending stage-aware
   evidence.

Order Initiate, Shard Seer and Shard Abstractor warrant tracking, but nerfing the
entire Order/mastery package together would prevent learning which component
causes the imbalance and risks ruining mastery as a strategic alternative. A
successful focused Giga adjustment could change the package without requiring
further cuts.

No new card is necessary to address the strongest current evidence. If a new
countermechanic is later desired, a modest damage/reroll incentive against an
opponent ahead in mastery is a clearer hypothesis than broad anti-healing. It must
be evaluated against both mastery races and ordinary damage games before design
numbers are fixed.
