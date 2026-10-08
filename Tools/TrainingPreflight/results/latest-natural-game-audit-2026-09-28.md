# Latest natural-game audit

The experimental hybrid still performs unnecessary actions. Some strategic choices look questionable, but the completed counterfactuals must not be presented as proof of blunders. This audit is separate from the installed AI and published 2,500-game balance cohort.

## Sample and validation

Twenty fresh completed games, one per ordered distinct-hero pair, seeds `9092000000000000000 + game index`. The profile uses the semantic value model, tactical guards, mixed resource/setup/menu/Scry plans, four rollout styles, four candidates, depth 24, two ordinary worlds, eight-world selective prior verification, action-prior cap 6, choice-prior scale 2 and end-resolution extension 64. The frozen runtime includes the defensive lethal and public-world card-identity corrections.

All **6,095 actions / 414 End Turn decisions** were reconstructed from exact action keys, with terminal outcomes checked. Turn sequences in games 0, 2, 6, 10 and 17 were additionally read across both players, covering all five heroes. This is a diagnostic sample, not a representative estimate of overall blunder frequency.

The expanded screen generated **29 end-turn alternatives**, each checked in **16 public-information worlds**. A separate check reproduced **50 no-gain paths × 16 worlds**: the paths preserved measured HP, mastery, gems, power, card collections and public knowledge. Synthesis still changes its exhausted flag; canceling Ko's menu still advances wrapper/decision bookkeeping. These are not claims of byte-identical states.

Everything ran headlessly with GPU inference and the existing eight-core affinity. The expert-experience collector was temporarily suspended for serial GPU diagnostics and resumed after completion. No balance statistics were published.

## Confirmed unnecessary actions

- **Ko: 23 canceled activations out of 66 hero activations.** Every cancellation preserved HP. Game 2, round 11, opens and cancels the menu at steps 277, 281, 291 and 297, between otherwise useful plays. These are repeated unnecessary decisions, not repeated HP sacrifice.
- **Synthesis: 27 of 40 activations happened below mastery 15.** The actual effect is `AtMastery(15, Draw(1))`; these activations drew nothing. No affected turn in this sample subsequently reached 15 mastery, so a lost later draw was not demonstrated here.

Together these account for 73 decisions, about 1.2% of the sample. Their effect on total runtime or match strength has not been measured separately.

At game 2 / step 277, normal search chooses Ko's ability and then cancels. Depth 64 chooses Thornshell Warden first; eight worlds and removal of the prior still open the menu. The critic gives many alternatives values above +0.99. Redundant menu paths and weak discrimination between almost-certain wins remain concerns; one depth-sensitive example is not evidence for globally raising the budget.

## Strategic choices requiring care

| Position | Recorded choice | Diagnostic finding |
|---|---|---|
| Game 0, round 9, step 230 | Ko loses 1 HP and banishes The Dispossessed from hand | Gives up its immediate 3 power. Playing it, drawing first, and banishing are all legal root alternatives. Default/depth-64/no-prior/eight-world search still chooses the hero ability. |
| Game 2, round 11, step 303 | Ko ends with Longshot and Pall Shades in hand | Pall Shades has no draw available and no Echo power in this position; skipping it is harmless. Longshot remains a meaningful uncertain alternative. Ko wins on the following round in the recorded game. |
| Game 0, round 6, step 140 | Volos delays a relic at mastery 11 | All three relics are considered. The critic prefers ending (+0.8544) to the best recruit branch (+0.8260), despite a strong root preference for recruitment. Volos recruits Entropic Talons on the next turn. |
| Game 17, round 10, step 225 | Decima ends at mastery 10 without a relic | All three relics are considered. Ending scores about −0.9954 versus −0.9982 to −0.9990 after recruitment. Decima loses before another turn; the relic would enter discard with eight cards still in the deck. |

The relic and Longshot decisions persist with depth 64, no prior, and eight worlds. Candidate pruning is not the explanation for those specific positions. Search is comparing turn-boundary critic estimates; more depth does not extend its normal horizon beyond that boundary.

The full-game comparisons completed **448 terminal continuations with zero censoring**. Each position uses 32 paired public root worlds per opening and the same hybrid for both later players. These estimate that fixed continuation, not optimal play. Later actions are free to change: “draw first” may still banish afterward, and “delay relic” may recruit on a later turn.

| Position | Forced opening | Wins / 32 |
|---|---|---:|
| Volos 0 / 140 | End Turn | 32 |
| Volos 0 / 140 | Entropic Talons / Panconscious Crown / Unknown God | 31 / 30 / 31 |
| Decima 17 / 225 | End Turn / Praetorian-01 / Praetorian-02 / Praetorian-03 | 0 / 0 / 0 / 0 |
| Ko 0 / 230 | Hero / The Dispossessed / first Arach Devotees / second Arach Devotees | 0 / 0 / 0 / 0 |
| Ko 2 / 303 | Longshot / End Turn | 32 / 32 |

This sample does **not** establish a win-rate loss from those suspicious choices. The two Arach options are distinct physical cards, not independent evidence. For Volos, every loss after immediate recruitment is paired with a win after delaying, but only one or two worlds differ per comparison: this is insufficient to establish a general timing advantage. Uniform wins/losses in the other positions give little guidance for improving the intermediate decisions. The replay identifies the actual choice mechanism; these outcome tests do not certify its optimality.

## False alarms and coverage limits

- The skipped Synthesis at game 2 / step 252 is below mastery 15 and has no effect.
- Five skipped Forged in Flame activations in game 6 also have no effect in exact replay. The same player correctly uses it at step 162 to banish a Crystal when its prerequisites and a target are available.
- Remaining unused cards on already winning final turns do not establish mistakes.
- All four Volos modes occur: mode 0 fifty-one times, mode 1 four times, mode 2 three times, mode 3 seven times. Raw frequencies do not establish correct mode selection in every position.
- This cohort contains no Rez–Longshot play, so it does not retest that interaction. The earlier Scry regression positions remain necessary gates.
- These checks do not prove there are no missed wins or information defects.

## Audit tooling correction

The earlier warning list omitted relic and destiny recruitment when checking unused end-turn actions. `ReviewRecorder` now includes them. The new `review_natural_games.py` generates exact-key alternatives, distinguishes multiple relic choices sharing one display name, records canceled Ko menus and keeps findings separate from proven errors. `PositionReview` accepts these exact keys and includes the actor's public set-aside cards in evidence.

The host builds without warnings/errors. The updated exact-key diagnostic successfully replayed all 20 complete transcripts for both the 29 end alternatives and 50 no-gain paths. It does not modify the acting planner.

Artifacts are under `/home/lva/.local/share/shards-training/2026-09-28/ten-hour-improvement`:

- `public-identity-fresh-review/reviews/`: full transcripts.
- `public-identity-fresh-review/expanded-audit-{screen,positions,evidence}.json`.
- `public-identity-fresh-review/no-gain-{positions,evidence}.json`.
- `latest-game-position-diagnosis/games.json`: five positions × four settings, including actual turn continuations.
- `latest-game-outcomes-*/games.json`: paired full-game comparisons.
