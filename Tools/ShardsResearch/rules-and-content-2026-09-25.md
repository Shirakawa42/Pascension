# Shards of Infinity: current rules, content, and information audit

Date: 2026-09-25. Target: this repository's two-player game with every implemented DLC, including Duel of Doom. This is a fresh audit of game code, card exports, the September 18 design session, and publisher rulebooks, updated with the user's subsequent rule decisions. No prior AI, training code, results, or git history informed the recommendations. Original audit measurements remain identified as pre-fix evidence.

## Findings that affect the training design

1. The target is a **custom, partially observed, two-player deck builder**, not an exact implementation of any single published edition. Train the rules that actually ship, with an explicit content/rules fingerprint.
2. All enabled sets resolve to **144 active card definitions**, including **95 market/monster definitions representing 180 cards**, **30 destinies**, **15 relics**, and **4 starter definitions**. There are five heroes; Duel drafts distinct heroes after the board and opening hands are dealt. The raw registry has 189 definitions because it retains superseded printings.
3. Buying is only one part of the strategy. Ordering plays and activations, reserving gems for Focus/abilities/rerolls, taking a destiny, choosing a relic, manipulating the market, and choosing champion kills all materially change outcomes.
4. The presentation snapshot omits strategically important public state. The original executable audit also found opponent condition glows exposing private-hand predicates; the corrected snapshot evaluates conditional hints only for the viewer. Audit every proposed training field against authorized player information, including after the fix.
5. The user resolved the acquisition and counting semantics: Comet is obtainable only by normal purchase, so free recruitment must be blocked; Allegiance intentionally includes temporary fast-plays even though the snapshot's permanent ownership list excludes them. The latter is not a bug or a pending design question.
6. A small feed-forward policy over card-count summaries, public state, and candidate actions is a reasonable first representation. Omitting exact public history is an approximation; omitting deck composition, active effects, choice context, or champion targeting is a much more consequential restriction. Fixed card scores or unconditional “play everything” rules would make some cards appear weak because the policy cannot use them.

## 1. Scope and sources of truth

The current flags are Base plus Relics of the Future (`1`), Shadow of Salvation (`2`), Into the Horizon (`4`), and Duel (`8`). `NormalizeDlc` makes Duel imply all three earlier expansions, so requesting Duel alone produces mask `15`. The engine accepts a player list; the training launcher must explicitly require two players. See [DLC definitions](../../Assets/Scripts/Shards/Engine/ShardsTypes.cs#L7), [normalization](../../Assets/Scripts/Shards/Engine/ShardsEngine.cs#L316), and [standard configuration](../../Assets/Scripts/Shards/Content/ShardsContentRegistry.cs#L45).

The priority for this investigation is executable C# behavior, then current tests and generated content, then recorded local design decisions, with official rules used to distinguish inherited rules from house changes. That order is necessary because local prose contains acknowledged corrections and stale passages.

The publisher currently links the original base game, the three original expansions, and the Saga Collection from its [official rules archive](https://stoneblade.com/pages/rules). Duel of Doom is represented locally as a custom pack; it is not an expansion listed in that archive. This report makes no claim that every promotional or edition-exclusive card is implemented. Cooperative campaign, solo Nemesis, teams, five-player alliance, and shadow-champion variants are outside the requested 1v1 target and the existing local scope.

The newer [Saga rulebook](https://cdn.shopify.com/s/files/1/0691/8805/9412/files/SOI_Saga-Rules-Web.pdf?v=1735833106), pp. 8, 25–29, is a distinct reference: it lists six heroes, twelve relics, six Ingeminex, 154 market cards or 176 for its Kickstarter edition, sends fast-played mercenaries to the banish pile, and deals private destiny choices by default while retaining the shared destiny row as a variant. Those are not the current repository rules. The archive's “Skulls & Sails” belongs to Ascension. This audit found no primary-source basis for treating “Skulls” or “Into the Abyss” as missing Shards expansions; no such inferred scope is added here.

## 2. Exact current content census

Counts below were recomputed from the current generated [Card Designer baseline](../CardDesigner/baseline.js), [replacement metadata](../CardDesigner/registry-metadata.json), and [card table](../ShardsData/cards-table.md), and checked against the registration and setup code. They are counts of the current implementation, not a claim of independently photo-verifying every physical printing.

| Set | Registered definitions, including old printings | Relevant components before replacement filtering |
|---|---:|---|
| Base | 49 | 88 market cards; 4 starter definitions making a 10-card deck per player |
| Relics of the Future | 20 | 24 market cards; 8 relics |
| Shadow of Salvation competitive content | 8 | 12 market cards, including 3 replacement Cloud Oracles; 2 Rez relics |
| Into the Horizon | 48 | 25 faction market cards; 5 Ingeminex; 30 destinies |
| Duel of Doom | 64 | 20 new definitions: 15 market definitions / 27 copies and 5 extra relics; 44 replacement definitions |
| Total registry | **189** | Old and replacement versions coexist in the registry |

Source anchors: [registry order](../../Assets/Scripts/Shards/Content/ShardsContentRegistry.cs#L12), [component tests](../../Assets/Tests/EngineTests/ShardsContentTests.cs#L39), [Duel registrations](../../Assets/Scripts/Shards/Content/ShardsDuelSet.cs#L23), and [export cardinality tripwire](../CardDesigner/generate-baseline.mjs#L37).

With all flags enabled, filtering removes 44 Duel-replaced definitions and the original Cloud Oracles definition. Hence `189 - 45 = 144` active definitions. The market contains `88 + 24 + 12 + 30 - 3 + 27 + 2 = 180` physical cards: three old Cloud Oracles are removed, 27 new Duel market copies are added, and Duel increases Cinder Scars from three copies to five. Do not train on the raw 189-entry registry as though all printings can appear together. [Pool filtering](../../Assets/Scripts/Shards/Engine/ShardsEngine.cs#L168) and [Duel replacement filtering](../../Assets/Scripts/Shards/Engine/ShardsEngine.cs#L323) define the actual pool.

| Active category | Definitions | Physical copies available under all-DLC rules |
|---|---:|---:|
| Market allies, mercenaries, champions | 90 | 175 |
| Ingeminex | 5 | 5 initially |
| Destinies | 30 | 30; six face up, 24 undealt |
| Relics | 15 | Three per hero; only the two drafted heroes' six relics are instantiated |
| Starters | 4 | Ten per player, twenty for a duel |

The 180-card initial market pool is distributed as Homodeus 40, Order 39, Undergrowth 39, Wraethe 42, Aion 15, and Ingeminex 5. These are not equal faction priors. Draft-complete 1v1 setup contains 236 card instances: 180 market/monster cards, 30 destinies, 20 starters, and six set-aside relics. Doom Gate can create another 25 Ingeminex once, so a fixed buffer sized only for setup is insufficient. [Doom Gate](../../Assets/Scripts/Shards/Content/ShardsDuelSet.cs#L864) and [monster creation](../../Assets/Scripts/Shards/Engine/ShardsEngine.cs#L1596) are the relevant paths.

The designer displays **199 faces**, which means 189 card definitions plus five character faces and five ability faces. It does not mean 199 playable card definitions. The September session's `baselineStamp.cardCount = 198` is a historical baseline marker, not the current pool size.

### September 18 balance changes are already represented in the current game

The open [design session](../CardDesigner/soi-design-session-2026-09-18.json) contains twelve modified entries and no added or removed entries. Its readme correctly says the JSON itself does not mutate the game; the current C# separately implements these changes. In particular, Crown is a Duel-only replacement rather than a change to the original printing.

| Entry | Current Duel behavior |
|---|---|
| Panconscious Crown | Base healing is 5; original non-Duel printing remains 2 |
| Comet | Cost 13 |
| Testudo Vanguard | Defense 4 |
| Praetorian-02 | Shield-doubling activation costs 2 gems |
| Praetorian-03 | Highest tier starts at M20 |
| Unknown God | Defense 6 |
| Multitask Brain | No Dominion mastery bonus |
| Doom Gate | Defense 5; adds 25 Ingeminex once per game |
| Heart of Nothing | 7 power, or 14 at M20 |
| World Piercer | Optionally return up to two mercenaries below M20; all at M20 |
| Volos | Free heal 3; pay 1 for 2 power; pay 2 to draw 1; pay 3 for 1 mastery |
| Rez | Scry 2; discount the next successful reroll this turn by 1 |

Sources: [new relic/card definitions](../../Assets/Scripts/Shards/Content/ShardsDuelSet.cs#L30), [replacement definitions](../../Assets/Scripts/Shards/Content/ShardsDuelSet.cs#L305), [ability metadata](../../Assets/Scripts/Shards/Engine/ShardsEngine.cs#L644), [Volos choices](../../Assets/Scripts/Shards/Engine/ShardsDuelEffects.cs#L54), and [September regression tests](../../Assets/Tests/EngineTests/ShardsContentTests.cs#L102).

## 3. Rules the training environment must preserve

### Setup and public draft

Each seat begins at 50 health, with a 50-health cap. Seat 0 begins at mastery 0 and seat 1 at mastery 1. Mastery caps at 30. Each starts with seven Crystals, one Blaster, one Shard Reactor, and one Infinity Shard; draws five cards; and keeps five in its deck. The market has six slots. Setup uses seat 0 as the first player; it does not randomize the first seat inside the engine. Randomize or balance seat assignments outside it. [Setup](../../Assets/Scripts/Shards/Engine/ShardsEngine.cs#L49), [rules constants](../../Assets/Scripts/Shards/Engine/ShardsTypes.cs#L214).

Duel creates the market and the six-card destiny row, draws both opening hands, and only then drafts heroes in reverse seat order. Seat 1 chooses from five heroes; seat 0 chooses from the remaining four. Each actor may condition its draft on its own opening hand, public market, public destinies, and previously drafted hero. It must not see the opponent's hand. Each drafted hero has three relic choices; one normal free recruitment unlocks at M10 and goes to discard. The six reserved relics are determined by the draft. [Hero draft](../../Assets/Scripts/Shards/Engine/ShardsEngine.cs#L250).

Initial market filling suppresses Ingeminex and reshuffles any encountered monsters back into the center deck. Therefore initial boards are always monster-free. This is a documented local design decision in [setup](../../Assets/Scripts/Shards/Engine/ShardsEngine.cs#L128), not a random sample from the unconstrained full pool.

### Ordinary actions and action order

During the turn, the player can interleave hand plays, champion/destiny exhausts, purchases, fast-plays, Focus, hero abilities, rerolls, destiny acquisition, relic recruitment, and Ingeminex attacks. No mandatory purchase phase separates them. Acquired cards normally go to discard; effects can redirect them to hand, deck top, or directly into the champion zone. Every market removal refills immediately, so each buy or reroll can change the next decision. The [action classes](../../Assets/Scripts/Shards/Engine/ShardsActions.cs) and [legal-action generator](../../Assets/Scripts/Shards/Engine/ShardsEngine.cs#L1790) define this interface.

Focus spends one gem, exhausts the character, and grants one mastery once per turn. Hero abilities are separate and unlock at M5:

| Hero | Additional ability | Strategic resource conflict |
|---|---|---|
| Decima | First bought card each turn costs one less | Choosing which buy receives the discount; fast-play purchases also consume it |
| Tetra | Pay two gems to draw two | Buy now versus draw for more resources/thresholds |
| Volos | Four modes listed above | Healing, immediate damage, cycling, or permanent mastery |
| Ko Syn Wu | Pay three health to optionally banish up to one hand/discard card | Deck thinning versus race survival; activation requires health greater than three |
| Rez | Scry top two center cards; next successful reroll discounted by one | Market denial, known refills, future draws from the market deck |

The exact legality and effects live in [hero ability methods](../../Assets/Scripts/Shards/Engine/ShardsEngine.cs#L644). Rerolls cost `max(0, 1 + prior rerolls this turn - discount)`, put the chosen market card on the center deck bottom, and refill its slot. The discount is consumed only by success and expires at cleanup. Comet cannot be rerolled. [Reroll cost and implementation](../../Assets/Scripts/Shards/Engine/ShardsEngine.cs#L840).

Mastery bonuses are evaluated when the corresponding effect node resolves. This cannot be reduced to a generic “mastery at turn start” feature. Fungal Hermit's sequential mastery gain precedes its threshold check. In contrast, Praetorian-03 selects a `BestByMastery` tier before granting that tier's mastery; the test explicitly pins M19 to the two-mastery/two-draw tier. See [sequential example](../../Assets/Scripts/Shards/Content/ShardsBaseSet.cs#L269), [tier selection](../../Assets/Scripts/Shards/Engine/ShardsEffects.cs#L196), and [Praetorian test](../../Assets/Tests/EngineTests/ShardsContentTests.cs#L130).

### Factions and deck construction

Unify checks another matching faction card played this turn or automatically reveals a matching card from hand. Champions count; a champion merely persisting from a previous turn does not. This is an explicit local deviation from the printed Ally requirement, which is still visible in the [Saga rulebook](https://cdn.shopify.com/s/files/1/0691/8805/9412/files/SOI_Saga-Rules-Web.pdf?v=1735833106), p. 4. [Current Unify](../../Assets/Scripts/Shards/Engine/ShardsEffects.cs#L252).

Duel Dominion requires at least three other faction cards covering at least three distinct factions, counting appropriate plays and chosen hand reveals. Prism covers all five factions but remains one physical card for the three-card requirement. Reveals do not become plays. Allegiance instead counts cards across personal deck, hand, discard, play zone, and champions, including temporary fast-plays as confirmed by the user. Echo, Inspire, faction counts, even/odd printed costs, played-card counts, and champion counts are separate predicates. [Dominion](../../Assets/Scripts/Shards/Engine/ShardsEffects.cs#L307), [Allegiance](../../Assets/Scripts/Shards/Engine/ShardsDuelEffects.cs#L104).

These distinctions are essential for balance measurement. A “number of green cards” feature alone cannot distinguish a large but slow deck from a small deck with the same active hand, or identify whether the required cards were actually played this turn.

### End-turn combat, shields, and cleanup

The engine rejects mid-turn power attacks on enemy champions. Champion kills are chosen during the end-turn power assignment; destroy effects remain legal earlier. Ingeminex remain legal mid-turn power targets. This differs from the published play-phase champion attack structure; for example, the [Saga rules](https://cdn.shopify.com/s/files/1/0691/8805/9412/files/SOI_Saga-Rules-Web.pdf?v=1735833106), p. 7, include champion attacks in the play phase. The local change removes many interleavings and is part of this target, not an optimization the trainer should silently reverse. [Legal actions](../../Assets/Scripts/Shards/Engine/ShardsEngine.cs#L1790).

With one enemy player, there is still a nontrivial choice between face damage and destroying persistent champions. Taunt and targeting immunities constrain the assignment. The engine's `soi.split` answer uses one target option ID per power point and allows repeats; a policy must use a more compact internal allocation representation and translate it at submission. Full allocation is required when the face is directly assignable; if taunt protects every face, the request permits wasting power. If there is only one face target and no champion targets, the split is automatic. [End-turn split](../../Assets/Scripts/Shards/Engine/ShardsEngine.cs#L1049).

Testudo makes shields reduce each assigned champion hit, and taunt survival can nullify the damage behind it. Overassignment can be necessary to kill through shields. Champion defense auras and targeting vetoes also matter. Simple “exact printed defense per kill, remainder to face” candidates are incomplete in this pack. [Deferred champion damage](../../Assets/Scripts/Shards/Engine/ShardsEngine.cs#L1141), [Testudo/taunt resolution](../../Assets/Scripts/Shards/Engine/ShardsEngine.cs#L1226).

Defenders reveal shields from hand and keep those cards. Praetorian-02 protects from play; Duel Datic Robes can supply a discard-pile shield at M20; shield doubling lasts through the opponent's turn; shield bypass nullifies prevention. More shielding has no card-consumption cost, but revealing redundant cards gives information. Selecting enough shields with minimal additional information disclosure is a possible cheap defensive controller; indiscriminate full revelation is an approximation. [Shield handling](../../Assets/Scripts/Shards/Engine/ShardsEngine.cs#L1280).

At cleanup, fast-played and warped cards normally return to the center deck bottom; owned played cards and unplayed hand cards go to discard; champions and destinies ready; a new hand is drawn, potentially reshuffling mid-draw; per-turn flags clear. Ingeminex due to attack then resolve against this fresh hand before the next turn. A retained fast-play becomes owned, so do not treat every played market card as either permanently acquired or permanently temporary. [Cleanup](../../Assets/Scripts/Shards/Engine/ShardsEngine.cs#L1469), [keep decisions](../../Assets/Scripts/Shards/Engine/ShardsEngine.cs#L1437), [monster attack queue](../../Assets/Scripts/Shards/Engine/ShardsEngine.cs#L1568).

### Alternative win and loss routes

Mastery 30 is not itself a win: Infinity Shard must subsequently be played. Comet destroys an opponent on play. Ordinary damage, direct unshieldable health loss, and self-inflicted health loss also end games. Simultaneous lethal health loss can draw. The code uses `WinnerIndex = -1` for a draw, so a reward should not automatically map every non-win to a loss. The [Into the Horizon FAQ](https://cdn.shopify.com/s/files/1/0691/8805/9412/files/SOI_004_Rules_Final-into-the-horizon.pdf?v=1670526648), p. 2, explicitly distinguishes health loss from damage and permits simultaneous-death ties.

Healing and mastery loss mean a naive health or mastery shaping reward is exploitable as a proxy. For example, Talons grants power from intended healing even at the health cap, while Nectar also grants overflow power; playing the enabling effects before a heal changes the result. [Health conversion](../../Assets/Scripts/Shards/Engine/ShardsEngine.cs#L1894). Preserve exact terminal outcomes and inspect training-only truncations separately from genuine draws.

## 4. What each actor may know

The game is deterministic given complete state and RNG state, but an ordinary player does not know the complete state. Do not confuse deterministic simulation with perfect information.

| Information | Status for a fair actor | Recommended representation |
|---|---|---|
| Own hand | Known | Card identities/counts; dynamic flags where relevant |
| Own total collection and discard | Known | Separate count vectors for hand, discard, played, champions, and unordered remaining deck |
| Own deck order | Normally hidden | Only explicitly known top/bottom positions from permitted effects |
| Enemy hand | Hidden except revealed identities/predicates | Count, revealed-card memory, optional belief summary |
| Enemy deck/discard composition | Public acquisitions and public zones permit substantial reconstruction | Public event ledger; remaining unseen multiset, never actual hidden ordering |
| Market row, destiny row, active Ingeminex, banished cards | Public | Entity lists/count vectors and relevant pending flags |
| Center deck order / undealt destiny identities | Hidden | Public initial counts minus known cards; own permitted Scry/reorder knowledge |
| Hero/relic availability | Public rules and draft determine it | Hero ID, chosen/available relic IDs, once-per-game counters |
| Current decision options | Available only to the decision owner | Context ID, enabled options, min/max/order semantics, semantic numeric data |
| RNG seed/state, full `ShardsState` | Simulator internals | Never actor inputs |

[Snapshot construction](../../Assets/Scripts/Shards/Engine/ShardsSnapshot.cs#L108) exposes only the viewer's hand and full unordered collection. `FullDeck` is sorted by definition ID and instance ID, not draw order. Draw events redact hidden identities for other seats, while reveal events remain public. [Event visibility](../../Assets/Scripts/Shards/Engine/ShardsEvents.cs#L33). Preserve legitimate public memory if using a recurrent or belief model; exact hidden state is unnecessary for the first policy.

Scry and Index of Futures provide private knowledge of the center top and choices affecting future refills. Their decisions expose top-down order or let the chooser set it. Grim Tutor deliberately sorts its deck options by definition/instance rather than draw order and reshuffles afterward. Treat those three mechanisms differently. [Scry and reorder](../../Assets/Scripts/Shards/Engine/ShardsDuelEffects.cs#L149), [tutor](../../Assets/Scripts/Shards/Content/ShardsDuelSet.cs#L827).

`ShardsEngineAdapter.PendingInput` contains the full global pending input. It is not player-filtered by itself. A headless environment must pass its request only to `PendingInput.PlayerIndex`; otherwise, shield, Scry, tutor, and private deck-reveal options reveal hidden cards. The regular snapshot only provides pending kind and player, not enough to choose an answer. [Adapter](../../Assets/Scripts/Shards/Engine/ShardsEngineAdapter.cs#L25).

### Snapshot omissions that matter

The snapshot is tailored to presentation. It omits, among other things:

- Per-card `FastPlayed` and `BanishAtCleanup` state.
- `PlayedThisTurn` and faction/ally play counts; champions already in play are not equivalent to champions played now.
- Health conversion, doubled healing, overflow conversion, shield bypass, shield doubling, and recruit-redirection counters.
- Extra-turn and Doom Gate once-per-game consumption flags.
- Which visible Ingeminex are still due to attack this turn.
- The full resolving-effect source, choice semantics, and deferred Testudo champion-hit amounts.

These are visible effects or own/public bookkeeping, not privileged deck order. A dedicated numeric observation should expose the authorized versions directly. Otherwise two strategically different game situations collapse to the same observation for accidental reasons. See [player/state fields](../../Assets/Scripts/Shards/Engine/ShardsState.cs#L43), [card flags](../../Assets/Scripts/Shards/Engine/ShardsTypes.cs#L64), and [snapshot fields](../../Assets/Scripts/Shards/Engine/ShardsSnapshot.cs#L7).

## 5. Resolved audit findings and finite audit limits

### A. Opponent condition glows must not reveal private-hand predicates

In the original engine, `BuildGlowHints` iterated every player's ready champions/destinies and evaluated their conditional effects against that owner's full state. `Dominion.ConditionMet` and `Unify.ConditionMet` inspect the owner's hand. Thus the viewer's public `ConditionGlowIds` could change when only the opponent's hidden hand/deck allocation changed.

The original executable audit placed an opponent Aegis Archivist in play, then swapped three Crystals in that opponent's hand for three faction cards from its deck. The public counts, total collection, and all serialized snapshot fields except `ConditionGlowIds` were identical; the opposing viewer's Archivist glow changed from false to true. The [original observation audit JSON](observation-audit-2026-09-25.json) preserves this pre-fix result; [ObservationAudit.cs](EngineProbe/ObservationAudit.cs) now serves as a regression fixture for the decided behavior.

The user confirmed that this is a bug and that no such information should leak. The corrected snapshot evaluates conditional hints only for the viewer's own cards and market choices; opponent champions and destinies do not receive these hints. Owner hints remain available. The training encoder should calculate actor-side predicates only from authorized inputs and retain differential hidden-state tests; receiving a host-generated display hint does not make it authorized information. This audit is not evidence that a learned agent previously exploited the leak. [Snapshot glow loop](../../Assets/Scripts/Shards/Engine/ShardsSnapshot.cs), [privacy regression tests](../../Assets/Tests/EngineTests/ShardsSeatSafetyTests.cs), [corrected audit results](observation-audit-fixed-2026-09-25.json).

### B. Allegiance intentionally includes temporary fast-plays

`AllegianceEffect.OwnedCount` counts all cards in the play zone without checking `FastPlayed`. By contrast, snapshot `FullDeck` excludes fast-played cards because they return to the center deck. The same fresh executable audit confirmed that a temporary Wraethe fast-play gives `OwnedCount(Wraethe) = 1` while it is absent from `FullDeck`.

The user confirmed this is normal behavior, not a bug. Preserve temporary-card inclusion in Allegiance and exclusion from the permanent owned-deck snapshot. An Allegiance feature must combine deck composition with eligible temporary cards in play; computing it solely from `FullDeck` would mislabel the intended rule. [Allegiance implementation](../../Assets/Scripts/Shards/Engine/ShardsDuelEffects.cs), [FullDeck exclusion](../../Assets/Scripts/Shards/Engine/ShardsSnapshot.cs).

### C. Comet must be acquired through a normal purchase

Comet's text says it must be bought with gems, and its hooks prohibit fast-play and reroll. In the original engine, Shard Defiant drew any next non-monster center card and offered Keep or Banish; Keep called unrestricted `RecruitLoose`, producing an owned acquisition with `CostPaid = 0`. That path had no Comet-specific guard. The destiny's two-gem exhaust cost was paid, but that was not Comet's purchase price.

The original executable audit confirmed that activating Shard Defiant with two gems and keeping a top-deck Comet put the cost-13 Comet in the player's discard and left zero gems. The original audit used deliberately constructed states to isolate these behaviors; it established the paths, not how often a natural game reached them. The [original results](observation-audit-2026-09-25.json) remain historical evidence; [the fixture](EngineProbe/ObservationAudit.cs) is updated to check the intended restriction.

The user confirmed that Comet can only be obtained through a normal purchase. A paid destiny activation is not a purchase of Comet. Free recruitment from either the center deck or market row now rejects it, and free-recruit effect choices exclude it. Shard Defiant still reveals Comet, but Keep is disabled and Banish is the legal default. Normal purchases still follow effective-cost rules, including applicable discounts. Playing or retrieving a Comet already owned is unaffected. This settles the former design question; balance experiments must use the corrected restriction. [Comet definition](../../Assets/Scripts/Shards/Content/ShardsDuelSet.cs), [Shard Defiant](../../Assets/Scripts/Shards/Content/ShardsHorizonSet.cs), [recruitment](../../Assets/Scripts/Shards/Engine/ShardsEngine.cs), [corrected audit results](observation-audit-fixed-2026-09-25.json).

### D. Local prose is not a machine specification

Concrete disagreements include:

- `rules-notes.md` still describes mid-turn champion power attacks, while the engine and local recorded decision disallow them.
- Its early sections say mastery never decreases; the current code has mastery-loss effects and Ingeminex Torment/Corruption.
- Its Relics section says Talons produces mastery; its later correction and the implementation say power.
- Its bottom-order note says the player chooses an order; cleanup currently inserts returned fast-plays in play-zone iteration order with no order choice.
- The card skill retains an older Volos four-health table below its newer four-mode entry; C# and the September session define the current modes.
- The `ShardsDuelSet` class summary still mentions a two-gem reroll; the actual escalating formula starts at one, potentially zero with Rez.

These are documentation problems, not an instruction to undo the current behavior. Relevant files: [rules notes](../ShardsData/rules-notes.md), [current cleanup](../../Assets/Scripts/Shards/Engine/ShardsEngine.cs#L1469), [current mastery loss](../../Assets/Scripts/Shards/Engine/ShardsEngine.cs#L1885), and [Duel class summary](../../Assets/Scripts/Shards/Content/ShardsDuelSet.cs#L7).

### E. Other implemented conventions to pin, not speculative bugs

Malice resolves highest-cost champion ties deterministically by lowest instance ID rather than prompting the owner. Very large power (`> 1000`) bypasses normal combat/shields and damages every opponent directly; this is used as the implementation's infinite-power convention. Neither should be reinterpreted in a replacement simulator without explicit equivalence tests. [Malice tie-break](../../Assets/Scripts/Shards/Engine/ShardsEffects.cs#L926), [large-power shortcut](../../Assets/Scripts/Shards/Engine/ShardsEngine.cs#L1053).

This audit did not independently verify every effect against card photography, prove game termination for arbitrary policies, or establish the strongest strategy. Rules tests protect known scenarios; passing them is useful evidence, not proof of a perfect simulator. The current original-expansion component totals agree with the checked publisher documents: [Relics](https://cdn.shopify.com/s/files/1/0691/8805/9412/files/SOI_ROF_Rules_v1-relics-of-the-future.pdf?v=1670526646), p. 1, has 24 market cards and eight relics; [Shadow](https://cdn.shopify.com/s/files/1/0691/8805/9412/files/SOI_003_Rules_Sheetv5_1_-shadow-of-salvation.pdf?v=1670526650), p. 1, adds twelve market cards, Rez's two relics, and replacement Cloud Oracles; [Horizon](https://cdn.shopify.com/s/files/1/0691/8805/9412/files/SOI_004_Rules_Final-into-the-horizon.pdf?v=1670526648), p. 1, has thirty center cards and thirty destinies, with a six-card shared destiny row. The original base PDF link failed through the browser tool; base specifics here are grounded in current code/tests rather than a claim to having read that PDF.

## 6. Practical action and observation reductions for a 12-hour experiment

Keep the exact rules environment. Reduce the policy representation and redundant decision count before changing mechanics.

| Reduction | Assessment | What must remain |
|---|---|---|
| Replace individual interchangeable cards with counts | Strong initial candidate | Zone, card identity, played/temporary status, exhausted state, relevant instance distinctions |
| Feed-forward policy without complete public history | Reasonable baseline approximation | Own hand/deck composition, public current state, turn flags; measure loss from forgotten Scry/reveals later |
| Ignore exact opponent hand belief | Reasonable first-stage approximation | Hand count, visible board and shields, public acquisition/deck summaries |
| Skip neural calls for forced single outcomes | Usually safe | Preserve side effects and distinguish optional pass from mandatory selection |
| Compact damage allocation candidates | Useful, but validate coverage | Face pressure, taunt, shield uncertainty, Testudo overassignment, dynamic defense, death-trigger consequences |
| Automatically reveal only enough shields | Potential low-cost controller | Champion hits under Testudo, shield doubling/bypass, limited disclosure and exact prevention |
| Sort every hand into a fixed play order | Too restrictive for balance claims | Banish-before-play, draw/reshuffle timing, heal conversion, mastery thresholds, copy targets, faction timing |
| Always take a destiny/relic immediately | Changes strategy | Waiting for a desired option, denial, hand/deck timing, reward exceptions |
| Remove rerolls/Scry | Strongly biases results | Rez, market manipulation, monster exposure, Comet access |
| Fixed purchase ranking independent of deck | Inadequate as the final policy | Synergy, game horizon, opponent race, current market, purchase/fast-play distinction |
| Access both hidden hands/deck orders | Different game | Never use as the acting policy for human-facing balance conclusions |

Examples showing why a full “autoplay turn” is unsafe:

- Playing a starter before a hand-only banish can remove it from the valid banish zone.
- Drawing before a purchase can miss the chance to put that purchase into an imminent reshuffle.
- Playing Talons or Nectar after healing misses their power conversion.
- Exhausting a Dominion/faction champion before enabling its condition can waste its exhaust.
- Spending gems on a purchase can prevent a Focus that would cross M5, M10, M15, or M20 before another card resolves.
- Waiting to use a free hero ability may be correct when its ordering affects what can be bought, drawn, banished, or revealed.

Do not run a language model over rules text on every move. The current content is finite and code-defined. Card embeddings plus numeric resource/threshold/faction features, legal candidate features, and a shared small network can learn changing card values without requiring a long text sequence. If some actions are delegated to deterministic controllers, record their versions and name the strategy space restriction when presenting balance results.

## 7. Balance experiments implied by these rules

Measure both natural-draft play and controlled hero matchups. Natural drafts estimate which heroes are attractive on available boards; controlled distinct-hero pairings estimate matchup strength without selection confounding. Five heroes give twenty ordered distinct-hero matchups, and the starter-mastery/draft asymmetry makes seat reporting mandatory.

For cards, record opportunities as well as choices: whether the card appeared, whether it was affordable, whether a free/fast-play path existed, whether a better competing action was available, acquisition timing, first-use timing, synergy state, and eventual outcome. A rarely bought expensive card is not automatically weak; it may rarely be reachable, or be a specialist win route. A high win rate after acquiring a late-game card can be a consequence of already winning.

Track special mechanisms separately: Comet acquisition path; relic/destiny choice and activation; mastery-threshold crossings; champion lifetime and shield protection; market rerolls and Scry follow-up; Ingeminex exposure, defeat, rewards, and Doom Gate immunity; banish targets and deck size; direct-health-loss kills versus combat kills; and actual draws versus artificial episode truncations.

Before making a card balance claim, retain the exact rules/content fingerprint, policy checkpoint and opponent population, information abstraction, deterministic-controller version, hero/seat/seed cohort, and sampling uncertainty. These identify what game the measured “meta” belongs to. The strongest strategy in a restricted controller family is valuable evidence, but it is not proof that an unchosen card is intrinsically bad.
