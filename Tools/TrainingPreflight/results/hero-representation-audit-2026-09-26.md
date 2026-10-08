# Rez and Ko Syn Wu: current V5 representation audit

Date: 2026-09-26. Scope: source inspection of the **current** real rules, V5 host, and active feedforward policy, plus an executed isolated CPU fixture against the frozen V5 DLL. No historical AI implementation was used. No trainer, checkpoint, identity, rules, or GPU process was changed. This audit does not estimate win rates or prove that any particular misplay occurred in sampled games.

## Result

**Rez has a demonstrated information limitation that prevents full use of Futureproof. Ko has no equivalent demonstrated banish-target blindness; his ability has concrete opportunities for costly misuse that must be measured.** These findings make current hero results evidence about this policy and representation, not an estimate of optimal hero balance.

V5 inherits `HostV4/Encoder.cs` and the shared current `Host/Adapter.cs`; its manifest explicitly keeps observation schema `shards-observation-v3` and unchanged rules. The active policy uses a feedforward two-layer state trunk and independent candidate scoring, with no recurrent state. [V5 manifest](../experiments/HostV5/manifest.json), [encoder](../experiments/HostV4/Encoder.cs), [model](../model.py), [learning wrapper](../learning_model.py).

## Proven Rez limitation

Futureproof requires Mastery 5, is free and once per turn, reveals the top two center-deck cards, permits moving either/both to the bottom, and discounts the next successful reroll by one gem. It does not reorder retained cards. The Scry decision exposes each card's definition and top-down option ordinal. The encoder supplies both to the candidate scorer, so the AI **can see what it is considering burying**. [Ability metadata/effects, lines 623–711](../../../Assets/Scripts/Shards/Engine/ShardsEngine.cs), [Scry, lines 151–192](../../../Assets/Scripts/Shards/Engine/ShardsDuelEffects.cs), [candidate encoder, lines 251–320](../experiments/HostV4/Encoder.cs).

Once the Scry decision is submitted, `Adapter.Submit` clears `Selected` and `SelectionTrace`. The encoder carries no persistent center-deck knowledge: it observes center-deck size but not the cards just revealed after that decision resolves. There is no actor memory in the network. Consequently two otherwise identical public post-Scry positions with different previously revealed retained top cards yield the same observation and legal-action features. The following buy/reroll distribution cannot depend on which top card was just seen. This is an architectural restriction, not merely insufficient training. [Adapter, lines 101–112](../Host/Adapter.cs), [observation encoding, lines 35–176](../experiments/HostV4/Encoder.cs), [feedforward forward pass](../model.py).

The current reroll **price is visible**: player scalars include `RerollsThisTurn` and exact `RerollCost`. The price is `max(0, 1 + rerolls - nextDiscount)`; successful rerolls consume the discount. The combination of reroll count and price preserves the current one-point discount state. Therefore a missing discount feature is not an explanation supported by this audit. [Scalar encoder, lines 123–125](../experiments/HostV4/Encoder.cs), [reroll implementation, lines 835–858](../../../Assets/Scripts/Shards/Engine/ShardsEngine.cs).

There is a narrower additional limitation during Scry: newly revealed option identities are candidate-only inputs, absent from the state/value trunk. Each candidate is scored against the same state query without pooling the menu. Before any selection, the log odds of burying card A versus finishing cannot change solely because the other revealed card B changes. Softmax probability can change through B's own score, and subsequent selections are visible in `SelectionTrace`; this is **not** total blindness to the menu. [Encoder selection trace](../experiments/HostV4/Encoder.cs), [candidate scoring](../model.py).

A repair should preserve only legitimately revealed per-player knowledge through subsequent decisions, advance it on known refills, and invalidate it on shuffles. It must not read unrevealed center-deck order to replenish the knowledge. Such a repair requires a new versioned observation/runtime and checkpoint migration; it should not be inserted into the frozen live V5 host.

### Executed confirmation against the live-version DLL

An isolated CPU fixture referenced, without rebuilding, V5 DLL SHA-256 `0b00ef87c842c320f368367be73349076902b74fd49355dcfd3824eccdfee797`. It constructed two same-seed, card-conserving states differing only in the order of Shard Abstractor and Fungal Hermit at the center-deck top, with Rez at Mastery 5. Both executed the actual legal Futureproof action and actual keep-all Scry submission through the frozen Adapter.

The Scry menus exposed opposite top-card order and **4 input floats differed during Scry**. Afterwards, **all 4,160 floats** (2,048 observation + 2,048 candidate features + 64 mask) were exactly equal. Both then submitted the same legal free-reroll candidate in slot 0. One received Shard Abstractor; the other received Fungal Hermit. Thus the encoder discarded genuine, decision-relevant information that the actor had just legitimately seen. This is a constructed-position fixture, not a complete game transcript or a measurement of win-rate impact. Execution exited successfully. [Recorded JSON](rez-scry-memory-alias-fixture-2026-09-26.json), [fixture source](../experiments/rez_scry_memory_probe/Program.cs), [reproduction instructions](../experiments/rez_scry_memory_probe/README.md).

## Ko: visible inputs, delayed benefit, identifiable waste

Sacrifice requires Mastery 5 and health greater than 3, costs 3 health, has no gem cost, and is once per turn. It resolves `BanishUpTo(1)`: hand and discard cards are eligible and the selection is optional (`Min=0`). The engine does not require that a target exists before charging the ability. If both zones are empty, it still pays 3 health and the effect immediately exits; choosing Finish with no selection also pays without banishing. This is existing legal behavior, not a proposed rules bug. [Ability availability/payment, lines 683–711](../../../Assets/Scripts/Shards/Engine/ShardsEngine.cs), [BanishUpTo, lines 550–590](../../../Assets/Scripts/Shards/Engine/ShardsEffects.cs).

The policy observes its hand, discard and total permanent collection counts, health, mastery, ability-used flag, and banish candidates' card identity and exact hand/discard zone. Thus it can in principle learn sacrifice timing and target selection. The fixed action prior favors playing cards by +1 logit but gives the ability 0; this is a mild prior the learned scores can overcome, not a prohibition. A possible failure is playing all starter cards before attempting to thin, leaving only valuable discard targets or no targets. That is a **hypothesis requiring behavior counts**. [Encoder](../experiments/HostV4/Encoder.cs), [action-kind mapping, lines 201–216](../Host/Adapter.cs), [learning prior](../learning_model.py).

The learning target is the complete terminal seat outcome, with `gamma=lambda=1`; there is no explicit short-horizon discount making thinning mathematically worthless. However the benefit can arrive several deck cycles after its cost, and all decisions receive terminal outcome minus their state value. Difficult credit assignment remains a plausible learning limitation, not a proven causal explanation of Ko's win rate. [Terminal returns and EpisodeStore.seal](../learning_rollout.py).

Useful behavioral measures are activation opportunities per **turn**, paid activations with zero banishes, activations ending with an empty optional selection, target identities/zones, health lost to Sacrifice, timing relative to playing starters, and resulting deck composition. Raw opportunities per decision are biased by long turns and repeated legal menus. A banished expensive card is not automatically a mistake; health-race context and card synergies matter.

## Relics make one-number hero conclusions particularly weak

Current Duel definitions give Rez three very different plans: Slipstream Shard grants mastery/draw and an M20 once-per-game extra turn; Star Seeker is a champion offering free row fast-play, twice at M20; Warpquartz banishes/copies hand/discard cards and grants 3 gems/power per card banished that turn. These invoke sequencing and future-card knowledge in different proportions. Ko can select Heart of Nothing (damage and a 10-damage extra-draw trigger), World Piercer (retrieve mercenaries, with an M20 mass-return threshold), or Doom Gate (25 monsters shuffled in once, immunity while the champion is in play, and exhaust-to-destroy). [Duel relic definitions, lines 61–75, 310–329, 398–430](../../../Assets/Scripts/Shards/Content/ShardsDuelSet.cs), [Warpquartz, lines 609–650; World Piercer, lines 724–769; Doom Gate, lines 864–900](../../../Assets/Scripts/Shards/Content/ShardsDuelSet.cs).

The encoder carries relevant public threshold state such as mastery, banished-this-turn count, maximum damage to one opponent, bonus draws, extra-turn-used and Doom-Gate-flood-used. Relic candidates carry their actual identities. This audit found no direct equivalent of the persistent Scry omission for basic relic selection. Different relic selections and acquisition survival biases should nevertheless be separated in the root statistical investigation.

## Claims this audit does not support

- It does not prove Rez or Ko are balanced, overpowered, or underpowered under expert play.
- It does not prove the Scry limitation accounts for the measured Rez deficit, or quantify its cost.
- It does not prove Ko is using Sacrifice badly; the paid-empty paths are instrumentation targets.
- It does not show that increasing model size alone would repair missing Scry history.
- It does not endorse changing hero numbers before fixed-policy, assigned-hero evaluations and behavior traces establish competence.
