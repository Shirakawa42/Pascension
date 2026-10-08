# Setup sequences and resource ordering

The experimental planner now corrects **nine of ten recorded suspect positions** under its default profile. Its exploratory paired-seat test is **164–156 / 320 (51.25%)**; this does not demonstrate a strength improvement. Nothing has been deployed and model weights remain generation 19062.

## Implementation

- Opt-in `SetupPlans` proposes short preparation sequences followed by the policy's intended payoff. Examples: deploy champions before Evokatus, activate Numeri before buying Primus, or raise mastery before a threshold effect. It reads effect structure and reviewed public counters rather than switching on card IDs.
- Setup proposals start from a sampled public world. Only initially visible own cards can supply prefix actions. After a draw/reveal, proposal construction can use a statically known mastery gain, but cannot use sampled new cards to construct later prerequisites. The live AI replans after each action.
- A narrow resource-order repair examines a sequence the planner already intended to play. It swaps a neutral starter with an already-planned affordable Focus or transparent free mastery exhaust only when real-engine replay preserves the state fingerprint after normalizing strictly improved gems/power. It rejects costs other than the planned Focus, hidden reveals, draws, opaque callbacks and differing world recommendations.
- Free-gain checks understand nonnegative Unify bonuses, a standard optional warp that can be declined, and Numeri's reviewed recruit-routing modifier. `Do.RecruitRouting` changes metadata only; its existing engine effect is unchanged. Opaque callbacks are not executed to classify safety.
- Preparation probes are shared across target actions to limit copying overhead.

## Verified improvements

Against the positions in [the preceding audit](latest-game-audit-2026-09-28.md), the default profile now:

- Focuses before Reactor in all four checked cases.
- Activates Datic before Infinity Shard to cross mastery 10.
- Deploys Systema, Star Seeker and Testudo before exhausting Evokatus.
- Activates Numeri, buys Primus directly into play, and exhausts Primus to draw two cards.
- Plays the previously unused Le’shai Knight and Brute.
- Uses Numeri's free gem in the separate end-turn omission.

The Star Seeker case is **not fixed**. The revised sequence still uses Star below mastery 20 and plays Slipstream later. Explicit preparation proposals are present, but the critic ranks an early-Star continuation more highly. Adding menu depth 1 does not repair this recorded turn either. Alternative profiles also retain occasional failures; the nine-of-ten result applies to the default configuration, not every setting.

Validation: **14/14 setup/order/privacy checks**, **33/33 gain checks**, **260/260 headless engine tests**, and **1,024/1,024 tactical/privacy cases**. The new regressions were observed failing before their respective fixes. No Unity was used.

## Fresh-game review

Generated another **20 complete games, 6,151 actions and 406 end turns**. Replayed every transcript, checked **16,019 public card-target lookups**, and tested **19 end-turn alternatives × 16 public worlds**. The diagnostic seeds are reused for comparison; this is not an independent strength sample or an exhaustive blunder-rate estimate.

Remaining examples:

- **Game 1 / step 74:** Reactor at mastery 4 with zero gems and Crystals in hand, followed by Crystals and Focus. The current repair cannot move Focus first because it is initially unaffordable. The needed sequence is Crystal → Focus → Reactor. The raw policy strongly prefers the Crystals; the search/resource macro overrides it.
- **Game 15 / step 277:** Star Seeker at mastery 18 with Slipstream already in hand; the second-warp opportunity is again lost.
- **Game 15 / step 295:** ends with Korvus Legionnaire in hand. Playing it gives **3 power in all 16 worlds**; its return-from-discard tail is not yet recognized by the free-gain guard.
- The initial screen counted **32 banish cancellations while playing Ko**. Follow-up attribution found **28 hero-preview cancellations** and four from card effects; the 28 hero cancellations paid no HP. See [the follow-up audit](manual-followup-audit-2026-09-28.md).

Several flags are legitimate: inactive destiny conditions, Bound for Life's four-HP cost, or an optional Scry. Pall Shades in game 10 / step 244 has inactive Echo and draws a Crystal; a zero hand-size/resource delta must not be mislabeled as “no effect,” because the hand's contents change. Volos uses all four modes: 32 heals, 9 power, 9 draws and 6 mastery.

## Strength and timing

The 320-game test swaps candidate/reference seats for each of 160 seeds. Candidate and reference share weights; reference uses the original hybrid settings. Results are grouped by seed, not adjacent completion order. Exact paired bootstrap limits are in `setup-complete-pilot/assessment.json` and include 50%.

The test took **264.94 seconds internally (1.208 games/s)**. A short CPU replay audit shared the same eight-core affinity during part of it; timing is indicative. The 20-game diagnostic batch took 32.26 seconds, including passive review work. This is substantially cheaper than the earlier nested-menu-depth-2 experiment, but no controlled universal speedup is claimed across different game trajectories.

Artifacts under `/home/lva/.local/share/shards-training/2026-09-28/ten-hour-improvement`:

- `setup-complete-diagnosis/`, `setup-menu1-diagnosis/`: recorded positions, four search profiles, full default continuations.
- `setup-complete-fresh-review/`: exact transcripts, screens and counterfactual evidence.
- `setup-complete-pilot/`: frozen runtime/source manifests and paired results.
- `draw-setup-green.json`, `numeri-routing-green.json`, `setup-complete-tactics/`: focused and tactical validations.

The 4,000-game outcome collector resumed after the isolated GPU tests. No optimizer updates have started during this campaign yet. The next training experiment will use completed-game returns rather than blindly imitate the remaining search mistakes. The improvement campaign remains active; published statistics and the installed AI are unchanged.
