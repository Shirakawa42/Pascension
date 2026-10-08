# Hero fixes deployed to the active training campaign

The active campaign now uses **V6**, resumed from the same learned policy, optimizer, archive, RNG state and shared 12-hour budget. Rules SHA remains `85d8b59299818c19d226f4143812d324343f4808184717f141ae8b891043df9c`.

## Changes

- **Ko chooses before paying.** The AI sees its existing banish menu as a preview. Declining costs zero health. Committing a card submits the real Sacrifice action and the exact selected banish consecutively, paying 3 health once. Empty hand/discard removes the preview action. An unchanged declined target set cannot be reopened until its cards/zones change or a new turn begins, preventing a no-cost loop. Other banish effects and human game rules retain their existing behavior.
- **Rez remembers revealed cards.** Each player has private memory for up to four known top center-deck cards, obtained from its own Scry/reorder options. Public refills consume the corresponding entries; known bottom returns preserve remaining top knowledge. Shuffles, opposing private manipulation and unmodeled reveal/deck changes invalidate it conservatively. This code never reads hidden center-deck entries to populate or repair memory.
- **The policy sees useful costs and identity.** Twenty formerly unused observation slots now encode known card ID/cost/faction, a five-way hero indicator, Sacrifice preview state and the hero's health/gem costs. All original dimensions and card indices are preserved. The new columns start at zero in every retained policy, so unrelated learned behavior survives migration.
- **Monitoring follows the new run.** Trainer and host detection recognize V6; current random/natural balance windows use only V6 sessions. Old V5 observations remain on disk and in the prior investigation, rather than being mixed into the post-fix sample. Frozen evaluation scheduling was restarted with disjoint seeds and a migrated anchor.

No blanket health-loss reward penalty was added. The actual health cost remains part of winning/losing the game; useful deck thinning remains available. Structural prevention is stronger than hoping a penalty eventually teaches the model to avoid paying for no banish. The policy still has to learn whether a particular banish is worth its cost.

Implementation: [HostV6 contract](../experiments/HostV6/README.md), [adapter](../experiments/HostV6/Adapter.cs), [private knowledge ledger](../experiments/HostV6/CenterKnowledge.cs), [features](../experiments/HostV6/HeroFeatures.cs), [runtime](../experiments/variant_v6_runtime.py), [migration](../experiments/migrate_runtime_v6.py).

## Validation and limits

1. Twenty focused C# checks passed: preview/decline/empty targets/reconsideration/atomic commit, Scry capture and privacy, retention/burying, sequential rerolls and shuffle invalidation.
2. Existing host self-tests passed, including 32 replay seeds, single/multiple worker equivalence, hidden-information fixtures, staged decision coverage and integer splits. Hero-curriculum routing/statistics tests also passed, including all 20 ordered hero assignments and unchanged setup RNG.
3. The legacy control mode matched V5 exactly over **600 steps × 8 lanes**: observations, candidates, masks, deciding seats, terminal flags and rewards. Training forcibly enables the fixes for both seats; the control switch is for explicit frozen evaluations.
4. A **640-game CPU comparison** completed with zero censored games. The same frozen weights played both sides; only policy A received the new behavior. Ko made **1,164 previews: 500 free cancellations and 664 committed banishes**. Health assertions checked every transition: preview/cancel spends zero; commit spends exactly 3. Score was **48.91%**, conservative paired-seed interval **41.31–56.50%**. This passed the predeclared regression screen but does **not** establish an immediate strength gain. New memory columns were zero during this comparison and require learning. [Full comparison](v6-hero-fixes-cpu.json).
5. A **256-game GPU collector check** passed with all five heroes and all 20 forced pairings, zero forced draft actions entering PPO experience, unchanged weights/checkpoint/budget, and maximum behavior-log-probability discrepancy **0.00002933**. [GPU evidence](v6-hero-fixes-gpu-smoke.json).
6. A bounded real training pilot produced **19 healthy generations / 4,864 completed games**, zero censors and zero rejected minibatches. Parameters and Adam state stayed finite. Maximum behavior discrepancy was below 0.000064. The pilot continued from generation 6348 to **6367**; its roughly 72.25 charged seconds count toward the same 12-hour allocation.
7. Monitoring/statistics tests passed (52 tests), followed by all six process-identity tests including the new V6 case. Live APIs recognize the new trainer/host and report no monitoring/statistics error.

The nearby steady-generation throughput medians were approximately 23,606 learning rows/s for V6 versus 22,606 for the preceding V5 sample. Hardware contention and trajectories were not controlled, so this is a sanity check against an obvious slowdown, not a speedup claim.

The active continuation is `/home/lva/.local/share/shards-training/2026-09-26/main-v6`. It started with **5.877 training hours remaining**, using the pilot's final checkpoint rather than discarding its learning. Main wrapper PID at launch: 101014; trainer: 101035; host: 101207; monitor: 100731; CPU frozen-evaluation watcher: 101262. PID values are historical identifiers and must be revalidated before any future signal.

Migration reports and immutable starting checkpoints are in `/home/lva/.local/share/shards-training/2026-09-26/migrated-v6`. The authoritative allocation remains the existing `budget.json`; no time was reset or refunded. The model still needs balanced frozen evaluations and tactical scrutiny before its hero results can justify balance changes.
