# SoI duel AI — handoff prompt for the next session

Paste everything below the line as the opening prompt. It is written to be self-contained.

---

Continue building the Shards of Infinity **duel** AI (2 players, all DLCs incl. Duel of Doom)
on branch `soi-ai-rebuild`. Read `.claude/skills/shards-engine/SKILL.md` and
`.claude/skills/project-map/SKILL.md` first, then `Tools/ShardsData/campaign-log.md` from the
`2026-07-27` heading down — that is the full record, including every negative result.

Verify with `cd Tools/EngineVerify && dotnet test --nologo` (272 tests, all green — keep them
green). The sim CLI is `dotnet run -c Release --project Tools/SoiSim -- <cmd>`.

## The goal

The strongest possible duel bot, trainable locally, fast enough to ship with a "think longer =
stronger" knob at **200–500 ms per decision**. The training must be able to discover any
playstyle by itself rather than have one hand-coded.

## State as of 2026-07-27 evening — the three handoff problems are closed

1. **`soisim fit` is deterministic to the digit.** The wobble was ConcurrentBag collection
   order feeding gradient accumulation; samples now merge in game-seed order. Two runs are
   byte-identical, and threads=1 == threads=15 (no shared-state races). The loop can now
   adjudicate a feature change.
2. **`soisim rank` exists — the sibling-ranking harness — and it settled the planner design.**
   It builds every candidate turn's end-of-turn leaf with the bot's own code, ground-truths
   each leaf with CRN terminal rollouts, and reports cross-validated HEADROOM (truth uplift
   of replacing V5's pick; selection on one half-sample, valuation on the other).
   - Action-granularity: top-2 truth gap **0.013**, headroom ≈ **+0.002/decision for every
     selector** — nothing to win. This acquits the evaluator (L1 ranks genuinely-different
     siblings at 88–96%) and convicts the granularity; it is the real autopsy of the old
     eval planner's 12.7%.
   - Basket-granularity: mean |Δtruth| **0.060**, rollout-CV headroom **+0.012/turn**,
     static-eval headroom **negative** (L1 −0.015). Rollout-scored baskets are the ONLY
     measured-positive selector. Do not revive eval-steered planning; do not gate anything
     on holdout accuracy.
3. **`ShardsBasketPlannerBot` (kind `basket`) works and beats the frozen benchmark.**
   Whole-turn purchase-basket search: ~15 spend-sets at each of MY turn starts, leaves on
   determinized CRN forks, terminal-rollout scoring with per-world leaf-dedup, state-driven
   cursor execution. Three rails, each earned by a failed gate (v1 was 39.3%, −76 Elo):
   MinRound 4 (openings stay pure V5 — the harness never measured rounds 1–3), two-stage
   CRN refinement (screen 8/world → decide finalists on 24 FRESH/world), DeviationMargin
   0.05 vs the natural turn (margin → ∞ degenerates to exactly V5: bounded downside).
   **Gate result: SPRT H1 accepted (≥15 Elo) at 1210 pairs — 52.1% [50.4–53.8] over 1224
   pairs, +14 Elo vs bench:greedy-v5**, at ~14 ms/decision average. First planner in either
   campaign to beat tuned greedy on a properly powered probe.

## Hard measurements — treat these as constraints, not opinions

| Fact | Value | Consequence |
|---|---|---|
| Clairvoyance (oracle vs honest) | +38 Elo, n=1000 paired | Spend nothing on hidden info. |
| Buy vs play axis (`soisim ablation`) | buy 126–209 Elo, play 11–39 | Acquisition carries 84–92% of strength. |
| Sibling headroom at ACTION granularity | +0.002/decision, all selectors | 1-step lookahead can never beat V5, with any evaluator. Retired. |
| Sibling headroom at BASKET granularity | rollout +0.012/turn; static evals NEGATIVE | Search baskets, score with rollouts only. |
| Basket planner v1 (no rails) | 39.3%, −76 Elo | Argmax over noisy estimates deviates on noise; act only in the measured domain, decide on fresh samples, require a margin. |
| Basket planner v1.1 (rails) | **52.1% [50.4–53.8], SPRT H1, n=1224 pairs** | Real but small. Budget barely touched (~14 ms/decision). |
| ISMCTS crossover | ~1200 it break-even vs greedy; +115 Elo/doubling above | The wall-clock bar the basket bot must eventually beat. |
| Engine throughput | ~1050 games/s single-thread | Rollouts are cheap; the 200–500 ms envelope funds thousands. |
| Old neural ladder | −410 Elo, deleted | Do not resurrect. |

## What works and is worth keeping

- **`soisim probe`** (paired, seat-mirrored, SPRT) — the only trustworthy strength instrument.
- **`soisim rank`** — sibling-ranking harness; run it BEFORE building any new selector.
  Modes: `--siblings actions|baskets`, `--tail frozen|greedy`. Deterministic (pinned by tests).
- **`soisim fit`** — now-deterministic Texel tuning of the 22 linear eval weights. The
  evaluator's remaining use is screening/analysis, NOT steering search (measured negative).
- **`soisim coverage` / `ablation`** — blind-spot detector / axis attribution.
- **Frozen benchmark ladder** `bench:heuristic | bench:greedy-v5 | bench:rollout-1200 |
  bench:rollout-4800` — never change these.
- **Shared code discipline**: the rank harness builds leaves with ShardsBasketPlannerBot's
  own statics (EnumerateBaskets / RunToTurnEnd / RolloutToTerminal / ShardsBasketCursor).
  Keep it that way — the measured thing must be the shipped thing.

## Open problems, in priority order

### 1. Grow the basket bot's edge (+14 Elo → something worth shipping)
The budget is barely used: the search runs once per turn (~200 ms) while cursor steps are
free, so per-DECISION average is 14 ms against a 200–500 ms envelope. Levers, cheapest first,
each gated by `probe --a <candidate> --b bench:greedy-v5 --games 400 --allow-small` then SPRT:
- **More deciding rollouts** (`basket-96` kind exists: 2×48) — noise se scales 1/√n and the
  margin can then shrink; re-measure the margin/rollout pair jointly.
- **Wider basket space**: pairs+focus, triple+focus are MISSING today (V5's real turns often
  buy AND focus, so challengers are handicapped vs natural); reroll-then-buy baskets;
  destiny/relic-aware baskets.
- **Search rounds 1–3** — but ONLY after extending `soisim rank` to measure opening turns
  (`--min-round 1`); v1's collapse shows what acting unmeasured costs.
- **Margin/refinement tuning**: stage-2 CRN pairing means the margin could key off the
  PAIRED diff se rather than a constant 0.05.

### 2. Gate A — the reason this line of work exists
Beat full-rollout ISMCTS head-to-head at **equal wall-clock**, paired, SPRT, n≥2000:
`bench:rollout-1200` first (it is break-even vs greedy, so basket+14 may already beat it
cheaply — measure, don't assume), then `bench:rollout-4800`. Only after Gate A does the
basket bot deserve a rank.

### 3. Then: joint retune under the planner
V5's weights were tuned for pure-greedy play; the basket bot changes the state distribution
its own rollouts see. A CMA-ES retune with the basket bot in the loop (expensive) — or at
least re-run `soisim rank` with the retuned vector — before any mint.

## Process rules, each learned expensively here

1. **Measure strength against a FROZEN external benchmark**, never champion-vs-challenger only.
2. **Never gate on a proxy.** Validation accuracy killed the last campaign; structural
   elegance killed the clock evaluator; and holdout accuracy said nothing about sibling
   ranking — `soisim rank` exists because the proxy was measured to be the wrong question.
3. **Run the cheap disqualifying probe FIRST.** n=400 costs ~2 minutes and killed basket v1
   before a day was spent on it.
4. **n≥1000 paired with SPRT before any claim; n≥2000 to publish.**
5. **A zero in coverage is a question, not a verdict.**
6. **Distrust a good number.** The 67.5% fit accuracy was a thread-order train/test leak.
7. **Act only inside the measured domain.** Basket v1 searched rounds 1–3, which rank had
   never measured, and paid −76 Elo for it.
8. Any card/rules change updates the `shards-cards` registry, `LocFrench.cs`, and a dated
   EN+FR `Changelog.cs` entry in the same commit.

## Suggested first moves

1. `probe --a basket-96 --b bench:greedy-v5 --games 400 --allow-small` — does doubling the
   deciding sample move the point estimate? (Then SPRT if yes.)
2. Add the missing focus-combo baskets, re-run `soisim rank --siblings baskets` to confirm
   headroom rises, then gate the enlarged space.
3. First Gate A skirmish: `probe --a basket --b bench:rollout-1200 --games 400 --allow-small`
   — bench:rollout-1200 is ~break-even vs greedy, so basket+14 may already beat it at a
   fraction of the wall-clock. Quote both think times in the log line.

Do not mint any rank from the basket bot until it clears Gate A. That bar is the whole
reason this rewrite exists.
