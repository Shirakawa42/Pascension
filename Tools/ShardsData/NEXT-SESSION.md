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
3. **`ShardsBasketPlannerBot` (kind `basket`) beats the frozen benchmark by +30 Elo.**
   Whole-turn purchase-basket search: ~21 spend-sets at each of MY turn starts (incl. the
   buy+focus/hero combo tier), leaves on determinized CRN forks, terminal-rollout scoring
   with per-world leaf-dedup, state-driven cursor execution. Two rails, each earned by a
   failed gate (v1 was 39.3%, −76 Elo): two-stage CRN refinement (screen 8/world → decide
   finalists on 24 FRESH/world) and DeviationMargin 0.05 vs the natural turn (margin → ∞
   degenerates to exactly V5: bounded downside). The measured progression, all vs
   bench:greedy-v5, all SPRT-decided: rails-only **+14 Elo** (1210 pairs) → combo baskets
   **+18** (890 pairs) → opening search enabled (MinRound 4→1) **+30 Elo — 54.2%
   [51.2–57.3], H1 at 410 pairs**, at 19 ms/decision. The opening turned out to be the
   richest domain of all once measured (`rank --min-round 1 --max-round 3`: headroom
   +0.0273/turn, sibling gaps 2× mid-game) — v1's opening losses were its biased
   same-sample argmax, not the domain.

## Hard measurements — treat these as constraints, not opinions

| Fact | Value | Consequence |
|---|---|---|
| Clairvoyance (oracle vs honest) | +38 Elo, n=1000 paired | Spend nothing on hidden info. |
| Buy vs play axis (`soisim ablation`) | buy 126–209 Elo, play 11–39 | Acquisition carries 84–92% of strength. |
| Sibling headroom at ACTION granularity | +0.002/decision, all selectors | 1-step lookahead can never beat V5, with any evaluator. Retired. |
| Sibling headroom at BASKET granularity | rollout +0.015/turn mid-game, **+0.027 opening**; static evals NEGATIVE everywhere | Search baskets, score with rollouts only; the opening is the richest domain. |
| Basket planner v1 (no rails) | 39.3%, −76 Elo | Argmax over noisy estimates deviates on noise; decide on fresh samples, require a margin. |
| Basket planner current (rails + combos + opening) | **54.2% [51.2–57.3], +30 Elo, SPRT H1 at 410 pairs** | Real. Budget barely touched (~19 ms/decision vs the 200–500 ms envelope). |
| basket-96 (2×48 deciding rollouts) | 53.2% [48.8–57.7] at n=400 | Indistinguishable from basket at this n; needs a paired basket-96-vs-basket probe at n≥1000. |
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

### 1. Gate A — CHECK THE CAMPAIGN LOG TAIL FIRST
A detached SPRT probe `basket vs bench:rollout-1200` (up to 1000 pairs) was launched
2026-07-27 ~19:05 and auto-appends its result line to campaign-log.md when it lands (log
file: `Tools/ShardsData/sim/gateA-basket-vs-rollout1200.log`). At 19 ms vs ~1,200 ms per
decision it is a ~60× compute handicap for the basket bot. If H1 accepted: run the same vs
`bench:rollout-4800`, then the formal equal-wall-clock framing (give basket the SAME
per-decision budget via a bigger RolloutsPerWorld) at n≥2000 before any mint talk. If H0:
the crossover story stands and the basket line needs more per-turn budget before retrying.

### 2. Grow the edge further (+30 → ?)
The budget is still barely used (19 ms vs the 200–500 ms envelope). Levers, cheapest first,
each gated by n=400 then SPRT vs bench:greedy-v5:
- **Deciding-rollout budget**: settle basket-96-vs-basket with a DIRECT paired probe at
  n≥1000 (vs-greedy probes cannot separate them). Then consider margin ∝ paired-diff se.
- **Wider basket space, round 2**: reroll-then-buy baskets; destiny/relic-aware baskets;
  4-item baskets late-game (economy peaks at 10+ gems).
- **Per-round margin**: the opening's true gaps are 2× mid-game — a smaller margin there
  may harvest more; measure per-round headroom-vs-margin with rank before changing.

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

1. Read the campaign-log tail: did the detached Gate A probe (basket vs bench:rollout-1200,
   SPRT) land, and which hypothesis did it accept? Everything branches on that.
2. `probe --a basket-96 --b basket --games 2000 --sprt` — settle the deciding-rollout budget
   directly (paired; vs-greedy probes cannot separate the two).
3. If Gate A skirmish won: `probe --a basket --b bench:rollout-4800 --games 2000 --sprt`,
   then the equal-wall-clock framing at n≥2000.

Do not mint any rank from the basket bot until it clears Gate A. That bar is the whole
reason this rewrite exists.
