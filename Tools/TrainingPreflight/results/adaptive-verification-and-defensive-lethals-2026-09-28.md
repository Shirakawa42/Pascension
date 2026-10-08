# Adaptive verification and defensive lethal checks

This iteration provides independent evidence that the preceding combined candidate is stronger than the baseline configuration, corrects the recorded Scry regression with selective extra sampling, and fixes a real premature End Turn that missed a win through shields. Natural games still expose unresolved decisions. No model, installed DLL, or published balance-statistics snapshot was promoted.

Artifacts: `/home/lva/.local/share/shards-training/2026-09-28/ten-hour-improvement`.

## Independent strength result

The frozen pre-Scry combined candidate scored **854–746 / 1,600 games (53.375%)**, paired-bootstrap 95% interval **51.375–55.375%**, paired sign-test p **0.001068**. There are 800 games in each treatment seat, with identical engine seeds and hero seats within each pair. Candidate seat scores are 59.5% and 47.25%; averaging both seats is essential. There are 159 improved, 105 worse and 536 neutral seed pairs.

This compares semantic weights plus the prior combined planner against original weights / baseline planner settings under the same frozen headless engine and public-world sampler. It is not an installed-application binary-versus-binary test and does not include the newer Scry, action-cap, verification or defensive-check changes. It confirms the independent direction of the exploratory 179–141 pilot. Evidence: `combined-independent-1600/assessment.json` and archived runtime/source manifests.

## Spend extra worlds only on cap-induced disagreements

`PriorVerificationWorlds`, default zero, is a new opt-in budget. The tested profile uses 8, with ordinary worlds 2 and action prior cap 6. When capping the prior changes the selected normal turn action compared with uncapped scoring, search re-evaluates that root with eight common public-information worlds. It rechecks all its candidates, does not recursively verify again, and replaces any speculative cached prefix with the verified prefix. Terminal checks still run afterward. Extra work is included in branch/step diagnostics.

The public-mastery fixture fails in two seat cases before implementation; all eight enabled/disabled cap/verification cases pass afterward. Evidence: `prior-verification-{red,green}.json`.

Both natural reproductions pass simultaneously:

- Old cohort `end-resolution-fresh-review`, game 5 / step 175: keep Cache Warden and The Rotten, Longshot before buying, reach mastery 10 and recruit a relic in the same turn.
- New counterexample `scry-action-fresh-review`, game 18 / step 143: Scry before Longshot. Two-world capped search chose Longshot; verification restores Scry. The recorded stage-232 Mining Drones / Grim Tutor sequence is unchanged and remains suspicious.

Both 512-case tactical suites pass. In a fresh 20-game cohort, verification ran on **128 of 5,022 searched decisions (2.55%)** and changed 67 choices. This cohort contains 5,713 actions / 394 End Turn decisions, with 34 alternatives replayed across 16 public worlds. It ran in 32.7 native seconds versus 27.3 for the preceding cap-only cohort, but the seeds and trajectories differ: this is not a controlled speed estimate. Recomputing eight worlds only at selected roots is much less work than eight worlds everywhere.

The frozen verification profile passed a **40-game A/A control**, 20–20 with all twenty paired outcomes/rounds/steps identical. A 320-game paired comparison against the previous combined profile is running. It uses the validated `prior-verification-fresh-review/runtime` binary, which predates the defensive changes below. Its source origin is explicitly recorded; later working-tree diagnostic files can differ.

## Diagnose Stolen Futures using full outcomes

At `scry-action-fresh-review`, game 6 / step 132, the policy gives Stolen Futures activation 99.972% probability, but the end-of-turn critic scores keeping it around +0.574 versus activating around +0.462. More depth, no prior and eight worlds still choose End Turn. Existing nested-menu depth 1/2 does not change this: the current eligible-menu predicate excludes destiny menus, so that experiment is not evidence that exhaustive destiny-pair search fails.

The offline outcome diagnostic now optionally uses the actual hybrid planner for both seats, rather than only a cheap greedy continuation. It uses paired public root worlds, each later actor's observation, real terminal results, a 2,000-step cap with explicit censoring, and source-immutability checks. It does not supply the original hidden state to either player.

| Fixed continuation | Activate | End Turn | Paired comparison |
|---|---:|---:|---|
| Guarded greedy, 64 worlds | 57 wins | 50 wins | 13 activation-only wins, 6 end-only wins |
| Current hybrid, 32 worlds | 28 wins | 28 wins | 4 activation-only wins, 4 end-only wins |

No trials were censored. The stronger-policy sample does not establish an advantage for either opening. The position remains an investigation target, not a justification for forcing every Stolen Futures activation. Evidence: `stolen-futures-outcomes-{greedy,hybrid}/games.json`.

## Reject sampled lethal claims that fail plausible hand shields

Fresh verification cohort game 0 / step 299 ended at 30 power with Thornshell Warden and Shardwood Guardian in hand. Search and the recorder accepted a sampled winning line, but the actual opponent survived the shield response. The game continued.

A minimized fixture places one possible Datic Robes in a large public opponent collection. The four ordinary samples miss it and accept End Turn as lethal, even though that hand allocation can prevent the kill. Both seat cases fail before the correction.

`TacticalSearch.DefensiveWorld` constructs an additional public-information stress case:

- Start from the existing public-world sampler.
- Preserve known opponent deck-top cards in their exact reserved positions.
- Choose the strongest possible hand shields from the remaining public hand-plus-deck multiset, preserving hand size.
- Use the real engine's shield value, including public mastery, destiny modifiers and shield doubling; passive-in-play shields are not counted as hand shields.

Winning-line validation now requires success in this defensive case as well as the ordinary sampled worlds. The hybrid also validates a sampled terminal line before giving it unconditional winning priority; previously it could retain winning priority even when the later acceptance check failed. Repeated identical paths within one ranking reuse the validation result.

This is not a proof against every hidden draw, future reveal or defensive sequence. It specifically challenges hand-shield under-sampling, using no actual hidden hand identities. Sixteen checks pass across both seats, compiled/reflection copiers, shield-ignore cases, known deck-top reservations, hidden-allocation invariance and source immutability. Evidence: `defensive-world-{red,green-verified}.json`.

The exact natural replay now plays **Thornshell Warden → exhaust for two power → Shardwood Guardian for two more power → End Turn**, reaching **34 power** and winning that turn through the recorded opponent shield response. The intervening Ko menu is canceled without HP payment. This is a verified natural win correction, not only a synthetic test. Evidence: `defensive-world-natural-diagnosis/games.json`, game 0 / step 299.

Both original- and semantic-model 512-case tactical suites pass with the defensive changes.

## Latest fresh-game audit and remaining work

Generated another 20 games with fresh seeds 9091000000000000000 onward: **6,185 actions, 412 End Turn decisions**, all full transcripts replayed; **27 End Turn alternatives × 16 public worlds** verified. Native time was 34.7 seconds. These diagnostics remain isolated from balance statistics.

The next concrete cases are:

- Rez game 10 / step 202 plays Longshot at mastery 8 before unused Scry. The raw policy strongly prefers Bleak Communion and gives both Longshot and the hero ability zero probability. Root candidate pruning is a leading hypothesis: the limited menu can include Longshot while omitting the later zero-probability hero action. Inspect actual built options before concluding.
- Ko banishes Raidian from discard (game 0 / 299), J Chord from discard (game 2 / 263), and The Dispossessed from hand (game 4 / 292). These deserve replay analysis; they are not automatically declared wrong from card names alone.
- Biotech Enhancements is unused at game 12 / 371; an exact alternative draws a card. Further continuation evaluation remains outstanding.
- Stolen Futures is genuinely available in games 16 / 198,236 and 18 / 148,180,217. Other Stolen Futures flags in game 8 occur below mastery 10 and do nothing.
- Datic Secrets, Soul Syphon and Strategic Mastermind flags in this cohort fail their activation conditions in exact replay. They are false alarms.

The earlier Legion Carrier skip is not a simple two-free-crystal dominance rule: its reveal effect also mills up to five deck cards. Earlier J Chord skips likewise need continuation evaluation, not an unconditional activation rule.

Replay positions for next work: `defensive-world-fresh-review/next-analysis-positions.json`. All source changes are reviewable in the workspace; current strengths and remaining limitations are distinct from installation or promotion.
