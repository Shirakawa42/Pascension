# Why Volos is losing: September 28 frozen-AI investigation

The current 1,500-game snapshot is complete and verified. Volos wins 255/600 games (42.5%). This is a mixture of an unfavorable strategic profile and demonstrated AI mistakes, not clean evidence that Volos himself needs a large buff. No balance, training or production AI changes were made by this investigation.

## Full-cohort evidence

| Opponent | Volos wins / 150 | Win rate | First seat wins / 75 | Second seat wins / 75 |
|---|---:|---:|---:|---:|
| Decima | 70 | 46.7% | 39 | 31 |
| Tetra | 65 | 43.3% | 38 | 27 |
| Ko Syn Wu | 63 | 42.0% | 37 | 26 |
| Rez | 57 | 38.0% | 37 | 20 |

Volos: 151/300 (50.3%) first, 104/300 (34.7%) second. Both seats face exactly 75 games against each other hero, so matchup-frequency imbalance cannot explain this split. The full cohort's first-seat win rate is 55.7%; Volos is particularly sensitive to going second.

Of his 345 losses, 184 are ordinary damage, 152 mastery and 9 Comet. Against Rez, 68 of 93 losses are mastery endings. Healing cannot stop these victory conditions. Losing Volos games still average 26.2 actual HP healed, but he reaches mastery 20 in only 62/345 losses. Across all his games he reaches M20 in 242/600, at mean round 10.09 among those that reach it. This timing is conditional on reaching the threshold, not an uncensored estimate.

All four hero modes are used: free healing 2,492 times (60.8%); draw 819 (20.0%); mastery 463 (11.3%); power 323 (7.9%). These are frequencies, not causal measures of mode quality.

He chooses Talons as his main relic in 524 games, Crown in 26, Unknown God in 8; 42 games have no main relic. Talons accounts for 93.9% of actual primary relic choices and wins 227/524 (43.3%). Only 220/524 Talons games reach M20. Crown's 15/26 and God's 4/8 wins are too sparse and selected to establish that forcing them would improve him.

Compared with the prior snapshot, Volos drops from 277/600 (46.2%) to 255/600 (42.5%). The cohorts use different seeds and several rules changed together. The 3.7 percentage-point change alone does not establish a causal patch effect.

## Direct tactical checks

The exact frozen GPU host, card-effect policy and search settings used by the current snapshot passed 48/48 constructed tests: eight variants each for power lethal, draw lethal, mastery lethal, Talons-before-healing lethal, Unknown God before champion exhaust, and Crown before Infinity Shard. These tests expose immediate tactical wins; they do not establish sound nonlethal resource choices.

A stratified 16-game diagnostic replay sample covers every opponent, seat and original win/loss category. Replays reproduce 14/16 original winners and 6/16 exact action counts. They are diagnostic games, not replacements for original statistics. Each newly recorded transcript was subsequently replayed by exact action keys: all 16 full transcripts, 4,926 actions and 13,384 action-target lookups verified, including public hand-memory checks.

The three alternatives below were then executed on copies of those transcript positions in 16 sampled public-information worlds each. All paths were legal and unblocked. They do not depend on knowing the actual hidden cards.

| Diagnostic game / step | Chosen sequence | Verified alternative | Immediate improvement |
|---|---|---|---|
| 33 / 113 | With Talons active, pay 1 crystal for 2 power | Free heal, which also grants 3 power | HP 50 instead of 49; 2 crystals instead of 1; power 11 instead of 10 |
| 2 / 356 | Free heal, then play Talons already in hand | Talons, then free heal | Same HP, mastery, crystals and drawn hand; 3 extra power |
| 101 / 69 | Pay 3 crystals for 1 mastery; later finish without Focus | Focus for 1 crystal, then free heal | Same mastery and power; 2 extra crystals and 3 HP |

Other apparent flags were checked rather than assumed wrong. In games 7 and 12, using the paid mastery mode before Focus was followed by additional crystal income and an actual Focus later that turn. Both sources of mastery were used. A blanket rule forbidding the paid mode until Focus is spent would therefore be an inappropriate fix.

## Why lookahead still permits these mistakes

The paid-power error reproduces under fresh search. The network gives paid power probability 0.99128 and free healing 0.00050. The search's mean leaf value actually favors free healing (0.74019 versus 0.73018), but the policy-prior penalty reverses the final preference. Turning off that penalty picks free healing. Depth 64 and eight sampled worlds still pick the paid mode.

For premature healing before Talons, depth 64 and removing the prior do not fix the choice. Increasing the public-world sample from two to eight picks Talons first. The bounded search's sampled continuation values, rather than a missing legal option, can misrank a reliably better ordering.

For the expensive mastery choice, the best rollout style ends the turn early on both the Focus and paid-mastery branches. Their best evaluated leaves are identical (-0.07034): unused crystals have expired. This comparison fails to exploit the extra two crystals and available free healing in the better intermediate state. Neither simply increasing depth nor removing the prior corrects this case.

Relevant information is present in the frozen encoder: own health, mastery, crystals, Focus-used flag, hero-used flag and health-to-power-active flag are encoded. This establishes availability for these cases; it is not a claim that every information feature has been exhaustively audited.

A fresh regression audit reproduces the paid-power and premature-healing errors. The existing 48 tactical tests passing is insufficient coverage; nonlethal dominance and resource-preservation cases are needed.

## Additional audit after release packaging

The remaining replay flags were checked under the frozen GPU profile, depth 64,
zero policy prior, and eight sampled worlds. Both flagged end-turn choices persist
under all four settings, but neither is a proven dominance error:

- Game 33 / step 167 already owns Entropic Talons (visible in discard). The available
  mastery-10 choices are Crown and Unknown God: this is passing an **additional** relic,
  not refusing to obtain Talons. Adding a card changes future draws, so automatically
  forcing another relic would require strength evidence.
- Game 20 / step 234 correctly skips Deadly Recruits: mastery is 19, so the
  maximum eligible cost is 2. Every market card costs at least 3 (including Reactor
  Drone). Activating it produces no target menu and the same end-of-turn value.
  This is a false positive from the generic "unspent legal options" audit, not
  missed free recruitment. The larger cost-4 range unlocks only at mastery 20.
- Game 39's early paid mastery is followed by another crystal and Focus in the same
  turn. Like games 7 and 12, it legitimately uses both mastery sources.
- The full-health heal flag has only the free heal affordable at its decision menu.
  It is a wasted activation, but the flag alone does not prove a superior menu choice;
  evaluating when to activate requires considering the later card plays.

These checks preserve the distinction between suspicious play and demonstrated
inferiority. They replay all 16 diagnostic transcripts again (4,926 actions), using
public-information worlds for alternative continuations. Outputs are in
`remaining-flags-search/games.json` and `remaining-flags-command.json`.

## Interpretation and next work

There is evidence of real AI inefficiency, especially around converting healing into damage and spending crystals efficiently. It is not defensible to label the 42.5% figure optimal Volos strength. The small replay sample does not quantify how many percentage points these errors cost.

Separately, the broad results show a hero whose common healing/Talons strategy is late against mastery races and particularly vulnerable from the second seat. That structural concern could remain after AI improvements.

Before another Volos balance change: add safe dominance checks for alternatives that are demonstrably inferior; validate Talons/healing ordering through engine comparisons; extend tests beyond immediate lethals; then evaluate the improved AI under unchanged rules. Avoid globally increasing search depth or forcing a relic merely on the basis of these selected win rates.

## Reproduction artifacts

Directory: `/home/lva/.local/share/shards-training/2026-09-28/volos-investigation`.

- `summary.json`: full current/prior cohort breakdowns.
- `replay-command.json`, `replay-specs.json`, `original-replay-outcomes.json`: frozen replay provenance and original outcomes.
- `replay-16/reviews`: action transcripts.
- `counterfactual-positions.json`, `counterfactual-results.json`: legal alternative lines, 16 public worlds each.
- `search-command.json`, `search-diagnosis/games.json`: default/depth64/no-prior/worlds8 comparisons.
- `check_choices.py`: reruns diagnosis against frozen artifacts and exits nonzero while the two confirmed bad choices persist.
- `regression-verdict.json`: fresh regression result.

All diagnostics were headless, with GPU inference and affinity limited to eight CPU cores. The published 1,500-game statistics remain unchanged.
