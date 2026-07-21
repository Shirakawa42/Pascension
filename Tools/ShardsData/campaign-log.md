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
