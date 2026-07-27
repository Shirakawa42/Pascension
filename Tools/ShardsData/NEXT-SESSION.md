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
3. **`ShardsBasketPlannerBot` beats the frozen benchmark by +31–45 Elo; champion kind is
   `basket-96`.** Whole-turn purchase-basket search: ~30 spend-sets at each of MY turn
   starts (v3 space: combo tier + feasible pairs + late quad + reroll-then-buy), leaves on
   determinized CRN forks, terminal-rollout scoring with per-world leaf-dedup,
   state-driven cursor execution. The load-bearing pieces, each earned by a failed gate:
   **successive-halving stage 1** (cheap CRN look at the whole field → top half earns
   ~1.5× evidence; sublinear in field size — the flat screen's noise-argmax is what
   killed space growth), **fresh-seed stage 2** (selection never peeks at the deciding
   sample; v1 without this was −76 Elo), **DeviationMargin 0.05** vs the natural turn
   (margin → ∞ degenerates to exactly V5: bounded downside), **MinRound 1** (the opening
   measured as the RICHEST domain: +0.0273/turn headroom, sibling gaps 2× mid-game).
   Ladder vs bench:greedy-v5, every rung SPRT-decided: +14 → +18 (combos) → +30 (opening)
   → **+45 (halving, v2 space, H1 at 250 pairs)** → +31–45 (v3 space; indistinguishable
   from v2+halving at current n, kept on harness headroom +0.0208 vs +0.0149 and the
   higher screen). **~29 ms/decision** — under a tenth of the think budget.

## Hard measurements — treat these as constraints, not opinions

| Fact | Value | Consequence |
|---|---|---|
| Clairvoyance (oracle vs honest) | +38 Elo, n=1000 paired | Spend nothing on hidden info. |
| Buy vs play axis (`soisim ablation`) | buy 126–209 Elo, play 11–39 | Acquisition carries 84–92% of strength. |
| Sibling headroom at ACTION granularity | +0.002/decision, all selectors | 1-step lookahead can never beat V5, with any evaluator. Retired. |
| Sibling headroom at BASKET granularity | rollout +0.015/turn mid-game, **+0.027 opening**; static evals NEGATIVE everywhere | Search baskets, score with rollouts only; the opening is the richest domain. |
| Basket planner v1 (no rails) | 39.3%, −76 Elo | Argmax over noisy estimates deviates on noise; decide on fresh samples, require a margin. |
| Basket planner ladder vs greedy-v5 | +14 → +18 → +30 → **+45** (halving) → +31–45 (v3 space) — all SPRT H1 | Measure → open → gate works. Champion: basket-96, ~29 ms/decision. |
| basket-192 / 192m / 96m vs basket-96 | 49.9% · 47.0% · 47.5% — all dead (flat-funnel era) | Rollout/margin knobs at optimum. ⚠ measured BEFORE halving landed — may deserve ONE re-screen under the new funnel. |
| Flat screen vs halving funnel | v3 space: 49.5–51.5% flat vs 58.0% halving (screens) | `rank` measures spaces under an IDEAL selector; the bot pays for selection. A richer space needs a funnel that scales. |
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

### 2. Grow the edge further — the SPACE is the only live lever left
The rollout/margin knob family was mapped and CLOSED on 2026-07-27 (all head-to-head vs
basket-96; the only probe kind that separates configs):
- basket-96 > basket: **+30 Elo, SPRT H1 at 470 pairs** (48→96 deciding rollouts pays fully);
- basket-192 raw **49.9%/630 pairs** · basket-192m (margin ∝ se) **47.0%** · basket-96m
  (smaller margin, same rollouts) **47.5%** — all dead at 200-game screens. The current
  basket space's headroom is FULLY harvested at 96 rollouts / 0.05 margin; looser filters
  admit only noise-deviations. Champion config: **basket-96**. Do not re-tune these knobs.
The successive-halving funnel LANDED and the v3 space is LIVE (see the ladder above) —
the "sublinear funnel first" prerequisite is done. Remaining growth candidates, all
200-game-screened first:
- **Re-screen the deciding-rollout ladder under halving** (basket-192 died in the
  flat-funnel era; the funnel change may have moved the optimum — one screen answers it).
- Space v4 ideas: destiny/relic-prescribed baskets (currently free-action greedy),
  multi-reroll baskets late-game, per-round margins (opening gaps are 2× mid-game).
- Separate v2-vs-v3 space properly only if it ever matters: the two are indistinguishable
  at current n and the next lever will likely change both.
Coverage flags to fix at a deliberate benchmark freeze point (they move the SHARED
ChooseAnswer — re-run key probes after, like the four-decisions fix): `soi.scry` options
never taken (Rez pays 1 gem then always keeps — a live no-op now that baskets fire his
ability 7.2/game) and `soi.reveal` always picks option 0 (unhandled default).

### 3. Then: joint retune under the planner
V5's weights were tuned for pure-greedy play; the basket bot changes the state distribution
its own rollouts see. A CMA-ES retune with the basket bot in the loop (expensive) — or at
least re-run `soisim rank` with the retuned vector — before any mint.

## Process rules, each learned expensively here

1. **Measure strength against a FROZEN external benchmark**, never champion-vs-challenger only.
2. **Never gate on a proxy.** Validation accuracy killed the last campaign; structural
   elegance killed the clock evaluator; and holdout accuracy said nothing about sibling
   ranking — `soisim rank` exists because the proxy was measured to be the wrong question.
3. **Run the cheap disqualifying probe FIRST — for EVERY matchup, 200 games before
   anything bigger** (user directive 2026-07-27, twice). The n=400 gate killed basket v1
   in 2 minutes; conversely a straight-to-SPRT basket-192 run burned 18 minutes to learn
   what 200 games would have shown. Escalate to SPRT/n≥1000 only when the screen is alive,
   and never start a multi-hour run while the architecture is still moving.
4. **n≥1000 paired with SPRT before any claim; n≥2000 to publish.**
5. **A zero in coverage is a question, not a verdict.**
6. **Distrust a good number.** The 67.5% fit accuracy was a thread-order train/test leak.
7. **Act only inside the measured domain.** Basket v1 searched rounds 1–3, which rank had
   never measured, and paid −76 Elo for it.
8. Any card/rules change updates the `shards-cards` registry, `LocFrench.cs`, and a dated
   EN+FR `Changelog.cs` entry in the same commit.

## Suggested first moves

1. **Gate A on the frozen champion** — the config is now worth the spend: launch
   `probe --a basket-96 --b bench:rollout-1200 --games 2000 --sprt` OVERNIGHT, detached
   (~7 h; Start-Process pattern, verdict auto-appends to campaign-log.md). Never
   mid-session. Read its line before doing anything else next session.
2. While it runs elsewhere: one 200-game re-screen of basket-192 vs basket-96 (the
   rollout ladder was closed in the flat-funnel era; halving may have moved the optimum).
3. At the next benchmark freeze point: handlers for soi.scry and soi.reveal (reuse tuned
   quantities, same recipe as the four-decisions fix), then re-run the ablation and the
   basket-96 SPRT since bench:greedy-v5's behaviour moves.

Do not mint any rank from the basket bot until it clears Gate A. That bar is the whole
reason this rewrite exists.
