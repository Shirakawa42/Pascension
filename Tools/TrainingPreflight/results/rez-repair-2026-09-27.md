# Rez memory repair and focused adaptation

**Subsequent audit:** the [five-hero tactical audit](hero-tactics-2026-09-27.md) found important generalization failures and regressions outside the acceptance suite below, including damage-based Longshot sequences and Ko Syn Wu sacrifice decisions. The memory fixes and recorded test results remain valid, but this report must not be read as evidence of broadly reliable tactical play.

The memory defects are fixed in the game and training adapter. Three separately bounded ten-minute training blocks completed before the tactical acceptance checks passed. The final policy is generation **19062**. No action override or scripted move selector is used during evaluation or gameplay.

## Implementation

- `ShardsCardsRevealedEvent.TakenFromCenterTop` explicitly identifies public center takes. Longshot and Shard Defiant mark their center reveals. Hand and personal-deck reveals leave the flag false.
- Both `CenterKnowledge` implementations advance the remembered prefix for actual center takes and preserve the remaining suffix. Unrelated reveals no longer erase it. Genuine unknown changes still invalidate knowledge; hidden engine deck order is never used to reconstruct an observation.
- The model now receives four **ordered semantic card embeddings** for remembered center cards. The existing card ID/cost/faction fields remain compatible. The semantic state projection expands from 704 to 960 inputs; added columns and Adam moments start at zero, preserving all existing learned columns and optimizer state.
- Native inference supports both the original and expanded semantic projection. Card-effect descriptors themselves were verified unchanged: this is an AI repair, not another balance patch.
- Training setup balances all eight ordered Rez/opponent combinations. Exactly one seat is Rez in every training game; the four opponents and Rez's seat vary. Final balance evaluation returns to all twenty ordered distinct-hero combinations.

## Training loop and why it needed three blocks

| Block | Method | Complete games | Charged seconds | Final generation | Decision |
|---|---|---:|---:|---:|---|
| 1 | Repaired memory and representation, ordinary Rez self-play | 40,704 | 570.340 | 18742 | Tactical checks still failed |
| 2 | Self-play plus supervised tactical examples | 40,704 | 570.352 | 18901 | Main suite passed; broader Ingeminex variants failed |
| 3 | Expanded examples covering other monsters, reveal depths, champions and starter banishes | 41,216 | 570.367 | 19062 | Main and transfer suites passed |

Total: **122,624 games, 1,711.058 seconds (28m31s)**. Each block had an independent 600-second ledger and retained the trainer's 30-second checkpoint/shutdown reserve. The initial CUDA indexing startup failure occurred before a training session or optimizer update and charged zero time. It was fixed by using graph-safe slice/stack indexing.

Block 1 showed that adding information and more self-play did not reliably teach these rare combinations. Blocks 2–3 therefore added a separate supervised loss, once per sixteen accepted PPO updates, using legal actions in constructed positions. It is explicitly separate from terminal game rewards. Every demonstrated winning line was executed successfully through the engine. Evaluation never loads a teacher to choose actions.

The first curriculum contained 704 decision examples; the expanded one contained 1,408. They include a counterexample to automatic Scry: take an immediate Infinity Shard win when it is already available. Evaluation uses different seeds and includes varied mastery, both seats, different cards and reveal positions. These are tactical tests, not a claim that every labelled preference dominates every possible strategic alternative.

No game was censored. All recorded model/optimizer finite checks passed. Block 3 skipped two minibatches through the existing policy-change acceptance guards; neither was a numerical failure. The trainer did not persist which of its two policy-change guards triggered, so that detail cannot be reconstructed from its aggregate log. Skipped minibatches did not update the model.

## Tactical and memory verification

The fixed acceptance criteria require at least 90% mean demonstrated-action probability and 95% top-choice accuracy in each tactical group. Multi-pick Scry is checked through completion: selecting another card first is not considered a failure if the monster is ultimately buried. Whole-turn tests additionally require at least 95% successful finishes in each group.

The final transfer suite passed all ten groups, with **99.36%–100%** mean success probability and 100% correct greedy choices. It covers Scry setup, burying incompatible champions, preserving the mastery card, playing Longshot after setup, taking the revealed mastery effect, Infinity timing, lethal Warpquartz banishes, all five Ingeminex at different depths with enemy Doom Gate, and immediate wins that should precede Scry.

Whole-turn results on the same held-out constructed positions:

| Task | Pre-repair policy | Final policy |
|---|---:|---:|
| Mastery sequence, greedy | 2/64 wins | 64/64 wins |
| Mastery sequence, sampled | 1/64 wins | 64/64 wins |
| Warpquartz sequence, greedy | 9/64 wins | 64/64 wins |
| Warpquartz sequence, sampled | 15/64 wins | 64/64 wins |

Fixtures were tightened during validation to use an actual relic recruitment and a public opponent collection that rules out shields when testing guaranteed normal-damage lethal. These final comparisons use identical tightened positions for both models. Raw earlier fixtures are retained separately rather than silently relabelled.

The original Longshot probe now chooses Scry with 99.54% probability at mastery 5, 99.05% at 14, 99.01% at 15 and 98.66% at 20. The deterministic Unify and Longshot memory regressions pass and are included in the headless NUnit suite.

In **800 additional complete natural games**, no incorrect remembered prefix, unrelated-reveal memory loss or lost Longshot suffix was observed. Private-order and opponent hidden-zone permutation checks pass; the opponent does not receive private Scry knowledge. These are measured checks, not a universal proof of perfect information handling.

Natural games still contain Longshot-before-Scry plays and monsters left unburied in other contexts. These counts alone do not establish errors: mastery, possible kills, opponent denial and information timing matter. No universal Scry/monster rule was hard-coded.

Other validation:

- 258 headless engine tests pass.
- Three semantic migration/gradient tests pass.
- Python/native inference agrees across 200 real observations: maximum probability error `6.11e-6`, value error `7.31e-7`.
- Game/training adapter observations and actions match exactly across 14,792 decisions in 40 complete games, covering all twenty ordered hero pairs.
- Unity compiled the repair and loaded the exact final policy hash.

## Strength evidence

The second checkpoint scored 48.44% against the pre-repair model in 4,096 seat-swapped all-hero games; its conservative 95% interval was 45.44%–51.44%. This did not establish overall superiority and is not presented as such.

The final checkpoint was then tested specifically as Rez against **identical frozen pre-repair opponents**, with four opponents and both seats balanced. Each policy played 4,096 games on paired engine seeds. Both used the corrected memory ledger and the same balance rules, so this comparison measures policy adaptation rather than isolating the ledger fix.

- Old Rez: **41.345%** score.
- New Rez: **45.605%** score.
- Change: **+4.260 percentage points**.
- Conservative paired 95% change interval: **+0.016 to +8.504 percentage points**.

This provides evidence of improvement for Rez against that opponent population. The interval is wide and only barely excludes zero. It does not prove globally optimal play or establish Rez's true balance under perfect play.

## Artifacts and reproducibility

Campaign: `/home/lva/.local/share/shards-training/2026-09-26/rez-repair-20260927`.

Each `block-NNN` retains its own ledger, initial/final checkpoints, source identity, frozen runtime, native policy export and tactical results. `training-verification.json`, `fixed-opponents.json`, `final-memory-games.json` and `final-original-cases.json` hold the final evidence. A compact checked-in summary is [rez-repair-2026-09-27.json](rez-repair-2026-09-27.json).

Final native policy SHA-256: `f15c9d40644f6ef7e84ebf44d325ec4d6f3bae771e3f6ac7f7904a52baf4a177`.
Frozen PyTorch policy hash: `53eae1c845bb8e64033004221b400cc994bea6d66ea545193afa43d193ae1c78`.

Tools: `rez_entry.py`, `rez_runtime.py`, `rez_policy.py`, `rez_tactical_learning.py`, `rez_tactical_gate.py`, `rez_strength.py`, `rez_fixed_opponents.py`, and `Tools/PolicyVerify`'s `--rez-audit`, `--rez-tactics`, `--rez-memory-regression`, `--rez-games` commands.

Reserved seed origins: self-play `0x7c00000000000000`; checkpoint comparison `0x7d...`; final statistics `0x7e00000000000000`. Diagnostic and teaching examples never enter the final statistics cohort.

## Fresh 50,000-game statistics

The final generation completed 50,000 evaluation-only games in 248.43 seconds. There were zero optimizer updates, censored games or unfinished discarded games. All twenty ordered hero pairs have exactly 2,500 games; each hero appears in each seat 10,000 times. The policy hash and all three inactive training ledgers remained unchanged.

Only this cohort is published at <http://localhost:8768/statistics?view=balance>. Snapshot `7df59d6b1ead973d`; strategy trace SHA-256 `77226285576d0fc59eea0c0eb3de99d6463b00bd85fa5a570643ca212fa4cead`. The prior 50,000-game cohort remains archived separately. The live API was checked against the new snapshot, count and policy provenance.

| Hero | Previous cohort score | New cohort score |
|---|---:|---:|
| Decima | 45.595% | 45.130% |
| Tetra | 61.150% | 62.163% |
| Volos | 50.638% | 48.938% |
| Ko Syn Wu | 51.023% | 50.060% |
| Rez | 41.595% | 43.710% |

Scores count a draw as half a win. Changes between these two cohorts are descriptive: the policy changed for both players. The fixed-opponent experiment above is the stronger test of Rez's improvement. Rez remains the lowest-scoring hero under this policy; these results do not distinguish remaining strategic limitations from hero balance conclusively.

Seat 0 won 26,182 games (52.364%); seat 1 won 23,804; 14 draws. Mean final round was 12.20554. Victory attribution: 20,511 mastery/Infinity, 322 Comet, 26,748 normal damage, 1,740 other health loss, 665 concessions, 14 draws, zero unattributed. The view contains 667 qualifying strategy rows, card images, hero/seat filters and the existing detailed balance analysis. Its scope explicitly discloses Rez-focused training and supervised examples.

## Playable delivery

The final policy and repaired runtime were built successfully for Windows with zero errors. The sole compiler warning is the existing obsolete `TMP_Text.enableWordWrapping` property in `MarketView.cs`. The hero draft table was refreshed from the new 50,000-game seat-specific matchups; it still picks Tetra first and now chooses Volos against Tetra in either seat. Every menu permutation passes the headless draft tests; 192 actual PolicyEngine draft checks also pass.

Installed at `E:\Bureau\pascension-windows-v1.0.4`. The complete previous install is retained at `E:\Bureau\pascension-windows-v1.0.4-before-rez-repair-20260927`. Executable, Unity runtime, engine/content/AI/game assemblies and resource archive hashes match the successful build. Source model SHA is recorded above. The existing one-second AI action delay is retained.

Post-delivery verification: 258 headless engine tests, 192 draft checks and 16 statistics/publisher/dashboard tests pass. Training and evaluation are stopped; the monitoring services remain available.
