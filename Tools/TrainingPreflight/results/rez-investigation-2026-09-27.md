# Rez investigation — September 27, 2026

The current policy has reproducible memory defects and tactical sequencing weaknesses. Its 41.595% Rez score is not a reliable estimate of optimally played Rez. This investigation does not establish how many percentage points fixing those defects would recover, or prove that Rez is balanced.

## Evidence and scope

- Reanalysed both completed 50,000-game cohorts, each containing 20,000 Rez appearances, evenly distributed across opponents and seats.
- Ran 1,600 additional frozen-policy diagnostic games: Rez against each other hero, 200 games per opponent/seat combination. These games were not published into balance statistics.
- Built controlled positions and evaluated the shipped policy's complete action probabilities. Subsequent actions and asserted winning lines use the actual engine.
- Native policy SHA-256: `cfb37fdb1dac6c6637cb0eacafa4629caf00bf2a2d2967c70943b0666961faca`.
- Raw evidence: `/home/lva/.local/share/shards-training/2026-09-26/balance-patch-20260927/rez-investigation-20260927/`. Includes controlled scenarios, all diagnostic game records, summary and hashes. Compact aggregate: [rez-investigation-2026-09-27.json](rez-investigation-2026-09-27.json).

Only diagnostic tooling and this report changed. No model updates, gameplay fixes, installed-game replacement or replacement of the published 50,000-game cohort were performed.

## 1. Revealed-card memory is faulty

`Assets/Scripts/Shards/AI/CenterKnowledge.cs`, also mirrored in the training host, stores an ordered prefix of up to four center cards. Scry initially records the right cards and correctly retains unburied cards. Ordinary resource plays preserve that knowledge.

However, `AfterSubmit` clears both players' knowledge on **every** `ShardsCardsRevealedEvent`. This includes unrelated hand/deck reveals from Unify, Duplication Fabricator and Legion Carrier. The center deck need not have changed. Longshot also clears the entire prefix after taking two cards, forgetting a previously known third card that remains on top.

Both problems have deterministic, failing regressions:

1. Scry and retain three cards; play Undergrowth Aspirant with another matching card in hand. Unify erases the remembered center cards.
2. Scry and retain three cards; play Longshot. The still-known third card is erased.

In 1,600 natural diagnostic games, valid knowledge was unnecessarily erased **350 times across 259 games (16.2%)**, plus two observed Longshot tail losses. The audit confirms that the previously known center prefix was unchanged when counting the first category. These counts need not cover every possible memory loss.

The observed nonempty remembered prefixes always matched the actual top cards. Hidden state was inspected solely by audit assertions, never fed into inference or used to select actions. Controlled hidden-order/opponent-hand-versus-deck permutations left observations, candidates and masks unchanged. Private Scry information was not delivered to the opponent. These checks found forgetting, not an information leak; they are not a proof covering every game mechanic.

The ledger also does not retain a history of known cards buried at the bottom. That is a representation limitation rather than the same invalidation bug.

## 2. Generic effects do not cover remembered top cards

`HeroFeatures.cs` encodes each known top card as three scalars: numeric card ID, cost and faction. It does not explicitly encode champion/monster status, fast-play eligibility or effects there.

`FrozenPolicy.cs` builds semantic effect context for card-zone bags and available relics, but does not apply that representation to these remembered-card slots. Candidate cards have semantic embeddings; remembered top cards only enter the ordinary numeric trunk. Thus the generic-effect migration did not cover this important input path. The model can theoretically learn ID associations, but its representation does not directly support the relationship between a known card's effects/type and Longshot's restrictions.

The policy is feed-forward and does not search engine continuations at decision time. It must learn all multi-action planning from training. Missing explicit effect information and broken memory plausibly make that harder; their individual causal contribution to win rate has not yet been measured.

## 3. Tactical probes

The percentages below are exact policy probabilities in constructed positions, not success rates across ordinary games. These positions are intentionally small and may be unusual compared with training states.

| Probe | Result |
|---|---|
| Longshot in hand, unused Rez ability, mastery 5 | 92.14% play Longshot immediately; 0.64% Scry first |
| Same setup at mastery 15 | 52.63% Longshot; 7.00% Scry |
| Scry reveals two champions followed by a playable ally, mastery 20 | 89.37% keep all three; champions remain incompatible with Longshot despite the higher cost limit |
| Known winning Scry → Longshot → Infinity Shard sequence | 52.42% end turn; 38.12% play Infinity too early; only 6.60% start with Scry |
| Scry Ingeminex Agony with enemy Doom Gate and zero power | 97.59% bury the monster |
| Same monster, no Doom Gate, zero power | 84.62% bury |
| Same monster, no Doom Gate, 20 power | 34.95% bury |
| Warpquartz can banish a Crystal for immediately lethal power, mastery 20, round 11 | 94.74% decline; actual banish-and-attack line wins |

The strongest Longshot test has mastery 28, Infinity Shard and Longshot in hand, and legally known center cards ordered as Additri, Testudo Vanguard, Shard Abstractor. There are no gems or unused free rerolls. Scry buries the two champions; Longshot fast-plays Shard Abstractor to reach 30 mastery; Infinity Shard wins. The engine verifies this sequence. Even after being forced to start Scry, the policy keeps all three cards with 86.53% probability.

The Warpquartz fixture moves the physical relic out of SetAside rather than duplicating it, and tests both round 1 and round 11. The latter still fails. However, this failure is **not representative of all natural Warpquartz decisions**: in the diagnostic games, the policy declined only once in 41 initial Warpquartz banish decisions containing a Crystal. The other 40 selected a banish target, not necessarily the Crystal. Mean decline probability was 2.25%. The constructed lethal miss exposes a situational blind spot, not universal refusal to banish starters.

Additional natural-game observations:

- Longshot was played 121 times while Scry was available; 117 of those lacked knowledge of both top cards. There were 323 Longshot hand plays total and 135 choices to Scry while Longshot was playable. Repeated decision opportunities are not independent trials.
- 3,267 free rerolls were taken with Scry available and no remembered prefix. This flags information sequencing for further targeted evaluation; it does not prove every such reroll was inferior.
- With enemy Doom Gate, 123/144 observed monster options were buried. In the narrower low-power, empty-hand, no-ready-champion subset, all 24/24 were buried. That subset does not rule out every destiny interaction and is small.
- Reversing the candidate array while retaining each card's legitimate revealed-depth ordinal preserved Scry probabilities to within `5.96e-8`. No candidate-position identity defect was reproduced.

Do not hard-code “always Scry before Longshot” or “always bury a monster.” Longshot can deny opponent purchases by returning incompatible cards to the bottom, and using it before Scry can reveal additional fresh cards. Monsters can have valuable rewards when killable. Known immediate-win tests distinguish genuine sequencing failures from these strategic exceptions.

## 4. Why the aggregate score barely moved

Scores count draws as half a win. Every matchup contains 5,000 Rez appearances per cohort.

| Opponent | Before patch | After patch | Change |
|---|---:|---:|---:|
| Decima | 47.99% | 46.73% | −1.26 pp |
| Ko Syn Wu | 44.35% | 41.64% | −2.71 pp |
| Tetra | 30.33% | 36.83% | +6.50 pp |
| Volos | 43.33% | 41.18% | −2.15 pp |
| Overall | 41.500% | 41.595% | +0.095 pp |

Improvement against Tetra is offset by worse results against the other heroes. These comparisons combine simultaneous rule changes and adaptation of both opponents; they do not isolate the effect of Rez's buff.

Rez uses the ability frequently: 8.54 activations per game after the patch, versus 8.55 before. Rerolls increased from 10.10 to 13.25 per game, about 31%. The passive is being used; the poor aggregate result is not explained by an unused hero button or absent reroll discount.

Relic selection is another coverage concern. In the patched cohort, normal Rez relic choices were Slipstream Shard 15,295, Star Seeker 3,655 and Warpquartz **463**. Thus Warpquartz represents only **2.39% of 19,413 normal relic selections**, despite its buff. The policy's 6% uniform relic exploration gives a 2% floor when all three choices are present, so much of Warpquartz's usage can be exploratory rather than learned preference. Some states have fewer available relics.

Warpquartz's conditional score for normal selections rose from 33.04% to 45.79%, while Star Seeker's fell from 51.00% to 34.25%. These are selected populations, not randomized relic comparisons. Extra reward acquisitions also explain why the monitor's total Warpquartz acquisition count is 593 rather than 463. None of these conditional scores alone establishes which relic is optimal.

## 5. Corrective priorities

1. Repair knowledge updates in game and training together. Public reveal events need enough origin/count information to distinguish center takes from unrelated reveals. Advance the known prefix for actual public center changes; preserve it for hand/personal-deck reveals; clear it for genuinely unknown reorder/shuffle effects. Never reconstruct memory from the hidden engine deck.
2. Encode remembered cards as ordered semantic effects, including type and current legal fast-play eligibility. Supply legally derivable immediate consequences such as reaching a mastery threshold; preserve an explicit unknown mask.
3. Add targeted training/evaluation positions for Scry/Longshot, mastery-before-Infinity, Warpquartz lethal, reroll information timing and monster/Gate interactions. Include counterexamples where delaying Scry or keeping a monster is correct.
4. Measure the repaired policy against the retained baseline with seat-swapped games and a held-out tactical suite. Separately compare Rez relics with randomized choices to reduce selection bias before publishing another balance conclusion.

Simply extending the same training cannot recover information the encoder discarded. Fixing the ledger alone also does not guarantee the existing model will learn to use the information well. Those changes need adaptation and measured validation before deciding whether Rez needs a further balance change.

## Reproduction

Run from the repository root (`dotnet` here is `/home/lva/.dotnet/dotnet`):

```bash
dotnet run --project Tools/PolicyVerify -c Release -- Assets/Resources/AI/shards-policy.bytes --rez-audit /tmp/rez-audit.json
dotnet run --project Tools/PolicyVerify -c Release -- Assets/Resources/AI/shards-policy.bytes --rez-games /tmp/rez-games.json 1600
dotnet run --project Tools/PolicyVerify -c Release -- Assets/Resources/AI/shards-policy.bytes --rez-memory-regression
```

The last command intentionally fails against the current implementation, naming both confirmed memory regressions. The first writes both passed and failed diagnostic expectations without treating a stochastic preference as an engine assertion. Winning-line assertions, memory and privacy results are separately labelled.

Diagnostic engine seeds are `0x7b00000000000000 + index`; action sampling uses `Random(271000 + index)`. Controlled fixtures use seed 270927 before explicit state construction. The 1,600-game run supersedes the earlier overlapping 800-game pilot; their counts are not added together.
