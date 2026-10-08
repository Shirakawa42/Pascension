# Corrected public collection contract — V9

The user's clarification is authoritative: **both players' complete permanent deck compositions are public; the opponent's allocation between hand and draw pile, and draw order, remain private.** Earlier V8 analysis treated the opponent's full composition as hidden. That assumption was incorrect for this task. In particular, a test that changes opponent card definitions while preserving only hand/deck sizes is no longer a valid privacy invariant: it changes authorized public information.

The existing UI snapshot constructs `FullDeck` only for its viewer. Its definition is still useful for deciding which zones belong to the collection: draw pile, hand, discard, permanent play-zone cards and champions; temporary fast-plays, set-aside relics, destinies and banished cards are excluded. The own full-deck window consumes that viewer-only snapshot. This historical UI limitation does not override the user's explicit information rule. V9 changes training inputs, not this UI. [Snapshot construction](../../../Assets/Scripts/Shards/Engine/ShardsSnapshot.cs#L151), [deck window](../../../Assets/Scripts/Game/Soi/SoiGameScreen.cs#L2372)

## New observation

V9 uses `shards-observation-v6`, 2,816 observation floats and unchanged 64 × 32 candidate fields. The previous 2,560 observation positions retain their meanings, except that an opponent champion's authorized defense now includes proven-public auras at all decision states. This is an intentional correction to the information available through the existing defense field. [V9 encoder](../experiments/HostV9/Encoder.cs)

New positions are relative to offset 2560:

| Positions | Meaning |
|---|---|
| 0–188 | Exact current opponent permanent collection counts per catalog definition, divided by 10 |
| 192–198 | Opponent Allegiance counts, divided by 20; includes temporary fast-played cards as required by the rules |
| 199–205 | Permanent collection faction counts, divided by 20 |
| 206 | Permanent collection size / 50 |
| 207 | Printed cost sum / 100 |
| 208–209 | Printed shield sum / 50 and number of shield cards / 20 |
| 210–216 | Counts for seven printed card types / 20 |
| 217–230 | Counts by printed cost 0 through 13 / 20 |
| Remaining | Reserved zeros |

These are recomputed from the current collection, so purchases, free permanent recruits, kept fast-plays and banishments are reflected immediately. This exact current histogram is separate from V8's cumulative purchase-event history. Faction membership honors Prism and Project Yggdrasil. Temporary fast-plays remain absent from permanent composition but present in Allegiance, matching the user's earlier clarification and the actual `OwnedCount` rule. [Allegiance](../../../Assets/Scripts/Shards/Engine/ShardsDuelEffects.cs#L126)

All collection aggregates use integer counts/sums and normalize once. Summing heterogeneous floating-point costs in zone order could otherwise produce tiny rounding differences when hidden hand/draw allocation changes, despite identical public totals.

## Public defense correction

The three registered defense auras are safe under the corrected rule:

- Duel Ferrata Guard reads full owned Homodeus count, including public temporary fast-plays. [Duel Ferrata](../../../Assets/Scripts/Shards/Content/ShardsDuelSet.cs#L201)
- Base Ferrata Guard reads the public hero identity. [Base Ferrata](../../../Assets/Scripts/Shards/Content/ShardsRelicsSet.cs#L46)
- One Mind One Army adds a constant two defense. [Destiny](../../../Assets/Scripts/Shards/Content/ShardsHorizonSet.cs#L418)

V9 calculates exact opponent defense when all applicable auras are from this reviewed list. A future unreviewed aura fails closed to defense already announced by a split decision, or printed defense outside a split. The encoder does not grant all future condition functions unrestricted access to hidden hands simply because current collection totals are public.

## Validation

- Release build: no warnings or errors.
- `public-deck-selftest`: **2,903 checks passed**, including all permanent zones, temporary versus kept fast-plays, immediate banishment, public-composition sensitivity, Ferrata thresholds and real-game private hand/draw redistributions. [Result](v9-public-deck-selftest-2026-09-27.json)
- `information-selftest`: **1,544 checks passed**, with the opponent privacy fixture corrected to preserve public composition.
- Existing replay, worker-equivalence, public-entity, legal-candidate and interval suite: **32 replay seeds passed**. Its old “hidden Ferrata composition” assertion now checks that public composition/defense changes are represented; hand order remains private.
- An isolated project referencing the V9 DLL tested **13,715 reached states across 64 completed games, zero censors**, covering priority and 24 decision contexts. All **4,928 input floats** were unchanged by composition-preserving private allocation/order/RNG perturbations. The probe exchanged **33,536 hand/draw memberships**, perturbed deck order in 13,715 states, included 208 states with remembered own-top information, and independently recomputed **10,175 legal-action menus**. [Raw result](v9-public-composition-privacy-2026-09-27.json), [probe](v9-privacy-probe/Program.cs)

The tested probe host hash is `bd67160aaa6849b2bc89a3ffe658ab1f09f43fd43d9a491405695a445961ef4c`. Test perturbations leave explicit decision-option definitions and legitimate remembered information unchanged and restore all mutated state before the next accepted action. This is broad input-invariance evidence, not a formal exhaustive privacy proof. The sample did not reach `soi.target`; some contexts, particularly shields, naturally have few states with a nonempty opponent hand that can be redistributed.

## Remaining limits

The new exact public collection does not expose which of those cards the opponent is holding. Previously revealed opponent-hand cards still need separate temporal memory if they are to constrain the current hidden hand. Effect-continuation memory, four-card known-top capacity, 64 individual public entities and the last-16 staged trace remain bounded as documented in the V8 audit. The corrected public collection rule resolves the major missing collection information; it does not make these other representations lossless.
