# Zero-depth Shards observation audit — 2026-10-02

This is the initial field inventory. The subsequent
[visibility follow-up](zero-depth-visibility-followup-2026-10-02.md) records the
v2 identity, hand/reveal/source corrections and broader real-engine regressions.
The [current validation](../../ZeroDepthTraining/VALIDATION.md) identifies the
fresh prepared campaign; the original v1 preparation is historical.

This audit covers the current engine, the first fast training host, the packaged AI,
and the new `Tools/ZeroDepthTraining` host. The new actor chooses one action from
the current observation and legal candidates. Its rollout host executes the real
rules without searching, cloning positions, evaluating alternative futures, or
calling the incumbent. Incumbent search is confined to evaluation.

The new representation substantially expands current public/own information and
removes the old silent truncations. **It is not a proof that every deduction from
the entire public history is represented exactly.** Current visible fields,
public card facts, all pending options, and exact legal action coverage are
auditable; uncertainty correlations and arbitrary effect-iterator continuation
locals have the limitations stated below. These qualifications must remain in
the training manifest and any strength report.

## Information boundary

The acting seat is the pending-input owner, which can differ from the turn owner
during shield or other effects. A full owned collection means an unordered
multiset of permanent deck/hand/discard/play/champion cards, excluding temporary
fast-play loans. It does not reveal unseen draw order. The packaged AI already
implements the user-authorized public opponent permanent collection contract,
while withholding its hidden hand/deck allocation. The presentation snapshot is
more restrictive and supplies `FullDeck` only to its viewer.
Sources: [state zones](../../../Assets/Scripts/Shards/Engine/ShardsState.cs#L28),
[snapshot FullDeck contract](../../../Assets/Scripts/Shards/Engine/ShardsSnapshot.cs#L50),
[production opponent collection boundary](../../../Assets/Scripts/Shards/AI/Encoder.cs#L213).

The new encoder aggregates the opponent collection before emitting its counts;
it emits the actor's own draw composition unordered. It never emits RNG state,
hidden center/destiny draw order, unseen opponent hand allocation, other-seat
Scry answers, or raw creation/decision counters. Per-card references are local
canonical ordinals. Remembered positions additionally retain their already-public
physical identity as `(InstanceId + 1) / 65536`, with zero for legacy unknown
identity; private unseen identities are never supplied.
Sources: [new histograms and entity references](../../ZeroDepthTraining/Host/Encoder.cs),
[private draw event redaction](../../../Assets/Scripts/Shards/Engine/ShardsEvents.cs#L38).

## Previous omissions

| Representation | Information omitted or compressed |
|---|---|
| First fast host, observation v2 | 2,048 floats; 24 per-card entity statuses; last 16 staged choices; no banished identities, known top memory, known opponent hand, deferred champion assignments, full public pool conservation, or all disabled/off-page revealed options. |
| Packaged AI, observation v7 | 3,328 floats; statuses extend to 64 entities, top memory remains four cards, staged order remains the last 16 choices, pending monster order stops at 56. Public hand memory exists in `SupplementKnowledge.Hand` but is never encoded. Market and public zones are combined in several count channels, and unavailable row slots cannot always be recovered from candidates. |
| Packaged action adapter | Pages retain actions, but its champion split implementation restricts some allocations through lethal/taunt interval handling. Its draft is selected by `HeroDraftPolicy`, not the trained actor. |

Sources: [first-host explicit partial-observation contract](../Host/CONTRACT.md),
[production dimensions/caps](../../../Assets/Scripts/Shards/AI/Encoder.cs#L12),
[production top/hand ledger](../../../Assets/Scripts/Shards/AI/Knowledge.cs#L15),
[production split adapter](../../../Assets/Scripts/Shards/AI/Adapter.cs#L339),
[production draft override](../../../Assets/Scripts/Shards/AI/PolicyEngine.cs#L110).

## New field inventory

The authoritative schema is `shards-zero-depth-observation-v1`, 24,576 observation
floats, 64 visible candidate slots, and 48 floats per candidate. Every count uses
the ordinally sorted card-definition catalog. Fixed division never clips values.
Every table capacity fails loudly instead of discarding rows. The catalog command
exports the exact dimensions, histograms, card mapping, static card definitions,
public effect summaries, hero abilities, and stated limitations.
Source: [schema and catalog](../../ZeroDepthTraining/Host/Encoder.cs#L14).

### Player scalars

Both relative seats have the same 64-slot block at `16 + relativeSeat * 64`.

| Field group | Fields represented |
|---|---|
| Resources and visible sizes | `Health`, `Mastery`, `Gems`, `Power`, `Hand.Count`, `Deck.Count`. |
| Hero and character | `CharacterId`, `CharacterExhausted`, `FocusedThisTurn`, `HeroAbilityUsedThisTurn`, `FirstBuyUsedThisTurn`, `FullControl`. |
| Lifetime availability | `RelicRecruited`, `DestinyTaken`, `ExtraTurnUsed`, `DoomGateFloodUsed`, `Eliminated`. |
| Turn effects | `IgnoreShieldsThisTurn`, `HealthToPowerThisTurn`, `HealingDoubledThisTurn`, `OverflowHealthToPowerThisTurn`, `ShieldsDoubledUntilNextTurn`, `CopyHomodeusAlliesThisTurn`. |
| Routing and turn counters | `NextRecruitsToHand`, `NextHomodeusChampionsIntoPlay`, `NextChampionsIntoPlay`, `BonusDrawsOnBigHit`, `MaxDamageDealtToOneOpponent`, `CardsBanishedThisTurn`, `RerollsThisTurn`, `NextRerollDiscount`, authoritative `RerollCost`. |
| Played faction state | All seven `FactionPlays` and seven `FactionAllyPlays` counts; complete ordered `PlayedThisTurn` identities are also a separate table. |

Names are presentation strings; seat identity is explicit and fixed host names
appear where a decision label/title uses them. Player index is represented by
absolute actor seat and relative owner references. All rules-relevant mutable
primitive fields in the current `ShardsPlayer` are accounted for above.
Sources: [complete player declarations](../../../Assets/Scripts/Shards/Engine/ShardsState.cs#L8),
[new player encoder](../../ZeroDepthTraining/Host/Encoder.cs).

### World and rules

The scalar prefix includes actor, turn owner, round, DLC mask, center/destiny
draw-pile counts, game-over/winner, extra-turn recipient, active Ingeminex attack
phase, end-turn-resolution phase, and all six `ShardsRules` values. Initial public
catalog counts and remaining center/destiny composition are supplied separately;
the latter are computed by conservation from public collection/zone counts,
temporary loans and publicly known Doom Gate floods, without reading hidden
deck identities. Initial counts are cached and refreshed when public draft picks
change the applicable relic pools.
Sources: [world state and rules](../../../Assets/Scripts/Shards/Engine/ShardsState.cs#L127),
[rules constants](../../../Assets/Scripts/Shards/Engine/ShardsTypes.cs#L215),
[public initial pool source](../../../Assets/Scripts/Shards/Engine/ShardsEngine.cs#L201),
[new conservation encoder](../../ZeroDepthTraining/Host/Encoder.cs).

### Zone composition

The 24 histograms begin at 256, with 192 reserved slots each. Actual card count
and restoration scale are supplied in `histogram_descriptors`.

| Index | Information |
|---|---|
| 0–6 | Own hand, unordered draw composition, discard, played allies/loans, champions, owned destinies, remaining set-aside cards. |
| 7–12 | Opponent full permanent collection, discard, play, champions, destinies, deterministically inferred remaining set-aside relics. |
| 13–16 | Market, shared destiny row, active monsters, banished cards. |
| 17–20 | Remembered public opponent hand lower bounds, initial pool counts, own/opponent cards played this turn. |
| 21–23 | Conservation-derived remaining center pool, undealt destiny pool, own permanent collection including cards temporarily removed into a public reveal window. |

Opponent set-aside membership derives from public hero identity and public relic
recruit/return events. Corruption's extra relic return updates this membership
even though it bypasses the normal relic-recruit action.
Sources: [deterministic public relic pool](../../../Assets/Scripts/Shards/Engine/ShardsEngine.cs#L225),
[Corruption reward](../../../Assets/Scripts/Shards/Content/ShardsHorizonSet.cs),
[new knowledge ledger](../../ZeroDepthTraining/Host/Knowledge.cs).

### Per-card and pending-input tables

| Table | Offset / capacity / stride | Fields |
|---|---|---|
| All visible card instances | 4,864 / 384 / 12 | Definition, relative owner, zone, exhausted, marked damage, effective defense, effective shield, fast-play flag, cleanup-banish flag, ordered pending-monster position, original public zone ordinal, deferred champion-hit amount. Temporary revealed cards have a distinct zone code. |
| All pending options | 9,472 / 384 / 20 | Definition, original option ordinal, visible entity link, amount, required/taunt hint, owner, disabled, selected, default multiplicity, full current target reference. Hero and Volos option identities are typed separately. Label identity and dynamic numeric values occupy the remaining fields. |
| Remembered positions | 17,152 / 384 / 8 | Definition, center/own/opponent pile kind, minimum and maximum top-relative position, exactness, uncertain-presence flag, presence marker and already-public physical identity. Exact public/private-seat center bottoms are retained along with tops. |
| Ordered staged answer | 20,224 / 384 / 4 | Definition, original option ordinal, allocation amount, visible entity link. |
| Ordered cards played this turn | 21,760 / 384 / 4 | Definition, relative player, visible entity link, temporary-loan status. |
| Public-source continuation descriptors | 23,296 / 64 / 8 | Definition, controller, entity link, matching registered play/exhaust/reward/monster-attack classification, monster-source classification. Unknown/private source entries are excluded. |
| Public copy/replay provenance | 23,808 / 96 / 8 | Public chosen copy/mode identities, copied-instance recursion guards, nested scope/target order, active/completed/upcoming status, remaining repetitions and local entity/option links. Only a currently authorized public source exposes this table. Scalar 176 is its count. |

Decision kind, ordered flag, min/max, number of options/selected choices, current
page, split target/remaining/interval, context identity and authorized title are
also present. Disabled and off-page options remain in the observation regardless
of the visible candidate mask. Pending monster order is encoded through the
per-instance order field, so no 56-entry tail is discarded. Deferred Testudo
champion allocations and face assignments are represented during shield choices.
Sources: [decision contracts](../../../Assets/Scripts/Core/DecisionRequest.cs),
[deferred damage flow](../../../Assets/Scripts/Shards/Engine/ShardsEngine.cs#L1192),
[new option/entity encoder](../../ZeroDepthTraining/Host/Encoder.cs).

## Public memory provenance

The original generic reveal event did not say which personal pile a card came
from, whether it stayed on top, or whether it was temporarily removed. New
optional metadata names only already-publicly-shown cards: parallel personal
seat/instance lists, the personal-top removal flag, and parallel center reveal
instance lists. Fabricator now remembers both shown tops even when no card can be
copied and no copy menu opens. Legion Carrier retains all shown cards—including
disabled nonchampions—while they sit in iterator locals outside engine zone
lists. Gatekeeper distinguishes its public top from a hand reveal. Shard Seer
and Riposte Doctrine now mark their unchanged public hand reveals correctly.
Sources: [new event provenance fields](../../../Assets/Scripts/Shards/Engine/ShardsEvents.cs#L254),
[Horizon reveal flows](../../../Assets/Scripts/Shards/Content/ShardsHorizonSet.cs),
[Duel reveal flows](../../../Assets/Scripts/Shards/Content/ShardsDuelSet.cs).

Accepted public movement events update the ledger. Private draw `DefId` and
instance identifiers are never consumed for opponent inference; a public known
top can imply a newly drawn hand card. Shuffles invalidate order. Arbitrary
retrieval/private reorder broadens position bounds instead of inspecting hidden
order. The ledger itself has no four-card cap; the tensor checks its reviewed
capacity rather than truncating. Stale temporary records are retired by exact
public instance identity when a card becomes ordinarily visible; distinct copies
of the same definition remain distinct.
Source: [knowledge update logic](../../ZeroDepthTraining/Host/Knowledge.cs).

Paid and free market removals now emit a public bottom-return event before any
refill. Doom Gate emits a public center-shuffle event before subsequent reveals;
the ledger invalidates order in event sequence. Mercenary returns retain their
already-public physical identity so later known reorder facts match the same card.
Sources: [public movement events](../../../Assets/Scripts/Shards/Engine/ShardsEvents.cs),
[engine emissions](../../../Assets/Scripts/Shards/Engine/ShardsEngine.cs).

Public copy/mode selections persist on their resolution context. Explicit scopes
record automatic and selected copies, all chosen future targets, repetitions and
copy recursion guards while a child menu is parked. The same scope path covers
Warpquartz and General Decurion direct play-effect replays without changing their
guard rules. No private iterator locals are inspected. The production tactical
deep copier clones these mutable lists/objects independently; a child-advance
regression verifies that hypothetical resolution leaves the live ledger intact.
Sources: [public context provenance](../../../Assets/Scripts/Shards/Engine/ShardsEffects.cs),
[public decision annotations](../../../Assets/Scripts/Shards/Engine/ShardsEngine.cs),
[copy/replay tests](../../ZeroDepthTraining/Host/SelfTest.cs).

## Canonical ordering and action coverage

Card definitions use `StringComparer.Ordinal`. Priority and unordered-selection
candidates sort by kind, definition, slot, label, and public instance/option tie
breaks, using cached numeric/string keys. Visible entities sort by relative
owner, zone, definition, status and a public instance tie break. Original public
zone/option order is retained as a feature; ordered decisions and played-card
history retain their original semantic order. Engine hands/decks are never
mutated to accomplish presentation normalization.
Source: [new adapter sorting](../../ZeroDepthTraining/Host/Adapter.cs).

Every engine-advertised priority action remains available, including concede.
Subset/permutation decisions allow all legal selections, with finish when min is
met and automatic submission at max. A 63-plus-next-page wrapper reaches every
option beyond the 64-slot candidate limit. Damage is chosen through binary
integer intervals covering every amount from zero through the remaining pool;
the last target receives the required residual. Taunt, shields, legality and
effect resolution remain authoritative in the engine. The trained actor also
chooses the real hero draft.
Sources: [new exact wrapper](../../ZeroDepthTraining/Host/Adapter.cs),
[engine split validation](../../../Assets/Scripts/Shards/Engine/ShardsEngine.cs#L1141).

Automation is optional and restricted to a single available wrapper candidate.
It does not force focus, shield revelation, card play, or end turn where another
candidate exists. Exercise parity compares complete final state hashes and real
submission counts with automation on/off; policy-choice, wrapper and engine
submission counts are reported independently. Throughput choice is based on
whole finished cohorts, not partially sampled games.

## Executable verification and remaining limits

The host selftest covers 1,166 real-game states across four naturally completed
games, legal/finite tensors, on/off automation parity, private order/allocation
invariance, every page, disabled-option visibility, memory beyond four cards,
hard overflow, pinned dirty reused-buffer equality with fresh complete encoding,
Fabricator no-menu top knowledge, Legion temporary-card conservation, Corruption
relic membership, temporary-identity reentry, uncertain singleton positions,
chronological shuffle/free-bottom returns, nested copy/replay provenance and
search-branch metadata isolation. `opponent-selftest` compares
the helper with the actual production `PolicyEngine`, including real searches.
Sources: [host selftests](../../ZeroDepthTraining/Host/SelfTest.cs),
[incumbent selftest](../../ZeroDepthTraining/Host/CurrentOpponentSelfTest.cs).

```sh
/home/lva/.dotnet/dotnet build Tools/ZeroDepthTraining/Host/ZeroDepthHost.csproj -c Release -t:Rebuild
/home/lva/.dotnet/dotnet Tools/ZeroDepthTraining/Host/bin/Release/net8.0/ZeroDepthHost.dll selftest
/home/lva/.dotnet/dotnet Tools/ZeroDepthTraining/Host/bin/Release/net8.0/ZeroDepthHost.dll opponent-selftest FROZEN_BUNDLE
```

The sampled-policy full-cohort benchmark exposed a stale temporary ledger record
reentering an ordinary public zone. The exact captured 409-step, 256-lane action
matrix was replayed successfully after the identity cleanup fix, with 124 natural
terminal lanes, zero censors, and 132 still-live lanes at its last captured step.
Replay evidence lives beside the benchmark reports in `Tools/ZeroDepthTraining/results`.

A later sampled cohort exposed a possibly-absent center fact whose position
interval had narrowed to a singleton. It was incorrectly asserted as certainly
present. Assertions and the neural exactness flag now require certain presence.
The exact 2,061-step, 128-lane capture replayed successfully with 127 natural
terminal lanes, zero censors and one still-live lane. Both failure matrices were
replayed again after the public copy and chronological movement annotations.

The following are **limits, not verified completeness claims**:

1. Public history is summarized into current state/card facts. A zero-memory
   feedforward actor does not receive the entire event stream. Extra deductions
   depending on detailed historical correlations are not proven recoverable.
2. Position intervals after unobserved reorders/removals retain remembered
   identities and bounds but do not encode the joint distribution/correlations
   of all possible pile orders. Exact currently known positions are represented.
3. Public-source queue summaries expose registered operation identity/order;
   public chosen modes and nested copy/replay dependency/target/repetition facts
   are explicit. Remaining bespoke iterator-local work outside those annotated
   scopes is not a complete executable continuation graph.
4. Static effect summaries pool some ordering/branch relationships. Exact card
   identity and immutable catalog/source hashes disambiguate cards; the learned
   actor still has to learn their detailed consequences from experience.
5. Text identity uses four exact hash bytes with runtime collision rejection;
   numeric parsing is bounded. Hero draft prose uses typed hero identity and the
   full static ability catalog instead of squeezing all its numbers into four
   numeric slots. This is a semantic observation, not raw language-model text.
6. The fixed capacities require an audit if content/rules grow. Overflow stops
   training visibly; no complete-information claim can rely on truncated rows.

A stronger formal completeness gate would require an event-sourced public view
with structured effect continuation descriptors supplied by the engine, plus
joint knowledge constraints or a recurrent public-history channel. It should be
specified explicitly before describing this version as mathematically containing
every possible inference from public history.
