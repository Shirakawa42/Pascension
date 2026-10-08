# Hero draft fix and destiny audit — 2026-09-27

## Hero draft

The actual `PolicyEngine.StepPolicy` caller reproduced the defect before modification:

```
dotnet run --project Tools/PolicyVerify -- Assets/Resources/AI/shards-policy.bytes --draft-regression
Draft regression seed=100001: opponent=decima, picked=volos, expected=tetra
```

`HeroDraftPolicy` now selects by stable hero definition, seat and already-public opponent pick. It uses the frozen gameplay policy's 20 ordered hero matchups, each measured over 2,500 games. First pick maximizes the worst matchup against the opponent's response. Second pick maximizes the measured same-seat matchup. This selects Tetra when available and Volos against Tetra. The table is specific to generation 18232 and the current balance: refresh after changing either. It does not claim to solve board-conditioned optimal drafting against humans.

No gameplay weights, card observations, legal actions or final statistics were changed. No retraining was required. New match records identify the policy as `generic-effects-18232-draft2`.

Validation:
- Before/after test through real PolicyEngine: failure above, then 192 passing cases (32 seeds × first pick/every opponent hero).
- All 120 initial menu permutations and all 24 response permutations for each opponent, both seats: pass; also disabled/unknown-hero checks.
- All 20 table entries verified directly against the frozen raw matchup counts.
- EngineVerify: 247 passed.
- Frozen inference: 200 real fixtures, maximum probability error 1.3709068e-6; 30 complete games / 11,019 decisions retain exact adapter observation/action parity against the evaluated runtime.
- Unity Windows build succeeded with no errors.

## Destiny investigation

Command:

```
dotnet run --project Tools/PolicyVerify -- Assets/Resources/AI/shards-policy.bytes --destiny-audit Tools/TrainingPreflight/results/destiny-position-audit-2026-09-27.json
```

108 completed diagnostic games with hero assignments cycling over all 20 ordered pairs yielded 322 audited choice states, covering all 30 active destinies and including 100 bonus-destiny choices. These games are excluded from the 50,000-game statistics.

Three separate interventions keep the game otherwise unchanged:
1. Reverse candidate menu order, comparing probabilities by card instance, not index: maximum difference 1.847744e-6 (0.000185 percentage points); no preferred-card changes outside numerical ties.
2. Reverse the actual destiny row: exactly zero probability change, zero preferred-card changes.
3. For bonus destiny decisions, reverse engine decision options and rebuild candidates, which also changes encoded ordinals: maximum difference 0.017010987 (1.7011 percentage points); zero preferred-card changes in 100 states.

Normal destiny acquisition is a card action (`kind=6`), not the ordinal categorical mode head responsible for hero drafting. Bonus acquisitions use ChooseCards, also outside that categorical head. Card definition IDs/effect embeddings are used. The remaining ordinal scalar gives bonus selections a small residual order dependence in this sample. This is not a proof of perfect order invariance or optimal destiny strategy, but there is no evidence of the hero-style second-option collapse.

The dashboard's score is the eventual outcome conditional on acquisition, not a controlled estimate of how much the destiny improves winning chances. The recorded games include additional destinies granted by monster rewards, not just the normal Mastery-5 choice. The six-card row is random and shrinks without refilling; each normal pick also removes an option from the opponent. The dataset lacks legal-menu exposure and does not separate normal versus bonus destiny acquisitions, so popularity is not pick rate when offered.

Examples from the fixed 50,000-game dataset:

| Destiny | Acquisitions | Observed score | Mean acquisition round |
|---|---:|---:|---:|
| Deadly Recruits | 10,596 | 58.57% | 4.27 |
| Crystal Gate | 773 | 74.13% | 9.41 |
| Datic Secrets | 1,991 | 69.41% | 8.75 |
| Advanced Medicine | 7,586 | 40.55% | 5.98 |

Tetra accounts for 34.3% of Crystal Gate acquisitions, versus 18.5% of Deadly Recruits acquisitions; its overall hero score is 70.45%. Acquisition timing, hero/deck specialization and extra monster rewards all confound comparisons. High score/low popularity cannot alone distinguish a strong overlooked destiny from a late reward obtained mostly in already-winning positions. A causal ranking would require paired counterfactual rollouts at the choice state, with both seat and public situation controlled.
