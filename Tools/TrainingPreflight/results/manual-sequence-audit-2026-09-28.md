# Natural-game sequence audit, 28 September

Two concrete sequencing errors were reproduced and repaired in the default experimental profile with opt-in `SequenceRepairs`. The installed AI and published statistics were not changed. Whole-game strength testing is still running; these local repairs are not proof of an overall strength gain.

Reviewed the `winning-paid-fresh-review` cohort: automated screening of 20 complete games / 5,850 actions, turn-by-turn reading of games 8, 11, 14, 16 and 18, plus targeted inspection of game 3 and optional banish decisions across the cohort. These are existing recent candidate games, not 20 newly generated independent games. Candidate weights are `outcome-value-semantic-4000/value-best.bytes`, using the complete winning-paid hybrid profile in the cohort manifest. All new simulations used the native headless host, GPU inference where needed, and affinity limited to eight CPU cores.

## Confirmed mistakes

### Declining a free banish and later paying for the same card

Game 3, step 257: Ko Syn Wu declines Shadow Apostle's optional banish despite Crystal instance 216 being in discard. After Order Initiate, removing Ferrata Guard and buying J-Chord, Ko activates its hero at step 261 and banishes that exact Crystal at 262, losing one health.

Paired replay of the recorded prefix versus banishing Crystal immediately succeeded in all 16 public-world samples. The reported resulting positions differ only in health (15 instead of 14) and availability of Ko's hero ability. This is a concrete wasted payment; the comparison does not establish that either entire game is otherwise optimal.

The neural policy assigns probability 1 to the free Crystal banish. Hybrid search overrides it. Re-solving with depth 64, no policy prior, or eight sampled worlds still declines. The captured free-banish rollout subsequently uses Ko to banish **The Dispossessed**, whereas the decline rollout later banishes Crystal and retains The Dispossessed. Default mean critic values are approximately -0.97566 and -0.95326 respectively. The search compares different-quality follow-up play, so the free action receives the blame for a subsequent bad decision. Increasing depth alone does not repair this example; both continuations already reach the turn boundary.

Added exact-instance detection of declined optional banishes followed by paid Ko banishes to `review_natural_games.py`. This is a screening flag requiring replay, not a blanket rule to banish every offered card.

### Playing Infinity Shard before a mastery threshold

Game 16, step 236: Volos plays Infinity Shard at 19 mastery, then Nature Dominance, then Order Initiate and declines its removal option. Order Initiate raises mastery to 21.

Reordering the same four actions to Order Initiate → decline → Infinity Shard → Nature Dominance succeeds in all 16 paired public-world samples. Every reported resulting state field matches except power: **10 instead of 8**. The recorded game was won, but two power were unnecessarily lost. The current setup ordering repair does not eliminate this natural example.

## Suspicious but unproven

Game 14, step 299: Ko banishes Doom Gate from discard during a mastery-winning turn. Compared banish versus decline from the same 64 sampled public worlds, with the same hybrid policy playing both seats to completion. Banish won 63/64 and decline won 64/64; no trials were censored. One discordant result is insufficient to establish a strength difference. The sacrifice remains questionable, but this does not justify forbidding Doom Gate banishes in general. These figures estimate the fixed continuation policy, not optimal play.

## False alarms and coverage limits

- Game 11 / 339: skipping Focus is sensible because mastery is already capped at 30.
- Game 16 / 43 and game 14 / 195: the flagged destiny activations provide no useful gain in the inspected positions.
- The previously flagged Volos relic delay misses no draw opportunity: six cards remain before a five-card draw; recruitment occurs next turn before recycling.
- The cohort contains no Rez playing Longshot, so it provides no new evidence for the Rez-before-Longshot tactic.
- Zero canceled Ko activations and no certified inactive destiny activations in this cohort do not rule out the newly found sequence mistakes.

## Reproduction artifacts

Artifacts are under `/home/lva/.local/share/shards-training/2026-09-28/ten-hour-improvement`:

- `manual-free-vs-paid-banish-positions.json` and `manual-free-vs-paid-banish-evidence.json`: two prefixes, each replayed over 16 public worlds.
- `manual-free-banish-diagnosis/games.json`: default full-turn continuation plus default/depth64/no-prior/worlds8 root analyses and leaf paths.
- `manual-mastery-order-positions.json` and `manual-mastery-order-evidence.json`: original and reordered four-action sequences, 16 worlds each.
- `manual-doom-gate-command.json` and `manual-doom-gate-outcome-64/games.json`: exact GPU command and 128 completed terminal continuations.

Both initial CPU comparisons replayed all 20 transcripts and checked source-state preservation and public-hand memory. Comparison output reports a selected state projection, not a complete proof of internal state equivalence.

## Repair and follow-up

`SequenceRepairs.cs` validates proposed prefix changes on four public-world copies. It compares engine fingerprints, RNG state, faction counts, played-card order and public knowledge after normalizing only the specific improved resources. The paid-banish comparison additionally normalizes the retained hero use, an opaque decision-ID counter and the unordered removed-card pile. It never copies the actual hidden allocation into its probes. Resource reorderings reject draws, reveals, refills, shuffles and turn changes; a visible optional decline can be included. An already validated winning line must pass the existing independent win validator again before replacement.

The first natural repair harness failed both positions. It now selects Crystal at 3/257 and Order Initiate at 16/236. The real default GPU planner makes those choices too, as do depth64 and no-prior diagnostic variants. The eight-world diagnostic does not make the same choices; repairs intentionally require agreement between the selected sampled continuation paths. Do not claim every search configuration is fixed.

Validation so far:

- 28 focused checks pass across both seats, including hand/discard targets, exact card identity, rejecting paid previews, unresolved menus and setups without an improved mastery tier, hidden-allocation invariance and source preservation.
- Existing setup, winning-cleanup, public-hand, defensive-world, no-effect, hybrid-invariant and nested-menu audits pass.
- Original and semantic models each pass 512 tactical cases with the new flag enabled.
- The same 20 natural seeds complete with 5,863 actions and two applied repairs. All winners are unchanged. Game 16 takes one extra round because later replanning changes the remainder of the turn; this is why a local resource gain is not a whole-game strength guarantee.
- The exact Crystal decline-then-payment pattern disappears. Doom Gate's delayed banish remains, and the changed Ko continuation later banishes Shard Reactor; neither is certified wrong by this audit.
- One replay timing is 36.63 seconds versus 35.49 seconds before, about 3.2% longer. This is a single same-seed run, not a controlled throughput estimate.

`sequence-repair-paired-160` completed **80–80** against the previous complete winning-paid profile, same semantic weights and corrected engine, alternating treatment seats on 80 paired seeds. The serialized profiles differ only in `SequenceRepairs`, which activated twice. All paired winners are unchanged. The empirical paired bootstrap degenerates to [50%, 50%] because all observed pair differences are zero; this is **not** certainty that the true strength difference is zero. The repair is retained for its demonstrated local corrections, without a claim of overall strength improvement.

The native game-facing assembly also passes a complete 174-decision game with 168 background searches: synchronous/background states agree and background search leaves its source unchanged. The native .NET test reports 49.84 ms mean and 300.36 ms maximum background time under concurrent evaluation; these are not Unity/Mono timing guarantees.

The final independent 800-game comparison was launched after inspecting these results, with the newly frozen host and this flag enabled. It includes both new natural positions among its regression probes. Deployment remains pending final evidence review. Artifacts include `sequence-repair-natural-diagnosis`, `sequence-repair-fresh-review`, `sequence-sequence-repair-audit.json`, `sequence-repair-paired-160/assessment.json`, `native-sequence-full-game-smoke.json` and `final-candidate-validation-status.json`.
