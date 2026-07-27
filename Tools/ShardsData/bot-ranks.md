# SoI Bot Ladder — technical specs per rank

> One entry per difficulty. **Update this file whenever a rank is minted or re-speced**
> (alongside `campaign-log.md`). Strength numbers cite the probe that produced them;
> ± ranges are 95% Wilson intervals.
>
> Properties shared by EVERY rank:
> - **Fair play** — no rank ever sees your hand, your deck order, or any deck order.
>   Search ranks re-imagine hidden zones from public card-counting only (pinned by an
>   invariance test: the choice cannot change when hidden information is shuffled).
> - **Rules-exact** — search ranks simulate real engine moves; effects always resolve
>   exactly as the rules say. Only the *choice* of action is intelligence.
> - **DLC-ready** — card knowledge derives from card *properties*, extracted automatically
>   from every card's effect tree. After a balance patch: `soisim tune` (~3 min).

## Current ladder — re-minted 2026-07-25, nets removed 2026-07-27

| Rank | Engine | Budget (total it) | Root workers | ~Wall clock |
|---|---|---|---|---|
| IRON | hand-written heuristic (uses hero ability + reroll) | — | — | instant |
| BRONZE | tuned value model V5, greedy argmax | — | — | instant |
| SILVER | ISMCTS, full ε-greedy rollouts to terminal | 2 400 | 8 | ~110 ms |
| GOLD | same | 6 000 | 8 | ~275 ms |
| PLATINUM | same | 12 000 | 8 | ~550 ms |
| EMERALD | same | 24 000 | 16 | ~550 ms |
| DIAMOND | same | 48 000 | 16 | ~1.1 s |

Every rank is a FIXED iteration count — never wall-clock — so the move is identical on any
machine; a slower CPU just takes longer. Root workers cut wall-clock only (seeded,
CPU-independent merge).

⚠ **Budgets above 4800 are EXTRAPOLATED, not measured.** The crossover run stopped at 4800
(71.4%, n=201) plus a partial 6000 (n=40). SILVER→DIAMOND are spaced by budget doublings
from the measured region, not validated individually. Re-measure before citing them.

## The frozen benchmark ladder (added 2026-07-27)

Four kinds that **must never change**, used to measure every future candidate. Pinned by
`Tools/SoiSim/Tests/SoiSimBenchmarkLadderTests.cs`.

| Kind | Agent |
|---|---|
| `bench:heuristic` | `ShardsHeuristicBot` — the frozen anchor "heuristic-v1" |
| `bench:greedy-v5` | greedy argmax pinned to `ShardsEvalWeights.V5` **explicitly**, not `Current` |
| `bench:rollout-1200` | full-rollout ISMCTS, 1200 it, single tree (≈ the crossover) |
| `bench:rollout-4800` | full-rollout ISMCTS, 4800 it, single tree (≈ 71% vs BRONZE) |

`bench:greedy-v5` pins V5 by name because a reference that follows `Current` stops being a
reference the moment the tuner runs — every comparison against it would then read ~50%. The
guard test also checksums V5's contents, so editing the vector in place fails loudly.

### ⚠ The crossover: ~1200 iterations. Below it, search is WORSE than no search.
Measured vs BRONZE (instant V5 greedy), paired, all DLC incl. Duel:
**300 it → 21.2% [18.2–24.3]** (n=400) · **1200 it → 52.5% [47.6–57.4]** (n=200) ·
**4800 it → 71.4% [67.3–75.5]** (n=201). ε-greedy rollouts give value estimates noisier than
simply trusting the tuned policy, so a small budget talks the bot out of good greedy moves.
Above the crossover search compounds fast (~115 Elo/doubling; a 4× step is worth 79.3%).
**Never set a rank budget from the scaling slope alone.**

### ⚠ What hidden information is worth: +38 Elo, and that is the ceiling
A cheating agent that skips determinization and plans against the opponent's real hand beats
an otherwise identical honest agent **55.4% [53.2–57.6] over 1000 pairs**. That is an upper
bound on everything belief-side work could ever buy — richer encoders, better determinization,
more samples. **Do not spend there.** SoI is effectively a stochastic game with nature-only
randomness, not an imperfect-information game: deck *contents* are public, only the shuffle
is hidden.

---

## The neural campaign, and why every trace of it was removed

**The shipped top rank was weaker than the instant one five rungs below it.** `legacy-diamond`
(gen-8 net, 3200 it, exactly what shipped) vs BRONZE, all DLC incl. Duel:
**8.5% [5.6–11.4] over 200 pairs** — about **−410 Elo**.

Measured causes, each independently sufficient:
- The net **loses to having no evaluator at all**: 40.6% [38.1–43.0] against a plain
  full-rollout agent at equal 200 it (n=1000), ≈ −66 Elo.
- It **flattens search scaling to zero**: at a 4× budget step the rollout agent gained
  **79.3%**, the net agent **52.2%**. With a miscalibrated leaf evaluator, more iterations
  buy better play toward a worse target.
- It is **Duel-blind** — no Duel bit, no hero identity, none of the 9 Duel per-player flags.
  Both embedded nets evaluated Duel positions with a function fit on a different game.

Process causes, which are the ones worth remembering:
- **Gated net-vs-net in mirror matches.** Structurally blind to a shared blind spot: nine
  generations all read ~50% against each other while the agent was −410 Elo in absolute terms.
  The no-net baseline probe was not run until four days and nine generations in.
- **Gated on validation accuracy**, which is *anti*-correlated with strength here: the gen-9
  variant with the best val acc (80.7%) played 46.0%. Nine generations moved val acc
  74.6% → 76.8% — a band carrying no strength information at all.
- **n=120 unpaired** (±8.9pt half-width) cannot distinguish 50% from 55%. Several "findings"
  in the campaign log came from exactly that.
- **Distribution collapse never solved** — every generation trained on a static blend anchored
  to one greedy bootstrap, so gens 1–9 are statistical twins.

Removed 2026-07-27: `ShardsNetWeights.g.cs` (7.5 MB), `ShardsNeuralEval`, `ShardsStateEncoder`,
`ShardsBaselineEvaluator`, `ShardsSearchConfig.RolloutEndTurns`, the `legacy-gold/platinum/
diamond` kinds, `emit-net`/`netfixture`/`selfplay` commands, and ~10.3 GB of self-play data
(generated with Duel excluded, by policies that could never reroll).

**The bar for any future evaluator**: it must beat full-rollout ISMCTS head-to-head at equal
**wall-clock**, measured paired with SPRT at n≥2000. Running that probe *first* is the single
process change this rewrite exists to enforce.

## Known dead ends — do not retry
- **Truncated rollouts scored by a leaf evaluator** (`RolloutEndTurns` ≥ 0) — the mechanism
  behind the inversion.
- **Encoder schema v2** (1140-feature tactical appendix) — probed three ways, rejected three
  ways (35.8% bootstrap-trained, 42.5% / 43.8% search-trained).
- **Wider/deeper nets** — width sweep flat across narrow/medium/wide/xwide (76.6–76.8% val acc).
- **2× budget steps** — 51.0% [47.0–55.0], indistinguishable from a tie. 4× is the smallest
  real step.
- **Bag-of-card-vectors features for a value function.** The target is built from ratios,
  minima and thresholds (`5/N × Σ`, `min(killClock, ascendClock)`, `health/damage`); a small
  MLP over summed bags cannot compute `Σ/N` because it never sees `N` multiplicatively
  against the sum. Any future evaluator must compute those ratios analytically.
- **The published Elo table** (IRON 1000 … DIAMOND 1402). Deleted rather than cited: it was
  not merely stale, it was **inverted at the top**, and every number in it predates Duel.

---

## Per-rank notes

### IRON (FER)
`ShardsHeuristicBot` — hand-written priority ladder (play hand by a static ordering score →
exhaust → kill Ingeminex → destiny/relic → best buy per gem → focus → hero ability → reroll →
end turn). Loses ~83% to BRONZE; beats a random-mover ~100%. Frozen forever as the tuner and
evaluation anchor "heuristic-v1".

### BRONZE (BRONZE)
`ShardsGreedyEvalBot` — argmax over `ShardsValueModel` **V5**: 49 weights over card-effect
atoms extracted automatically from every card's effect tree, exact condition probes,
synergy-aware play ordering (enablers before dependents), value-based decision answers,
champion-kill-aware damage splits. sep-CMA-ES self-play, 300 gens × λ16 × 240 games.
**V5 vs V4: 69.9% [67.0–72.7]** — V5 is the first vector tuned with Duel ON.
Microseconds per decision. Also serves as every search rank's rollout policy and move prior.

⚠ The hero ability is priced **multiplicatively** (`net × W.HeroAbilityValueScale`). The
additive version was worth **exactly nothing** (49.8% over 784 pairs); multiplicative is worth
**+42 Elo** (56.0% over 1000 pairs). An additive base large enough to matter in the action
ladder swamps a net value of ±2, so the ability fires unconditionally.

### SILVER → DIAMOND
All the same agent: `ShardsSearchBot` running SO-ISMCTS with full ε-greedy rollouts to
terminal and **no evaluator**, at doubling fixed iteration budgets. They differ only in budget
and root-worker count. `RolloutEpsilon = 0.03` is load-bearing: 27% vs greedy at ε=0.15/200it,
48% at ε=0.03/200it, 77% at ε=0.03/600it.

---

### Measurement provenance
- BRONZE/IRON numbers: `soisim evaluate` (2,000 mirrored games per pairing).
- Search-rank numbers: `soisim probe` — mirrored pairs, seat-swapped, all-DLC incl. Duel,
  random characters. Full history: [campaign-log.md](campaign-log.md).
- **Publish floor: 200 pairs.** `ProbeCommand` refuses to write a conclusion below it without
  `--allow-small`. Pairing cancels seed, matchup and the ~56.5% first-player advantage, and
  measures ~1.4× tighter than pooling the same games.
- ⚠ Every win-rate claim dated **before 2026-07-25** is unpaired, n=120, Duel-blind and
  mirror-matched. Four compounding flaws — do not cite them.
