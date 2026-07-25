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

## Current ladder — RE-MINTED 2026-07-25 (no nets, full rollouts, fixed iterations)

| Rank | Engine | Budget (total it) | Root workers | ~Wall clock |
|---|---|---|---|---|
| IRON | hand-written heuristic (now uses hero ability + reroll) | — | — | instant |
| BRONZE | tuned value model V5, greedy argmax | — | — | instant |
| SILVER | ISMCTS, **full rollouts to terminal, no net** | 2 400 | 8 | ~110 ms |
| GOLD | same | 6 000 | 8 | ~275 ms |
| PLATINUM | same | 12 000 | 8 | ~550 ms |
| EMERALD | same | 24 000 | 16 | ~550 ms |
| DIAMOND | same | 48 000 | 16 | ~1.1 s |

Every rank is a FIXED iteration count — never wall-clock — so the move is identical on any
machine; a slower CPU just takes longer. Root workers cut wall-clock only (seeded,
CPU-independent merge).

### ⚠ The crossover: ~1200 iterations. Below it, search is WORSE than no search.
Measured vs BRONZE (instant V5 greedy), paired: **300 it → 21.2%**, **1200 it → 51.4%**,
**4800 it → ~70%**. ε-greedy rollouts give value estimates noisier than simply trusting the
tuned policy, so a small budget talks the bot out of good greedy moves. Above the crossover
search compounds fast (~115 Elo/doubling; a 4× step is worth 79.3%). **Never set a rank
budget from the scaling slope alone.**

### ⚠ The old net ladder was INVERTED — legacy DIAMOND scored 8.5% vs BRONZE
`legacy-diamond` (gen-8 net, 3200 it, exactly what shipped) vs BRONZE, all DLC incl. Duel:
**8.5% [5.6–11.4] over 200 pairs** — about −410 Elo. The top difficulty was far weaker than
the instant one two rungs below it. Causes, all measured: the net is Duel-blind (no Duel
bit, no hero identity, none of the 9 Duel flags), it loses to a plain rollout agent at equal
budget (40.6%), and it flattens search scaling to nothing (52.2% for a 4× step vs 79.3%
without it). The published Elo table below is not merely stale — it is wrong at the top.

A net returns to the ladder only when one beats the rollout agent head-to-head at equal
wall-clock. That is the bar; gen-8 is 66 Elo below it.

> ### ⚠ 2026-07-25 — every strength number below predates Duel of Doom
> The ladder was minted with `SimConfig.AllDlc` **excluding** `ShardsDlc.Duel`, so all of
> it — the Elo table, the per-rank win rates, both embedded nets — describes a game
> without hero drafts, hero abilities or row rerolls. Mirror matches hid this completely:
> both sides shared the blind spot, so every probe read 50% while a human using those
> mechanics took the whole edge.
>
> Fixed so far (see `campaign-log.md` 2026-07-25): Duel is now the default sim mask;
> IRON's ladder gained both actions; the hero ability is priced multiplicatively so its
> value decides whether to use it (the additive version was worth **exactly nothing** —
> 49.8% over 784 pairs — and the multiplicative one is worth **+42 Elo**); Scry / reorder /
> hand-strip carry real atoms; **V5** weights are tuned with Duel ON and beat V4 at
> **69.9% [67.0–72.7]**.
>
> Still Duel-blind: **the two embedded nets**. `ShardsStateEncoder` has no Duel bit, no
> hero identity and none of the 9 Duel per-player flags, so GOLD→DIAMOND evaluate Duel
> positions with a function fit on a different game. Their *priors and rollouts* improved
> with V5; their *value estimate* did not. Re-mint after the encoder v3 + retrain.
>
> Treat the Elo table as a pre-Duel baseline, not the current ladder.

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

## DIAMOND (DIAMANT) — minted 2026-07-22 · gen 8 · 3200 it (4× budget) · the TOP rank
- gen-8 net at a 4× budget over EMERALD. Beats EMERALD@800 **56.0% [53.8-58.2]** over 2000 games —
  the same ~56-57% adjacent-rank ceiling. The last rung of the deterministic ladder.
- **Multi-threading (2026-07-22)**: the 3200-iteration search is delivered as **K=8 root-parallel
  trees of 400 iters each** (3200 total, merged by summed visits + ordinal tie-break, early-stop off).
  The move is **CPU-independent** — identical on any machine, since the 8 seeded trees + budgets fix the
  result; fewer cores just run the trees sequentially, same answer, slower. ~80 ms on multi-core vs
  ~480 ms single-tree — same total search budget, so architecturally the same strength, just faster.

## Ladder concluded at DIAMOND (2026-07-22)
- **MASTER→CHALLENGER not pursued.** The value-net plateaued: a gen-9 sweep of **10 variants** (width,
  depth, epochs, q-weight, champion-weighted data mixes) all TIED or LOST to gen-8 at equal iterations —
  best was g9v1 **51.5%** (a tie); champion-weighted mixes were *worse* (down to 35.5%), and the q0.7
  variant's higher val-accuracy (80.7%) played worse (46.0%, a smoother-target artifact). Seven
  generations, two encoders, a width sweep: the **encoder** (pooled information-set) is the ceiling,
  not capacity or data. A stronger net for a MASTER rank isn't reachable; lifting DIAMOND to 60% would
  need only more search, which the user declined as a rung.
- **Full round-robin benchmark** (`Tools/ShardsData/benchmark/`, visual published as an artifact):
  IRON–PLATINUM at 800–2000 mirrored games/pair; EMERALD/DIAMOND steps from their mint gates
  (same net, deeper search — structurally monotone, not re-run). Elo (Bradley-Terry, IRON=1000):
  IRON 1000 · BRONZE 1212 · SILVER 1242 · GOLD 1251 · PLATINUM 1312 · EMERALD 1360 · DIAMOND 1402.
  Informative jumps: the **net step** GOLD→PLATINUM = **61.5%** (gen-0→gen-8 at equal depth, the
  biggest neural gain), while SILVER→GOLD = **50.9%** (2× iterations ≈ coin-flip — depth alone on the
  weak net barely helps). Root parallelism, formerly a "reserved lever," is now DIAMOND's speed path.

---

### Measurement provenance
- BRONZE/IRON numbers: `soisim evaluate` (2,000 mirrored games per pairing).
- Search-rank numbers: `soisim probe` promotion duels (60–80 mirrored games; seat-
  swapped, all-DLC, random characters). Full history: [campaign-log.md](campaign-log.md).
- All duels use equal budgets on both sides; "equal simulations" comparisons isolate
  decision *quality* from raw speed.
