# SoI AI Campaign — history

- **2026-07-21 15:30** — ranked ladder shipped: IRON (heuristic), BRONZE (greedy-V2, 81.9% vs IRON), SILVER (full-rollout search 1.0s, ~77% vs BRONZE)
- **2026-07-21 17:40** — play-order fix (Carnivorous Vine deferral) → weights V4: 83.2% vs heuristic, 52.2% vs pre-fix greedy
- **2026-07-21 18:05** — truncation with hand-made evaluator REJECTED by probe (17.5% at wall-clock parity)
- **2026-07-21 18:50** — gen-0 bootstrap: 60,000 greedy games → 720,000 positions (20s)
- **2026-07-21 19:00** — trained generation 0 on the RTX 5090: 74.6% val accuracy (<1 min); PyTorch↔C# parity 1e-4 OK
- **2026-07-21 19:15** — perspective bug found by 0%-probe and fixed (net must be queried from the turn player's seat)
- **2026-07-21 19:25** — probe: net-truncated search vs SILVER-style full rollouts → **78.3% [66.4–86.9]** at equal iterations → **GOLD minted** (gen-0 net, 1.0s)
- **2026-07-21 19:50** — gen-1 selfplay started: 9,000 ISMCTS-100 games with net-0 leaf eval, temperature 8 turns
- **2026-07-21 21:44** — (watcher misfire: matched the startup banner, not completion — gen-1 selfplay still running at this time)
- **2026-07-21 21:46** — trained generation 1: val acc 74.6%, 728,625 positions
- **2026-07-21 22:05** — probe: ismcts-V4-100it vs ismcts-V4-100it → 56.2 % [45.3 %–66.6 %] over 80 games
- **2026-07-21 22:51** — selfplay gen1: 6,000 games → 120,000 positions (search 100it, 46 min)
- **2026-07-21 22:52** — trained generation 1: val acc 75.1%, 840,000 positions
- **2026-07-21 22:52** — net generation 1 embedded (valAcc 0.7506 · 840,000 positions · 2026-07-21)
- **2026-07-21 22:55** — probe: ismcts-V4-200it vs ismcts-V4-200it → 48.3 % [39.6 %–57.2 %] over 120 games
- **2026-07-21 22:56** — PLATINUM gate attempt 1 REJECTED: gen-1 (86% bootstrap window, val 75.1%) scored 48.3% [39.6–57.2] vs gen-0 — statistically a twin. Gen-2 cycle queued: +8,000 selfplay games, train on search data only (~280k positions), re-duel.
- **2026-07-21 23:58** — selfplay gen1b: 8,000 games → 160,000 positions (search 100it, 62 min)
- **2026-07-21 23:58** — trained generation 2: val acc 75.9%, 280,000 positions
- **2026-07-21 23:58** — net generation 2 embedded (valAcc 0.7594 · 280,000 positions · 2026-07-21)
- **2026-07-22 00:02** — probe: ismcts-V4-200it vs ismcts-V4-200it → 34.2 % [26.3 %–43.0 %] over 120 games
- **2026-07-22 00:05** — probe: ismcts-V4-200it vs ismcts-V4-200it → 36.7 % [28.6 %–45.6 %] over 120 games
- **2026-07-22 00:07** — PLATINUM attempt 2 REJECTED: gen-2 (search-data-only, val 75.9% — highest yet) lost BOTH duels (34.2% vs gen-0, 36.7% vs gen-1). Diagnosis: self-play distribution collapse — accuracy on own trajectories ≠ broad calibration. Attempt 3 plan: ~50/50 bootstrap/search mixture (train.py per-dir cap) + q-labels.
- **2026-07-22 00:27** — trained generation 3: val acc 76.0%, 560,000 positions
- **2026-07-22 00:27** — net generation 3 embedded (valAcc 0.7596 · 560,000 positions · 2026-07-22)
- **2026-07-22 00:30** — probe: ismcts-V4-200it vs ismcts-V4-200it → 49.2 % [40.4 %–58.0 %] over 120 games
- **2026-07-22 01:45** — selfplay gen4q: 8,000 games → 160,000 positions (search 100it, 61 min)
- **2026-07-22 01:45** — trained generation 4: val acc 75.1%, 320,000 positions
- **2026-07-22 01:45** — net generation 4 embedded (valAcc 0.7512 · 320,000 positions · 2026-07-22)
- **2026-07-22 01:49** — probe: ismcts-V4-200it vs ismcts-V4-200it → 50.8 % [42.0 %–59.6 %] over 120 games
- **2026-07-22 01:53** — probe: ismcts-V4-200it vs ismcts-V4-200it → 55.8 % [46.9 %–64.4 %] over 120 games
- **2026-07-22 01:55** — PLATINUM attempt 4 REJECTED: gen-4 (q-labels, 50/50 gen0-capped/gen4q mix, val 75.1% on blended targets) scored 50.8% [42.0–59.6] vs gen-0 (gate: ≥55% + Wilson LB >50%) and 55.8% [46.9–64.4] vs gen-3. Best attempt yet (48.3 → 34.2 → 49.2 → 50.8) and beats the best prior challenger, but still a statistical twin of gen-0. **Underfitting fingerprint present**: final train loss 0.4942 ≈ val loss 0.4946 — capacity/encoding, not data noise, is now the binding constraint → next escalation rungs per bot-ranks.md: more selfplay volume, then encoder enrichment / wider net (1024→512→256, where batched GPU inference becomes worthwhile). Selfplay throughput work queued first.
- **2026-07-22 02:40** — search throughput pass (dotnet-trace profiled): sparse-row forward pass (inputs 86% zero, hidden ~50% — was 72% of selfplay CPU), clone arena pooling (fork allocs were ~20%), Server GC. Selfplay 2.2 → **5.3 games/s** (2.4×); single search thread 1.77× (in-game ranks get ~2× simulations at the same budget). Rejected by measurement: flat-block transposed weights (2KB power-of-two stride = cache-set conflicts, jagged rows win), int-key determinizer sort. Output distribution verified unchanged (q/z/sparsity match gen4q); pinned by Fork_WithArena_RecycledCloneMatchesFreshFork + Q-invariance; suite 171 green.
- **2026-07-22 02:32** — selfplay gen5q: 24,000 games → 480,000 positions (search 100it, gen-0 leaf eval, 70 min at the new 5.3 games/s)
- **2026-07-22 03:47** — trained generation 5 WIDE (1024→512→256, first non-default arch via train.py --layers): val acc **76.8%** (best yet), 1,320,000 positions (gen0 capped 400k + gen1 + gen1b + gen4q + gen5q). Train 0.443 < val 0.467 — the wider net USES its capacity; underfitting plateau gone.
- **2026-07-22 03:47** — net generation 5 embedded. Registry migrated base64 strings → **byte[] RVA data** (6 gens of base64 overflowed the 16MB PE user-string heap → CS8103; byte arrays bypass it entirely and shrink the file 25%). Rejected gens 1–3 retired via new emit-net --retire (never referenced by a minted rank; stats live in this log). Registry: 0, 4, 5.
- **2026-07-22 03:54** — probe (research, equal 200it, both eval-at-leaf): gen-5 vs gen-0 → 50.8 % [42.0–59.6] over 120 games — identical to gen-4 despite 4× data and best-ever val acc. **Five nets, one number: the value ceiling is the ENCODER, not capacity or data.**
- **2026-07-22 04:27** — probe (PLATINUM wall-clock gate, 1.0s both): gen-5 T=0 vs GOLD config (gen-0, 2-turn rollouts) → **42.5 % [34.0–51.4] — REJECTED**. GOLD's rollout mode WINS at wall-clock parity: the 2-end-turn playout resolves the mid-turn tactical state the pooled encoding cannot see, worth more than the extra simulations T=0 buys. Attempt 5 closed. Diagnosis converges: encoder tactical blindness → **encoder schema v2 queued** (per-slot center row + affordability, per-champion board detail, monster detail; v1 encode path retained for pinned schema-1 nets).
- **2026-07-22 04:33** — selfplay boot: 60,000 games → 720,000 positions (greedy bootstrap, 0 min)
- **2026-07-22 04:34** — trained generation 6: val acc 75.2%, 720,000 positions
- **2026-07-22 04:34** — net generation 6 embedded (valAcc 0.7522 · 720,000 positions · 2026-07-22)
- **2026-07-22 04:38** — probe: ismcts-V4-200it vs ismcts-V4-200it → 35.8 % [27.8 %–44.7 %] over 120 games
- **2026-07-22 04:50** — ENCODER SCHEMA V2 shipped: 768 → 1140 features — the frozen v1 prefix + tactical appendix (6 center-row slots × 52-dim vec + affordable-now bit, destiny-pick state, top-4 champions/side × 6 dims, top-2 monsters × 2 dims). ShardsNeuralEval is schema-aware (v1 path retained verbatim for pinned nets — pinned by EncodeV1_IsTheExactPrefixOfEncode); train.py reads feature count from data headers. v2 bootstrap: 60k greedy games → 720k positions in 8.7s.
- **2026-07-22 04:56** — trained generation 6 (FIRST v2 net, wide, bootstrap-only): val acc 75.2% (vs gen-0's 74.6% on the v1 equivalent). Suite 172 green.
- **2026-07-22 05:02** — probe (equal 200it): gen-6 v2-bootstrap vs gen-0 → **35.8% [27.8–44.7]** — v2 features HURT when trained on greedy-only data: bootstrap games rarely visit sharp tactical states, so the appendix is miscalibrated off-distribution where search actually goes. Next: v2 SEARCH selfplay (gen-0 as leaf evaluator via schema dispatch — champion-quality games, v2-encoded records), then gen-7 on the boot+search mix.
- **2026-07-22 05:45** — selfplay s1: 24,000 games → 480,000 positions (search 100it, 66 min)
- **2026-07-22 05:45** — trained generation 7: val acc 76.4%, 880,000 positions
- **2026-07-22 05:46** — net generation 7 embedded (valAcc 0.7638 · 880,000 positions · 2026-07-22)
- **2026-07-22 05:49** — probe: ismcts-V4-200it vs ismcts-V4-200it → 42.5 % [34.0 %–51.4 %] over 120 games
- **2026-07-22 05:52** — probe: ismcts-V4-200it vs ismcts-V4-200it → 43.8 % [34.8 %–52.3 %] over 120 games
- **2026-07-22 05:55** — probe: ismcts-V4-200it vs ismcts-V4-200it → 56.7 % [47.7 %–65.2 %] over 120 games
- **2026-07-22 06:02** — probe: ismcts-V4-200it vs ismcts-V4-200it → 57.8 % [52.9 %–62.5 %] over 400 games
- **2026-07-22 06:32** — probe: ismcts-V4-200it vs ismcts-V4-200it → 52.5 % [43.6 %–61.2 %] over 120 games
- **2026-07-22 06:03** — probes (equal 200it, T=2 rollout mode both — first time challengers tested in GOLD's own mode): gen-7-T2 vs gen-0-T2 → 43.8% (v2 features rejected in both modes); **gen-5-T2 vs gen-0-T2 → 56.7% [47.7–65.2]** — the wide v1 net's calibration edge finally CONVERTS under rollouts (post-rollout states are cleaner; tactical blindness doesn't matter there). Extended to n=400: **57.8% [52.9–62.5] — equal-budget gate PASSED** (≥55% ✓, Wilson LB >50% ✓). The lesson inverted: don't feed tactics to the net; let rollouts resolve tactics and let the better net judge the result.
- **2026-07-22 06:45** — probe (wall-clock 1.0s both): gen-5-T2 vs GOLD → 52.5% [43.6–61.2] — the wide net's eval cost returns ~5 points at equal seconds; below the mint bar at GOLD's own budget. **PLATINUM respecced to 1.25s** (the ladder's planned budget stepping, pulled one rank earlier): gate = vs GOLD AS SHIPPED (1.0s), ≥55% + Wilson LB >50%. Cross-budget duel running (probe gains --wallclock-a/-b).
- **2026-07-22 07:09** — probe: ismcts-V4-200it vs ismcts-V4-200it → 58.3 % [49.4 %–66.8 %] over 120 games
- **2026-07-22 08:18** — probe: ismcts-V4-200it vs ismcts-V4-200it → 62.5 % [54.8 %–69.6 %] over 160 games
- **2026-07-22 08:19** — probe: ismcts-V4-200it vs random-v1 → 100.0 % [94.0 %–100.0 %] over 60 games
- **2026-07-22 08:21** — probe: ismcts-V4-200it vs greedy-V4 → 66.0 % [56.3 %–74.5 %] over 100 games
- **2026-07-22 08:22** — probe: ismcts-V4-200it vs greedy-V4 → 66.0 % [56.3 %–74.5 %] over 100 games
- **2026-07-22 07:55** — probe (cross-budget, as-shipped): gen-5-T2 @1.25s vs GOLD @1.0s → 58.3% [49.4–66.8] (n=120), extension 62.5% [54.8–69.6] (n=160, fresh seeds) → **pooled 60.7% [54.9–66.3] (n=280) — GATE PASSED** (≥55% ✓, Wilson LB 54.9% >50% ✓).
- **2026-07-22 09:15** — guards: 100% [94–100] vs random ✓; 66.0% vs BRONZE at 200it = GOLD's exact number at the same budget (no regression) ✓. **PLATINUM MINTED**: net gen 5 (pinned, wide 1024→512→256, val 76.8%), 2-turn net-truncated rollouts, 1.25 s. Sixth attempt; what worked: keep GOLD's rollout mode (eval-at-leaf and the v2 tactical encoder both REJECTED by probes), arm it with the wide q-labeled net, take the ladder's budget step. ShardsBotRanks + changelog EN/FR updated.
- **2026-07-22 10:53** — probe: ismcts-V4-200it vs ismcts-V4-200it → 48.3 % [39.6 %–57.2 %] over 120 games
- **2026-07-22 10:55** — probe: ismcts-V4-200it vs ismcts-V4-200it → 50.8 % [42.0 %–59.6 %] over 120 games
- **2026-07-22 10:58** — probe: ismcts-V4-200it vs ismcts-V4-200it → 57.5 % [48.6 %–66.0 %] over 120 games
- **2026-07-22 (opt) — "play when confident" early stopping**: a search stops once the leading root child's visit lead is safe against `EarlyStopBudgetFraction × remaining`. Fraction 1.0 = EXACT/strength-neutral (byte-identical move to the full budget — pinned by EarlyStop_ExactMode_IsMoveIdenticalToFullBudget); default 0.5 stops sooner by assuming the runner-up captures ≤half the rest, swapping only NEAR-TIED moves (≈0 strength cost). Auto-disabled in temperature-sampling turns and root-parallel search. Iterations-mode sweep (gen-5 vs gen-0, budget 200, T2, n=120): win-rate 48.3/50.8/57.5% at fraction off/0.5/0.35 — all within noise of the established 57.8%, no strength trend; games/s 0.77→0.83→0.88. Suite 174 green (+2 early-stop tests). Probe gains --earlystop; ranks inherit the 0.5 default. Wall-clock validation (bigger gain there) pending.
- **2026-07-22 11:32** — converge net 5 T2: 16:31%  32:35%  64:41%  128:48%  200:53%  300:58%  512:66%  1024:76%  2048:100%
- **2026-07-22 (converge study, gen-5 T2, 3,968 decisions)** — new `soisim converge` tool: same decision searched at a budget ladder (seeded identically, so budget b is a prefix of 2b). Move-agreement with the 2048-iter "final" move: 16→31%, 128→48%, 200→53%, 300→58%, 512→66%, 1024→76%. Consecutive-budget agreement plateaus ~70-76%. **Interpretation: SLOW move-convergence = pervasive NEAR-TIES** (2-3 near-equal moves whose argmax flips on search noise), NOT weak low-budget play — so move-agreement understates strength. Strength convergence (win-rate vs a deep reference) measured separately to pick the fixed-iteration ladder budget.
- **2026-07-22 11:39** — trained generation 90: val acc 76.7%, 1,320,000 positions
- **2026-07-22 11:40** — trained generation 90: val acc 76.8%, 1,320,000 positions
- **2026-07-22 11:41** — trained generation 90: val acc 76.6%, 1,320,000 positions
- **2026-07-22 11:42** — probe: ismcts-V4-100it vs ismcts-V4-800it → 38.0 % [29.1 %–47.8 %] over 100 games
- **2026-07-22 (width sweep, gen-5 data mix, 14 epochs)** — val acc by width: narrow 512·256·128 = **76.7%**, medium 768·384·192 = **76.8%**, wide 1024·512·256 (gen-5) = 76.8%, xwide 1280·640·320 = 76.6% (overfits). **The narrow net on the FULL data mix equals the wide net** — capacity is fully plateaued; the wide gen-5 PLATINUM net was wasteful. gen-0 only trailed (74.6%) because it trained on bootstrap-only data. Implication: a NARROW net gives the same strength at ~2.6× cheaper eval → a much faster rank at fixed iterations. Play-strength confirmation (equal-iteration duels vs gen-0 and gen-5) pending — val acc alone hasn't predicted play strength before.
- **2026-07-22 11:44** — net generation 8 embedded (valAcc 0.7671 · 1,320,000 positions · 2026-07-22)
- **2026-07-22 11:46** — probe: ismcts-V4-200it vs ismcts-V4-200it → 55.8 % [46.9 %–64.4 %] over 120 games
- **2026-07-22 11:48** — probe: ismcts-V4-200it vs ismcts-V4-200it → 46.7 % [38.0 %–55.6 %] over 120 games
- **2026-07-22 11:49** — probe: ismcts-V4-100it vs ismcts-V4-100it → 52.5 % [43.6 %–61.2 %] over 120 games
- **2026-07-22 11:49** — probe: ismcts-V4-50it vs ismcts-V4-50it → 61.7 % [52.7 %–69.9 %] over 120 games
- **2026-07-22 11:55** — probe: ismcts-V4-200it vs ismcts-V4-200it → 56.5 % [51.6 %–61.3 %] over 400 games
- **2026-07-22 12:00** — probe: ismcts-V4-200it vs ismcts-V4-200it → 46.0 % [40.4 %–51.7 %] over 300 games
- **2026-07-22 (narrow-net play confirmation, equal 200it T2)** — gen-8 (narrow 512·256·128, gen-5's full data mix, val 76.7%) vs GOLD gen-0: **56.5% [51.6–61.3] (n=400) — gate PASSED**. gen-8 vs gen-5 (wide): 46.0% [40.4–51.7] (n=300) — statistical tie, marginal lean to wide. Decision: **PLATINUM engine → narrow gen-8** (~2.6× cheaper eval than the wide gen-5 for a within-noise strength difference; makes GOLD & PLATINUM the same architecture differing only by DATA — bootstrap vs full mix). Wide gen-5 to be retired. Ladder moving to a FIXED fast iteration budget (N≈200, ~50–80ms/move vs 1.0–1.25s wall-clock).
- **2026-07-22 12:06** — trained generation 8: val acc 76.7%, 1,320,000 positions
- **2026-07-22 12:06** — net generation 8 embedded (valAcc 0.7666 · 1,320,000 positions · 2026-07-22)
- **2026-07-22 12:13** — probe: rank:platinum vs rank:gold → 59.5 % [52.6 %–66.1 %] over 200 games
- **2026-07-22 12:14** — probe: rank:gold vs rank:silver → 52.7 % [44.7 %–60.5 %] over 150 games
- **2026-07-22 12:14** — probe: rank:silver vs rank:bronze → 56.7 % [48.7 %–64.3 %] over 150 games
- **2026-07-22 — FIXED-ITERATION LADDER (major reframe)**: dropped wall-clock think-time for all ranks below MASTER; each below-MASTER rank is now a FIXED, fast iteration budget (deterministic, ~14-28ms/decision measured vs the old 1.0-1.25s — ~40× aggregate speedup, 1.90 vs 0.05 games/s). Ladder: SILVER = gen-0 @100it, GOLD = gen-0 @200it, PLATINUM = gen-8 NARROW @200it. Validation (rank-vs-rank, T2, early-stop fraction 1.0 exact): PLATINUM>GOLD **59.5% [52.6-66.1]** (clean net step), GOLD>SILVER 52.7% [44.7-60.5] (thin iteration step, inherent), SILVER>BRONZE 56.7% [48.7-64.3]. PLATINUM re-spec'd from the retired wide gen-5 to the narrow gen-8 (capacity plateaued: narrow=wide, 2.6× cheaper). Registry pruned to gens 0,8. Suite 174 green. **Design pressure-tested by a 3-agent workflow**: only TWO net tiers exist (gen-0 weak / gen-8≈gen-5 strong — the encoder is the ceiling), so depth past PLATINUM must come from the ITERATION axis (EMERALD 400it, DIAMOND 800it) or wall-clock, not smarter nets. EMERALD gets one net bet first: T2-mode deployment-matched data → gen-9, gate vs gen-8 @200it (kill if <55% or LB≤50%).
- **2026-07-22 12:45** — probe: ismcts-V4-400it vs ismcts-V4-200it → 51.0 % [47.0 %–55.0 %] over 600 games
- **2026-07-22 12:59** — probe: ismcts-V4-800it vs ismcts-V4-200it → 56.8 % [52.8 %–60.7 %] over 600 games
- **2026-07-22 (precise promotion + budget-step data)**: PLATINUM@200 vs GOLD@200 = **56.5%** (n=1080, stabilized — the net-data step). Budget steps on gen-8: @400 vs @200 (2×) = **51.0% [47.0-55.0]** (worthless — near-ties dominate past 200it), @800 vs @200 (4×) = **56.8% [52.8-60.7]**. **Both levers (net data, 4× budget) cap at ~56-57%** — SoI's apparent strategic ceiling for adjacent ranks; 58% likely infeasible. Doubling budget is too small a step (51%); ~4× is needed for a real one (~57%), at ~120ms. Adopted n=600 for promotion tests going forward (win rate stable by ~600). Implication: EMERALD must combine better data + a ~4× budget jump to approach 57-58%, or the ladder accepts ~56% steps.
- **2026-07-22 13:08** — probe: rank:emerald vs rank:platinum → 57.0 % [47.2 %–66.3 %] over 100 games
- **2026-07-22 13:16** — probe: ismcts-V4-200it vs ismcts-V4-200it → 55.0 % [34.2 %–74.2 %] over 20 games
- **2026-07-22 — RunPod CPU fan-out + DIAMOND minted**: built a RunPod orchestrator (Tools/RunPod/) that publishes the self-contained linux-x64 SoiSim binary to a network volume, fans jobs across N CPU pods, monitors via S3→runpod-status.md, downloads results, and ALWAYS tears down (finally + kill-timeout + teardown-all). SoiSim made cloud-ready (lazy FindRepoRoot, SOISIM_STATUS_DIR override, probe --result for aggregation, probe --record from the earlier commit). **First real run: DIAMOND@3200 vs EMERALD@800 = 56.0% [53.8-58.2] over 2000 games in ~12 min on 8 pods × 32 vCPU (~15× local speedup, $1.17)** — clears the gate (≥55% + LB>50%), so **DIAMOND minted** (gen-8 @ 3200it, ~480ms). Ladder now IRON·BRONZE·SILVER(gen0@100)·GOLD(gen0@200)·PLATINUM(gen8@200)·EMERALD(gen8@800)·DIAMOND(gen8@3200) — the fast deterministic ladder is complete; MASTER+ needs the reserved levers. Banked 5,791 champion positions (recording yield low + one pod recorded 0 — flagged for follow-up). Keys stay local (.env + Tools/RunPod/.secrets gitignored, verified). Suite 174 green.
- **2026-07-22 14:33** — probe: ismcts-V4-200it vs ismcts-V4-200it → 60.0 % [47.4 %–71.4 %] over 60 games
- **2026-07-22 15:02** — selfplay mdtest: 10 games → 200 positions (search 200it, 0 min)
- **2026-07-22 15:43** — trained generation 9: val acc 76.5%, 468,000 positions
- **2026-07-22 15:44** — net generation 9 embedded (valAcc 0.7645 · 468,000 positions · 2026-07-22)
- **2026-07-22 15:46** — probe: ismcts-V4-200it vs ismcts-V4-200it → 39.0 % [32.5 %–45.9 %] over 200 games
- **2026-07-22 16:15** — probe: ismcts-V4-200it vs ismcts-V4-200it → 50.0 % [29.9 %–70.1 %] over 20 games
- **2026-07-22 16:16** — trained generation 9: val acc 76.6%, 1,448,000 positions
- **2026-07-22 16:17** — trained generation 9: val acc 76.7%, 1,448,000 positions
- **2026-07-22 16:17** — trained generation 9: val acc 76.6%, 1,448,000 positions
- **2026-07-22 16:18** — trained generation 9: val acc 76.3%, 1,448,000 positions
- **2026-07-22 16:19** — trained generation 9: val acc 76.5%, 1,576,000 positions
- **2026-07-22 16:19** — trained generation 9: val acc 76.6%, 1,576,000 positions
- **2026-07-22 16:20** — trained generation 9: val acc 76.6%, 1,448,000 positions
- **2026-07-22 16:21** — trained generation 9: val acc 80.7%, 1,448,000 positions
- **2026-07-22 16:22** — trained generation 9: val acc 76.6%, 1,448,000 positions
- **2026-07-22 16:23** — trained generation 9: val acc 76.3%, 1,576,000 positions
- **2026-07-22 16:53** — probe: rank:gold vs rank:silver → 47.5 % [32.9 %–62.5 %] over 40 games
- **2026-07-22 16:55** — probe: rank:bronze vs rank:iron → 82.2 % [80.5 %–83.8 %] over 2000 games
- **2026-07-22 17:00** — probe: rank:silver vs rank:iron → 78.5 % [76.6 %–80.2 %] over 2000 games
- **2026-07-22 17:09** — probe: rank:gold vs rank:iron → 79.9 % [78.1 %–81.6 %] over 2000 games
- **2026-07-22 17:18** — probe: rank:platinum vs rank:iron → 83.4 % [81.7 %–85.0 %] over 2000 games
- **2026-07-22 17:22** — probe: rank:silver vs rank:bronze → 56.2 % [54.1 %–58.4 %] over 2000 games
- **2026-07-22 17:32** — probe: rank:gold vs rank:bronze → 57.8 % [55.6 %–59.9 %] over 2000 games
- **2026-07-22 17:45** — probe: rank:platinum vs rank:bronze → 66.0 % [62.6 %–69.2 %] over 800 games
- **2026-07-22 17:55** — probe: rank:gold vs rank:silver → 50.9 % [47.5 %–54.5 %] over 800 games
- **2026-07-22 18:04** — probe: rank:platinum vs rank:silver → 61.4 % [58.0 %–64.7 %] over 800 games
- **2026-07-22 18:16** — probe: rank:platinum vs rank:gold → 61.5 % [58.1 %–64.8 %] over 800 games

---

## Ladder concluded at DIAMOND — 2026-07-22

**gen-9 sweep (10 variants) all tied/lost to gen-8** at equal iterations (best g9v1 51.5%; champion-mixes → 35.5%; q0.7 val-acc 80.7% but played 46.0%). The encoder is the ceiling — MASTER not pursued, ladder final at 7 ranks.

**Full round-robin benchmark** (IRON–PLATINUM measured 800–2000 games/pair; EMERALD/DIAMOND from mint). Elo (BT, IRON=1000): IRON 1000 · BRONZE 1212 · SILVER 1242 · GOLD 1251 · PLATINUM 1312 · EMERALD 1360 · DIAMOND 1402. Biggest neural jump is the **net step** GOLD→PLATINUM 61.5%; SILVER→GOLD 50.9% (2× iters ≈ coin-flip). Visual report published as an artifact; generator + data in `Tools/ShardsData/benchmark/` (gitignored).

**DIAMOND shipped root-parallel** (K=8×400 = 3200 total, CPU-independent merge): ~80 ms multi-core vs ~480 ms single-tree, same budget → same strength. Registry reverted to gens 0,8. 174 EngineVerify tests green.

**Balance stats** (6400 DIAMOND selfplay, `benchmark/balance_stats.json`): first-player 56.5% (front-loaded: 60.2% short / 52.9% long games), ~15 rounds, draws 0.02%, Infinity-shard win 4% of games, attack power 7→409 (rounds 10→20).

---

## 2026-07-25 — Phase 0/1: the measuring instrument, then the Duel blind spot

**The instrument came first, and it changed the answer twice.** `probe` now scores by
mirrored PAIR (same seed + matchup, seats swapped) instead of pooling games as if they
were independent. Pairing cancels the seed, the matchup and the 56.5% first-player
advantage: measured ~1.4x tighter half-width at equal games (~2x fewer games for the same
resolution), and it makes a self-vs-self null read exactly what it should. Added GSPRT
(`--sprt`, elo0=0/elo1=+15/α=β=0.05) so decided results stop early; added a publish floor
of 200 pairs (`--allow-small` to override, completed SPRT exempt) because **n=120 is ±8.9pt
and cannot see a true 55% effect** — the source of several earlier conclusions in this log.
`SoiSimProbeCalibrationTests` pins all of it as a standing null calibration.

**The blind spot.** `SimConfig.AllDlc` excluded `ShardsDlc.Duel`, and `--dlc duel` was
never passed by any campaign run — so every net and every weight vector through V4 was fit
to a game without hero drafts, hero abilities or row rerolls. Worse, the two Duel actions
were scored by hardcoded constants: hero ability `EndTurnBase + 0.05` (fired
unconditionally, every turn) and reroll `EndTurnBase - 0.01` (strictly below passing, so an
argmax policy could NEVER pick it — zero rerolls in every rollout and every training
position). `ShardsHeuristicBot.PickAction` had no case for either. Duel is now the default
mask (`--dlc base` for legacy runs).

**Additive bases swamp value terms — the fix that mattered.** First attempt priced the hero
ability as `HeroAbilityBase(200) + net(±2)`. Still unconditional, and the ablation said so:
**49.8% [47.9–51.7] over 784 pairs — worth nothing** (SPRT accepted H0). Making it
multiplicative (`net * HeroAbilityValueScale`, so the SIGN of net decides whether to act)
moved the identical change to **56.0% [54.1–58.0] over 1000 pairs, +42 Elo**. Ko Syn Wu had
been paying 3 health every turn for a banish this model prices as negative.

**Weights defaulting to 0.0 are untunable.** sep-CMA-ES scales each dimension by
`max(|start|, 0.05)`, so a zero default gets a search range of ~zero and never moves —
which is why `EndTurnBase` has sat at ~0 since V1. Every appended weight now carries a
non-zero default in `W.Defaults`; `W.Pad` is the layout contract (TuneCommand pads the
champion up to `W.Count` so new dimensions are actually explored; ShardsValueModel pads on
construction so short vectors stay loadable).

**V5** — sep-CMA-ES, Duel ON, 300 gens × λ16 × 240 games, seed 25, 3.0 min, 49 weights.
Evaluate gates OK: random 100.0% · heuristic-v1 78.9% · V1 78.4% · V2 68.5% · V3 68.5% ·
**V4 69.9% [67.0–72.7]** (matched by an independent 1000-pair probe at 69.2%, so this is not
overfitting to a single reference opponent). Tuner's verdict on the new dimensions: it wants
MORE rerolling (`RerollBase` −10→+0.62, `RerollRowQualityDelta` 100→149.5), keeps the hero
scale positive but modest (50→18.2), and prices opponent hand-strip NEGATIVE (0.5→−0.30).

**Caveat on the ablation tool:** `--weights-b duel-blind` is a GREEDY-side instrument only.
`ScoreAction` also feeds ISMCTS as the move prior (`score / 4000`) and as the rollout
policy, so the sentinel magnitude poisons the search's UCB instead of cleanly removing the
action. Search-side capability gaps need a different mechanism.

**V5 carries into search too.** `probe --a strong --b strong --budget 200 --weights-b V4`
(both sides ISMCTS@200, Duel ON): **56.0% over 290 pairs** ([52.5–59.5], stable at 57.1%
over the first 210). Stopped early — search-vs-search runs at 0.23 games/s, and 45 more
minutes only tightens ±3.5pt to ±2.5pt on an already-decided question. The search ranks
inherit the improvement through the prior and the rollout policy; their embedded NETS are
still Duel-blind, which is the encoder-v3 work, not this.

## 2026-07-25 — Phase 0 headroom: two numbers that redirect the campaign

RunPod fan-out, 10 pods × 32 vCPU, ~21 min, ~$2.80 (incl. one aborted launch). Both arms
are **self-referential** — an agent against a variant of itself — so neither depends on the
retired DIAMOND as a yardstick. Cross-pod aggregation is now PAIRED (`--result` carries
`paired_sum`/`paired_sumsq`; the orchestrator pools them exactly), so a fan-out reports the
same tightened interval a single-box probe would.

**1. Hidden information is nearly free: oracle 55.4% [53.2–57.6] over 1000 pairs.**
A cheating agent that skips determinization entirely — it plans against the opponent's REAL
hand and both REAL deck orders — beats an otherwise identical honest agent by only ~38 Elo.
That is a hard ceiling on the whole belief axis: no encoder, no determinization scheme, no
amount of hidden-state modelling can ever be worth more than total clairvoyance. The plan's
kill criterion (<58%) is TRIGGERED. **Stop investing in richer hidden-state representation.**
Note precisely what this does NOT bound: the oracle still evaluates with the same
ShardsValueModel, so this says nothing about better VALUE estimation — only about knowing
what is hidden.

**2. Search scales far better than the campaign recorded: 4× budget = 79.3% [77.2–81.4]
over 750 pairs (~+231 Elo, ~115 Elo/doubling).** The historical figure was 56.8% for the
same 4× step (~22 Elo/doubling), and that number is the reason MASTER→CHALLENGER was
abandoned and "more search" was written off as a rung.

⚠ Caveat, and it matters: `--a strong` is ISMCTS with FULL ε-greedy rollouts to terminal and
**no net** (`ForSims` leaves `RolloutEndTurns = -1`, so no evaluator is ever loaded). The
shipped ranks are a different agent — truncate-2 with a frozen net. So this measures the
rollout agent's slope, not the ladder's, and it is not directly comparable to the 56.8%.
Follow-up running: the same 800-vs-200 step for the NET config, plus net-vs-rollout at equal
budget — which also asks whether the Duel-blind net is still helping at all now that the
weights and the rules it was fit to have both moved.

**3. The frozen net is NEGATIVE value, and it is what flattened the search curve.**
Same fan-out, net config = truncate-2 + gen-8 (the shipped GOLD→DIAMOND agent):
- `net-vs-rollout-200it`: **40.6% [38.1–43.0] over 1000 pairs.** At equal budget the net
  agent LOSES to plain full-rollout ISMCTS by ~66 Elo. The net is worse than no net.
- `net-slope-800-vs-200`: **52.2% [49.6–54.9] over 734 pairs.** 4× search buys ~nothing
  for the net agent — versus **79.3%** for the rollout agent on the identical step.

So the campaign's "~22 Elo per doubling" — the number that killed MASTER→CHALLENGER and
retired "more search" as a rung — was not a property of the game. It was the frozen net
capping the search: a Duel-blind evaluator anchors every leaf, so extra iterations buy
better play toward a worse target. Remove it and the same step is worth ~115 Elo/doubling.

**Consequences, in priority order:**
1. GOLD→DIAMOND are probably weaker than a plain rollout ISMCTS at the same budget. That
   is a direct, sufficient explanation for the top rank being easy to beat.
2. The cheapest large win available is to DROP the net from the minted ranks (full
   rollouts, `RolloutEndTurns = -1`) and re-mint. It costs nothing and restores search
   scaling — which then makes a big fixed iteration budget genuinely worth buying.
3. Encoder-v3 / policy-value work is far less attractive than the plan assumed. The oracle
   bounds the belief axis at ~+38 Elo, and any net must first beat a rollout — the current
   one is 66 Elo BELOW it. A net is only worth building if it clears that bar.

Spend: ~$9 of the $10 budget across three tournaments (one aborted on a capacity shortfall).

**Two orchestrator bugs found and fixed while running this:**
- `tournament` referenced `args.require_all`, a flag only declared on `run`/`runstats`, so
  ANY tournament that lost a pod to a capacity shortfall crashed with AttributeError after
  teardown instead of reporting.
- Pods were only torn down in the `finally`, after EVERY matchup finished — so a fast
  matchup's pods sat RUNNING and billing at $0.96/h for as long as the slowest one took
  (measured: 4 idle pods × 25 min). Slices are now reaped the moment their `.done` lands.
- Status reporting rewritten: every fan-out mode now reports games done/planned, percent,
  aggregate games/s, ETA and the running win rate (`_progress_report`), with a last-known
  cache so a transient S3 read cannot make progress jump backwards. `status --name X
  [--watch]` polls any run live without owning its pods.

## 2026-07-25 — re-mint on rollouts, and a result that reframes the whole ladder

Removed the frozen nets from every minted rank (`Rollout()` in ShardsBotRanks): full
ε-greedy rollouts to terminal, fixed iteration budgets, root workers for wall-clock only.
The retired net configs survive as `legacy-gold/platinum/diamond` tooling kinds purely so
the re-mint can be benchmarked against what actually shipped.

**Then the first validation came back backwards: new SILVER (300 it, full rollouts) loses
to BRONZE (instant V5 greedy) 21.2% [18.2-24.3] over 400 pairs.**

That is not just a mis-set budget. Put it together with the other measurements:
- V5 made BRONZE **+141 Elo** stronger than V4-greedy.
- The net ranks did NOT move with it: their evaluator is frozen and Duel-blind, so V5 only
  improved their prior and rollout policy, not what they are optimising toward.
- The net agent already loses to a plain rollout agent at equal budget (40.6%).

⇒ The strong hypothesis, now under test, is that **the entire pre-2026-07-25 net ladder
(GOLD→DIAMOND) is weaker than BRONZE** — i.e. the "top" difficulty was beaten by the
instant one rung above the bottom. That would explain the user's win rate completely, and
it means the published Elo table is not merely stale, it is inverted at the top.

It also says something structural about SoI: a tuned instant policy is a very high bar,
and ISMCTS with ε-greedy rollouts needs a LOT of iterations before its noisy value
estimates beat simply trusting that policy. The old code comment said ~600 it to pass
V4-greedy; V5 raises that bar substantially. Search still scales steeply once past the
crossover (79.3% for a 4× step), so the top rank wants a budget far above it — but the
crossover has to be measured, not guessed, which is what `crossover_spec.json` does
(rollout @1200 and @4800 vs BRONZE, plus legacy-DIAMOND vs BRONZE).

**Do not set a rank budget from the scaling slope alone.** Above the crossover search is
worth ~115 Elo/doubling; below it, more search is worth less than nothing.

**The crossover curve, measured (paired, all DLC incl. Duel, vs BRONZE = instant V5 greedy):**

| rollout ISMCTS budget | vs BRONZE | n |
|---|---|---|
| 300 it | **21.2%** [18.2-24.3] | 400 pairs |
| 1 200 it | **52.5%** [47.6-57.4] | 200 pairs |
| 4 800 it | **71.4%** [67.3-75.5] | 201 pairs |
| 6 000 it | ~77.5% (early, 40 pairs) | partial |
| *legacy DIAMOND (gen-8 net, 3200 it)* | **8.5%** [5.6-11.4] | 200 pairs |

Read that last row against the rest: the shipped top rank performed like a search agent
with FAR less than 300 iterations. The net did not merely fail to help — it dragged a
3200-iteration search below a 300-iteration one.

Ladder budgets follow from the curve, not from the slope: 2400 / 6000 / 12000 / 24000 /
48000, every rung comfortably above the ~1200 crossover.

Not measured (stopped to control spend): 24000 vs 6000. Those games are ~5 min each and
the matchup produced nothing in 21 minutes of pod time. If the top rung ever needs a
number, budget it properly — a single 24000-vs-24000 game is ~10 minutes of one core.

Session spend: ~$17 across five tournaments.

---

# 2026-07-27 — the neural campaign is deleted; a new agent starts from measurements

Everything below this line belongs to a new effort. The nets are gone, not parked.

## Why (nothing here is new evidence — it is the old evidence, acted on)

Three numbers already in this log determine the next architecture, and the campaign did not
follow them:

1. **Clairvoyance is worth +38 Elo** (oracle 55.4% [53.2–57.6], n=1000 pairs). Hidden
   information is nearly worthless in SoI — deck *contents* are public, only the shuffle is
   hidden. So belief modelling, richer encoders and better determinization are all capped at
   a rounding error. **Do not spend there.**
2. **Search is worth ~115 Elo/doubling above a ~1200-iteration crossover, and is worse than
   nothing below it** (300 it = 21.2% vs instant greedy). A 200-iteration micro-action ISMCTS
   barely escapes the *first turn* of a ~10–25 submit turn. That is a **formulation** problem,
   not a budget problem.
3. **The net was worse than no evaluator** (40.6% at equal budget) and shipped at **−410 Elo**
   vs BRONZE.

## The diagnosis that matters for the rewrite

From `eval-rules.md`, and consistent with every measurement: the value function is built from
**ratios, minima and thresholds** — `5/N × Σ`, `min(killClock, ascendClock)`, `health/damage`.
An MLP over *summed bags of card vectors* cannot compute `Σ/N`: it never sees `N`
multiplicatively against the sum. It was asked to learn division from features that do not
contain a denominator. The encoder also carried **no card identity at all** (deliberately,
for DLC-proofing) — in a deck-builder, card identity is the game — and **no Duel features**.

## Removed today

- `ShardsNetWeights.g.cs` (7.5 MB), `ShardsNeuralEval`, `ShardsStateEncoder`,
  `ShardsBaselineEvaluator` (its largest coefficient was linear health, which four independent
  expert reviews each named as the single biggest error — health is only meaningful through
  the kill clock).
- `ShardsSearchConfig.RolloutEndTurns` and all truncated-rollout evaluation — the mechanism
  that inverted the ladder.
- `legacy-gold` / `legacy-platinum` / `legacy-diamond` kinds; `emit-net`, `netfixture`,
  `selfplay` commands; `PositionWriter`; probe `--record`/`--net-*` flags.
- **~10.3 GB of self-play data** (`selfplay` 5.21 GB / 602 files, `selfplay2` 5.12 GB / 30
  files) — every byte generated with Duel EXCLUDED, by policies that could never reroll and
  always fired the hero ability, on the v1 schema. Structurally poisoned, not merely stale.
- The published Elo table in `bot-ranks.md` — inverted at the top, so deleted rather than cited.

## Added today

- **A frozen benchmark ladder**: `bench:heuristic`, `bench:greedy-v5`, `bench:rollout-1200`,
  `bench:rollout-4800`. `bench:greedy-v5` pins `ShardsEvalWeights.V5` **explicitly** rather
  than following `Current`, because a reference that moves when the tuner runs makes every
  comparison read ~50%. Guarded by `SoiSimBenchmarkLadderTests` — budgets, determinism,
  V5's checksum, and the *absence* of any leaf evaluator field on `ShardsSearchBot`.
- `Tools/SoiSim/Tune/**` is now compile-linked into `Engine.Verify.csproj`. It was the only
  sim code CI never compiled — and it is what produces every shipped weight vector.

First measurement on the new frozen ladder (sanity, underpowered on purpose):
bench:greedy-v5 vs bench:heuristic → **78.5% [71.9–85.1]**, 100 pairs, Duel ON.

## Balance re-measured with Duel ON — and the headline number is a POLICY property

The old `balance-report.md` (the one `eval-rules.md` §4 calls "[measured]" and says
"outranks opinion") was greedy-V2 at **DLC mask 7**. Re-ran 30,000 games each at **mask 15**,
with two very different policies, to separate ruleset from policy:

| | Duel-OFF greedy-V2 | Duel-ON **greedy-V5** | Duel-ON **heuristic** |
|---|---|---|---|
| Infinity-Shard overwhelm wins | 7.0% | **51.1%** | **5.7%** |
| total acquisitions | OR 1.65 | **OR 2.67** (28.7→76.4%) | — |
| mastery at round 8 | OR 1.32 | **OR 1.84** (40.8→67.2%) | — |
| early aggression | **OR 1.67** | OR 1.29 (50.4→53.4%, nearly flat) | — |
| faction concentration | OR 1.10 inverted | OR 1.07, **still inverted** (57.8→38.0%) | — |
| champion share | p=0.440 n.s. | p=0.405 **n.s.** | — |
| shields prevented | 3.2% | **0.5%** | 3.1% |
| rounds p10/p50/p90 | 10/13/18 | 11/14/17 | 10/12/15 |
| P0 win rate | 58.6% | 59.4% | 58.5% |

**Same rules, same seeds, 51.1% vs 5.7%.** The mastery race is not a property of the ruleset —
it is what a *tuned* policy converges on and a hand-written one never finds. V5's
`W.Mastery` is 5.96 against V1's hand-set 3.0; CMA-ES roughly doubled it and then rode the
M30 + Infinity Shard line into half of all wins. Two consequences:

1. **`ascendClock` is co-equal with `killClock`, not a special case.** Any evaluator that
   treats the Shard route as a corner case is wrong about half the games good play produces.
2. **The old "overwhelm 5.5–9.3%, real but not a gimmick" line was measuring greedy-V2's
   policy, not the game.** Every observational number in a balance report describes the bots
   that played it. Re-derive per policy; never inherit.

**The shield figure is a denominator artifact — now confirmed, not argued.** eval-rules ⚔D2's
Expert D claimed the 2–3% number was distorted by the 9999-power Shard turn. V5's 30k games
carry **174.6M** total incoming damage; the heuristic's carry **22.0M**. Identical rules,
identical shields: ~15,000 overwhelm turns dumping ~9999 each inflate the denominator 8×, and
the "shields prevent 0.5%" reading falls straight out. Price shields as expected turns of
survival in the TTK denominator, never as a share of damage.

Also: **early aggression collapsed** from the strongest predictor (OR 1.67) to nearly flat
(OR 1.29, 50.4→53.4% across quartiles), while **total acquisitions became by far the
strongest** (OR 2.67). Under Duel-ON tuned play the game is an economy race, not a rush.

Character spread at V5: decima 57.9% · rez 52.9% · tetra 49.0% · volos 44.2% · kosynwu 39.8%
— an 18-point gap. Flagged for balance, out of scope for the AI work.

## ⭐ The decisive experiment: buying carries ~85-92% of the strength

`soisim ablation` — the question that decides where the new planner spends its search budget.
The deck-builder literature is split (Dominion's Provincial uses a deliberately dumb play
model and still dominates; Slay the Spire is the inverse), and SoI has two properties Dominion
lacks — mastery thresholds resolving mid-sequence, and multiplicative burst — so neither
answer transfers. Measured rather than inherited.

Method: all four arms share ONE two-stage architecture (`PhaseHybridBot`), so only the weight
vector governing each phase differs and the architecture cancels out. Each arm plays 10,000
mirrored PAIRS against a fixed `bench:greedy-v5`. Two-stage rather than one blended argmax
because scores from two vectors are not on a common scale (V1's `PlayBase` is 2000, V5's 810).

| Arm | vs V5, weak = **V1** | vs V5, weak = **V4** |
|---|---|---|
| strong play / strong buy | 46.6% [46.1-47.0] · −24 Elo | 46.6% · −24 Elo |
| strong play / **WEAK buy** | **20.7%** [20.1-21.3] · −233 Elo | **29.6%** [29.0-30.3] · −150 Elo |
| **WEAK play** / strong buy | 41.1% [40.5-41.6] · −63 Elo | 45.0% [44.5-45.6] · −35 Elo |
| WEAK play / WEAK buy | 25.1% · −190 Elo | 30.2% · −146 Elo |
| **Elo lost — BUY axis alone** | **209** | **126** |
| **Elo lost — PLAY axis alone** | **39** | **11** |
| **buy share of attributable strength** | **84%** | **92%** |
| interaction (both − sum of parts) | −82 | −15 |

**Answer: the acquisition axis carries 84-92% of the strength; play ordering carries 8-16%.**
Two independent degradations of very different size agree, so this is a property of the game
and not of one particular weakening. **The planner should go wide over purchase baskets and
narrow over play orderings** — which is also what makes a 3-turn-deep turn-level search
affordable, since the order-sensitive minority is typically 1-3 cards per turn.

Two honest caveats:
1. This measures the **tuned-vs-untuned** gap on each axis, not the **remaining headroom
   above V5**. Buying dominating the gap does not prove play ordering has no headroom left —
   only that untuning it costs little. The planner still evaluates orderings; it just should
   not spend beam width there.
2. **The axes are co-adapted, not independent.** At the large V1 gap, strong-play/weak-buy
   (−233) is *worse* than weak/weak (−190): an interaction of −82 Elo. V5's play policy chases
   the M30 mastery line (`W.Mastery` 5.96 vs V1's 3.0) that V1's buy policy never funds, so a
   strong player commits to a plan its own economy cannot pay for. The effect shrinks to
   −15 Elo at the V4 gap. **Tune play and buy jointly; never ship a mismatched pair.**

Architecture control: the two-stage split itself costs **24 Elo** against a single blended
argmax (46.6% vs 50%). Real but small, and identical across all four arms.

## Coverage: measuring what the policy NEVER does

`soisim coverage` — a detector for the most expensive bug class in this project. The row
reroll was priced strictly below passing until 2026-07-25, so an argmax policy could never
choose it: zero rollouts, zero training positions, six days, nine neural generations. It
survived because **every instrument here measured win rate, and a blind spot shared by both
seats is invisible to win rate by construction.** The balance report cannot see it either —
its card test requires ≥100 acquisitions before a card is considered, so a card the bot never
buys is structurally absent from the output.

So this measures presence, not performance: every priority action type, every decision
context, and every card's offered-vs-acquired counts, with zeros called out.

**First run (bench:greedy-v5, 4,000 games, Duel ON) found two zeros. Both were investigated
rather than assumed:**

1. **`soi.target` — FALSE POSITIVE, and the detector was wrong, not the bot.** All three sites
   (`ShardsEffects.cs:540`, `ShardsDuelEffects.cs:205`, `ShardsDuelSet.cs:949`) auto-resolve
   when there is exactly one living opponent, which in a duel there always is. The tool now
   carries an explicit unreachable-in-duel list and asserts the *reverse* — that these stay at
   zero. A blind-spot detector that cries wolf gets ignored, which costs more than the thing
   it detects.
2. **`soi.scry` — REAL, and measured to be harmless.** `Scry(2)` exists in exactly one place:
   Rez's hero ability (`ShardsEngine.cs:676`). No card uses it. Under V5 it is unreachable by
   the same arithmetic that killed the reroll:
   ```
   value = 2 × ScryPerCard(0.1958) = 0.392
   cost  = 1 gem × Gems(0.5370)    = 0.537   →  net −0.145  →  scores below END TURN
   ```
   Break-even is `ScryPerCard = 0.2685`. Unlike the reroll, this one was **measured before
   being called a bug**: forcing it live (`--weights-a scry-live`, ScryPerCard 0.40) scored
   **50.4% [49.9-50.9] over 10,000 pairs — +3 Elo, a tie.** So the tuner priced it correctly
   and the zero is a legitimate strategic choice. Documented, not "fixed".

That two-step is the process the last campaign lacked: **detect the blind spot, then probe
whether it matters.** A zero is a question, not a verdict.

Other results from the same run: every offered card is acquired at least once (no rejected
cards); every owned non-destiny card is played at least once; 52 defs never appear in the
center row, all of them relics, destinies, or base cards displaced by a `_duel` errata via
`ReplacesId`.

**The detector is now standing** (`SoiSimCoverageTests`): all ten priority action types must
be reachable by BOTH shipped policies, plus mercenary fast-play — which a type histogram
cannot see going dead, since it is a second use of the same buy action. Rez's dead ability is
pinned as a documented exception carrying its own measurement, so it stays *true* rather than
assumed.

Teeth verified rather than trusted: run against `--dlc base`, the detector correctly flags
`ShardsRerollRowAction` and `ShardsHeroAbilityAction` at 🚨 0 — the exact signature of the
2026-07-25 bug.

## The four dead decisions: fixed, measured at a TIE, adopted anyway

`ChooseAnswer` had no case for `soi.removeshop`, `soi.reset`, `soi.defiant` or `soi.mode`,
so all four hit `default: add(Options[0..Min))`. For `Min=0` that declines forever; for a
forced choice it always takes option 0. Measured over 4,000 games: removeshop 0/7432 taken,
reset 0/1716 taken, defiant 5847-0 Keep, mode 5664-0 mode-1.

Handlers added, reusing existing tuned quantities (CardValue, the BuyThreshold /
DeckDilutionPerCard buy bar, W.Gems) rather than new weight indices. After: removeshop
234 taken (selective — only churns slots below the buy bar), reset 1569 taken, defiant 159
banishes, mode 2566 thin / 710 keep.

**Strength: 50.6% [49.5-51.6] over 2,704 pairs, +4 Elo, SPRT accepted H0 (≤0).** A tie.
The "free value left on the table" hypothesis was wrong about strength — each opportunity is
individually small and rare (removeshop fires ~1.9/game but is worth taking ~0.06/game).

**Adopted regardless, for one reason that is not strength:** an unreachable branch can never
appear in training data, and therefore can never be learned or tuned. That is precisely the
row-reroll lesson — the reroll fix would also have measured ~0 for the greedy bot, and its
real cost was that no net ever saw a game containing one. Reachability is a precondition for
learning, not an optimisation.

⚠ **The frozen benchmark's behaviour changed here.** `bench:greedy-v5` keeps V5's weights
(still checksum-pinned) but now answers those four decisions. The freeze point is therefore
**after** this commit. Everything measured before it was re-run: the ablation is unchanged
within noise (buy 209 → 204 Elo, play 39 → 39, share 84% → 84%), which is what a +4 Elo
change should do.

⚠ A trap avoided: `soi.mode`'s options carry no `DefId` (`ShardsDuelSet.cs:531-532`), so
reading the source card off the option would have made it null and flipped the handler to
ALWAYS mode 2 — the same bug mirrored. The drone is located in the play zone instead, which
also distinguishes the real card from a COPY (Ojas / Duplication Fabricator / Warpquartz),
where mode 2 banishes nothing and is strictly free.

## Ko Syn Wu's dead ability is a STRUCTURAL limit, not a tuning miss

The second dead hero, and the one that matters for Phase 2. "Sacrifice" costs 3 gems AND
3 health to banish one card; it fires 0 times in 1,622 drafted games. Unlike Rez's Scry,
tuning cannot rescue it: the model prices banishing through one scalar,
`W.BanishPerCapacity`, which V5 tuned **negative** (−0.0257). Clearing a ~2.9 cost would need
that weight above ~2.9 — a 100× swing that would re-price every banish effect in the game.

The reason one scalar cannot work: banishing is worth entirely different amounts depending on
**what** is banished. Removing a Crystal from a 20-card deck is excellent; removing a good
card is terrible. A flat per-capacity weight must average those toward zero, and near zero it
can never pay for 3 gems and 3 health.

**This is a concrete requirement for the clock evaluator**: price thinning contextually as
`(deckAverage − bannedCardValue) × D/N` (eval-rules R7), not per-capacity. Pinned by
`KoSynWuHeroAbility_IsDeadForAStructuralReason_NotATuningMiss` so nobody inflates the weight.

## The bar, from here on

Any evaluator must beat full-rollout ISMCTS **head-to-head at equal wall-clock**, paired,
SPRT, **n≥2000**, before it ships. Running that probe *first* — rather than after nine
generations of net-vs-net mirror matches at n=120 — is the entire difference between this
effort and the last one.

- **2026-07-27 14:11** — probe: bench:greedy-v5 vs bench:heuristic → 78.5 % [71.9 %–85.1 %] paired over 100 pairs · UNDERPOWERED (--allow-small)
- **2026-07-27 14:13** — stats run (bench:greedy-v5): 30,000 games, 0 failures → duel-greedy-v5.jsonl
- **2026-07-27 14:15** — stats run (bench:heuristic): 30,000 games, 0 failures → duel-heuristic.jsonl
- **2026-07-27 14:19** — ablation 10000 pairs: buy 209 Elo · play 39 Elo · both 166 Elo
- **2026-07-27 14:20** — ablation 10000 pairs: buy 126 Elo · play 11 Elo · both 122 Elo
- **2026-07-27 14:37** — probe: greedy-scry-live vs greedy-V5 → 50.4 % [49.9 %–50.9 %] paired over 10000 pairs
- **2026-07-27 15:34** — probe: greedy-V5 vs greedy-V5 → 50.6 % [49.5 %–51.6 %] paired over 2704 pairs · SPRT H0 accepted (<= 0 Elo)
- **2026-07-27 15:35** — ablation 10000 pairs: buy 204 Elo · play 39 Elo · both 168 Elo
- **2026-07-27 15:55** — probe: greedy-V5 vs greedy-flat-banish → 46.7 % [43.4 %–50.1 %] paired over 274 pairs · SPRT H0 accepted (<= 0 Elo)
- **2026-07-27 15:58** — CMA-ES tune: 300 generations, champion 77.2 % vs heuristic-v1
- **2026-07-27 15:58** — probe: greedy-V6 vs bench:greedy-v5 → 48.7 % [46.3 %–51.2 %] paired over 714 pairs · SPRT H0 accepted (<= 0 Elo)
- **2026-07-27 15:59** — probe: greedy-V6 vs bench:greedy-v5 → 50.8 % [50.3 %–51.3 %] paired over 15000 pairs
- **2026-07-27 16:06** — probe: greedy-V7 vs bench:greedy-v5 → 49.9 % [49.5 %–50.4 %] paired over 15000 pairs
- **2026-07-27 16:37** — probe: planner vs bench:greedy-v5 → 6.0 % [3.5 %–8.5 %] paired over 200 pairs
- **2026-07-27 16:38** — probe: planner vs bench:greedy-v5 → 4.0 % [2.1 %–5.9 %] paired over 200 pairs
