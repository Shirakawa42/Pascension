# Frozen V9: public information, resources, relics and effect representation

The current AI receives its own resources accurately and the normal Mastery-10 relic rule executes correctly. However, the observation is still lossy: this audit demonstrates five same-rules state aliases plus an effect-only rebalance alias. Identical inputs cannot be distinguished by this feed-forward policy, regardless of how many games it trains on. This is separate from whether the learner uses distinguishable information well.

No frozen training source, binary, checkpoint or live process was modified. All probes used an isolated process referencing host SHA-256 `bd67160aaa6849b2bc89a3ffe658ab1f09f43fd43d9a491405695a445961ef4c`, with CPU-only execution. The C# probe records 43 checks; sixteen check resource scalars. [Raw findings](v9-whole-information-probe-2026-09-27.json), [probe source](v9-whole-information-probe/Program.cs), [complete paired inputs](v9-whole-information-fixtures-2026-09-27.json)

## Own resources and the relic threshold

The actor receives health at observation 16 (`health/50`), mastery at 17 (`mastery/30`), gems at 18 (`gems/20`) and power at 19 (`power/100`). All sixteen comparisons against actual engine fields passed, including 100 gems and 500 power. These are normalized values, not binary affordability indicators. [Encoder](../experiments/HostV9/Encoder.cs#L292)

An accepted action sequence also passed: start at Mastery 9, one gem, seven power and 41 health; Focus spends exactly one gem and raises mastery to 10; all four resource inputs then match the updated engine fields; three hero relic recruitment actions appear immediately; recruiting one is free, puts it in the discard pile and removes normal relic recruitment actions thereafter. The one-time flag and normal threshold are correct. Additional-relic effects such as Corruption legitimately bypass the ordinary restriction. [Normal recruitment](../../../Assets/Scripts/Shards/Engine/ShardsEngine.cs#L818), [Corruption reward](../../../Assets/Scripts/Shards/Content/ShardsHorizonSet.cs#L337)

The inherited policy transform applies signed `log1p` after clamping **normalized** numeric inputs to ±1000. It does not cap power at 200: 199, 200, 201, 500 and 9,999 all remain distinct in the actual CPU transform. Power starts saturating at 100,000 because its encoder scale is 100. Gems would saturate at 20,000, health at 50,000 and mastery at 30,000; ordinary health/mastery game caps are much lower. Infinity Shard's actual Mastery-30 output is 9,999 power, so one use remains below the clipping threshold. This is explicit numerical compression, not an erroneous 200-power limit. [Transform and configuration](../learning_model.py#L32), [transform result](v9-resource-transform-audit-2026-09-27.json), [Infinity Shard](../../../Assets/Scripts/Shards/Content/ShardsBaseSet.cs#L45)

The initial hero-to-relic mapping is deterministic and public:

| Hero | Three obtainable relics |
|---|---|
| Decima | Praetorian-01, Praetorian-02 Duel, Praetorian-03 |
| Tetra | Datic Robes Duel, Multitask Brain, Terminal Crescents Duel |
| Volos | Entropic Talons, Panconscious Crown Duel, Unknown God |
| Ko Syn Wu | Doom Gate, Heart of Nothing Duel, World Piercer Duel |
| Rez | Slipstream Shard Duel, Star Seeker, Warpquartz Duel |

The engine exposes that mapping through `RelicIdsFor`, and the hero-draft UI uses it. The policy receives hero identity and mastery, so it **can learn** the fixed mapping and the value of reaching 10. It does not receive the future relic identities/effects as an explicit pre-threshold menu or execute that lookup itself. Its own remaining set-aside relics are omitted until they appear as legal candidates. In a constructed same-hero Mastery-9 fixture, three remaining relics versus two produce identical complete inputs; at Mastery 10 they correctly produce three versus two candidates. This does not mean the initial pool is random—it demonstrates a missing representation of an own-known pool that can change during play. [Mapping](../../../Assets/Scripts/Shards/Engine/ShardsEngine.cs#L224), [legal recruitment generation](../../../Assets/Scripts/Shards/Engine/ShardsEngine.cs#L1851)

## Confirmed current-rules information aliases

Each pair has **zero differences across all 4,928 observation, candidate and mask floats**. The fixtures begin from constructed positions; accepted engine actions are used where stated. They do not establish the frequency of these positions in current training or prove complete initial-seed reachability.

| Missing distinction | Reproduction and consequence | Information class |
|---|---|---|
| Banished card identities | Reverse two initial center-top cards, then legally exhaust Shard Defiant and choose Banish. The removed card is Shard Abstractor versus Fungal Hermit; inputs are identical after resolution. Remaining center composition differs. | Current public pile; only total banished count is encoded. |
| Which monsters have pending attacks | Identical Brutality/Torment board and one pending attack, but change which monster is pending. Accepted End Turn loses five health versus two mastery. | Public reveal history/current pending status; only pending count is encoded. |
| Effect continuation progress | With General Decurion duplication already armed, accepted Reactor play/choice sequences reach equal gems and identical source/menu, once at its first resolution and once at its second. Selecting two gems leaves another Reactor choice in the first case and returns to priority in the second. | Publicly reconstructible continuation; current source is encoded, remaining queued effects are not. |
| Per-card temporary/banish association | Two physical Reactors are in play, one permanent and one temporary. Swap which has `BanishAtCleanup`. Totals and inputs are unchanged, but accepted cleanup retains zero versus one permanent Reactor. | Per-card public consequence of played mode; aggregate temporary/banish counts lose the association. |
| Remaining own set-aside relics | Same hero and Mastery 9, three remaining relics versus two. Inputs coincide until recruitment becomes legal. | Own-known current information; static hero identity alone cannot describe a changed pool. |

The count-only fields are explicit in the encoder. Per-card `FastPlayed` and `BanishAtCleanup` appear on candidate/source features, but not on every public entity; their own aggregate counts cannot identify the marked card. [Counts](../experiments/HostV9/Encoder.cs#L342), [candidate flags](../experiments/HostV9/Encoder.cs#L505), [engine continuation queue](../../../Assets/Scripts/Shards/Engine/ShardsEngine.cs#L24)

The probe has a red-capable `--require-distinct` mode. It currently fails on the first demonstrated public-banished alias, as expected; the normal audit mode verifies that the frozen aliases and their differing consequences remain reproducible. [Recorded red result](v9-whole-information-red-2026-09-27.log)

## Confirmed Reactor cleanup anomaly

A separate sequence uses actual card effects: at Mastery 20, exhaust Deadly Recruits, choose a cost-three Reactor from the row, decline to keep it, choose the physical Reactor's three-gem mode, then end the turn. The engine returns this card to the center deck with `BanishAtCleanup=true` still attached. No fixture code manually sets either temporary or banish flag in this sequence.

The cause is concrete: cleanup tests `FastPlayed` first, returns the card and clears that flag; its `else if (BanishAtCleanup)` branch therefore never executes. The only other source assignment sets the flag during Reactor's mode. A later normal acquisition does not reset it. This is a confirmed persistent-state anomaly; the intended precedence between temporary return and Reactor's explicit end-turn banish should be codified before a rules fix is adopted. It is not sufficient to fix only the policy observation. [Cleanup](../../../Assets/Scripts/Shards/Engine/ShardsEngine.cs#L1477), [Reactor mode](../../../Assets/Scripts/Shards/Content/ShardsDuelSet.cs#L520), [normal acquisition](../../../Assets/Scripts/Shards/Engine/ShardsEngine.cs#L519)

The fixture initially attempted Deadly Recruits below Mastery 20; the engine correctly rejected the cost-three option because the lower tier allows at most cost two. The final successful reproduction uses Mastery 20 and the actual cost-four tier. That earlier rejection was a test setup correction, not a trainer failure.

## Card encoding and rebalance transfer

V9 is primarily a card-identity policy. It has a learned 193-entry candidate embedding table (including the null identity), a state trunk, numeric candidate attributes, a mean of legal candidate embeddings, supplementary state projections and typed ordinal heads. It does **not** receive a general effect program, effect graph, rules text or explicit gains/draws/damage descriptors for each card. Numeric candidate fields include printed cost/type/faction, authorized defense/shield and effective acquisition cost; selected current readiness conditions are authored separately. [Policy](../experiments/choice_policy_v9.py#L35), [candidate encoder](../experiments/HostV9/Encoder.cs#L483), [base embedding/scorer](../model.py#L40)

The active all-DLC pool contains **144 definitions**: 82 have play effects, 54 exhaust effects, five monster rewards and five monster attacks. Twenty-two cards use at least one of the audited static hooks. Examples whose general semantics are not explicit feature programs include exhaust gem costs (three definitions), targeting vetoes (three), dynamic shield (two), Testudo's champion shield protection (one), Doom Gate immunity (one) and Unknown God's doubled exhausts (one). Some consequences are represented indirectly: legality masks enforce affordability/targeting, effective defense/shield fields expose current values, public card identity lets the learner memorize the fixed rule. Missing explicit semantics is not equivalent to missing all consequences.

A structural traversal found 145 `Gain` nodes, 25 `AtMastery` nodes, 29 `If` nodes, 27 `Custom` nodes and ten `Do` nodes; **35 active cards** contain a `Custom` or `Do` callback. This is a static reachable-node count per card, not exhaustive expansion of runtime-created effects. It shows why a general effect encoder would need explicit schemas for bespoke callbacks, rather than assuming all content is already a uniform declarative graph. [Definition/effect schema](../../../Assets/Scripts/Shards/Engine/ShardsTypes.cs#L82), [raw inventory](v9-whole-information-probe-2026-09-27.json)

The rebalance fixture changes only Crystal's play effect from one gem to four inside the isolated process, retaining the same ID and printed attributes. **Every input remains identical**, while accepted play gains four gems. The registry is restored in a `finally` block; the frozen DLL and running game are untouched. A model cannot adapt to that change through input awareness alone. Retraining can relearn the ID's value, and changes to directly encoded cost/defense/shield are more visible, but one cannot assume immediate transfer across arbitrary effect rebalances. Production catalog/rule identity guards should continue preventing accidental checkpoint reuse across silently changed rules.

## Planning depth and next-state reasoning

The deployed forward pass scores current legal candidates against a shared current-state representation. It has no recurrent state, explicit transition model, engine rollout, minimax or MCTS in action selection. Its value head predicts a value for the current state; it is not called on separately simulated hypothetical successor states by this policy. Therefore explicit search depth is **zero simulator plies**. Training can still teach behavior that anticipates later outcomes, including Focus at Mastery 9, through learned policy/value associations. That is implicit prediction from experience, not verified counterfactual planning. Exact input aliases necessarily share the same deterministic logits/value under this architecture. [Forward computation](../experiments/choice_policy_v9.py#L35), [action sampler](../model.py#L78)

## Prioritized follow-up without interrupting this run

These findings justify a tested future observation/rules version, not an unvalidated mid-run change:

1. Fix and regression-test the Reactor flag lifetime/cleanup rule; inventory whether frozen evaluation games encounter it.
2. Add public pending-monster identity/status and a small semantic continuation descriptor. These distinguish immediate outcomes currently aliased.
3. Preserve per-entity temporary/banish association. Add a public banished-definition histogram and explicit remaining hero-relic identities/availability before eligibility. These are finite additions, not a need to expose hidden hands or deck order.
4. For rebalance studies, define versioned effect descriptors and retraining/evaluation gates. Start with declarative gains, thresholds, costs and named hooks; cover bespoke callbacks explicitly and validate descriptors against the engine.

A first extension can stay modest: five pending-monster type counts plus entity pending bits, a 189-definition banished histogram, three remaining-relic flags/IDs derived from the public hero mapping, per-entity temporary bits and a bounded continuation descriptor. Exact duplicate-monster identity/ordering still needs care; five aggregate type counts alone are not sufficient for every target-selection state.

Revealed opponent-hand memory, four known top cards, 64 individually represented public entities and the last sixteen staged entries remain additional bounded approximations. Their strategic impact is not established by this audit. In particular, public collection composition is correctly represented in V9 and must remain fixed in privacy tests; opponent hand/draw allocation and order remain hidden. This report does not claim that every information leak, engine bug or strategic omission has been exhausted.
