# September 27 balance adaptation

## Rules

- In 1v1, seat 1 opens with six cards and retains one starting mastery. Subsequent hands still draw five. Multiplayer opening hands are unchanged.
- Ko Syn Wu pays one health; Tetra pays three gems.
- At mastery five, Rez independently discounts every reroll by one. Exhausting the ability scries three. The passive gives reroll prices 0, 1, 2, … without activation.
- Duel Warpquartz resolves each banished card's play effect twice. Its separate three-gem/three-power bonus per card banished this turn is applied once.
- Doom Gate has seven defense. Duel Praetorian-02 provides four in-play shields, or eight at mastery twenty.
- Duel Cinder Scars has four copies instead of five. The base-set definition retains three.

The canonical engine, training host, card rules, French text, dated changelog and card registry were updated together. Historical balance entries remain historical.

## Migration and budget

The source is retained generation 18232 from the previous campaign. All learned tensors and Adam state were preserved. Public effect buffers were refreshed in the learner, initial policy, champion and 16 archived policies. Only Doom Gate, Duel Praetorian-02 and Duel Warpquartz changed card-effect rows. Hero costs and the effective reroll discount are read through the observation encoder. All input shapes and the learned architecture remain unchanged.

This adaptation has a separate 1,200-second authorization. It does not extend or reset the previous overnight ledger. Training uses balanced randomized distinct-hero pairs throughout. The existing supervisor reserves time at the end of the grant for a clean checkpoint flush.

Campaign: `/home/lva/.local/share/shards-training/2026-09-26/balance-patch-20260927`.

The patched simulation and runtime sources are archived under `frozen-patch` inside that campaign. Their calculated identity exactly matches the live training identity. The original frozen Python runtime remains a shared dependency at the preceding campaign's `frozen-evaluation` directory. Starting and intermediate checkpoints are retained; the previous game policy assets are backed up under `previous-game-policy`.

## Verification

- Explicit engine regressions cover the one-time sixth opening card, unchanged multiplayer hands, Tetra affordability and payment, Ko Syn Wu payment and banish, Rez's passive before activation across successive rerolls, Scry 3, card stats and center-deck composition.
- Warpquartz tests cover one/three copied cards and two independent copied decisions that suspend and resume effect resolution.
- Python/native inference is compared using 200 actual observations. The migrated starting model's maximum probability error was `5.55e-6` and value error `7.16e-7`.
- The canonical in-game adapter and patched training adapter produced identical observations, candidates and masks throughout 40 full games covering all 20 ordered hero pairs: 14,423 decisions for the migrated starting model.
- All 12 win-attribution checks pass, including actual Infinity Shard and Comet finishes.
- Publication tests reject incomplete or mismatched-policy cohorts and verify that the new evaluation supplies its own patched card catalog without needing or merging the old dataset.

## Evaluation design

The adaptation check uses 8,192 games on 4,096 paired engine seeds. Each model occupies each seat once per seed; both use the same patched rules and balanced random hero setup. It performs no updates. Confidence bounds account conservatively for pairing. These comparison games do not enter the balance dataset.

The balance evaluation uses exactly 50,000 newly completed games with one frozen policy on both sides: 2,500 games for every ordered distinct-hero pair, 10,000 appearances per hero per seat. It records complete passive strategy and victory traces and publishes only this cohort at http://localhost:8768/statistics?view=balance.

Reserved seed origins: training `0x7700000000000000`; smoke test `0x7800000000000000`; final balance evaluation `0x7900000000000000`; paired strength comparison `0x7a00000000000000`.

Changes in self-play balance combine the rule changes and the policy's adaptation; they do not isolate the causal contribution of each individual patch item.

## Completed adaptation

- Actual charged time: 1,170.331 seconds within the separate 1,200-second grant.
- 351 training generations; 89,856 new completed games; final generation 18583.
- Zero censored games, zero rejected minibatches; every recorded parameter/optimizer finite check passed.
- Final model against migrated starting model: 4,224 wins, 3,968 losses, no draws in 8,192 seat-swapped games: **51.5625%**.
- Conservative paired-seed 95% score bound: **49.4405–53.6845%**. This suggests a modest improvement but does not establish superiority under that bound. The retrained final model was selected for the requested balance evaluation.
- Frozen comparison elapsed: 104.701 seconds; both policies remained unchanged.
- Final native parity: probability error `1.91e-6`, value error `5.82e-7`; 14,703 decisions in 40 complete games covering all ordered hero pairs.
- Native exported asset SHA-256: `cfb37fdb1dac6c6637cb0eacafa4629caf00bf2a2d2967c70943b0666961faca`.

## Final 50,000-game results

Exactly 50,000 new games completed in 235.196 seconds. No censoring or unfinished discarded games. Both model weights and the training ledger remained unchanged throughout evaluation.

| Measure | Previous 50,000 | Patched 50,000 |
|---|---:|---:|
| Seat 0 wins | 57.156% | 52.304% |
| Mean final round | 12.16530 | 12.20726 |
| Decima score | 44.335% | 45.595% |
| Tetra score | 70.455% | 61.150% |
| Volos score | 49.815% | 50.638% |
| Ko Syn Wu score | 43.895% | 51.023% |
| Rez score | 41.500% | 41.595% |

Hero scores count draws as half a win; each hero has 20,000 outcomes split evenly between seats. Tetra remains strongest in this policy population. Ko Syn Wu is now near 50%; Rez remains around 41.6%. Twenty minutes of adaptation and one policy population do not establish optimal hero strength.

| Ending | Previous | Patched |
|---|---:|---:|
| Mastery / Infinity Shard | 17,971 | 20,203 |
| Comet | 250 | 231 |
| Normal damage | 28,994 | 27,346 |
| Other health loss | 2,092 | 2,003 |
| Concession | 676 | 192 |
| Unattributed | 0 | 0 |
| Draw | 17 | 25 |

## Changed-card observations

Counts are player-games with an acquisition, not physical cards or acquisition opportunities. Scores are conditional associations; a different selection of decks and game states can change them.

| Card | Previous acquired player-games | Patched acquired player-games | Previous score | Patched score |
|---|---:|---:|---:|---:|
| Warpquartz | 540 | 593 | 38.333% | 50.590% |
| Doom Gate | 5,851 | 9,195 | 41.215% | 40.239% |
| Praetorian-02 | 1,669 | 2,148 | 42.780% | 46.345% |
| Cinder Scars | 54,394 | 47,712 | 51.686% | 52.187% |

## Final integrity and delivery

- All 20 ordered matchups contain exactly 2,500 games; all 10 hero/seat groups contain 10,000 games.
- The complete per-game trace contains 50,000 unique games; all 2,105,511 acquisition events reconcile by definition to ranking totals.
- All 18 analysis groups reconcile wins, final-round samples and winner health/mastery samples. Every finish has an attributed cause.
- The live API exposes only the new final cohort, including its patched card catalog, 662 strategy rows and full balance analysis.
- 256 engine tests, 192 actual in-game hero-draft checks, 47 Python statistics/monitor tests, 12 attribution checks and native inference/adapter parity passed.
- Unity Windows build succeeded with zero errors; the editor loaded the exact exported policy hash before building.
- Installed to `E:\Bureau\pascension-windows-v1.0.4`. Previous complete installation retained at `E:\Bureau\pascension-windows-v1.0.4-before-balance-20260927`.
- Installed engine, content, AI assemblies and bundled resource files match the new build by SHA-256.
- Hero drafting uses the new 50,000-game table. Its best response to Tetra is Volos in seat 0 and Ko Syn Wu in seat 1; its first choice remains Tetra. The one-second action delay is retained.

Policy SHA-256: `1d0108fd9fb2f649b86c31a0c68ae5fdf47a7eb8646fcc78862528091a90d9f0`.
Trace SHA-256: `23e579a890f3542b3965d53ce1d6451f343a73dcc9d96468bfa2b06717155ffc`.
Published snapshot: `c361083c94663d29`.

Statistics: http://localhost:8768/statistics?view=balance. No training is running.
