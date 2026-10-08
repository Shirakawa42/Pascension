# Optional choices, funded Focus and another natural-game audit

Experimental only; the installed AI and published 2,500-game statistics remain unchanged. Generation-19062 weights are still in use. The preceding turn made progress by identifying a reproducible search-scoring defect and fixing the Korvus omission. This turn implements two further planner changes, validates the original positions, and reviews another 20 games. Overall strength improvement is **not yet demonstrated**.

## Changes

`OptionalChoices` is a new opt-in search setting, with candidate and reference CLI switches. Optional menus retain decline even when its model probability is zero and the candidate budget is exhausted. At controller-visible optional effect menus, the log-prior penalty is capped at six nats and scaled by the common root uncertainty `max(.001, 1 - rootValue²)`. It therefore cannot impose the former ~0.414 penalty on a float-rounded-zero alternative near a saturated value. Private reveal/Scry menu scoring is unchanged. This is an experimental regularization choice, not a mathematically optimal constant.

Both recorded Doom Gate menus now cancel under default, depth-64, no-prior and eight-world settings. The previously omitted decline in game 13 / step 266 is now searched and selected. The ordinary Infinity Shard menu retains cancellation with the default settings; removing all priors still makes that position worse. Four focused both-seat tests cover decline retention, the conflicting-prior failure, preservation of a sensible prior against a small noisy value difference, hidden-allocation invariance and source preservation. Two cases were observed failing before the fix. The broader battery passes **512/512** tactical cases.

`SetupPlanning` also repairs a planned Focus that first needs a Crystal. It only considers an initially visible neutral starter whose resource tier improves after one mastery, and only when the rollout already plans Focus. Real-engine replay checks legality and rejects draws, reveals, shuffles, turn changes and pending menus. One already-planned resource card may fund Focus before the threshold card; state fingerprints must match after normalizing the strictly improved resource pools and the newly played neutral-resource suffix order. Existing cards retain their positions. As with other resource commutation, a later seeded shuffle can produce a different permutation of the same multiset; the repair does not claim an identical future transcript. Four new cases failed before implementation; all **18/18** setup/order/privacy checks now pass.

The original game 1 / step 74 now begins **Crystal → Focus**, then replans its remaining actions. Reactor consequently yields its higher tier. All four tested search profiles choose Crystal first. The retained Korvus fix also continues to pass the recorded position under all four profiles.

## Testing before activation catches a remaining defect

Re-solving only an effect menu is insufficient because a root activation plan may cache its target. Seven root positions were therefore tested with complete default turn continuations:

- Both Doom Gate activations now cancel and retain the relic, though they still open and cancel the preview multiple times as the turn changes. These no-cost virtual steps should eventually be elided.
- The game 13 activation retains Oblivion Gatekeeper; Korvus returns it and it gets played. A later hero activation banishes a Crystal.
- **Game 12 / step 262 still caches the Infinity Shard banish.** The standalone menu would cancel, but the root continuation evaluator prefers the committed banish plan. Do not claim this tactical behavior is fixed by the optional-menu change.
- **Star Seeker still activates below mastery 20** in the recorded turn. Different first actions do not repair the eventual sequencing.

## Paired strength result

The isolated OptionalChoices ablation compares it on/off with identical weights and otherwise identical SetupPlans/guards/mixed/menu/four-style configurations. It uses **160 new seeds, paired across candidate seats**, rather than the prior exploratory seed bank.

Result: **157 wins, 163 losses / 320 = 49.0625%**. Paired-seed bootstrap 95% interval: **46.5625–51.5625%**. This gives no evidence of a strength increase. Internal time: **356.88 seconds, 0.897 games/s**; short CPU builds shared the eight-core affinity during the run, so timing is indicative. The runtime was frozen before the funded-Focus repair; the latter is not covered by this strength result. An A/A control and larger independent confirmation remain promotion requirements.

## Twenty new games

Fresh diagnostic seeds `9071000000000000000 + index`, covering every ordered distinct-hero pair: **20 games, 5,670 actions, 392 end turns**. Exact replay validates **55 alternatives × 16 public worlds**. These are diagnostic games, not an independent estimate of the patched planner's strength against another policy.

- No Doom Gate hero-banish appears. One game explicitly recruits, deploys and uses it.
- **78 Ko previews: 37 confirmed banishes and 41 cancellations.** Nonstarter targets include Infinity Shard, Wraethe Skirmisher and G-48; their long-term merit is unresolved. In the Infinity case, several Crystals were also available, so it warrants further value/sequence investigation.
- Volos modes: **35 heals, 8 power, 2 draws, 6 mastery**. Coverage is present; optimality is not established.
- Three screened Reactor-before-Focus cases had zero gems and no immediately available funding card. They are not established mistakes.
- **Game 17 / step 55:** Decima has 4 gems and mastery 4, plays Reactor, buys Systema, then Focuses. The narrow repair stops at the intervening purchase. Focus first would also unlock Decima's discount; verify and improve this separately.
- **Game 0 / step 177:** Infinity Shard is played at mastery 9 before Bulwark Chanter fulfills Dominion and raises mastery to 11. Dominion is not yet understood as an achievable preparation by the setup proposal builder. Its known own-hand reveal choices must be handled without treating unrevealed cards as known.
- A Star Seeker flag at mastery 14 is inconclusive: its warp draws supply the later mastery opportunities, and the initial hand contains only Infinity Shard.
- Every screened free-gain omission at End Turn in this cohort is followed by an actual immediate win. Other end-turn flags are paid Bound for Life or inactive conditions. An observed win does **not** prove that a sampled-world lethal shortcut is safe against every unknown opponent hand; do not use this as an exhaustive dominance guarantee.

## Value training

The 4,000-game outcome collector is running again. A live supervisor waits for its exact process and finalized manifest, then runs two bounded experiments: **16 epochs updating the value head**, followed by **8 epochs updating shared semantic features plus the value head with policy-KL restraint**. No actor imitation and no deployment are scheduled. These runs use the collector's frozen policy and completed terminal outcomes. At the time of writing, no optimizer update has started yet.

A provisional held-game calibration check on 2,420 completed games (487 represented held games, 26,317 rows) gives baseline Brier 0.1570; early-round Brier is 0.2196. Predictions below 1% win average 0.40% but win 1.92% of those sampled positions; predictions above 99% average 99.61% and win 98.57%. These are correlated positions from partial data, not independent games or an assessment of perfect play. They provide a baseline for the forthcoming completed-dataset calibration.

Artifacts under `/home/lva/.local/share/shards-training/2026-09-28/ten-hour-improvement`: `optional-choice-{diagnosis,tactics,paired}/`, `optional-funded-{root-diagnosis,tactics,fresh-review}/`, `optional-choice-{red,green-verified}.json`, `funded-focus-{red,green}.json`, `partial-outcome-calibration.json`, and `outcome-training-supervisor.json`.
