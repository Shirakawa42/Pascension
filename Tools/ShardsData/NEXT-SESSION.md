# SoI duel AI — handoff prompt for the next session

Paste everything below the line as the opening prompt. It is written to be self-contained.

---

Continue building the Shards of Infinity **duel** AI (2 players, all DLCs incl. Duel of Doom)
on branch `soi-ai-rebuild`. Read `.claude/skills/shards-engine/SKILL.md` and
`.claude/skills/project-map/SKILL.md` first, then `Tools/ShardsData/campaign-log.md` from the
`2026-07-27` heading down — that is the full record, including every negative result.

Verify with `cd Tools/EngineVerify && dotnet test --nologo` (266 tests, all green — keep them
green). The sim CLI is `dotnet run -c Release --project Tools/SoiSim -- <cmd>`.

## The goal

The strongest possible duel bot, trainable locally, fast enough to ship with a "think longer =
stronger" knob at **200–500 ms per decision**. The training must be able to discover any
playstyle by itself rather than have one hand-coded.

## Hard measurements — treat these as constraints, not opinions

| Fact | Value | Consequence |
|---|---|---|
| Clairvoyance (perfect-info oracle vs identical honest agent) | **+38 Elo**, n=1000 paired | Hidden info is nearly worthless. Spend nothing on belief modelling. Deck *contents* are public; only the shuffle is hidden. |
| Buy axis vs play axis (`soisim ablation`) | **buy 126–209 Elo, play 11–39** | Acquisition carries **84–92%** of strength. Search should go wide over purchase baskets, narrow over play orderings. |
| Axes are co-adapted | interaction −15 to −82 Elo | Never ship a mismatched play/buy pair; tune them jointly. |
| ISMCTS crossover | 300 it = 21.2% vs instant greedy; ~1200 it = break-even; then ~115 Elo/doubling | Below the crossover, more search is **worse than none**. |
| Overwhelm (M30 + Infinity Shard) wins | **51.1%** for tuned V5, **5.7%** for the heuristic, identical rules | The mastery race is a property of GOOD PLAY, not of the ruleset. Confirmed by human match history (both humans Focus 5–10×/game, M10 by round 7–9). |
| Engine throughput | 1051 games/s single-thread, 287 submits/game (~3.3 µs/submit), 11.4× on 15 threads | Training throughput is not the bottleneck. |
| Old neural ladder | shipped DIAMOND scored **8.5% vs BRONZE** (−410 Elo) | Deleted. Do not resurrect. |

## What works and is worth keeping

- **`soisim fit`** — logistic regression (Texel tuning) of ~22 weights over analytic features
  to game outcomes. 152k positions in <3 s. Convex, no generations, no collapse. Beats the
  health+mastery baseline every run (63–65% vs 62.1%). `ShardsEvalFeatures` +
  `ShardsLinearEval` + `ShardsEvalLinearWeights.g.cs`.
- **`soisim coverage`** — finds what a policy NEVER does (actions, decision branches, hero
  abilities, cards). It has already caught six dead branches that win-rate testing cannot see,
  because a blind spot shared by both seats is invisible to win rate by construction.
- **`soisim ablation`** — buy-vs-play Elo attribution.
- **`soisim probe`** — paired, seat-mirrored, SPRT. The only trustworthy strength instrument.
- **Frozen benchmark**: `bench:heuristic`, `bench:greedy-v5`, `bench:rollout-1200`,
  `bench:rollout-4800`, pinned by tests including a 353-move play fingerprint.
- `ShardsDeckStats` — analytic per-deck rates (gems/power/mastery/draws per turn, D as a fixed
  point, faction composition, Unify liveness, board output kept OUTSIDE the deck cycle).

## The three open problems, in priority order

### 1. The planner does not work, and the evaluator is not why
`ShardsPlannerBot` scores **12.7%** against instant greedy. Improving evaluation accuracy from
58.1% to ~64% moved it only 11.7% → 12.7% — nine points of evaluator bought one point of play.
So the search design is wrong, not what it steers by.

Current design: for each candidate action, fork → submit → complete the turn with free
(non-gem-spending) actions → evaluate the end-of-turn leaf. Two ideas not yet tried:
- **Rank the tuned policy's top-k candidates instead of replacing it.** V5 is a strong
  hand-tuned policy refined over 1.2M games; replacing its entire action selection with a
  1-step lookahead on a 64%-accurate evaluator plausibly loses more than it gains. ISMCTS
  *uses* V5 as prior and rollout policy and beats it above 1200 iterations.
- **Enumerate complete purchase BASKETS**, per the original plan — that is where 84–92% of the
  strength was measured, and two baskets differ substantially at the leaf, whereas
  first-action-plus-greedy-tail differences wash out.

### 2. `soisim fit` has ~1 point of unexplained run-to-run variance
Raising epochs 400 → 4000 narrowed it but did not remove it, so something in the parallel
collection is not reproducible. **Until this is fixed the loop cannot adjudicate a 2-point
feature change.** Suspects: shared `ShardsValueModel` across threads, `ConcurrentBag` ordering
feeding gradient accumulation in a different order each run, lazy caches in `ShardsCardStatics`
or `ShardsState.FindCard`. Fix by making collection deterministic (per-thread ordered buffers
merged by game seed) and confirming two runs agree to the digit.

### 3. Mid-game prediction has a low ceiling — know what the metric can and cannot say
~40% of wins are comebacks, so a static evaluator scoring mid-game positions caps somewhere
near 65%. Accuracy is a fast *screening* tool, not a strength gate. The real question for a
planner is whether the evaluator RANKS SIBLING candidate turns correctly, which is not the same
thing and is not yet measured. Consider building that harness before tuning the evaluator
further.

## Process rules, each learned expensively here

1. **Measure strength against a FROZEN external benchmark**, never champion-vs-challenger only.
   Non-transitive cycling is what let nine neural generations all read ~50% while the shipped
   agent was −410 Elo.
2. **Never gate on a proxy.** Validation accuracy killed the last campaign; structural elegance
   killed my clock evaluator (58.1% against a naive two-term baseline's 64.7%). Gate on
   outcomes.
3. **Run the cheap disqualifying probe FIRST.** New agent vs `bench:greedy-v5` at n=400 costs
   ~6 seconds and would have saved the previous campaign six days.
4. **n≥2000 paired with SPRT before any claim.** n=120 unpaired has a ±9 point half-width.
5. **A zero in coverage is a question, not a verdict.** Investigate, then measure. Rez's dead
   Scry was measured at +3 Elo and correctly left alone; Ko Syn Wu's needed a structural fix.
6. **Distrust a good number.** A 67.5% accuracy that looked great was label leakage from a
   thread-order-dependent train/test split.
7. Any card/rules change updates the `shards-cards` registry, `LocFrench.cs`, and a dated EN+FR
   `Changelog.cs` entry in the same commit.

## Suggested first moves

1. Fix problem 2 (fit determinism) — everything downstream depends on trusting that number.
2. Build the sibling-ranking harness from problem 3.
3. Then attack problem 1 with the "rank the tuned policy's top-k" idea, gating on
   `probe --a <new> --b bench:greedy-v5 --games 400 --allow-small` before anything else.

Do not ship any evaluator into a bot until it beats full-rollout ISMCTS head-to-head at equal
**wall-clock**, paired, SPRT, n≥2000. That bar is the whole reason this rewrite exists.
