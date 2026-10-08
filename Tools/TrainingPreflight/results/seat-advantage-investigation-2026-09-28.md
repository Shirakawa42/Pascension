# Seat advantage investigation, 28 September

No seat-compensation change has been approved or applied. The accepted model/search remains fixed.

## Evidence

The retained 1,500 original outcomes contain 808 seat-0 wins, 691 seat-1 wins, and one draw: 53.9% seat-0 score. Equal weighting of all twenty ordered hero pairs gives 53.8%; unequal hero proportions do not explain the gap. A descriptive 95% interval is approximately 51.4–56.4%, excluding uncertainty from AI skill and early-completion selection.

Detailed evidence covers the first 1,393 original games only. No non-equivalent recovery replays are used.

| Finishing cause | Seat 0 wins | Seat 1 wins | Difference |
|---|---:|---:|---:|
| Mastery / Infinity Shard | 277 | 190 | +87 |
| Normal damage | 453 | 422 | +31 |
| Comet | 11 | 12 | -1 |
| Other health loss | 13 | 15 | -2 |

Mastery accounts for 87 of the 115 extra seat-0 wins in the detailed subset. This decomposes the observed gap; it does not prove that changing the mastery rule would recover those wins.

### Opening access

Round-one acquisition presence in the 1,393 games:

| Card | Seat 0 | Seat 1 |
|---|---:|---:|
| Order Initiate | 148 | 43 |
| Shard Abstractor | 145 | 26 |
| Shard Seer | 142 | 44 |
| Umbral Scourge | 134 | 44 |
| Duplication Fabricator | 85 | 32 |
| Cinder Scars | 50 | 121 |

Seat 0 gets the first market choice; these counts show a strong opening selection difference favoring mastery/economy tools. They do not distinguish availability from every policy preference. Order Initiate only grants mastery conditionally through Dominion; it is not an unconditional opening mastery source.

### Six-card opening changes deck cycling

The engine correctly starts seat 0 with five cards and mastery 0, seat 1 with six cards and mastery 1. Ordinary redraws are five cards. With ten-card starter decks and no intervening draw effects:

- Seat 0 has five undealt cards after its opening hand. Its second hand consumes those cards; the first reshuffle occurs when drawing its third hand, including both first- and second-turn purchases.
- Seat 1 has four undealt cards. Drawing its second hand requires an early reshuffle of the discard pile to draw the fifth card, including first-turn purchases. After the second turn, enough cards usually remain in the deck to draw the third hand without another shuffle. Second-turn purchases stay in the discard pile longer.

Observed first-play timing for acquired allies (excluding card identities that were ever recorded as fast acquisitions):

| First acquired | Seat 0 played by round 3 | Seat 1 played by round 3 |
|---|---:|---:|
| Round 1 | 410/879 = 46.6% | 996/1,187 = 83.9% |
| Round 2 | 396/949 = 41.7% | 43/851 = 5.1% |

This is a real timing tradeoff, not a claim that the extra opening card is harmful overall. Card identity, drawing effects, and selection also differ. A six-card hand strengthens first purchases but delays incorporating second purchases. That interacts with seat 0's first access to the market.

### Existing compensation is used

In a separate preserved 20-game diagnostic cohort using the accepted AI, all openings had the correct hand size and mastery. Seat 0 made 23 first-turn purchases and focused in 18/20 openings; seat 1 made 36 purchases and focused in 19/20. Both left four crystals unspent in total across those twenty openings. This small audit does not establish optimal opening play but does not suggest a systematic failure to use seat 1's resources.

In the 1,393-game detailed cohort, seat 1 reaches mastery 5 at mean round 4.095 versus 4.676 for seat 0, and mastery 10 at 7.125 versus 7.366 among players reaching those thresholds. Yet seat 0 reaches mastery 20 in 709 games versus 541, and mastery 30 in 298 versus 198. Means conditional on reaching a threshold are survivor-selected; they must not be treated as causal speed estimates.

Seat 0 starts a mean 10.308 turns versus seat 1's 9.766. The engine ends a won game immediately; it does not grant an equalizing reply turn. This mechanically advantages initiative in races, though the observed turn difference is also a consequence of who wins and includes extra-turn mechanics.

## Interpretation and recommendation

Evidence supports first access to strong opening cards, different reshuffle timing, and initiative in mastery races as contributors. It does not isolate their causal contributions or prove that the AI handles every seat equally well. No incorrect opening grant or mislabeled seat was identified in these checks.

Do not add a crystal merely because seat 1 wins less often: it already makes more opening purchases, and additional money cannot restore a card already taken. Keep the current seat rules for the first approved patch, measure again after the hero/mastery changes, then select a targeted compensation if the gap persists. Any rule intervention requires explicit user approval.
