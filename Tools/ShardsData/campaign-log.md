# SoI AI Campaign — history

- **2026-07-21 15:30** — ranked ladder shipped: IRON (heuristic), BRONZE (greedy-V2, 81.9% vs IRON), SILVER (full-rollout search 1.0s, ~77% vs BRONZE)
- **2026-07-21 17:40** — play-order fix (Carnivorous Vine deferral) → weights V4: 83.2% vs heuristic, 52.2% vs pre-fix greedy
- **2026-07-21 18:05** — truncation with hand-made evaluator REJECTED by probe (17.5% at wall-clock parity)
- **2026-07-21 18:50** — gen-0 bootstrap: 60,000 greedy games → 720,000 positions (20s)
- **2026-07-21 19:00** — trained generation 0 on the RTX 5090: 74.6% val accuracy (<1 min); PyTorch↔C# parity 1e-4 OK
- **2026-07-21 19:15** — perspective bug found by 0%-probe and fixed (net must be queried from the turn player's seat)
- **2026-07-21 19:25** — probe: net-truncated search vs SILVER-style full rollouts → **78.3% [66.4–86.9]** at equal iterations → **GOLD minted** (gen-0 net, 1.0s)
- **2026-07-21 19:50** — gen-1 selfplay started: 9,000 ISMCTS-100 games with net-0 leaf eval, temperature 8 turns
- **2026-07-21 21:44** - selfplay: 9000 games, budget 100 iters, 12 positions/game, threads=15 -> Tools\ShardsData\selfplay\gen1
