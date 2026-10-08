# Natural-game audit: public memory, free gains, and questionable banishes

The current experimental hybrid still makes questionable choices. This audit screened two twenty-game cohorts covering every ordered distinct-hero pairing, **12,503 recorded decisions** altogether. Suspicious positions were replayed through the native engine with public-information alternatives. This is not an exhaustive verification of every tactical sequence or proof that the experimental model is stronger. No Unity simulation, deployment, or published balance-statistics replacement occurred.

Artifacts are under `/home/lva/.local/share/shards-training/2026-09-28/ten-hour-improvement`. Game and step identifiers below are zero-based.

## Confirmed defects fixed in the development build

### Aegis Archivist's unconditional gain was discarded by the safeguard

In `terminal-semantic-fresh-review`, game 18 / step 237, the AI ended with Aegis ready, two crystals, and 14 mastery. Exhausting it gives two unconditional crystals and, in this position, three more from Dominion. `SafeTurnGains.Read` rejected the Dominion wrapper and lost the guaranteed gain preceding it.

The safeguard now accepts a nonnegative, draw-free Dominion bonus without counting that optional bonus as guaranteed. It still rejects draw effects and negative-cost bonuses. Four new cases failed before the change; the complete natural-gain suite now passes **47/47**. Replaying the original position with the semantic model now exhausts Aegis, rerolls, buys Ferrata Guard, then ends. This confirms the missed-gain correction, not optimal purchasing or a new immediate win.

Evidence: `dominion-gain-{red,green,natural-turn,integration}.json`.

### Sampled worlds forgot public opponent deck-top information

`TacticalSearch.PublicWorld` shuffled the enemy hand and draw pile together and cleared `Supplement.Top(enemy)`, even when Fabricator had publicly revealed the top card. The encoder already represented this public fact, but lookahead could contradict it.

Sampling now reserves the publicly known prefix in the opponent's draw pile, preserves its order and public memory, and shuffles only the unknown allocation. Private opponent center-Scry knowledge remains cleared. Inconsistent public prefixes are rejected. This introduces no new model inputs.

Both seats, both copiers, single and stacked public prefixes pass **8 cases × 32 sampled worlds**. Tests include source immutability and invariance under changes to unknown hand/deck allocation. Natural replay validates 30 positions × 16 worlds; 21 positions have a nonempty public opponent prefix. All preserve it and agree with the actual source state, checked offline only.

Evidence: `public-top-{red,green-verified,natural-evidence,integration}.json`.

The main build succeeds. Both original-weight and semantic-weight GPU tactical suites pass **512/512** with both fixes. An initial validation launch was rejected because a source timestamp was one second newer than the DLL; a nonincremental rebuild resolved it before successful validation. No stale binary results were accepted.

## Remaining questionable choices

### Shard Seer banished while Infinity Shard was in hand

In `terminal-semantic-fresh-review`, game 3 / step 388, Ko has 28 HP, 20 mastery, Infinity Shard and Shard Seer in hand. Lookahead activates Ko and banishes Seer, costing one HP. The policy preferred playing Seer; at the target menu it preferred cancellation.

Across 16 public worlds, playing Seer and revealing Infinity instead keeps 28 HP, draws one card, and reaches 22 mastery. Banish leaves 27 HP and 20 mastery with no draw. This is a strong tactical concern, but the alternative changes deck composition, so it is not a formal long-term dominance proof. The full recorded turn does not immediately win.

Evidence: `shard-seer-{alternatives,reveal-alternatives}.json` and the cohort's `shard-seer*-positions.json`.

### Infinity banish persists in fresh games

In the new `public-top-dominion-fresh-review`, game 14 / steps 92–93, Ko has six mastery and can banish one of three discarded Crystals. The policy assigns their combined probability about 98.94%, while Infinity receives approximately `2.8e-19`. Nevertheless the activation-plus-target plan commits to banishing Infinity.

Exact GPU diagnosis reproduces the commitment. Solving the target menu independently selects a Crystal in all four tested profiles. At the preceding activation root, expanding depth 24 to 64 or worlds two to eight retains the problematic plan. The rollout value ranks the Infinity-removal leaf around −0.666 versus −0.764 for Crystal removal in the default sample. The bounded target prior does not overcome that estimated advantage.

This isolates a short-horizon valuation / committed-plan problem rather than a missing legal action or a simple depth limit. The actor itself proposes the sensible target. Increasing search without improving how alternatives are valued is not enough here.

An additional diagnostic uses 64 paired public worlds and actual game endings under a fixed guarded greedy continuation: Infinity removal wins 14/64; each Crystal removal wins 13/64. **That does not establish a strength penalty for Infinity removal**, and the continuation is weaker than the hybrid. Do not report the suspicious banish as statistically proven to lose games, or these results as evidence that banishing Infinity is good.

Evidence: `fresh-infinity-banish-diagnosis/games.json`, `natural-choice-outcomes/games.json`.

### Stolen Futures activation delayed

Fresh games 0 / step 164 and 10 / step 316 end with usable Stolen Futures. It can transform into two destinies. Both traces activate it on the following own turn. The reason for delaying this value remains unresolved.

Fixed-policy terminal rollouts are inconclusive: immediate activation versus ending wins 4/64 versus 3/64 in game 0, and 64/64 versus 63/64 in game 10. These small differences are not strength proof.

## False alarms and coverage limits

The fresh cohort contains **6,006 actions, 415 End Turn decisions**, and 20 recorded single-action alternatives at End. Complete transcripts replay successfully with 15,478 public card-index lookups.

- Sixteen alternatives occur on actual winning final turns. Leaving those cards unused is not evidence of a missed win.
- Game 12 / step 264 leaves Doom Gate ready with no Ingeminex on the board; activating has no effect.
- Game 19 / step 300 leaves Advanced Weapons ready without satisfying its condition; activating gains no power.
- The remaining two alternatives are the Stolen Futures delays above.
- A Kiln Drone banish in game 2 / step 310 occurs on a turn that subsequently reaches 30 mastery using two Shard Seers and True Leader, then wins with Infinity. It is unnecessary-looking, but not a failed winning sequence.
- Volos uses every mode in the fresh cohort: heal 29, power 6, draw 12, mastery 8.
- There are no Rez-plus-Longshot plays in this cohort. It provides no new natural-game evidence for that specific interaction.

The prior cohort had 6,497 actions and 430 End Turn decisions; its audit and alternative evidence remain in `terminal-semantic-fresh-review`.

Public opponent deck-top preservation does not establish complete information coverage. Tracking retained publicly revealed enemy hand cards remains a separate unresolved question.

## Strength testing status

The completed 320-game planner pilot scores **171–149 (53.44%)**, paired-bootstrap 95% interval **49.06–57.81%**. It predates the latest terminal/public-top/Aegis changes and does not prove improvement. The semantic-versus-original 1,600-game comparison remains a separate running experiment with a frozen runtime predating the public-top/Aegis fixes.

That job was briefly paused for GPU validation and diagnosis and resumed in `finally` blocks. Its elapsed time is not a clean speed benchmark. The installed model and published 2,500-game snapshot remain unchanged.

Next investigation should address value misranking of irreversible choices, test improved evaluation on these exact states and fresh states, and measure paired-seat strength before promotion. Passing scripted tactical cases did not eliminate these natural-game concerns.
