# SoI Bot Ladder — technical specs per rank

> One entry per difficulty, minted or planned. **Update this file whenever a rank is
> minted or re-speced** (alongside `campaign-log.md`). Strength numbers cite the probe
> or evaluation that produced them; ± ranges are 95% Wilson intervals.
>
> Properties shared by EVERY rank:
> - **Fair play** — no rank ever sees your hand, your deck order, or any deck order.
>   Search ranks re-imagine hidden zones from public card-counting only (pinned by an
>   invariance test: the choice cannot change when hidden information is shuffled).
> - **Rules-exact** — search ranks simulate real engine moves; effects always resolve
>   exactly as the rules say. Only the *choice* of action is intelligence.
> - **DLC-ready** — card knowledge derives from card *properties*, not card identity;
>   after a balance patch: `soisim tune` (~3 min) + one selfplay/train/gate cycle.

**Budget model (reframed 2026-07-22):** ranks below MASTER use a FIXED, fast iteration
budget — NOT wall-clock. Each neural rank is a BETTER TRAINED NET at the *same* iteration
count (deterministic, ~50-80 ms/decision, ~15× faster than the old 1.0-1.25 s). Wall-clock
and larger budgets are held back for the top ranks, adopted only once "better net at equal
iterations" stops producing gate-clearing generations.

| Rank | Status | Engine | Net | Budget |
|---|---|---|---|---|
| IRON | ✅ shipped | hand-written heuristic | — | instant |
| BRONZE | ✅ shipped (default) | tuned value model, greedy | — | instant |
| SILVER | ✅ minted | ISMCTS, 2-turn rollouts → net | gen 0 (frozen) | 100 it |
| GOLD | ✅ minted | ISMCTS, 2-turn rollouts → net | gen 0 (frozen, narrow, bootstrap) | 200 it |
| PLATINUM | ✅ minted | ISMCTS, 2-turn rollouts → net | gen 8 (frozen, narrow, full data) | 200 it |
| EMERALD | ✅ minted | ISMCTS, 2-turn rollouts → net | gen 8 (frozen) | 800 it (4×) |
| DIAMOND | ✅ minted | ISMCTS, 2-turn rollouts → net | gen 8 (frozen) | 3200 it (4×) |
| MASTER → CHALLENGER | planned | ISMCTS + reserved levers (wall-clock / root parallelism) | promoted gen | — |

---

## IRON (FER) — the original bot
- **Algorithm**: `ShardsHeuristicBot` — a hand-written priority ladder (play hand by a
  static ordering score → exhaust → kill Ingeminex → destiny/relic → best buy per gem
  → focus → end turn) with fixed effect-scoring constants.
- **Strength anchor**: loses ~83% to BRONZE; beats a random-mover ~100%.
- **Role**: beginner rank; frozen forever as the tuner/evaluation anchor "heuristic-v1".

## BRONZE (BRONZE) — tuned instant AI *(menu default)*
- **Algorithm**: `ShardsGreedyEvalBot` — argmax over `ShardsValueModel` V4: 43 weights
  over card-effect atoms (extracted automatically from every card's rules code),
  exact condition probes, synergy-aware play ordering (enablers before dependents),
  value-based decision answers, champion-kill-aware damage splits.
- **Training**: sep-CMA-ES self-play, ~1.3M games total (initial 300-gen tune + 120-gen
  retune after the ordering fix). Reward = win/loss vs a champion/anchor pool.
- **Strength**: **83.2% [81.5–84.8] vs IRON**; 100% vs random (2,000-game evaluations).
- **Cost**: microseconds per decision. Also serves as every search rank's rollout policy
  and move-ordering prior.

## SILVER (ARGENT) — the entry search rank *(re-spec 2026-07-22)*
- **Algorithm**: `ShardsSearchBot` — the same ISMCTS + 2-turn net-truncated rollouts as
  GOLD, using **gen-0's net at 100 iterations** (half of GOLD's budget). A fast iteration
  step below GOLD, same net.
- **Why not full rollouts**: the original pre-neural SILVER (full rollouts, 1.0 s) can't be
  both fast AND stronger than BRONZE — full rollouts score ~48% vs BRONZE at 200 iters and
  need ~600 (≈0.6-1.0 s) to reach 77%. The fast-below-MASTER reframe forces the net here.
  The archival full-rollout search lives on as the `strong`/`strong-fast` tooling kinds.
- **Budget**: fixed 100 iterations (~30-40 ms), deterministic. Beats BRONZE, loses to GOLD.
- **Role**: the pre-neural "MASTER" preserved exactly; baseline the nets must beat.

## GOLD (OR) — first neural rank · net generation 0 (pinned)
- **Net**: MLP 768→512→256→128→1 (~560k params, f16 ≈ 1.1 MB, embedded in the build);
  input = 768-float *information-set* encoding (14 viewer-relative zone pools ×
  52 card-property dims + dynamics + scalars — no card identity).
- **Training data**: 720,000 positions from 60,000 BRONZE self-play games (gen 0
  bootstrap); labels = final win/loss. **74.6% val accuracy**; trained in <1 min on
  the RTX 5090; PyTorch↔C# parity pinned at 1e-4 in CI.
- **Search**: ISMCTS with 2-end-turn rollouts, leaf scored by the net (queried from
  the turn player's seat — the in-distribution viewpoint). ≈ 2,000 simulations/s.
- **Strength**: **78.3% [66.4–86.9] vs SILVER's method at equal simulation counts**
  (and ~2× cheaper per simulation on top).
- **Frozen**: hard-pinned to generation 0 — future nets mint new ranks instead.

## PLATINUM (PLATINE) — minted 2026-07-22 · net generation 8 (pinned, narrow) · 200 it
- **Net**: NARROW 768→512→256→128→1 (~560K params, f16 ≈ 1.1 MB) — same architecture as
  GOLD — 76.7% val acc, trained on the same 1.32M-position mix that produced the (retired)
  wide gen-5 (gen-0 bootstrap capped at 400k + every search-selfplay batch, 640k **q-labeled**:
  target 0.5z + 0.5q, corr(q,z)=0.60). **The width sweep proved capacity is plateaued** —
  narrow 76.7% = medium 76.8% = wide 76.8% = xwide 76.6%; the wide gen-5 was retired as
  wasteful (gen-8 ties it, 46.0% [40.4-51.7] n=300, at ~2.6× cheaper eval).
- **Search**: the SAME 2-end-turn net-truncated rollouts as GOLD — eval-at-leaf was probed
  for play and REJECTED (see history below) — at the shared fixed **200-iteration** budget.
  So GOLD and PLATINUM share one architecture, speed, and budget, differing only by TRAINING
  DATA (bootstrap vs full mix): the one honest "better net at equal iterations" step.
- **Promotion**: **56.5% [51.6–61.3] vs GOLD at equal 200 it (n=400)** — gate passed
  (≥55% ✓, Wilson LB 51.6% > 50% ✓). Guards: 100% vs random; ~66% vs BRONZE at 200it.
- **Speed**: ~50-80 ms/decision (fixed 200 it, deterministic) vs the retired 1.25 s wall-clock
  — a ~15-20× latency cut, from dropping wall-clock AND the wide net.
- **The five-attempt history** (gens 1–4 + gen-5-at-1.0s, all vs gen-0): 48.3% ·
  34.2% (distribution collapse) · 49.2% · 50.8% · 52.5%. What finally worked —
  and the campaign's core lesson: value nets at this game sit near the
  information-set noise floor (~76% val acc); **eval-at-leaf amplifies encoder
  tactical blindness** (pooled features can't see per-slot row/board detail), so
  net gains only CONVERT to strength when rollouts resolve the tactical state
  first and the net judges the cleaner post-rollout position. A schema-2 tactical
  encoder (1140 features: per-slot row + affordability + per-champion detail) was
  built and probed both ways — REJECTED (35.8% bootstrap-trained, 42.5%/43.8%
  search-trained); the v1 pooled information-set encoding + rollouts stands.

## EMERALD (ÉMERAUDE) — minted 2026-07-22 · gen 8 · 800 it (4× budget)
**The net axis is exhausted after ONE step**, so depth above PLATINUM is carried by SEARCH.
Only two net tiers exist — gen-0 (bootstrap, weak) and gen-8 ≈ gen-5 (full-mix, strong), the
GOLD→PLATINUM boundary; the width sweep is flat (76.6-76.8%) and gen-8 vs gen-5 is a tie
(*five nets, one number* — the ceiling is the ENCODER, not capacity or data).
- **Budget sizing (measured)**: a 2× step is worthless — gen-8 @400 vs @200 = **51.0% [47.0-55.0]**
  (near-ties dominate past 200 it). **4× is the smallest real step**: gen-8 @800 vs @200 =
  **56.8% [52.8-60.7]** — the same ~56-57% ceiling as the net step (PLATINUM vs GOLD = 56.5%,
  n=1080). So EMERALD = gen-8 @ 800 it, ~120 ms/decision, deterministic.
- **The 58% target is above SoI's ceiling**: both levers (better net, 4× more search) top out at
  ~56-57% between adjacent ranks — the game isn't complex/deep enough for a bigger gap. Ranks are
  spaced ~56% apart by design; chasing 58% would mean far fewer, coarser ranks.
- **"Data later"** (user decision 2026-07-22): a better-data net (champion-quality T2 selfplay)
  replaces gen-8 here ONLY IF a future generation actually beats it at equal iterations — expected
  to tie (encoder ceiling), so not blocked on. Partial T2 selfplay data is parked on disk.
- Known dead ends (do not retry): eval-at-leaf for PLAY; the schema-2 tactical encoder
  (35.8% / 42.5% / 43.8%); wider/deeper nets; 2× budget steps (51%).

## MASTER (MAÎTRE) → CHALLENGER — the reserved-lever ranks
- Beyond the iteration steps, these adopt the held-back levers: larger budgets, **root parallelism**
  (implemented, needs a clean idle-machine probe), wall-clock think time, and a living-champion net.
  Batched **GPU inference** becomes worthwhile only if a much wider/deeper net ever earns its keep,
  which the plateau says it won't.

## CHALLENGER — the living champion
- **Always re-pointed to the newest promoted net** (not frozen, unlike every rank
  below it), at the full 2.5 s budget, plus root-parallel search (K independent trees
  merged by visits — implemented, adoption pending a clean idle-machine probe;
  expected ~8× simulations at the same wall-clock).
- If the campaign promotes more generations than there are ranks, intermediate ranks
  get re-spaced by Elo at campaign end so adjacent ranks stay evenly separated.

---

### Measurement provenance
- BRONZE/IRON numbers: `soisim evaluate` (2,000 mirrored games per pairing).
- Search-rank numbers: `soisim probe` promotion duels (60–80 mirrored games; seat-
  swapped, all-DLC, random characters). Full history: [campaign-log.md](campaign-log.md).
- All duels use equal budgets on both sides; "equal simulations" comparisons isolate
  decision *quality* from raw speed.
