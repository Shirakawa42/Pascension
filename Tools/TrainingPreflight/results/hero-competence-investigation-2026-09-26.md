# Current hero results and policy competence — 2026-09-26

**The statistics are relevant to the current policy population, but they are not yet a reliable estimate of expert-level balance. There is direct evidence that the AI mishandles Ko Syn Wu, and a verified representation limitation prevents Rez from fully exploiting Scry. Neither hero should be buffed on this evidence alone.**

Training remained running throughout. These investigations used read-only snapshots, single-threaded CPU evaluation with a 2 ms host-step throttle and low process priority, and an isolated engine fixture. No GPU evaluation, optimizer updates, rule changes, live-host rebuilds, checkpoint migrations, or training-budget resets were performed. The CPU probes have some contention cost; they are not claimed to be free.

The captured dashboard window contains **100,000 completed random-assignment games**, zero censored games and 50 draws. The behavior probe froze learner **generation 5873**; a separate draft-probability probe used generation **5906**. All numbers below refer to these saved snapshots, not an indefinitely updating dashboard. [Statistics snapshot](hero-investigation-2026-09-26/statistics-snapshot.json), [behavior evidence](hero-investigation-2026-09-26/behavior-current.json), [draft evidence](hero-investigation-2026-09-26/draft-probabilities.json).

## What the hero numbers actually say

Scores count a draw as half a win. Each real game contributes two hero perspectives, so the table's sample counts must not be summed and called independent games.

| Hero | Random-assignment appearances | Score | Seat 0 | Seat 1 |
|---|---:|---:|---:|---:|
| Tetra | 40,024 | 64.92% | 68.74% | 61.13% |
| Decima | 39,957 | 52.56% | 57.36% | 47.80% |
| Volos | 40,218 | 49.61% | 54.18% | 45.07% |
| Rez | 39,910 | 45.99% | 49.94% | 41.92% |
| Ko Syn Wu | 39,891 | 36.88% | 41.25% | 32.52% |

Random assignment covers the 20 ordered distinct hero pairings approximately equally. Seat 0 scores 54.28% overall; seat advantage does not explain away Ko's deficit, which remains on both seats. Tetra is ahead against every other hero in this policy pool. Ko scores 36.23% against Decima, 42.81% against Rez, 29.57% against Tetra and 38.80% against Volos. Rez beats Ko (57.19%) but trails the other three. These are useful and well-sampled **descriptive matchup results**. Training policies change, 25% of games use archive opponents, and neither optimal strategy nor human strength was established. [Snapshot, random cohort](hero-investigation-2026-09-26/statistics-snapshot.json), [population implementation](../experiments/variant_v5_runtime.py), [opponent sampling](../train_campaign.py).

The normal-draft cohort is a different experiment: Ko has **4 wins in 272 appearances (1.47%)**, Rez **1 in 226 (0.44%)**. Those numbers cannot be substituted for assigned-hero competence. On 32 fresh boards, the current policy gives Ko a mean first-pick probability of only **2.90e-8**, and Rez **3.88e-12**. It picks Tetra first with mean probability 99.23%; after Tetra is taken, it chooses Decima with mean probability 99.83%. This is almost complete draft collapse, despite broad post-draft training coverage.

The archive explicitly retains the initial policy, whose measured first-pick probabilities remain approximately 20% per hero. With the configured archive mixture, this initial opponent alone is expected in about 1.17% of training games before sampling fluctuations, enough to contribute roughly a few hundred rare-hero appearances per 100,000 normal-draft games. Thus old/initial opponents are a strongly supported explanation for the near-zero natural-draft results. **Exact attribution is unavailable:** the pooled C# statistics do not tag each hero result with policy role/version. We must not label every rare choice as an initial-policy choice. [Draft probe](hero-investigation-2026-09-26/draft-probabilities.json), [archive retention and sampling](../train_campaign.py).

## Ko: a measured decision-quality failure

The frozen current policy played both sides of **320 games**: ten distinct unordered hero matchups, 16 fresh engine seeds per matchup, each seat-swapped. Each hero appeared in 128 games. All games completed, zero were censored, all frozen-weight hashes remained unchanged, and CUDA was never initialized. The probe took 82.18 seconds of match wall time and about 68.93 combined Python/host CPU seconds. Its purpose was behavior inspection; 128 appearances per hero are not a precise balance estimate. [Reproducible probe](../experiments/hero_behavior_probe.py), [full report and activation records](hero-investigation-2026-09-26/behavior-current.json).

Ko activated Sacrifice **833 times**, on 866 observed turns where it was available:

| Resolution | Activations |
|---|---:|
| Banished a card | 481 |
| Paid health, then explicitly declined the banish | 255 |
| Paid health with empty hand and discard; no banish request existed | 97 |
| **Paid without banishing** | **352 / 833 = 42.26%** |

That is **1,056 health spent without a banish**, or **8.25 health per Ko game**. All 97 empty-target records have both hand and discard counts exactly zero. One explicit recorded case pays at 44 health with only Infinity Shard offered, then declines. The issue is activating before declining; declining to banish Infinity Shard can itself be sensible. Of the 481 actual banishes, 312 target Crystal and 77 Blaster, so the policy has learned some starter thinning, but its activation decision remains poor.

This is not an information restriction like Rez's: Ko's hand, discard, collection, health, mastery, target identity and target zone are encoded. The engine charges 3 health before opening optional `BanishUpTo(1)` and allows activation without a target; the health-loss method does not reward an empty use. These are existing legal rules, not a proposed rule change. [Source audit](hero-representation-audit-2026-09-26.md), [ability and health-loss implementation](../../../Assets/Scripts/Shards/Engine/ShardsEngine.cs), [banish effect](../../../Assets/Scripts/Shards/Engine/ShardsEffects.cs).

The 42% activation rate is descriptive; activations within a game are correlated. It proves substantial misuse, but **does not measure how many win-rate points a correction would recover**. A paired policy intervention is required for that claim.

## Rez: a verified information loss

Rez's policy can read card identities and ordinals **during** the Scry decision. Immediately after submission, the adapter clears the selection trace and the feedforward policy has no persistent revealed-card memory. Its next reroll or buy therefore cannot depend on which top card it just saw. The reroll discount itself is correctly represented by reroll count and current price.

An executed fixture against the exact frozen V5 DLL confirmed this. Two card-conserving states reveal Shard Abstractor and Fungal Hermit in opposite top-deck orders. After legally keeping both cards, **all 4,160 observation, candidate and mask floats are identical**. The same legal zero-cost reroll then reveals Shard Abstractor in one state and Fungal Hermit in the other. This is a constructed engine fixture, not a proof of full seed-to-position reachability; it proves the encoding alias and does not quantify its win-rate cost. [Executed evidence](rez-scry-memory-alias-fixture-2026-09-26.json), [fixture source](../experiments/rez_scry_memory_probe/Program.cs), [detailed audit](hero-representation-audit-2026-09-26.md).

The behavior probe shows that Rez does activate Futureproof: 997 activations on 1,027 observed opportunity turns, with 726 subsequent reroll actions after ability activation. Thus simple failure to press the ability is not the main explanation. The policy cannot fully coordinate the ability with its next decisions even if it trains longer in this unchanged representation.

## Is training still improving?

Nonoverlapping random-assignment windows show some relative improvement for Ko since forced assignment began, but not a monotonic approach to parity:

| Completed-game interval | Ko | Rez | Tetra |
|---|---:|---:|---:|
| 0–100,001 | 33.83% | 44.21% | 64.12% |
| 100,001–200,000 | 36.16% | 45.88% | 63.25% |
| 200,000–300,000 | 37.18% | 48.59% | 62.91% |
| 300,000–400,000 | 37.13% | 46.56% | 63.96% |

These changing-policy, changing-opponent windows cannot establish causality. More games alone have not established strong play. [Saved trend evidence](hero-investigation-2026-09-26/trend-evidence.json).

A second 320-game behavior probe used the frozen checkpoint from the start of random-assignment training, generation 3415, with the same declared seeds and matchup allocation. It completed without censors or weight changes. Ko already paid without banishing on **296 of 794 activations (37.28%)**, versus 42.26% in the current probe. Trajectories diverge between policies and this is not a head-to-head strength test; the safe conclusion is that several hours of broad hero exposure have **not eliminated the activation mistake**. [Earlier-checkpoint behavior](hero-investigation-2026-09-26/behavior-at-randomization-start.json).

The existing natural-draft frozen dashboard contained **zero current-window Ko/Rez appearances**. Its results principally measure Tetra/Decima. The last four saved inline tests against fixed champion generation 1827 averaged **51.12% over 1,024 games**, versus 58.79% in the first four V5 tests. They use different changing learner checkpoints and small tests; this is a plateau/regression warning, not proof of a particular regression or a controlled hero-training tradeoff. The latest saved CPU test at generation 5635 scored 50.78% against that champion and 46.88% against anchor1977, each over 256 games with broad uncertainty. These tests do not certify expert play or improvement on Rez/Ko. [Saved evaluations](hero-investigation-2026-09-26/trend-evidence.json).

Relic associations are likewise hypotheses to test. For example, Ko games acquiring Doom Gate score 33.34%, Heart of Nothing 50.24%, World Piercer 55.94%; Rez games acquiring Star Seeker score 45.08%, Slipstream Shard 63.07%, Warpquartz 67.70%. Acquisition time, survival, current mastery, deck composition and policy choice confound these figures; acquisition groups can overlap. A high conditional win rate is not evidence that switching to that relic would cause the same improvement. [Relic rows in snapshot](hero-investigation-2026-09-26/statistics-snapshot.json).

## Training and balance priorities supported by this investigation

1. Keep random hero assignment and evaluate saved checkpoints on **fixed, balanced, seat-swapped hero panels**, in addition to natural draft. Select for broad competence; do not assume the newest checkpoint is best.
2. Add Ko-specific quality measurements: paid-empty activations, paid-declined banishes, target quality/zone, health expenditure and activation opportunity turns. Test a narrow intervention against frozen opponents before adopting it; preserve the game's legal rules.
3. Prepare a versioned, tested Rez knowledge representation that remembers only legitimately revealed center-deck information, updates it on observed refills and invalidates it on shuffles. A larger feedforward network alone cannot recover forgotten information. Any live adoption needs migration and controlled validation, not an edit to the frozen host.
4. Consider explicit hero/ability-cost features and structured ability evaluation as ablations for Ko's activation decision. The present observations permit better decisions, so this is a learning/representation hypothesis, not a proven required architecture change.
5. Before balance changes, test hero-specific best responses, relic plans and tactical competence against several frozen opponents and expert play. Current Tetra dominance is worth investigating, but Ko and Rez have demonstrated AI-side disadvantages that must be addressed first.

No new training configuration or hero adjustment was deployed as part of this investigation.
