# V8 information boundary and representation audit

The new encoder fixes two demonstrated information aliases and several smaller missing public features. It does **not** establish that the representation contains every piece of information a human could remember. Tests and a source audit support the specific privacy guarantees below; they are not a proof over all reachable states.

## Implemented contract

The first 2,048 observation values and all 64 × 32 candidate values preserve their previous meaning. An additional 512 observation values produce `shards-observation-v5` and `ObsDim=2560`. The supplement includes explicit own-card prerequisites, authorized current effect source, remembered publicly revealed personal deck tops, public purchase history, and 40 additional individual public entities. [Encoder](../experiments/HostV8/Encoder.cs#L11)

Supplement offsets below are relative to 2048:

| Offset | Meaning |
|---|---|
| 0–4 | Authorized source known flag, card code, actual physical Reactor cost flag, banish-at-cleanup flag, zone |
| 5–6 | Own next-reroll discount and actual cards played this turn |
| 7–11 | Played Cinder Scars count, played shield-card count, source played this turn, played copies of source definition, relative source owner |
| 16–47 | 32 named own/public prerequisite flags; `ReadinessNames` records their order |
| 48–55 | Four known own top cards, each `(card code, known flag)` |
| 56–63 | Four publicly known opponent top cards, same representation |
| 64–252 | Opponent cumulative non-fast-play purchase/recruit events plus relic recruitment, per definition |
| 256–495 | Individual public entities 24 through 63, with the same six fields as the existing first 24 |
| 496–508 | Total entities, residual overflow, own played-card type/cost counts and temporary-state summaries |

Unlisted positions are reserved. Card identities in the supplement are normalized catalog codes, not new categorical embeddings. The runtime must provide its own trainable handling of these channels. Purchase history is explicitly **cumulative acquisition evidence**, not an exact current opponent collection or hand estimate. It does not subtract banishments or capture all kept fast-plays. [Knowledge event handling](../experiments/HostV8/Knowledge.cs#L44)

The 32 prerequisite channels include all current named destiny conditional exhaust predicates, Primus Pilus, mastery thresholds, selected per-count nonzero conditions and available low-cost row targets. They do not remove conditional actions. The flags describe a prerequisite, not whether using an ability immediately is optimal or even beneficial: Nature Dominance may improve after further plays; a satisfied banish gate can still have bad targets; healing may convert into power. General Decurion, Numeri Drones and other future-effect setters must remain usable before the actions they modify. [Registered destiny predicates](../../../Assets/Scripts/Shards/Content/ShardsHorizonSet.cs#L408), [Duel replacements](../../../Assets/Scripts/Shards/Content/ShardsDuelSet.cs#L465)

## Demonstrated aliases removed

1. A legal Fabricator play publicly reveals the two personal top cards. After copying the same opposing Crystal, identical deck multisets with opposite own order previously produced identical observations, despite a different known next draw. The new event-derived memory distinguishes these states without inspecting hidden deck identities. Normal Fabricator revealing only champions is remembered even though no copy decision opens. [Fabricator implementation](../../../Assets/Scripts/Shards/Content/ShardsDuelSet.cs#L653)
2. Physical Reactor and Ojas copying Reactor previously had the same inputs, despite the three-gem mode banishing only a physical Reactor source in its controller's play zone. The new source fields distinguish the contexts. The adapter's separate source-proven automatic choice removes the free two-versus-three-gem decision when appropriate. [Reactor implementation](../../../Assets/Scripts/Shards/Content/ShardsDuelSet.cs#L520)

The Reactor encoding regression is a deliberately parked effect-context fixture, inspected before adapter rebuilding can automatically resolve a dominated copied mode. It is not a complete legal replay. The Fabricator regression starts from a constructed position and then submits accepted engine actions.

## Memory validity and privacy

- Personal top memory consumes public `CardReturned(ToDeckTop)` events, public draw **counts**, public shuffle events and Fabricator's public ordered reveal contract. It never reads a drawn opponent card's `DefId`. Public draw-pile searches clear the remembered prefix conservatively. [Event visibility definitions](../../../Assets/Scripts/Shards/Engine/ShardsEvents.cs#L38)
- Before an action, personal deck sizes are recorded. Unexplained public size changes clear remembered tops. This covers Gatekeeper and Legion Carrier, which remove deck cards without normal draw events. It avoids retaining a stale known top after these effects. [Legion Carrier reveal flow](../../../Assets/Scripts/Shards/Content/ShardsHorizonSet.cs#L150), [Gatekeeper](../../../Assets/Scripts/Shards/Content/ShardsHorizonSet.cs#L265), [Grim Tutor](../../../Assets/Scripts/Shards/Content/ShardsDuelSet.cs#L825)
- A generic `CardsRevealed` event is **not** interpreted as personal deck order. Unify and Dominion also emit that event for hand cards. Fabricator identification uses its public play or explicitly chosen copy effect, or the exact revealed-copy decision contract. [Unify](../../../Assets/Scripts/Shards/Engine/ShardsEffects.cs#L299)
- Current effect source is obtained from the engine's parked context but emitted only when the actual object is in the viewer's own hand/set-aside or a public zone. Hidden opponent hand/deck membership cannot satisfy this guard. No instance IDs, random state, raw context object, hidden queue or hidden source metadata enter the observation. [Source guard](../experiments/HostV8/Knowledge.cs#L139)
- Readiness predicates inspect only the acting player's own state and public row. No opponent collection-dependent condition is evaluated. Opponent defense continues to use printed defense outside a split and the already-announced remaining-defense contract during a split; this avoids the Ferrata hidden-collection leak. [Defense boundary](../experiments/HostV8/Encoder.cs#L371)

## Validation

`dotnet build experiments/HostV8/TrainingHostV8.csproj -c Release` succeeded with zero warnings/errors. `information-selftest` passed **1,544 checks**. These cover accepted Fabricator reveal memory; Fabricator with no copyable cards; unrelated hand reveal rejection; public return/draw/shuffle/removal handling; source differences; a whole-input hidden opponent membership/order perturbation; 19 registered own predicates across 40 constructed states; entity 50 status; and explicit overflow beyond 64.

The test initially exposed two test-fixture assumptions, both corrected: mastery wrappers intentionally do not implement the UI conditional-glow interface, and synthetic top-return/draw events must include the corresponding public deck-size changes. The source fixture was adjusted when copied Reactor became automatic. These were test adaptation issues, not failures in the running V7 trainer.

An additional isolated project references the frozen V8 DLL without rebuilding it. Across **64 completed games, zero censors, and 23,914 reached decision states**, it checked **13,715 states** (6,820 seat 0 / 6,895 seat 1). All **4,672 observation/candidate/mask floats** remained exactly equal after counterfactual hidden-input changes: 152,409 opponent hand/deck definition mutations, 13,715 personal/center deck-order and RNG-state perturbations, including 208 states with remembered own-top information. It also recomputed and compared the engine's priority legal-action menu in **10,175 states**, rather than relying only on cached candidates. All comparisons passed. [Raw result](v8-hidden-invariance-2026-09-26.json), [isolated probe source](v8-privacy-probe/Program.cs)

Coverage comprised priority plus 24 decision contexts, including 37 copy, 33 mode, 41 tutor, 471 shields and 430 split states. `soi.target` was not reached in this sample; this is not a claim of complete context coverage. Captured decision options, their revealed card definitions, public zones and legitimately retained memory were left unchanged. Every perturbed value was restored before submitting the next accepted action. These are artificial counterfactual invariance checks at real reached states, not a proof that every perturbed state is independently reachable or that the complete game cannot leak information through some other interface. The tested host SHA-256 was `82af9cf87b82c69c82800f155f83316ffcfe332e8537d48afcc7e2a3c9a9cdc7`.

## Remaining information gaps and bounded compression

These are material limitations to preserve in reporting rather than saying “no missing information.”

1. **Revealed opponent hand memory remains absent.** Shields, Unify and Dominion publish card identities that may remain in the hand. The policy does not retain that per-card knowledge across subsequent decisions, nor a belief over remaining cards after public plays and private draws. The new acquisition histogram is not a substitute. [Shield reveal](../../../Assets/Scripts/Shards/Engine/ShardsEngine.cs#L1345), [Unify/Dominion reveal](../../../Assets/Scripts/Shards/Engine/ShardsEffects.cs#L427)
2. **Effect queue and nested-copy progress are not represented explicitly.** Current authorized source is now encoded, but pending later effects, continuation position and the copied-card chain are not. Many consequences can be inferred from board state and current decision, but that is not an equivalence proof. Multi-copy Fabricator/Warpquartz continuations can carry publicly reconstructible context that a feed-forward observation omits. [Queue and pump](../../../Assets/Scripts/Shards/Engine/ShardsEngine.cs#L1689), [Fabricator nested resolution](../../../Assets/Scripts/Shards/Content/ShardsDuelSet.cs#L685)
3. **Public top memory is conservative and bounded to four cards.** More than four public top returns lose the deeper known prefix. A nested multi-copy Fabricator that reveals only champions, opens no copy decision, and was not the single explicitly chosen next effect is not recognized by the conservative provenance detector. No hidden-state lookup repairs this omission. Public size mismatch invalidation can also forget still-valid deeper information instead of deriving the exact removed subset.
4. **Individual entity capacity is 64, not a proven global bound.** The original first 24 remain; another 40 are appended. Aggregate card counts and legal candidates continue beyond this limit and a residual overflow counter is present. A constructed 65-champion state explicitly tests reporting. The available card pool and monster flood do not justify claiming that all reachable 1v1 public entities fit in 64. Per-card temporary/banish flags are still only available for an active candidate/source plus aggregate own counts, rather than for every public entity.
5. **Staged selections retain only the last 16 ordered entries.** All staged cards still contribute to aggregate counts and the adapter retains the full selection to execute correct rules. Publicly distinct allocations to multiple same-definition champions can therefore still compress identically when earlier target entries fall outside the retained trace. Actual split target reordering and legal masks reduce some immediate ambiguities, but they are not a complete representation of the earlier allocation sequence. [Trace encoding](../experiments/HostV8/Encoder.cs#L71), [Engine ordered split](../../../Assets/Scripts/Shards/Engine/ShardsEngine.cs#L1100)
6. **No general recurrent event history.** Current card counts, authored readiness predicates and critical Cinder/Riposte summaries cover more strategy-relevant state, but they do not encode every historical public event or distinguish every inferred opponent strategy. Feed-forward policy inputs remain a deliberate finite approximation.

These limitations should be measured in future frozen-policy probes before adding a much larger wire format. A privacy-safe richer representation would use an explicit redacted event ledger, semantic effect-progress metadata and pooled variable-length public entities, rather than reading hidden engine state to fill gaps.
