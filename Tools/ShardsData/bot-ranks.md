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
| PLATINUM | 🔄 in training | ISMCTS, eval-at-leaf | gen 1 (frozen) | 1.0 s |
| EMERALD | planned | ISMCTS, eval-at-leaf | gen 2+ | 1.25 s |
| DIAMOND | planned | ISMCTS, eval-at-leaf | promoted gen | 1.5 s |
| MASTER | planned | ISMCTS, eval-at-leaf (+q-labels) | promoted gen | 1.75 s |
| GRANDMASTER | planned | ISMCTS (+wider net if needed) | promoted gen | 2.0 s |
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

## PLATINUM (PLATINE) — net generation 1 *(training tonight)*
- **Net**: same architecture; trained on the sliding window = gen-0 bootstrap +
  ~120,000 positions from 6,000 *search-quality* self-play games (GOLD-level play,
  100 simulations/decision, root moves temperature-sampled for the first 8 turns).
- **Search**: **eval-at-leaf** (no rollout at all — probed: 56.2% [45.3–66.6] vs the
  2-turn rollout at equal simulations while 2.6× cheaper) ⇒ ≈ 2,800 simulations/s
  at the same 1.0 s budget.
- **Promotion gate** (pending): ≥55% AND Wilson lower bound >50% vs GOLD at equal
  budget; regression guards ≥95% vs random, no >3-pt drop vs BRONZE.

## EMERALD (ÉMERAUDE) → DIAMOND (DIAMANT) — generations 2+
- Same loop, each on the previous champion's games: overnight selfplay (~1 evening per
  generation post-speedups) → 5090 training (minutes) → parity → promotion duel.
- Budgets step up (1.25 s / 1.5 s). Planned data upgrade: **q-labels** (blend the
  search's own root estimate into the target, halving label noise) once the pipeline
  records them.

## MASTER (MAÎTRE) → GRANDMASTER (GRAND MAÎTRE) — late generations
- Minted only if the loop keeps clearing gates. Escalation levers, each probe-gated:
  wider net (1024→512→256) **if** the underfitting signature appears (train ≈ val
  accuracy stuck despite better data) — at which point batched **GPU inference**
  becomes worthwhile and is the designed-in next step; PUCT selection at high
  simulation counts; deeper think budgets (1.75 s / 2.0 s).

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
