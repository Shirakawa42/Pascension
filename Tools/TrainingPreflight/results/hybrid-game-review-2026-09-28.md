# Review of the frozen hybrid AI's games — 2026-09-28

The hybrid still makes avoidable tactical mistakes. In particular, it can end a turn with free healing, power, or useful resources available. Passing the prepared tactical battery and retaining the best match-tested model did not eliminate these errors in natural games.

## Scope and reproducibility

Reviewed 14 complete games, **3,324 decisions and 243 end turns**, from the current 2,500-game cohort. The sample covers every hero in both seats: ten stratified selections from the first 160 seeds, plus four short games. It is diagnostic, not a representative estimate of the blunder rate.

Game indices: 5, 26, 40, 53, 64, 65, 87, 90, 103, 119, 141, 142, 147, 158. Indices and action steps below are zero-based engine identifiers; rounds are the displayed game rounds.

The original frozen host and weights were replayed with the original 160-game batch size. All 14 games matched their stored seed, hero assignment, winner, round count, decision count, and override count. The original cohort did not store every action key, so those checks should not be overstated as a hash comparison of original full transcripts. The newly recorded complete transcripts were subsequently reconstructed using exact action keys, with all outcomes checked again.

A preliminary 14-game batch did not reproduce several original outcomes. Turning its recorder off reproduced that smaller-batch run exactly, ruling out the recorder as the cause in that control. Restoring the original batch size restored the selected games' recorded results. Batch-sensitive inference/search decisions remain a reproducibility concern; this audit does not establish the exact numerical cause. Findings from the divergent small-batch runs are excluded below.

All work was headless, with GPU inference and an eight-CPU affinity cap. No training, live AI setting change, game deployment, or statistics publication occurred. The dashboard still shows exactly the original 2,500 games, snapshot `39c5f2e31bab5d13`.

## Confirmed avoidable omissions

Each listed action sequence was legal in **16 independently sampled public-information worlds**. The immediate resource gains did not depend on hidden draw order. These checks establish available gains, not a quantified increase in eventual win probability.

| Position | Actual play | Available improvement |
|---|---|---|
| Game 26, round 7, Volos, step 180 | End at 38 HP with unused hero power | Hero power, healing mode: **38 → 41 HP**, no gem/mastery/power cost |
| Game 141, round 9, Tetra, step 237 | End with Shard Reactor, three Crystals and Longshot in hand | Reactor + three Crystals: **0 → 7 gems**, enabling Tetra's draw power and several purchases/fast plays |
| Game 141, round 10, Rez, step 250 | End with Infinity Shard in hand, mastery 5 | Play Infinity Shard: **1 → 3 power**, at no resource cost; extra unblocked damage still depends on defense |

The Tetra example is particularly informative: the available starter-card sequence is simple and entirely visible, yet the hybrid does not evaluate that complete resource-collection sequence as a root option. Longshot itself has hidden-card risks; the guaranteed seven-gem improvement does not require playing it.

## Other suspicious behavior

- **Ko Syn Wu, game 40, round 8, step 214:** leaves Numeri Drones ready. Exhausting it gives a free gem, increasing 2 to 3 and unlocking Korvus Legionnaire or Shadebound Sentry purchases. Whether either acquisition is strategically better needs further evaluation. The diagnostic continuation taps Numeri and immediately ends, wasting the gem, so its endpoint value is exactly equal to ending immediately.
- **Tetra, game 141, round 10, step 262:** leaves Arach Devotees and Longshot in hand. Playing Arach produces a card draw; healing varies with its faction condition. Eight-world search changes the choice to Longshot, but extra depth alone does not. This is suspicious sequencing/continuation quality, not proof that every draw is universally dominant.
- **Ko Syn Wu opens and cancels the banish menu 16 times** across five reviewed games. In game 147, round 8, this happens four times in one turn. HP stays unchanged on every cancellation, so the old pay-health-without-banishing defect is not recurring here. These redundant actions still waste search effort and make the UI needlessly slow to follow.

## What causes the end-turn errors

Seven positions were independently reconstructed and evaluated on the GPU with four variants: current profile, depth 64, policy-prior weight zero, and eight hidden-card worlds. Every current-profile diagnostic selected the recorded action. Variants were analyst-only and never changed the replay.

### The policy prior can overrule a better evaluated continuation

Volos's healing is the cleanest example. The search evaluates the correct two-action sequence, reaches the turn boundary, and gives it a higher raw value:

| Branch | Raw value | Value after policy-prior penalty |
|---|---:|---:|
| End turn | 0.707348 | 0.707218 |
| Hero power → heal → end | 0.720304 | 0.691018 |

The strong learned preference for ending reverses the ranking. Setting the prior to zero selects the hero power. Increasing depth to 64 or worlds to eight still ends. These values are model scores on −1 to +1, not calibrated win probabilities.

Rez's unplayed Infinity Shard has the same ranking reversal relative to ending: raw value −0.989968 versus −0.990530, but the prior-adjusted scores prefer ending. With no prior, search chooses a reroll instead, so removing the prior alone is not a general solution or proof of the best complete turn.

### The continuation policy misses useful sequences

Tetra's seven-gem position is not a 24-step cutoff failure. All current-profile branches reach the turn boundary within six continuation steps. Depth 64 produces the same decisions and scores.

The examined paths do things like:

- play one/two/three Crystals → spend on rerolls → end;
- play Shard Reactor → reroll → perhaps play one Crystal → reroll → end;
- play Longshot → decline its reveal → end.

None combines Reactor plus all three Crystals and then explores spending the seven gems. The current macro groups equivalent copies of one card definition; it does not search the complete turn or combine all distinct safe resource types. After the initial option, a single greedy policy continuation supplies the rest of the line. More rollout depth cannot repair a policy that chooses to stop early.

Near-saturated values are a further limitation: Tetra's relevant branch values are around +0.993, with differences of only a few thousandths or less. The experiments establish which mechanism selects each action; they do not establish that the critic accurately orders all available plans.

## False alarms and competent play

The end-turn screen raised 13 flags, several of which should not be classified as blunders:

- Rez leaves Datic Secrets unused three times in game 87. Its two-Order-allies condition is not met. Engine checks produce **zero gain**.
- Volos leaves Bleak Communion in hand in game 26, round 9. Playing it loses **4 HP** for two draws. Declining it is a legitimate tradeoff, not a free-card omission.
- Ko leaves a Crystal in game 87, round 6; playing it creates a gem but no useful legal spending action in that position.
- Ko ends game 87 with Cache Warden unplayed while delivering the actual winning attack. There is no need to play the rest of a winning hand. The limited search did not establish a missed lethal elsewhere in the reviewed end-turn positions; that is not an exhaustive lethal proof.
- Decima leaving a Crystal that would only enable a cheap permanent purchase is not automatically wrong: avoiding deck dilution can be reasonable.
- All four Volos modes occur in the sample: healing 11, power 5, draw 6, mastery 1. Volos in game 158, round 5, plays Entropic Talons before the healing mode, correctly gaining the synergy.
- No reviewed Infinity Shard at mastery 9/19/29 had an immediately available Focus action. Those threshold cases were checked instead of assuming premature play from mastery alone.
- No Rez–Longshot play occurred in these 14 games. This sample therefore does not retest that specific interaction.

## Implications

The statistics remain valid observations of this frozen AI, but they are not proof of optimal play or definitive balance. These examples show errors for both weak and strong heroes; Rez's rank cannot be explained solely from this sample.

The next improvement should target complete useful continuations and reliable rules for narrowly proven safe gains: collect compatible resources before comparing spending choices, give search alternatives beyond one greedy continuation, prevent policy-prior penalties from making demonstrably dominated end turns attractive, and avoid reopening a canceled Ko menu when no relevant state changed. Keep HP-cost draws, conditional abilities, and hidden-card risks outside blanket automation rules.

These natural-game positions should join the tactical validation gate. A global depth increase alone is not supported by the diagnosis, and another generic imitation pass could copy the same flawed search advice.

## Evidence and reproduction

- `hybrid-game-review-2026-09-28/chronological-transcripts.md`: all 3,324 decisions.
- `hybrid-game-review-2026-09-28/verified-positions.json`: visible states, alternative paths, and gains across 16 worlds.
- `hybrid-game-review-2026-09-28/search-comparisons.json`: seven positions × four search settings.
- `hybrid-game-review-2026-09-28/summary.json`: replay checks, mode counts, canceled menus, preserved statistics identity.
- Raw state/branch evidence: `/home/lva/.local/share/shards-training/2026-09-28/hybrid-review-search-diagnosis/games.json`.

The new `position-review` / `position-review-gpu` modes are offline diagnostics only. They replay exact recorded keys, run alternatives on copies, and assert that the original state fingerprint and final outcome remain unchanged.
