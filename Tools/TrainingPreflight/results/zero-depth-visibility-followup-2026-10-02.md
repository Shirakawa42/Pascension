# Shards zero-depth visibility follow-up — 2026-10-02

The follow-up checks inspect the actual float arrays consumed by the policy,
including candidate features and legal masks. They use the real C# rules engine,
with small explicit state fixtures for card-specific cases and independent hidden
state oracles for conservation checks. No outcome learning was started.

The current observations cover the audited public and own current-state fields,
full collection compositions, public zone statuses, authorized pending options,
and retained revealed-card facts. This is not a claim that the observation stores
every correlation inferable from an unlimited history: remembered uncertainty is
still represented by membership/position ranges rather than a joint belief state.

## The requested examples

| Situation | What reaches the policy | When it changes |
|---|---|---|
| Duplication Fabricator reveals personal deck tops | Own and opposing revealed card definitions, public physical identity and exact top-relative position, even if no copy menu opens. | Unrelated actions retain the fact. Drawing the card removes its deck-position fact and retains its identified presence in the owner's hand, visible to the other seat. A personal shuffle invalidates remembered deck order; public moves shift/refine positions. The fact lasts until the card moves or order becomes unknown, rather than automatically lasting until a shuffle. |
| Look/reveal three center cards, followed by unchanged turns | The viewing seat retains all three exact card/position facts across both turns. A private Index of Futures/Scry look remains restricted to its viewer; a public reveal is remembered by both seats. | A normal paid/free market refill consumes the known top and shifts the remaining two to positions zero/one. Revealed monsters can consume additional prefix cards while the refill bypasses them. Public bottom returns and chosen Scry bottom order are retained. A public center shuffle invalidates order before subsequent same-action reveals. |
| Full opponent deck/collection and every public zone | The full unordered permanent collection includes the opponent's deck, hand, discard, permanent play cards and champions. Discard/play/champions/destinies, banished cards with relative owner, visible market/monsters/shared destinies, inferred remaining set-aside relics, known hand counts and identified publicly shown hand cards have separate representations. | The unknown split between hand and draw pile and unseen order remain private. Temporary fast-play loans and unearned relics/destinies are separate from permanent deck ownership. A public temporary reveal outside ordinary lists remains visible and participates in collection/pool accounting. |

Sources: [personal/center reveal lifecycle regressions](../../ZeroDepthTraining/Host/VisibilitySelfTest.cs),
[zone/current-state tensor regressions](../../ZeroDepthTraining/Host/ZoneVisibilitySelfTest.cs),
[observation schema](../../ZeroDepthTraining/Host/Encoder.cs),
[public memory updates](../../ZeroDepthTraining/Host/Knowledge.cs),
[engine state and zone declarations](../../../Assets/Scripts/Shards/Engine/ShardsState.cs),
[event visibility contracts](../../../Assets/Scripts/Shards/Engine/ShardsEvents.cs).

## Focused reveal and hand regressions

`VisibilitySelfTest.Run()` passes **14 named groups**. These tests submit actual
Fabricator, Index of Futures, Rez, Order Initiate, Grim Tutor, Longshot and reveal
actions. Small fixtures choose the relevant cards/turn state; transitions use the
real rules engine. Assertions inspect encoded position rows and count channels,
including the actual deciding defender's observation before attacker cleanup.

| Regression group | Verified information | Test source |
|---|---|---|
| Fabricator lifecycle | Both revealed personal tops survive unrelated actions and the other player's cleanup; drawing removes the deck fact and creates a known hand fact; unknown draws stay hidden. | [FabricatorLifecycle](../../ZeroDepthTraining/Host/VisibilitySelfTest.cs#L115) |
| Center three across turns | The viewing seat retains all three after both turns. One ordinary reroll consumes the top and leaves the next two known at positions zero/one; the returned market card is remembered on the bottom. | [ReorderLifecycle](../../ZeroDepthTraining/Host/VisibilitySelfTest.cs#L144) |
| Scry bottom order | Kept cards retain top order. Chosen bottom order matches actual insertion order and shifts correctly after a later public refill. | [ScryBottomLifecycle](../../ZeroDepthTraining/Host/VisibilitySelfTest.cs#L156) |
| Free refill and monster bypass | Free removal retains the same information as paid removal. A revealed monster and the following row card consume two known prefix cards, leaving the third known on top. | [FreeRefillLifecycle](../../ZeroDepthTraining/Host/VisibilitySelfTest.cs#L171) |
| Opposing private Scry | Private choices widen ranges without disclosing the answer. A public refill retires the matching identity and retains certainty that different identities remain. Hidden-order permutations do not repair memory. | [PrivateScryRefinement](../../ZeroDepthTraining/Host/VisibilitySelfTest.cs#L191) |
| Identical center copies | A public refill removes the correct physical copy and preserves the other same-definition copy. | [DuplicateIdentityRefinement](../../ZeroDepthTraining/Host/VisibilitySelfTest.cs#L210) |
| Later authorized peek | The deciding seat confirms observed tops and excludes the complete viewed prefix from an unseen remembered card's range. | [PrivatePeekRefinement](../../ZeroDepthTraining/Host/VisibilitySelfTest.cs#L232) |
| Opposing private reorder | A remembered top-three group stays within those three positions. Two public refills establish the remaining exact top. | [PrivateReorderRefinement](../../ZeroDepthTraining/Host/VisibilitySelfTest.cs#L245) |
| Personal Tutor shuffle | Tutor invalidates remembered deck positions. Public search-result hand facts expire at cleanup and unknown fresh redraw. | [TutorInvalidation](../../ZeroDepthTraining/Host/VisibilitySelfTest.cs#L222) |
| Longshot's two bottom returns | Both revealed cards retain exact physical identities and bottom order for both seats; temporary records disappear. | [LongshotBottoms](../../ZeroDepthTraining/Host/VisibilitySelfTest.cs#L263) |
| Known hand copies | Playing a different same-definition copy preserves the identified card still held. Its row has an unknown hand-position sentinel; cleanup removes the old fact. | [KnownHandIdentity](../../ZeroDepthTraining/Host/VisibilitySelfTest.cs#L277) |
| Public hand reveal paths | Unify, Dominion, Riposte, Shard Seer and selected hand shields retain only cards actually shown. Passive or unrevealed shields do not become known hand cards. | [PublicHandReveals](../../ZeroDepthTraining/Host/VisibilitySelfTest.cs#L311) |
| Longshot's identical split copies | Fast-playing one revealed copy and returning the other retires the correct temporary identity; inferred center composition matches the physical oracle. | [LongshotSameDefinition](../../ZeroDepthTraining/Host/VisibilitySelfTest.cs#L345) |
| Repeated Legion after shuffle | Actual General Decurion M20 doubles Legion's effect. Five-card, seven-card partial-repeat and seven-card selected-champion variants preserve each physical card once, count the owned collection exactly, retire discarded temporary records before shuffle and preserve the separately selected champion's known hand identity. Old Fabricator top facts expire correctly; hidden enemy allocation swaps leave the whole observation unchanged. | [RepeatedLegionReveal](../../ZeroDepthTraining/Host/VisibilitySelfTest.cs#L360) |

The common [tensor fact assertions](../../ZeroDepthTraining/Host/VisibilitySelfTest.cs#L27)
check definition, public identity, bounds, exactness and possible absence. The
final combined host selftest passes all 14 focused groups, its original 1,166
real states over four completed games and the broader zone suite below.

## Broader tensor checks

`ZoneVisibilitySelfTest.Run()` passes eight groups, including ten naturally
completed games over base, Relics of the Future, Shadow of Salvation, Into the
Horizon and Duel rules: **4,140 observed rule states**. Each state independently
compares conservation-derived center/destiny compositions against actual physical
pile counts, inferred enemy set-aside relics against actual membership, and known
hand lower bounds against actual hands. Hidden lists are read only by the test
oracle; the encoder does not use them to repair memory.

- Every public integer/boolean player field except the fixed seat index has an
  explicit audited tensor column, checked by reflection and perturbation for both
  relative seats. Hero identities and faction/allied-faction counters are also
  tested. A newly added primitive field fails the inventory check until reviewed.
- All six rule constants, round, extra-turn recipient and terminal winner/draw
  values change their expected tensor fields.
- Each own/opponent/shared zone is tested separately, including its definition,
  ownership, zone identity and permanent-collection inclusion. Ordered
  `PlayedThisTurn` is preserved independently of alphabetical entity ordering.
- Exhaustion, marked damage, fast-play loan status, cleanup banish flags,
  public aura-adjusted defense, visible dynamic shields and ordered pending
  monster attacks are checked. **Seventy** monster entries verify visibility past
  the incumbent's former 56-entry limit.
- **330** pending options retain definition, source order, amount, required hint,
  relative owner, disabled status, default multiplicity, target and numeric label
  values, including off-page rows. Every legal option remains reachable; disabled
  options remain visible and absent from selectable candidates.
- Entire observations, candidate features and masks are exactly invariant under
  unseen center/destiny/personal draw permutations, hidden enemy hand/draw
  allocation swaps, RNG changes and private creation-counter changes.
- Three actual defeated-monster reward flows (Corruption, Agony and Malice) retain
  source/controller during parked child menus after the monster has returned to
  the center bottom. Queued helper-generated reward provenance is checked too.
  Synthetic unknown active/queued sources do not expose private identities,
  copy guards or private continuation counts.
- A public Doom Gate 35-card flood has an explicit exact pool-conservation check.

Sources: [all broad checks](../../ZeroDepthTraining/Host/ZoneVisibilitySelfTest.cs),
[rules constants and mutable card status fields](../../../Assets/Scripts/Shards/Engine/ShardsTypes.cs),
[initial public pool counts, Doom Gate additions and monster reward order](../../../Assets/Scripts/Shards/Engine/ShardsEngine.cs),
[canonical wrapper pagination](../../ZeroDepthTraining/Host/Adapter.cs).

## Concrete follow-up fixes

The first broad test run exposed a public continuation omission: a defeated
monster is moved to the center bottom before its reward opens a menu, so its
public source disappeared when the encoder required a currently face-up entity.
Observation schema v2 now accepts an already-public, certainly-present remembered
physical identity with matching definition. Positive references identify visible
entities; negative references identify known-position rows. Hidden card status is
not added to the visible entity table. All three natural reward regressions and
unknown-source privacy tests pass.

The reveal review established failing tests before the following corrections:

| Omission proved by the failing case | Correction |
|---|---|
| After opposing private Scry, a publicly refilled remembered card stayed in pile memory and different remaining cards became possibly absent. | Public movement retires the exact physical identity and preserves the presence of different remembered cards. |
| A later complete private peek still allowed an unseen remembered card in the viewed prefix. | Authorized peeks exclude those positions and confirm observed identities. |
| Opposing private reorder widened known top-three positions to the entire deck. | Reorder preserves prefix membership; it does not have Scry's ability to bottom cards. |
| Longshot returned two public cards with missing/default IDs, so the second bottom fact replaced the first. | Each return supplies its already-public `InstanceId`; missing bottom-return identity now fails visibly. |
| A known top was drawn to hand, but playing a different same-definition card erased that still-held fact. | Public hand IDs are separate from anonymous count lower bounds. Play, banish and discard retire the matching ID; cleanup rebuilds only known new draws. |
| Hand reveal events retained definitions without their publicly shown physical identities. | Parallel `HandInstanceIds` accompany selected Unify/Dominion/Riposte/Shard Seer/hand-shield reveals. Known opposing hand IDs have an explicit unknown hand-position sentinel in the tensor. |
| Longshot fast-played one copy and returned an identical second copy, retiring the wrong temporary record. | Return records are retired by physical identity, preserving pool conservation. |
| A copied Legion effect resolved, shuffled and revealed cards again within one submission. Repeated identities duplicated records, while a partial repeat left older cards in the hidden deck falsely marked as still temporary. | A public batch discard-release event retires resolved records and their already-removed markers before subsequent shuffles. Each latest public reveal also replaces any previous physical record defensively, preserving the new reveal order. Only currently held cards enter the temporary entity/collection representation. |

These changes add public provenance and observation coverage. Effect amounts,
accepted game actions, draw order and RNG calls are unchanged. No unrevealed hand
identities or hidden hand order are added. Known hand facts come from public
reveals, public returns and a previously known exact top being drawn. Private draw
identities are not used to populate another player's known hand.

Sources: [v2 public source references](../../ZeroDepthTraining/Host/Encoder.cs),
[memory refinement](../../ZeroDepthTraining/Host/Knowledge.cs),
[Longshot public return provenance](../../../Assets/Scripts/Shards/Content/ShardsDuelSet.cs#L901),
[chronological personal reveal release](../../../Assets/Scripts/Shards/Content/ShardsHorizonSet.cs#L191),
[public release validation and memory retirement](../../ZeroDepthTraining/Host/Knowledge.cs#L132),
[repeated reveal identity replacement](../../ZeroDepthTraining/Host/Knowledge.cs#L189),
[hand identity and lower-bound updates](../../ZeroDepthTraining/Host/Knowledge.cs#L201),
[public hand identity tensor rows](../../ZeroDepthTraining/Host/Encoder.cs#L229),
[focused lifecycle checks](../../ZeroDepthTraining/Host/VisibilitySelfTest.cs).

The longer frozen benchmark exposed the repeated-Legion failure at vector 675 in
cohort seed 17,896. Both the original dense observation transfer and the lossless
packed transfer produced the same failure. Their preserved **675 × 128** action
matrices replay fully after the identity fix: **108 natural terminals, zero
censors, 20 unresolved games**, with no inference or learning updates during
replay. The capture stops at the former error; the unresolved games are not counted
as completed or assigned utilities. Sources:
[dense trace](../../ZeroDepthTraining/results/visibility-dense-cohorts-failure-b128-w8-graph-singleton.npz),
[packed trace](../../ZeroDepthTraining/results/visibility-packed-cohorts-failure-b128-w8-graph-singleton.npz),
[CPU-only replay checker](../../ZeroDepthTraining/replay.py).

## Further adversarial CPU audit

The deterministic [stress command](../../ZeroDepthTraining/Host/StressSelfTest.cs)
runs varied legal actions through the real engine, with an independent sampler
and no inference or learning. Three seed blocks completed **1,560 games**
naturally, with **596,637 decision boundaries**, **35,808 reused/fresh buffer
comparisons** and **9,684 hidden-state permutation checks**. No censor or new
game/visibility defect occurred. The corpus covers all nine normalized DLC masks,
all five heroes, 146 played card definitions and games up to round 45.

At every boundary, independent test oracles check physical-zone identity
uniqueness, temporary-card retirement, full setup/flood pool conservation,
center/destiny composition, both permanent collections, enemy set-aside relics,
known hand IDs/count lower bounds and remembered position validity. The final
1,024-game block also checks every remembered fact's actual tensor identity,
definition, range and uncertainty fields. Privacy checks reverse unseen pile
slots and exchange unrevealed opposing hand/draw cards while retaining the known
facts and hand lower bounds. Their complete original lists and card-zone values
are restored before the next real action. Failing runs save exact configuration,
seed, policy choices, boundary and host hash for `stress-replay`.

Reproduction:

```sh
dotnet Tools/ZeroDepthTraining/Host/bin/Release/net8.0/ZeroDepthHost.dll stress-selftest 1024 240200 Tools/ZeroDepthTraining/results 8000
dotnet Tools/ZeroDepthTraining/Host/bin/Release/net8.0/ZeroDepthHost.dll stress-replay /path/to/captured-failure.json
```

Reports: [24 games](../../ZeroDepthTraining/results/visibility-stress-24-s120000.json),
[512 games](../../ZeroDepthTraining/results/visibility-stress-512-s120100.json),
[1,024 games](../../ZeroDepthTraining/results/visibility-stress-1024-s240200.json),
[all existing host regressions](../../ZeroDepthTraining/results/visibility-stress-host-selftest.json).
The final Host rebuild is clean and its SHA-256 is
`fb3fd1191865a8a7a328a183fe5b2643b4f23bd8b46205eae2e8ee94d3413aa1`.
Only stress commands/oracles changed during this additional C# audit.
The existing EngineVerify suite was rebuilt with zero warnings/errors and all
278 tests passed, with zero failures/skips. Logs:
[engine build](../../ZeroDepthTraining/results/visibility-stress-engine-build.log),
[engine tests](../../ZeroDepthTraining/results/visibility-stress-engine-tests.log).

Two separately captured GPU policy censors reproduce on their exact binaries:
all **2,424** vectors in the first capture and **4,095** in the second. Each ends
with 127 natural terminals and one still-living game reaching **round 401**,
the wrapper's explicit administrative round limit. Neither replay throws a game
or encoding exception. These are censors rather than losses or successful game
completions. Reports: [first frozen replay](../../ZeroDepthTraining/results/stress-audit-initial-gpu-failure-replay.json),
[second frozen replay](../../ZeroDepthTraining/results/stress-audit-diversified-gpu-censor-s202613604-replay.json).

## Precisely bounded remaining limits

The opponent's full permanent collection and public zones are visible. Unrevealed
hand/draw allocation, unknown shuffled order, unseen center/destiny order and RNG
state remain hidden. The AI can infer possibilities from the collection, public
zones, known held cards and zone sizes without receiving the private answer.

Unobserved private rearrangements retain remembered identity membership and
position ranges, refined by later authorized peeks and public movement. Those
ranges are not a complete joint probability distribution over all compatible
histories. History is summarized into current fields and retained facts rather
than an unbounded action transcript. Audited public sources, queues and annotated
copy/replay continuations are represented; arbitrary effect-iterator internals
are not a universal continuation graph. The passing checks demonstrate the
listed fields and transitions rather than formally proving every deduction from
an entire public game history. Source:
[declared schema limitations](../../ZeroDepthTraining/Host/Encoder.cs#L78).

The previous prepared v1 artifact is intentionally preserved. Source/schema pins
must be regenerated in a new preparation directory before launching this v2
pipeline; old checkpoints cannot silently resume with changed semantics.
Sources: [preparation identity](../../ZeroDepthTraining/prepare.py),
[training source/host/catalog identity](../../ZeroDepthTraining/train.py).
