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

| Rank | Status | Engine | Net | Think time |
|---|---|---|---|---|
| IRON | ✅ shipped | hand-written heuristic | — | instant |
| BRONZE | ✅ shipped (default) | tuned value model, greedy | — | instant |
| SILVER | ✅ shipped | ISMCTS, full rollouts | — | 1.0 s |
| GOLD | ✅ minted 2026-07-21 | ISMCTS, 2-turn rollouts → net | gen 0 (frozen) | 1.0 s |
| PLATINUM | ✅ minted 2026-07-22 | ISMCTS, 2-turn rollouts → net | gen 5 (frozen, wide) | 1.25 s |
| EMERALD | planned | ISMCTS, 2-turn rollouts → net | next gate-clearing gen | 1.5 s |
| DIAMOND | planned | ISMCTS + root parallelism (probe pending) | promoted gen | 1.75 s |
| MASTER | planned | ISMCTS | promoted gen | 2.0 s |
| GRANDMASTER | planned | ISMCTS | promoted gen | 2.25 s |
| CHALLENGER | planned | ISMCTS + root parallelism | always-newest champion | 2.5 s |

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

## SILVER (ARGENT) — the first search AI *(frozen pre-neural config)*
- **Algorithm**: `ShardsSearchBot` — Single-Observer ISMCTS: per decision, re-imagines
  hidden zones (determinization), then simulates complete games through the real
  engine with ε-greedy BRONZE playouts (ε = 0.03, a measured-critical constant);
  UCB1 + availability + BRONZE priors; plan cursor answers in-chain decisions from
  the searched subtree.
- **Budget**: 1.0 s wall-clock ≈ 600–1,000 full-game simulations per decision.
- **Strength**: **~77% [64.6–85.6] vs BRONZE** (60-game probe).
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

## PLATINUM (PLATINE) — minted 2026-07-22 · net generation 5 (pinned) · 1.25 s
- **Net**: the first WIDE net (768→1024→512→256→1, ~1.44M params, f16 ≈ 2.9 MB),
  76.8% val acc — trained on 1.32M positions (gen-0 bootstrap capped at 400k +
  every search-selfplay batch, 640k of them **q-labeled**: target 0.5z + 0.5q,
  corr(q,z)=0.60, which cut label std 0.50→0.37).
- **Search**: the SAME 2-end-turn net-truncated rollouts as GOLD — eval-at-leaf
  was probed for play and REJECTED (see history below) — at the ladder's first
  budget step, **1.25 s**.
- **Promotion**: 57.8% [52.9–62.5] vs GOLD's method at equal 200-iteration budget
  (n=400); **60.7% [54.9–66.3] vs GOLD AS SHIPPED** (1.25s vs 1.0s wall-clock,
  n=280, idle machine). Guards: 100% [94–100] vs random; 66% vs BRONZE at 200it —
  identical to GOLD's 66% at the same budget (no regression).
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

## EMERALD (ÉMERAUDE) → DIAMOND (DIAMANT) — the next rungs
- Same loop on the new champion's games, in T=2 ROLLOUT mode (the proven config),
  budgets stepping 1.5 s / 1.75 s. Candidate levers, each probe-gated:
  **root parallelism** (implemented; needs a clean idle-machine wall-clock probe —
  K worker trees ≈ K× simulations at the same seconds), longer q-label searches
  (budget 200+ for sharper labels), PUCT selection at high sim counts.
- Known dead ends (probed 2026-07-22, do not retry blindly): eval-at-leaf for
  PLAY (fine for cheap selfplay data gen); schema-2 tactical encoder features;
  expecting equal-iteration net-vs-net gains — value nets sit near the
  information-set noise floor, so rungs differentiate on search/budget instead.

## MASTER (MAÎTRE) → GRANDMASTER (GRAND MAÎTRE) — late generations
- Minted only if the loop keeps clearing gates (2.0 s / 2.25 s). If net progress
  stays flat, these become search-quality rungs; batched **GPU inference** becomes
  worthwhile only if a much wider/deeper net ever earns its keep in T=2 mode.

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
