# V10 public-information extension

V10 appends 512 observation floats, bringing the observation to 3328 under `shards-observation-v7`. It preserves the prior 2816 observation positions and every candidate/mask field. Five previously demonstrated same-rules aliases now produce different inputs: public banished identities, pending monster identities, current duplicate-effect progress, per-card temporary/banish association and remaining own relics before eligibility. The information extension itself does not change rules. A subsequently authorized, isolated V10 engine fork corrects Reactor cleanup; that separate change and its regression evidence are documented in [Reactor cleanup fix](v10-reactor-cleanup-fix-2026-09-27.md).

## Layout

All offsets in the table are absolute. The authoritative machine-readable description is `Encoder.InformationV10Descriptor`. [Encoder](../experiments/HostV10/Encoder.cs)

| Positions | Meaning |
|---|---|
| 2816–3004 | Public banished-definition histogram, 189 definitions, counts / 10 |
| 3005–3007 | Exact remaining own relic codes, sorted, code / 192; zero means empty |
| 3008 | Remaining own relic count / 3 |
| 3009 | Mastery still required for normal relic eligibility / 10 |
| 3010 | Reserved zero |
| 3011–3202 | First 64 public entities: `(FastPlayed, BanishAtCleanup, pending monster attack)` in the same order as existing entity records |
| 3203–3207 | Pending counts for Agony, Brutality, Corruption, Malice and Torment, / 10 |
| 3208 | Total pending monsters / 20 |
| 3209 | Number of public entities beyond 64, / 64 |
| 3210 | Reviewed same-public-source continuation descriptor available |
| 3211 | Queued exact same-source registered play effects / 4 |
| 3212 | Queued exact same-source registered exhaust effects / 4 |
| 3213 | Other continuation is not fully encoded; based only on public decision presence |
| 3214–3325 | Up to 56 pending monsters in attack order: `(public entity ordinal + 1) / 64`, `card code / 192` |
| 3326 | Pending order entries beyond 56 / 64 |
| 3327 | Reserved zero |

The current hero registry supplies three initial relics per hero. Effects remove relics from set-aside; they do not append arbitrary foreign relics. More than three remaining own relics therefore fails closed as an unsupported schema rather than silently truncating. This representation carries remaining identities before Mastery 10 and supports the separately implemented semantic relic lookup. [Relic setup](../../../Assets/Scripts/Shards/Engine/ShardsEngine.cs#L224)

Pending order is included because the same pending multiset in another order can have different consequences. Current 1v1 content has five initial monsters and at most 25 added per player's once-only Doom Gate flood, giving an upper bound of 55 instances under these creation paths. Capacity 56 covers that bound; an explicit overflow remains for future content. [Initial monsters](../../../Assets/Scripts/Shards/Content/ShardsHorizonSet.cs#L293), [flood guard](../../../Assets/Scripts/Shards/Content/ShardsDuelSet.cs#L864)

## Information authorization

- Banished cards are in a public snapshot zone. Counts use catalog identity, never global instance IDs.
- Remaining relics inspect only the acting player's own set-aside list. Opponent private set-aside contents are not read.
- Entity temporary/banish flags apply only to public zones and describe public fast-play/mode consequences. The first 64 entities use exactly the old entity ordering, so new flags remain associated with the correct old entity record.
- Pending monsters must be present in `ActiveMonsters`, otherwise encoding fails. Existing creation paths append pending attacks together with a public `ShardsMonsterRevealedEvent`. Ordered targets use public entity ordinals and definition codes, not raw instance IDs. [Reveal paths](../../../Assets/Scripts/Shards/Engine/ShardsEngine.cs#L1988)
- Continuation deliberately exposes **only** entries whose source is the same already-authorized visible active source, whose controller matches that source's owner, and whose effect object is exactly the registered source `PlayEffect` or `ExhaustEffect`. These references are queued by public play/General Decurion and exhaust/Unknown God paths. Hidden sources, other sources, custom nested effects, null-source flows and delegate captures remain unexamined. No generic queue length is emitted. [Double exhaust queue](../../../Assets/Scripts/Shards/Engine/ShardsEngine.cs#L734), [double play queue](../../../Assets/Scripts/Shards/Engine/ShardsEngine.cs#L1967)
- The partial-continuation flag depends on the public presence of a decision, not on the existence or count of unencoded hidden queue entries. Appending a synthetic hidden other-source queue entry leaves the entire packet unchanged in the regression test.
- Public full collection composition remains available as in V9; opponent hand/draw allocation and deck order remain hidden. All new histograms/counts use integer counting before normalization where order could otherwise affect floating-point sums.

This is a reviewed, partial continuation descriptor—not unrestricted access to engine internals or a complete effect interpreter. It fixes the first/second doubled-Reactor ambiguity without claiming to describe arbitrary nested iterator progress.

## Validation

`InformationV10SelfTest` passed **2422 checks**. It reproduces each of the five old aliases, verifies that the old 2816-feature prefixes and legal candidate blocks still match within each pair, and verifies that the new observations distinguish the public facts. It also checks pending attack order, exact pre-eligibility relic identities, sorted relic invariance, hidden-source queue rejection and 2400 reached-state private allocation/order/RNG invariance comparisons. [Result](v10-information-selftest-2026-09-27.json), [self-test](../experiments/HostV10/InformationV10SelfTest.cs)

Before the subsequent Reactor cleanup correction, a separate process loaded frozen V9 and built V10 as distinct assemblies. Across **32 completed games, 12,342 states, zero censors and 24 contexts**, it compared all prior 2816 observation floats, all 64×32 candidate floats and all 64 mask floats. Every value was exactly equal along shared accepted trajectories; exercise choices and termination matched. [Prefix comparison](v10-prefix-equivalence-2026-09-27.json), [probe source](v10-prefix-probe/Program.cs)

The initial tested V10 build hash is `f7c6ab546e283edd645014914a03da5c0ad0ea66cea6c6ff2fd23caa1a7277a7`. Later integration builds may change the binary hash when descriptor export or test dispatch is added; rerunning these probes against the final build records the exact final hash. The information encoder and self-test sources were unchanged after this validation. The final coordinated integration build, including the corrected V10 engine, passed all 2422 information checks again. The earlier cross-version trajectory comparison remains evidence for the representation-only extension; the Reactor rule correction intentionally permits later trajectory differences. Policy comparisons must run both policies under the same corrected rules.

## Remaining bounds

Individual entity identity/state still has a 64-entry capacity; overflow is visible, but per-card flag association beyond that limit is not fully encoded. The existing four-card top-memory and last-16 staged trace bounds remain. Revealed opponent-hand memory and arbitrary nested-copy/iterator progress are not made lossless here. Effect-only rebalance semantics are handled by the separate effect-descriptor project and are not claimed fixed by these state slots. No running checkpoint or frozen V9 file was modified.
