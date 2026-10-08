# Hybrid planner implementation and evaluation

## Outcome

Implemented an opt-in hybrid planner using the **same frozen weights** as the current AI. It improves the reviewed tactical decisions and is faster on the matched headless self-play sample. The match tests do **not** establish an overall strength improvement yet, so the existing in-game default remains unchanged. No training was started, no installed Windows DLL was replaced, and no experimental games were added to the 1,400-game statistics view.

| Measurement | Current planner | Hybrid |
|---|---:|---:|
| Final fresh head-to-head, 160 games | 77 wins | **83 wins (51.875%)** |
| Both-player self-play, 80 games, first timing | 74.469 s | **63.441 s** |
| Current-AI timing repeat, identical trajectories | 80.879 s | — |
| Self-play throughput | 0.989–1.074 games/s | **1.261 games/s** |
| Final tactical trials | Earlier frozen reference: 4,602/4,608 | **4,608/4,608** |
| Previously reviewed bad EndTurn positions | EndTurn in all eight | **Alternative action in all eight** |

The final head-to-head score has a **45.625–58.125% paired-bootstrap 95% interval**. It includes 50%; calling this a proven stronger model would be unjustified. The experiment changes planning, not model weights. The earlier corrected profile scored 88–72 (55%) on a separate 160-game set, also inconclusive; it still retained one reviewed bad EndTurn. These runs are different profiles and are not pooled into a single win-rate claim.

The speed result is **17.4–27.5% higher throughput** relative to the two current-AI timings. It is a small machine-local benchmark, not a guaranteed speedup on every game or hardware configuration. The first control timing overlapped a short build; the repeat had no deliberate competing benchmark/build. Both control runs reproduced all 80 game records exactly. The hybrid played different trajectories: 23,816 decisions versus 25,920, and average ending round 10.725 versus 11.225. Thus throughput reflects both less work per decision and changed games; a shorter game is not itself proof of stronger play. Decision throughput improved from approximately 348/s and 320/s in the controls to 375/s in the hybrid.

## What changed

The implementation is in `Assets/Scripts/Shards/AI/HybridLookahead.cs`, selected through `PolicySearchSettings.Hybrid`. The legacy path remains available unchanged.

- **Restricted, effect-based symmetry:** interchangeable neutral starter copies are grouped only when their effects contain nonnegative gems/power gains, compositions, and mastery gates. Health, mastery gains, draws, custom effects, faction cards, champions, differently flagged copies and paged candidate sets are excluded from this reduction. Card names do not determine eligibility. A resource segment also stops if newly affordable actions cross the paging boundary.
- **Resource plans:** search considers playing one through all copies in a selected group. Keeping cards for banishing remains possible. A chosen multi-card sequence can be cached across decisions, with actor, round, wrapper/submission counters, legal action keys and resource eligibility rechecked. The asynchronous game adapter transfers this cache between planning snapshots.
- **Longer bounded continuations:** the tested profile considers four highest-probability strategic groups plus the sampled fallback group, with every allowed resource-prefix length within those groups, two public-information worlds, and up to 24 continuation steps after the initial action/plan. It usually reaches the next turn, but has a hard cap; it is not exhaustive full-turn search. Turn-start events also identify extra-turn boundaries correctly. Defending-player decisions are distinguished from a change of active turn.
- **Better candidate probabilities:** identical resource-copy probabilities are summed when ranking a group and selecting greedy continuations. Different physical copies no longer consume separate strategic-group slots. Macro alternatives share their group's probability mass.
- **EndTurn scoring:** the hybrid does not retain an inferior sampled choice merely because its value difference is below the old fixed 0.04 margin. Policy-confidence regularization is retained for effect menus; it tapers near saturated values at ordinary action positions. This prevents it overwhelming small estimated benefits from a productive play near a win.
- **Forced actions:** a sole non-resignation choice does not need a search. During simulated continuations, its neural evaluation can also be skipped while preserving the same primitive-step budget, path and terminal/boundary handling. Actual gameplay still submits individual actions, preserving the presentation delay.
- **No AI resignation in this profile.** Concede remains legal for human/game rules; the hybrid excludes it from its candidates and continuations.
- **Retained finishing search:** the existing 512-node narrow/wide finishing search remains. Attempts to lower its budget lost valid Volos combinations and were rejected.

This is a first conservative hybrid, not the complete exhaustive hand solver described as a possible long-term design. Complex strategic choices and most non-starter sequencing still use the learned policy/value plus bounded search. It does not introduce a second rules engine or inspect the real hidden order.

The final self-play run used 170,264 rollout branches versus the current planner's 329,304, and 1,460,498 inference rows versus 1,994,032. It reused 852 cached resource steps and bypassed 1,388 forced root decisions. Of its 170,264 leaves, 3,246 (1.91%) still hit the continuation cap before reaching a terminal/turn boundary. The finishing search remained a major cost: 28.65 of 63.44 seconds. Increasing GPU utilization is not the objective when fewer neural calls complete games faster.

## Fairness of the strength test

- Identical policy file on both sides: generation 19062, SHA-256 `f15c9d40644f6ef7e84ebf44d325ec4d6f3bae771e3f6ac7f7904a52baf4a177`.
- New `search-paired` mode runs **current all-turn lookahead against hybrid all-turn lookahead**. It does not compare search against a network-only opponent or an older checkpoint.
- 80 seed pairs, two games each; the hybrid plays seat 0 in one and seat 1 in the other. Initial board/hero assignments and per-seat sampling seeds are paired.
- All 20 ordered, distinct hero pairings are covered evenly. Heroes are forced for controlled comparison rather than selected by either draft policy.
- Final hybrid seat scores: **45/80 from seat 0, 38/80 from seat 1**.
- An A/A control put the old planner on both sides: every one of the 20 paired seeds reproduced identical winner, rounds, decisions and overrides, yielding exactly **20–20** despite seat/hero advantages.
- Confidence resamples the **80 seed pairs**, keeping each pair together. Treating all 160 games as independent would discard their correlation.
- Pilot and fresh-test seeds are separate. No intermediate score was used to stop a test early.
- No Unity, no artificial UI delay; GPU-batched FP32 inference and at most eight pinned CPU workers.

## Tactical review

Passive probes replayed the original eight games without changing their acting AI or outcomes. At the previously flagged positions, the hybrid selected:

| Game / step | Hero | Alternative to the previous EndTurn |
|---|---|---|
| 1313 / 218 | Rez | Scry |
| 1313 / 334 | Volos | Play Nil Assassin |
| 1338 / 151 | Tetra | Play Crystal |
| 1338 / 179 | Tetra | Play Crystal |
| 1283 / 436 | Ko Syn Wu | Fast-play Shadow Apostle, gaining power and opening a banish choice |
| 1402 / 201 | Ko Syn Wu | Play J Chord |
| 1402 / 241 | Ko Syn Wu | Play J Chord |
| 1418 / 177 | Tetra | Focus |

These demonstrate removal of the eight specific premature endings, not proof that each alternative is globally optimal or that every later continuation is correct. The Shadow Apostle choice also illustrates why a planner must preserve spending/banish alternatives instead of automatically emptying the hand first.

The complete tactical suite covered 32 families × 16 perturbations × 9 greedy/sampled fallbacks. Its 4,608/4,608 result includes Rez's Doom Gate/Ingeminex scry choice and all tested Volos modes. It is a regression battery, not a population estimate of tactical accuracy.

## Rejected variants and remaining work

Removing policy-confidence regularization entirely produced 46–34 in an initial 80-game pilot but failed Rez's obvious scry choice in three of 384 screening trials. That variant was rejected despite its encouraging small match score.

Reducing the finishing budget to 256 missed Volos draw/mastery wins. Explicitly retaining the wider beam at 256 and 384 nodes still missed cases; those reductions and the temporary wider-beam API change were removed. The final setting remains 512.

The final profile is faster and addresses concrete tactical defects, but a higher overall win rate has not been demonstrated. The next learning experiment should calibrate the value function on the planner's afterstates and teach verified search improvements across varied real positions, then test against this frozen reference. Training a large pile of repetitive hand-authored puzzles alone is not justified by these results. No new training budget has been consumed here.

## Validation and reproduction

- Headless engine suite: **258/258**.
- Native netstandard2.1 and headless .NET 8 builds passed.
- Resource invariants: **116 positions**, **348 observation/candidate symmetry comparisons**, all tested partial resource-prefix lengths preserved, mutable flags separated, source immutability and public-world privacy checks passed. A deliberately oversized hand also verified fallback to primitive choices when candidates require paging.
- Forced-continuation optimization: **64 differential positions** with deterministic scoring matched complete paths, depths, leaf state fingerprints, values and chosen actions. A second differential test using the real GPU model matched **64 positions / 468 leaves**, with **zero observed value difference**.
- The full tactical battery and 160-game final strength test preceded that last equivalent forced-continuation optimization; the timing test includes it. No value/selection rule changed in that optimization. The subsequent paging guard was checked with the oversized-hand regression rather than claimed as a newly benchmarked profile.
- Maximum native/GPU policy deviation in the final match test: `2.0861626e-6`, below the `1e-4` parity limit.
- The diagnostic probes preserved all eight frozen game outcomes, round counts, step counts and override counts.

Artifacts:

- [Comparison summary](hybrid-comparison-2026-09-27.json)
- [Invariant results](hybrid-invariants-2026-09-27.json)
- Frozen run directories under `/home/lva/.local/share/shards-training/2026-09-27/`: `hybrid-current-aa`, `hybrid-release-heldout`, `hybrid-release-tactics`, `hybrid-final-manual-probes`, `hybrid-speed-current`, `hybrid-speed-current-repeat`, `hybrid-speed-release`, and `hybrid-gpu-equivalence`. Each GPU run retains its runtime and source snapshot.
- Earlier candidates are retained separately as `hybrid-pilot-*`, `hybrid-v2-*`, `hybrid-v3-*`, and `hybrid-v4-*`; they are not merged into the final result.

To run another paired comparison, choose a new output directory and fresh seed:

```sh
/home/lva/.dotnet/dotnet build Tools/GpuSearchHost -c Release --nologo
/home/lva/.venvs/shards-preflight/bin/python Tools/TrainingPreflight/gpu_search.py \
  --mode search-paired --hybrid 1 --candidates 4 --depth 24 \
  --worlds 2 --prior .015 --terminal-nodes 512 \
  --games 160 --batch 80 --workers 8 --copy compiled \
  --seed 8960060000000000000 --output /tmp/shards-hybrid-new-comparison
```

Use `--mode both` with the same seed/game/batch settings for throughput comparisons; omit `--hybrid 1` and use the normal 8-candidate, depth-8 defaults for the current planner. Keep these experiments isolated from the published balance cohort.
