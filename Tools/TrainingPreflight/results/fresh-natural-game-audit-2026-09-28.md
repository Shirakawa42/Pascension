# Fresh natural-game audit, 28 September

Reviewed the final candidate's 20 fresh games (5,896 actions, 399 turn endings), with detailed turn-by-turn inspection of games 1, 12 and 19 and targeted checks across the other games. These are independent seeds starting at 9105000000000000000. The installed AI and published statistics have not been changed by this review.

The preceding 800-game independent comparison passed at 456–344 (57%; paired bootstrap 95% interval 54.0–59.875%). That supports the complete model-and-search candidate against the original model/search on the same corrected engine. It does not establish that individual decisions are consistently good.

## Confirmed search coverage failure

Game 19, step 71: Rez, mastery 6, Longshot in hand, hero ability available, no known center top. The policy gives Longshot 91.8% and scry 0.194%. The four candidate slots are Longshot and three destiny choices. **Scry is never simulated.** Increasing depth to 64, removing the prior, or using eight worlds cannot repair an excluded candidate.

This is a coverage failure, not proof that scry first wins every possible continuation. Saving scry can reveal cards further into the center deck later, so it should not be made an unconditional forced action.

## Future choices and prior bias

Game 19, step 181: Rez, mastery 14, Longshot in hand, five gems, scry available, unknown center top. Scry is a candidate, but ordinary rollouts resolve its future choices greedily even though the live search enumerates complete revealed scry selections. Thus the root assesses a weaker follow-up than the live AI would use.

An experimental `FutureScryPlans` setting retains direct scry-effect activations, branches on future scry selections in actor information sets, and uses the configured larger verification-world budget for available hero scry decisions. It does not preselect actions using the actual hidden center deck. Both seats and one/eight worker configurations pass coverage, source-preservation and actual-hidden-allocation invariance checks. Existing scry, nested-menu, public-hand and hybrid-invariant audits pass, as do 512 tactical cases.

With the full experiment, step 181 becomes scry → selected bottoms → Longshot. Step 71 becomes take a destiny → Longshot → later scry; it is **not** fixed to the suggested ordering. The experiment therefore does not justify claiming that Rez sequencing is solved. A paired 320-game comparison against the preceding candidate is running; the feature remains off by default and is not deployed.

## Questionable Ko banishes

- Game 1, step 284: Ko pays one HP at 18 health to banish Limiter Drones from hand. This gives up a draw card and its possible free banish. Removing the prior chooses to play Limiter Drones; eight-world search instead plays Arach Devotees. Depth 64 alone retains the sacrifice. The baseline critic is near a saturated loss estimate (root value -0.938), and small differences between poor continuations are strongly affected by policy preference and sampling. This is suspicious; it is not a proven dominated action from the root evidence alone.
- Game 12, step 293: Ko pays one HP to banish Cloud Oracles from discard at mastery 18, against Rez at mastery 24. The deck is empty. Ko then buys Limiter Drones and ends with 32 power against 36 health. The actual game is lost to Rez's mastery victory next turn. That outcome alone does not prove the banish caused the loss.

Paired sampled-world terminal continuations are running for the first case and the two Rez cases. They estimate the fixed hybrid continuation, not optimal play.

## A plausible false alarm: not playing a draw card

Game 19, step 250: Volos ends with Thorn Zealot in hand, no draw pile and nine cards already in discard. Playing it immediately reshuffles those nine cards before the current play zone is discarded. Ending instead includes the current played cards in the reshuffle for the new hand. This is a real timing tradeoff, so automatically playing every draw card would be unsafe.

An initial shield-retention hypothesis was wrong: remaining hand cards are discarded at cleanup. There is no direct retention of Thorn Zealot's shield into the next hand. All four diagnostic settings prefer ending; terminal continuation comparison is pending.

## Other checks

- No canceled Ko hero payments in these 20 games.
- No declining a free banish and subsequently paying to remove the exact same instance; the earlier regression does not reappear here.
- All four Volos modes occur: healing 31, power 2, draw 9, mastery 9. Counts alone do not establish optimal mode selection.
- An unused Volos power at full health with zero gems is not a missed useful activation.
- An unused Soul Syphon at full health needs no activation when no healing conversion applies.
- Legal alternatives and unusual choices are screening flags, not automatic bug verdicts.

Artifacts are under `/home/lva/.local/share/shards-training/2026-09-28/ten-hour-improvement`: `final-candidate-fresh-review`, `fresh-suspects-review`, `fresh-suspects-outcomes-32`, `future-scry-natural-review`, `future-scry-verified-natural-review`, `future-scry-audit.json` and `future-scry-validation-status.json`. Commands and frozen runtime/model copies are retained with each GPU experiment.

## Statistics priority and shallower scry experiment

The user requested a fresh 2,500-game cohort. It started at 09:03:59 UTC with the independently validated 57% candidate, with FutureScryPlans disabled. The terminal-continuation and 320-game future-scry experiments were suspended to give this run priority; the statistics supervisor resumes them afterward. Their wall-clock timings include the pause and must not be used as throughput evidence.

Before suspension, the two-position depth probe completed. Future-scry depths 1 and 2 both choose scry before Longshot at game 19 / step 181; depth 0 does not. All three still play Longshot before scry at step 71. This supports testing a cheaper extension, not promoting it without whole-game evidence. `future-scry-depth-assessment.json` records the paths.
