# Five-hero tactical audit — 27 September 2026

**The current AI does not handle obvious hero tactics reliably.** This audit covers the five playable heroes, as clarified by the user, rather than every board-champion card. It tests 32 scenario families, 16 variants per family with both seats equally represented, and nine continuations per position: one greedy plus eight seeded samples using the actual gameplay distribution. That is 512 constructed positions and 4,608 short policy rollouts per checkpoint; four checkpoints were compared on identical positions and sampling seeds, for 18,432 rollouts.

Only **3/32 families** meet the diagnostic target of at least 95% success in both greedy and sampled play: Rez's Warpquartz starter banish, the already-available mastery-30 Infinity win, and burying an Ingeminex against Doom Gate. Some other tasks have good greedy performance but substantial sampling errors. Most important failures also occur greedily, so changing sampling alone is insufficient.

These are deliberate tactical challenges, not representative natural games or hero win rates. Most objectives are a guaranteed win this turn; one is completing Scry safely. Each teacher line was executed through the actual engine and verified before evaluating the network. Equivalent winning orders are accepted. The network must complete the sequence; merely choosing the correct first action is insufficient. Unfinished sequences fail. The sacrifice-preview test intentionally isolates a decision; its failure frequency is conditional on entering that preview, not a natural-game incidence estimate.

No training, balance change, policy replacement, or statistics-cohort replacement was performed. The existing 50,000-game view is still the same cohort.

## Every scenario

Greedy = successful completed sequences out of 16 positions. Sampled = successful completed sequences out of 128 rollouts. The last column repeats the same sampled test using the frozen checkpoint immediately before Rez adaptation. A dash is never substituted for a failure.

| Hero | Scenario / expected sequence | Current greedy | Current sampled | Before Rez sampled |
|---|---|---:|---:|---:|
| Decima | Use the first-buy discount to fast-play Nil Assassin with one gem. | 0/16 | 6/128 (4.7%) | 8/128 |
| Decima | Do not spend the first-buy discount on a free Skirmisher when discounted Nil Assassin wins. | 0/16 | 9/128 (7.0%) | 4/128 |
| Decima | Focus from 19 to 20 before Praetorian-01: 12 power wins; 8 does not. | 15/16 | 45/128 (35.2%) | 8/128 |
| Decima | Deploy a champion to recover Praetorian-01 from discard and win. | 16/16 | 121/128 (94.5%) | 56/128 |
| Decima | Play Praetorian-03 at mastery 27 before Infinity Shard. | 16/16 | 106/128 (82.8%) | 120/128 |
| Tetra | Spend three gems to draw the two remaining Blasters and win. | 14/16 | 73/128 (57.0%) | 85/128 |
| Tetra | Fast-play Nil Assassin for lethal; drawing two healers consumes the needed gems. | 0/16 | 6/128 (4.7%) | 14/128 |
| Tetra | Focus from mastery 4 to 5, then use the hero draw to find lethal. | 5/16 | 23/128 (18.0%) | 49/128 |
| Tetra | Focus 17 to 18 before Terminal Crescents; its own +2 then unlocks 20 power. | 0/16 | 15/128 (11.7%) | 0/128 |
| Tetra | Deploy Homodeus and Wraethe champions before Multitask Brain for lethal faction scaling. | 16/16 | 95/128 (74.2%) | 44/128 |
| Volos | Choose the one-gem power mode for immediate lethal. | 5/16 | 43/128 (33.6%) | 32/128 |
| Volos | Choose the two-gem draw mode to draw the only remaining card, Nil Assassin, for lethal. | 0/16 | 0/128 (0.0%) | 0/128 |
| Volos | Choose mastery at 29 before Infinity Shard; Focus is already spent. | 0/16 | 2/128 (1.6%) | 1/128 |
| Volos | Play Entropic Talons before the free heal mode to convert three health into lethal power. | 12/16 | 65/128 (50.8%) | 12/128 |
| Volos | Deploy Unknown God at mastery 20 before exhausting Axia, doubling seven power to lethal fourteen. | 0/16 | 12/128 (9.4%) | 1/128 |
| Volos | Play Panconscious Crown at mastery 28 before Infinity Shard. | 0/16 | 0/128 (0.0%) | 38/128 |
| Ko Syn Wu | Do not sacrifice the only Nil Assassin in hand: playing it wins now. | 16/16 | 114/128 (89.1%) | 50/128 |
| Ko Syn Wu | In the sacrifice preview, decline when the only target is a mastery-30 Infinity Shard. | 0/16 | 10/128 (7.8%) | 122/128 |
| Ko Syn Wu | World Piercer should return two Nil Assassins, not Spore Cleric, to assemble ten lethal power. | 0/16 | 0/128 (0.0%) | 0/128 |
| Ko Syn Wu | Play World Piercer at mastery 28 before Infinity Shard. | 0/16 | 0/128 (0.0%) | 46/128 |
| Ko Syn Wu | Doom Gate destroys Torment for four mastery, unlocking Infinity Shard at 30. | 0/16 | 0/128 (0.0%) | 3/128 |
| Rez | Scry away two incompatible champions, then Longshot the Nil Assassin for lethal. | 2/16 | 27/128 (21.1%) | 100/128 |
| Rez | Focus 19 to 20 before Star Seeker for two Nil Assassin warps instead of one. | 15/16 | 50/128 (39.1%) | 0/128 |
| Rez | Warpquartz banishes a starter to produce immediate lethal power. | 16/16 | 128/128 (100.0%) | 36/128 |
| Rez | Take the already available mastery-30 win while Scry is also available. | 16/16 | 128/128 (100.0%) | 124/128 |
| Rez | After Scry keeps a known Nil Assassin on top, reroll a champion for free and fast-play the Assassin. | 0/16 | 28/128 (21.9%) | 12/128 |
| Rez | Complete Scry by burying Ingeminex when the enemy has Doom Gate and neither target can be killed. | 16/16 | 128/128 (100.0%) | 128/128 |
| Decima | Focus from mastery 29 to 30 before playing Infinity Shard. | 0/16 | 7/128 (5.5%) | 2/128 |
| Tetra | Focus from mastery 29 to 30 before playing Infinity Shard. | 0/16 | 1/128 (0.8%) | 1/128 |
| Volos | Focus from mastery 29 to 30 before playing Infinity Shard. | 0/16 | 4/128 (3.1%) | 3/128 |
| Ko Syn Wu | Focus from mastery 29 to 30 before playing Infinity Shard. | 0/16 | 3/128 (2.3%) | 1/128 |
| Rez | Focus from mastery 29 to 30 before playing Infinity Shard. | 0/16 | 7/128 (5.5%) | 8/128 |

## Findings by hero

**Decima:** The AI can recall Praetorian-01 through a champion deployment and generally orders Praetorian-03 before Infinity correctly when choosing the highest-probability action. It fails both discounted Nil Assassin lethal cases greedily: buying the card for later or rerolling is preferred to fast-playing it now. Praetorian-01's mastery-20 threshold is usually understood greedily, but market rerolls and sampling frequently spend the necessary gem or end the turn early.

**Tetra:** Drawing the remaining Blasters sometimes succeeds, and the Multitask Brain faction-order test is correct greedily. She frequently consumes the gems needed for a lethal mercenary on a purchase, reroll or hero draw instead. She can fail to Focus before unlocking her hero power or Terminal Crescents' mastery-20 tier. This is not a hero-cost bug: three gems and the required mastery are correctly enforced and visible.

**Volos:** All four modes are legal when affordable and are used in real games. The existing 50,000-game trace contains 110,671 heal, 14,151 power, 30,436 draw and 13,567 mastery selections. Mode availability/exploration is not the missing component. In the forced draw-for-lethal probe, even after manually opening the menu with two gems intact, the current network assigns the correct draw mode only about **0.083%** probability in the first variant; it prefers two power, which is insufficient. In contrast, when the mastery menu is reached at 29, it selects the correct mastery mode with about **99.989%** probability. The failure there is earlier: it plays Infinity first. It also exhausts Axia before deploying Unknown God and plays Infinity before Panconscious Crown. Entropic Talons before the free heal is improved but still inconsistent.

**Ko Syn Wu:** Playing the lone winning Nil Assassin is usually good, but an isolated sacrifice preview is dangerous: the current greedy policy banishes a mastery-30 Infinity Shard in **16/16** positions. The pre-Rez and first ordinary-self-play checkpoints declined in **16/16**. In World Piercer positions it may sacrifice a needed Nil Assassin or stop without assembling the available win. Doom Gate can grant the required four mastery, but the model plays Infinity before exhausting it. These are actual legal decision errors, not a missing button or unpayable cost.

**Rez:** The previous repair still passes Warpquartz and Doom Gate burial tests. Star Seeker's mastery threshold is often handled greedily but is fragile under sampled play. The new damage-based Scry/Longshot test is a regression: the old checkpoint won 16/16 greedy variants; the current checkpoint wins 2/16. It often buries the required Nil Assassin along with the incompatible champions, or rerolls away the carefully prepared top. The earlier acceptance suite emphasized mastery-based Longshot chains and did not catch this transfer failure. Free reroll into a known Nil Assassin is recognized, but the policy commonly buys it instead of fast-playing for immediate lethal.

**Shared by every hero:** with 29 mastery, one gem, Focus unused and Infinity Shard in hand, the winning sequence is Focus → Infinity → end turn. Every hero fails all 16 greedy variants. Depending on hero/position, the model spends the gem rerolling or plays Infinity too early. All four tested checkpoints already had this problem; it was not introduced solely by Rez adaptation.

## What the checks establish about causes

### 1. Relevant resources and legal choices are present

The harness asserts exact observation values for own health, mastery, gems, power, opponent health and the hero one-hot. All demonstrated actions are present and accepted by the real adapter/engine. It verifies that changing the opponent's private hand/draw partition while preserving their full public collection leaves observations, candidates and masks identical. Reversing the candidate array preserves identity-matched probabilities to within **2.99e-7**. These checks pass in all 512 current-model positions. They exclude missing resources, candidate suppression and this form of information leakage/array-position dependence as explanations for these cases; they are not a proof of universal information correctness.

The two draw-certainty scenarios use all remaining deck cards or a single remaining card, so their winning lines do not rely on hidden draw order. Opponent public collections contain only non-shield starters, making the damage wins provable from permitted information. Relics are selected for their actual heroes, and every fixture uses the current Duel balance rules. Fixtures intentionally simplify holdings and late center supply; results do not estimate how often the mistakes occur in ordinary games.

Public-health counterfactuals change the correct observation slots and affect action probabilities. For example, Decima's first discounted-lethal position changes only slot 16 when own health is lowered to two. The model value changes from about 0.995 to 0.774, but it still prefers a reroll or normal purchase. Seeing a threat is therefore not sufficient for choosing the tactical response.

### 2. The generic effect summary loses rules structure — directly reproduced

The 512-dimensional descriptor is a pooled phase/operation summary, as its own source comments state. `AtMastery` adds gate counts and threshold sums while its inner rewards are added into shared conditional-resource bins. Composite effect order is not represented. Two executable probes demonstrate the consequence:

| Programs compared | Mastery | Generic descriptors | Actual power |
|---|---:|---|---:|
| M10: +2 power, M20: +8 vs M10: +8, M20: +2 | 15 | Exactly identical, all 512 values | 2 vs 8 |
| +2 mastery then M20: +20 power vs M20: +20 power then +2 mastery | 18 | Exactly identical, all 512 values | 20 vs 0 |

These are synthetic pure-resource probes; no real card definition was altered. They establish a representation limitation, not that it alone causes each observed mistake. Learned identity embeddings can memorize current cards, but the generic summary cannot uniquely describe these balance edits. It should not be treated as a complete, automatically balance-proof effect grammar.

### 3. There is no action lookahead

The shipped network evaluates encoded state and candidates, then samples an action. It does not simulate Focus followed by Infinity, a relic followed by an exhaust, or a draw followed by a play. It must approximate all those dependencies through learned weights. Static mastery thresholds and resource quantities are not equivalent to a computed per-action consequence. For example, it can choose Volos's mastery mode correctly once in the menu, while failing to enter that menu before playing Infinity.

### 4. Recent adaptation caused measurable negative transfer

The same repaired engine and encoder were used for every checkpoint. The decline-sacrifice task stayed 16/16 greedy through ordinary Rez self-play, fell to 2/16 after the first supervised block, then 0/16 after the expanded block. The damage-based Longshot task went 16/16 → 16/16 → 9/16 → 2/16. Some trained tasks, including Warpquartz, improved strongly.

This timing is consistent with the shared network over-specializing on the narrow tactical curriculum: correct banishing in Warpquartz contexts does not justify banishing a winning card in a sacrifice preview; burying unsuitable Longshot reveals does not justify burying the useful damage card. The supervised updates modify the shared model and do not include a broad cross-hero retention loss/gate. Because PPO and supervised updates both ran within those blocks, this comparison does **not** isolate the exact fraction attributable to either optimizer objective. It does prove that passing the earlier narrow suite was insufficient to accept the new checkpoint as broadly tactically reliable.

### 5. Terminal credit and value saturation provide weak tactical feedback

The rollout implementation assigns each retained action its player's complete terminal outcome (`gamma=lambda=1`) and computes advantages from that outcome minus the stored value. An inefficient action in a game still eventually won receives the same terminal return as a clean finish. Win-only rewards are not inherently invalid, but this implementation supplies no direct distinction between those lines.

Many of these constructed high-mastery positions have raw value output near +1 even after the immediate win is spoiled. This is evidence of poor discrimination of immediate tactical alternatives in these probes, not a calibrated probability estimate or a proof that the eventual-game value is wrong. Combined with no search and a limited curriculum, it is a plausible learning bottleneck. Increasing random actions alone would not repair the many deterministic highest-probability mistakes.

## What should change before another broad training run

1. Preserve ordered effect structure and bind gates to their effects. Add public, state-dependent consequences for candidate actions, particularly exact current threshold payoffs and what +1/+2 mastery unlocks. Keep hidden draws and opponent hands out of these computations.
2. Add a bounded tactical search for guaranteed same-turn wins over legal public-information transitions. Stop or branch appropriately at genuinely hidden outcomes; never simulate the actual secret deck order as if the player knew it.
3. Train balanced scenarios across all five heroes with positive and negative examples: spend/save the last gem, use/decline sacrifice, keep/bury a revealed card, heal before/after conversion, play a mastery source before Infinity, and deploy Unknown God before exhausts. Split holdouts by interaction/card family as well as random seed. Do not train on this exact gate and then call its success independent validation.
4. Require cross-hero tactical retention and seat-balanced strength evidence before promoting a checkpoint. Aggregate self-play win rate and one hero's targeted tests are insufficient.
5. Retain stochastic exploration for learning, but protect proven tactical wins during play. Simply making all actions greedy would still fail many of these cases.

The present 50,000-game statistics remain observations of this policy. They should not be interpreted as optimal hero balance, particularly for Volos, Ko Syn Wu and Rez.

## Reproduction and artifacts

From the repository root, using the available .NET executable (or `dotnet` on PATH):

```sh
/home/lva/.dotnet/dotnet build Tools/PolicyVerify -c Release
/home/lva/.dotnet/dotnet Tools/PolicyVerify/bin/Release/net8.0/PolicyVerify.dll Assets/Resources/AI/shards-policy.bytes --hero-tactics /tmp/hero-audit.json 16
python3 Tools/TrainingPreflight/hero_tactical_gate.py /tmp/hero-audit.json
```

The last command intentionally exits **2** while the policy fails. A minimal, deterministic red-capable reproduction runs in approximately 0.2 seconds after compilation:

```sh
/home/lva/.dotnet/dotnet Tools/PolicyVerify/bin/Release/net8.0/PolicyVerify.dll Assets/Resources/AI/shards-policy.bytes --hero-tactics /tmp/hero-minimal.json 2 decline_bad_sacrifice --require-pass
```

Observed twice identically: `kosynwu/decline_bad_sacrifice: greedy 0/2; sampled 1/16`, exit 2. This exercises the actual frozen network and actual sacrifice adapter path.

Effect-structure probe:

```sh
/home/lva/.dotnet/dotnet Tools/PolicyVerify/bin/Release/net8.0/PolicyVerify.dll Assets/Resources/AI/shards-policy.bytes --effect-structure-audit /tmp/effect-structure.json
```

Raw results: [current](hero-tactics-2026-09-27/current.json), [pre-Rez](hero-tactics-2026-09-27/before-rez.json), [ordinary self-play](hero-tactics-2026-09-27/block-001.json), [first supervised block](hero-tactics-2026-09-27/block-002.json). They include per-variant action probabilities, demonstrated-line probabilities, failure traces, root observations and counterfactual probes. [Gate](hero-tactics-2026-09-27/current-gate.json), [descriptor probes](hero-tactics-2026-09-27/effect-structure.json), [hash manifest](hero-tactics-2026-09-27/manifest.json).
