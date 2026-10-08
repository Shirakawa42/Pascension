# Reroll denial and public opponent collections — 2026-09-27

The clarified information rule matters directly for denial play: **both complete deck compositions are public; opponent hand membership and draw order are hidden**. V8's exact opponent collection was absent. Its acquisition history and currently face-up cards were incomplete substitutes. This limitation can prevent the model from distinguishing which center-row card best supports the opponent.

## Concrete representation failure

An isolated frozen-V8 engine fixture constructs two otherwise identical positions, with three-card opponent decks:

- Undergrowth: Arach Devotees, Nectar Alchemist, Thorn Zealot.
- Order: Cloud Oracles, Index of Futures, Portal Monk.

All **4,672 input floats**—2,560 observations, 2,048 candidate features, and 64 legality values—are exactly identical. Hand count, deck count, resources, heroes and market are held fixed. This is a constructed representation test, not a full initial-seed legal replay, and does not establish the optimal reroll in either position. It establishes that the policy cannot respond to this legitimately public composition difference. Changing opponent mastery from 5 to 20 correctly changes observation slot **65** alone. [Fixture and encoded inputs](reroll-composition-alias-v8.json), [construction source](reroll-composition-probe/Program.cs).

The proposed additional public-collection histogram is necessary. Its privacy regression should preserve output under exchanging hidden opponent hand/deck membership with the same full collection and counts, and under permuting hidden draw order; it should change when the public collection changes. Public ownership totals must not silently become an encoding of current hidden zone membership.

## What reroll currently does and what the policy sees

A reroll pays `max(0, 1 + rerolls_this_turn - next_reroll_discount)`, sends the targeted center card to the **bottom of the center deck**, and immediately refills that slot. It does not permanently banish the card. Comet explicitly cannot be rerolled. Rez's discount applies to the next successful reroll and is then consumed. [Engine implementation](../../../Assets/Scripts/Shards/Engine/ShardsEngine.cs#L835).

V8 represents reroll as action kind 8 with the target's card identity, faction, type, printed cost, and market slot. It provides the acting player's gems and actual next reroll price in observation slots **18** and **44**. Candidate feature **24** remains the target's effective **purchase** cost, not the reroll charge; the model has to combine the action kind with the separate price scalar. Opponent mastery is available at **65**, visible discards/play/champions/destinies in public count channels, and public acquisition history in the V8 supplement. Exact current opponent ownership is missing. [Candidate encoding](../experiments/HostV8/Encoder.cs#L395), [resource encoding](../experiments/HostV8/Encoder.cs#L235), [supplement history](../experiments/HostV8/Encoder.cs#L159).

Rerolls are not removed or specifically penalized by the action prior. The PPO objective receives their consequences through the final deciding-player win/loss outcome. A denial benefit is therefore learnable in principle when its relevant public information is present, but the signal is delayed until the opponent's later purchases and the eventual game result. No local reward explicitly distinguishes denial from looking for a card to buy. [Action prior](../learning_model.py#L25), [terminal credit](../learning_rollout.py#L43).

## Measurement interpretation

[The frozen CPU probe](../experiments/reroll_denial_probe.py) uses unchanged generation **8672**, 160 equal-allocation hero/seat games, seed namespace `0x8600000000000000`, and sampling seed **860927**. It records actual acting decisions, target identities and printed costs, real gem expenditure, whether the same card could have been bought or fast-played, and whether paying the reroll would consume affordability of every existing other buy option.

Faction affinity is only a descriptive proxy: the target's printed faction share in the acting player's currently encoded permanent collection versus the opponent's most recently observed own collection summary. The latter can become stale after intervening ownership changes. This audit-only summary is never injected into the policy. Printed faction also misses Prism, Project Yggdrasil and card-specific cross-faction combinations. These measurements cannot label a particular target a good or bad denial.

A reroll followed by no later own purchase or fast-play before ending the turn is compatible with denial, but also with an unsuccessful search. A policy probability changing when opponent mastery changes shows contextual sensitivity, not correct strategic valuation. A causal assessment requires paired continuations for competing reroll targets from the same state, including the opportunity cost of the spent gems and uncertainty in the refill.

## Observed usage

The panel completed **160 games**, **65,028 acting decisions**, and **zero censors**, in **71.92 seconds**. Weights remained unchanged and CUDA was not initialized. [Full results](reroll-denial-v8-2026-09-27.json).

- **3,061 rerolls**, spending **3,405 gems** in total.
- Prices: **447 free** (Rez's discount), **1,932 at 1 gem**, **586 at 2**, **84 at 3**, **11 at 4**, and **1 at 5**.
- The targeted card was also currently buyable in **395** cases and fast-playable in **31**.
- **985** rerolls would consume enough gems to lose affordability of every currently available other buy. This can still be justified by denial, a valuable refill, or later gem generation.
- **1,866** rerolls had no later own buy/fast-play before the observed end action. This is compatible with denial but is also compatible with an unsuccessful search.
- **824** occurred against an opponent at mastery 15 or higher, including **41** against mastery 30.

The faction-affinity proxy does not show preferential denial:

| Target affinity proxy | Selected rerolls | Candidate opportunities | Selections / opportunities |
|---|---:|---:|---:|
| Opponent's faction share at least 20 percentage points higher | 515 | 32,231 | 1.598% |
| Own faction share at least 20 percentage points higher | 520 | 29,455 | 1.765% |
| Similar shares | 1,986 | 104,721 | 1.896% |

These are repeated menu opportunities, not independent trials. Card identity, gem availability, hero, mastery, turn, and ownership changes confound the groups. They neither prove that rerolls are bad nor establish that the policy has learned denial well.

The most frequently rerolled cards by raw count included Reactor Drone (88), Cryptofist Monk (76), Swyft (73), Century Forge (70), and Shardwood Guardian (66). Raw frequency is heavily affected by how often each card is offered; the full result retains opportunity counts and summed action probabilities for each target.

## Actual policy response on the representation fixture

Generation 8672 assigns **identical** reroll probabilities and value to the two different public opponent compositions. Total reroll probability is **93.302%** in both; Grim Tutor and Cache Warden receive most of it. This equality follows directly from the identical encoded inputs.

Changing only the encoded opponent mastery from 5 to 20 changes total reroll probability to approximately **1.07 × 10⁻³¹** and value from **0.8094** to **−0.6260**. This extreme response proves sensitivity to the existing mastery field, not good strategic reasoning. The sparse constructed fixture is unlike ordinary sampled positions; it is unsuitable for judging calibration or deciding which reroll was best.

For a preservation migration, newly added public-collection features should differ across the composition fixture, while zero-initialized policy projections will intentionally leave old logits equal initially. After training, repeat fixed-board public-composition comparisons to verify that the learner uses this information. Changed probabilities alone still do not establish correct denial; paired continuations from the same public state are required to evaluate that.

## Recommendation

Supply exact public ownership counts and privacy tests, then continue outcome training with the improved representation. Keep reroll optional. Do not hard-code removal of cards matching the opponent's dominant printed faction: it ignores cross-faction combinations, the actor's own needs, rising reroll prices, and the replacement card. A later frozen tactical benchmark should compare spending the gem, buying first, rerolling different targets, and ending the turn under the same public state and sampled hidden deals. Such a benchmark can quantify denial value rather than guess intent from selection statistics.

Checkpoint provenance: the source was the live `main-v8/latest.soicp` path, copied atomically by the evaluator. Its actual loaded generation was **8672**, payload SHA-256 `26b767b08d2ec8558fb86c81a8f0d3f7b4017cd432448cf53dd54ab512967002`. An earlier status read showed generation 8652, but the checkpoint advanced before the isolated copy. Every reported panel and fixture response above uses the same frozen 8672 payload.
