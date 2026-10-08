# Continuing training improvements — 2026-09-26

This records the continuation investigation requested during the first main run.
The same12-hour ledger covers every real learning branch. Frozen diagnostics and
evaluation do not consume additional learning grants. Current operational state
is available at [the local dashboard](http://localhost:8768/).

## Recovery and precision

The original run stopped itself at generation1996 when all-row behavior
verification found log-probability error0.0129366 against its0.01 limit. Its
generation1977 checkpoint remained valid. The ledger closed at6151.3536 charged
seconds, leaving37048.6464 seconds. The recorded uncommitted1,554,413 decisions
and5,120 episodes were marked discarded; their time was not refunded.

Batch-shape-dependent numerical error is a supported explanation: actors use
32/64/128/256 rows while the verifier uses2048-row chunks. PyTorch documents
differences between batched/sliced arithmetic and TF32's reduced input precision.
[Numerical accuracy](https://docs.pytorch.org/docs/2.14/notes/numerical_accuracy.html),
[CUDA precision controls](https://docs.pytorch.org/docs/2.14/notes/cuda.html#tensorfloat-32-tf32-on-ampere-and-later-devices).

Three frozen cohorts per precision mode, about234,000 retained rows each, found
worst selected-log-probability errors0.0018673 under TF32 and0.000030756 under
IEEE FP32. The original failed batch/model state was not saved, so this is
mechanism evidence rather than an exact replay. IEEE is the selected recovery
mode. The numerical guard remains unchanged.
[Raw precision diagnostic](behavior-precision-diagnosis-2026-09-26.json).

## Exact speed improvements

The selected copy path removes four redundant GPU gathers, an index upload and
explicit conversion temporaries when retained actor rows already form a packed
prefix. Arbitrary-index and aliased-storage cases retain the original fallback.
Input validation retains every original schema, mask, finite-value and card-ID
check with equivalent NumPy reductions.

All candidates were compared with identical frozen weights, engine seeds,
learner/opponent assignments and restored RNG. Full sampled-action hashes,
owned inputs, packets, episode ownership, outcomes, returns and advantages
matched exactly across three real256-game cohorts per candidate.

| Candidate | Retained rows/s, summed rows / summed time |
| --- | ---: |
| IEEE baseline before trials |29,252 |
| IEEE baseline after trials |30,084 |
| Contiguous copy |32,425 |
| Validation only |29,638 |
| Queued actors only |30,098 |
| **Contiguous copy + validation** |**33,396** |
| Copy + validation + queued actors |33,769 |

The selected pair is12.6% faster than the bracketed baseline for collection plus
behavior verification. Queued coordination adds an inconsistent incremental
gain and remains disabled. These timings include complete-episode draining and
sealing, exclude initialization/optimizer work, and make no strength claim.
[Raw paired trials](frozen-speed-candidates-ieee-2026-09-26.json).

## Representation repair without restarting learning

A real-engine fixture exposed identical v2 inputs for different own Allegiance
states, with different mastery outcomes from the same Mainframe Abbot play.
The v3 encoder adds seven exact own-faction counts to unused input columns and
keeps the2048-dimensional model. Existing game rules, including temporary
fast-played Allegiance, are preserved. Privacy and catalog-capacity guards are
explicit. [Representation research](representation-quality-research-2026-09-26.md),
[alias fixture](representation-alias-fixture.json),
[host differential](host-v3-differential.json).

The migration keeps all19 policies and the complete Adam/RNG/budget state.
Only the seven previously unused first-layer columns are zeroed so new inputs
initially contribute zero. Their Adam moments were already zero and unchanged.
All19 policies passed exact CPU output/action parity. A real256-game GPU test
then matched74,956 retained decisions, every sampled action, legacy feature,
behavior packet, credit and advantage exactly with both models using IEEE.
[Migration record](v3-migration-1977-2026-09-26.json),
[GPU parity](v3-real-policy-parity-2026-09-26.json).

The three-epoch continuation completed its300-second supervised grant, including
the ordinary checkpoint reserve, from generation1977 to2087:110 generations,
270.1788 charged seconds, no censored games and finite model/optimizer state.
One minibatch hit the unchanged guarded early-stop rule. The largest observed
behavior-log-probability discrepancy was0.0000634, well below the0.01 limit.
The ledger then held6421.5323 charged seconds across all learning branches.

Its frozen1,024-game match against the migrated1977 learner scored46.73%, with
the conservative95% paired-seed bound40.73–52.73%. This does not demonstrate a
strength gain or regression. The seven new features are not yet claimed to
improve playing strength. Raw report:
`/home/lva/.local/share/shards-training/2026-09-26/evaluations/v3-three-epochs-vs-anchor.json`.

The six-epoch challenger completed the same300-second grant from the exact
same migrated1977 source:99 generations and270.2120 charged seconds. The audited fork changes only the two saved epoch
configuration fields and provenance; model, Adam, RNG, archive, seeds and budget
state are preserved. Both branches consume the original shared allocation.
[Fork record](reuse-fork-v3-e6-2026-09-26.json),
[predeclared strength comparison and anchor veto](ppo-reuse-research-2026-09-26.md).

The primary4,096-game frozen head-to-head gave six epochs a49.15% score
(2,013 wins,2,083 losses, no draws/censors), with the conservative paired95%
bound46.14–52.15%. It failed the predeclared superiority criterion, so three
epochs remain selected. The conditional anchor-veto matches were not needed.
[Selection record](ppo-reuse-selection-2026-09-26.json).

## Continuing measurements and research

The exact HostV4 counter optimization and batched statistics collector passed
the frozen GPU comparison and were adopted at generation3096. The combined
change is throughput-neutral in the complete pipeline, despite faster isolated
CPU requests. Live tables now distinguish heroes, relics, cards, destinies,
hero-specific choices, seat advantage and matchup coverage. See
[monitoring implementation and measured cost](balance-monitoring-2026-09-26.md).

The generation2087 draft-probability probe found first-drafter Tetra probability
99.5988% across32 initial boards, and a Decima response probability99.8751%
conditional on Tetra. This is learned concentration, not hardcoded selection.
It motivates the separate75% uniform distinct-hero /25% normal-draft experiment.
Forced hero choices are setup actions outside PPO credit; natural and forced
statistics remain separate. The bounded continuation and frozen comparison
gates were fixed before candidate training or comparison outcomes in the
[hero protocol](hero-curriculum-protocol-2026-09-26.json).

Both pilots and the predeclared comparisons have now completed. Mixed training
scored50.325% in4,000 balanced games and50.537% in4,096 ordinary-draft games,
with both95% bounds including50% and no censors. It passed the operational
adoption gates and supplies broad hero coverage; stronger play is not established.
The main V5 continuation resumed from the selected generation3415 checkpoint.
See [full results and hardware-contention limits](hero-curriculum-results-2026-09-26.md).

The CPU shadow evaluator measures sampled frozen policies against champion and
a fixed pre-continuation anchor, with seat-swapped seeds, hero/seat coverage,
legal-menu entropy, action use and value diagnostics. Separate CPU precision is
reported. Its scheduler uses one thread/worker, bounded child processes, retained
snapshots, disjoint seeds and no automatic retry or training control.

The preliminary16-game v2 smoke drafted Decima in seat0 and Tetra in seat1 in
all games. This motivates coverage measurement; it does not prove those are
globally optimal heroes or justify judging rarely explored heroes as weak.
[Live quality audit](live-quality-audit-2026-09-26.md).

CPU evaluation is not free: during one256-game shadow match, fresh training
rows/s fell from32,398 before it to26,714 during it, then recovered to33,278.
Those observational windows indicate roughly18% instantaneous interference.
Both reuse pilots received comparable unthrottled shadow intervals for the
time-budget comparison. Production shadow monitoring will use a measured
per-reply delay, one worker and infrequent bounded jobs; its reports expose
actual CPU time and sleep time. Evaluation has no separate learning charge,
but concurrent training wall time still consumes its normal budget.

Further bounded investigations cover PPO sample reuse, exact one-pass faction
counting, public/revealed history and FP32 arithmetic throughput. BF16x9 support
must not be inferred for the5090 from datacenter Blackwell documentation.
[FP32 throughput research](fp32-throughput-research-2026-09-26.md).

Validation this iteration includes16 fastpath CPU tests,7 queued-actor tests
including CUDA,13 migration tests with CUDA initialization forbidden,13 existing
model/PPO tests including CUDA,5 audit-wrapper tests,15 CPU evaluation/watch
tests, plus the real C# and GPU differential runs above. This is an incremental
record, not a claim that one combined suite or the remaining hours have passed.
