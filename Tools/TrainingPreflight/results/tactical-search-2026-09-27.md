# Tactical search and training experiments — 27 September 2026

**Accepted and installed:** the existing generation-19062 network plus public-information, same-turn lookahead. **Rejected:** the small distillation pilot. The live network weights were not replaced.

The Windows build is installed at `E:\Bureau\pascension-windows-v1.0.4`. The prior installation is preserved at `E:\Bureau\pascension-windows-v1.0.4-before-tactical-20260927-173854`. Build output is `Builds/WindowsTacticalAI/pascension.exe`.

## Measured improvement

The independent tactical confirmation uses 32 families across all five heroes, 16 new variants per family, shuffled hand order and one to three additional cards drawn from a broader distraction set. These positions were generated after the search implementation was frozen. Every prescribed goal line was first executed through the actual engine. Each position receives one greedy continuation and eight seeded stochastic continuations: **512 positions, 4,608 rollouts per AI**.

| Hero | Existing network | Network + lookahead |
|---|---:|---:|
| decima | 421/864 (48.7%) | 864/864 (100.0%) |
| tetra | 374/864 (43.3%) | 864/864 (100.0%) |
| volos | 212/1008 (21.0%) | 1008/1008 (100.0%) |
| kosynwu | 150/864 (17.4%) | 864/864 (100.0%) |
| rez | 591/1008 (58.6%) | 1003/1008 (99.5%) |

Overall: **1,748/4,608 (37.93%) → 4,603/4,608 (99.89%)**. All 32 families pass the pre-existing requirement of at least 95% success in both greedy and stochastic play, versus 3/32 for the network alone. All 512 greedy confirmations succeed. Five sampled Star Seeker continuations still fail; this is not a claim of perfect play.

The earlier scenario sets became development/regression tests during debugging. Their final result is 4,608/4,608, but they should not be described as untouched held-out evidence. The fresh confirmation above is separate.

Full-game strength was checked against the **identical frozen network without lookahead**, not an older/weaker checkpoint:

| Cohort | Search wins / losses | Win rate | Matched seed pairs |
|---|---:|---:|---:|
| Release test on development seeds | 220 / 180 | 55.00% | 200 |
| Independent confirmation on fresh seeds | 89 / 71 | 55.63% | 80 |
| Combined descriptive result | 309 / 251 | 55.18% | 280 |

Each pair uses the same game seed and hero placement. Search controls seat 0 in one game and seat 1 in the other. All 20 ordered distinct-hero matchups are equally represented; action sampling has separate, fixed RNG streams per seat. No training occurs during evaluation. The confirmation and release binaries have identical search-source hashes, and their seed ranges do not overlap.

The fresh cohort's paired bootstrap 95% interval is **52.5–59.4%**. Nine pairs improve from a split to two search wins, zero become two search losses, and 71 remain split; the paired sign-test two-sided p-value is 0.00390625. The combined descriptive paired interval is 53.4–57.0%, but the fresh cohort is the independent confirmation. Repeating development seeds across prototype versions is not counted as additional independent evidence.

These are strength comparisons between different inference profiles. Their per-hero scores are **not a new balance/self-play cohort**. The statistics window continues to show the previous 50,000-game dataset, generated without lookahead.

## What changed

`Assets/Scripts/Shards/AI/TacticalSearch.cs` copies the running rules engine, including suspended effect/decision continuations. It explores legal action sequences using the real card effects and current balance values. There are no hero/card-specific winning recipes in the planner. Simple resource scores order exploration; only an actual terminal win in the simulation can justify an override.

Before exploring, it replaces private information with samples constructed from public facts: own unordered deck and remembered top cards, the opponent's public complete collection and public zone sizes, public center/setup quantities, visible cards and remembered center tops. It uses its own planning RNG, never the real game's RNG state to predict draws. Opponent private memory is cleared. The defender reveals every available shield in each sampled world.

A concrete winning sequence must replay successfully in four sampled worlds, with the same choices. Separate optimistic continuations for each world are insufficient. Accepted continuations are retained between actions and revalidated against newly available information. Otherwise the original sampled network chooses the action.

Search is limited to the current turn and 12 wrapper decisions. It tries narrow and wider beams, with a larger final budget for Scry/reorder menus where many prefixes look equally valuable. In full games it is activated around plausible finishing positions: mastery at least 25, or opposing health within current power plus 20. This is a bounded tactical planner, not multi-turn minimax or a guarantee against every possible hidden draw. The center sampling model is approximate, and strategic deck-building/market denial still relies on the network. Generic-effect encoding's previously identified loss of effect-tree structure is not repaired by this change; search instead executes the current engine rules.

Two prototype defects were caught and removed before release:

- Runtime queued effects can capture the engine in delegates. Sharing all effect objects let copied continuations touch live state. The copy now preserves the full mutable reference graph, with integrity checks.
- The existing replay hash omits zone boundaries and suspended-continuation details. Using it to merge search nodes discarded valid World Piercer lines. Search no longer uses this hash for pruning.

## Runtime and integration checks

`PolicyEngine.PreparePolicy()` starts thinking during the existing presentation delay. `TryStepPolicy()` polls a background task and submits only on the main thread. Search operates on a copied, sanitized position; a game-state revision change discards stale work. `SoiSoloMatch` retains the minimum one-second gap between completed actions.

Ten complete synchronous/asynchronous parity games matched across **3,594 decisions**, including all five heroes. A separate test submitted a real action during thinking and confirmed the stale result was rejected. The checks cover engine state, public observations, candidates, memory, hidden-state perturbations, and source-state preservation.

A Unity/Mono game completed with the same winner and 277 decisions in both execution modes. The largest synchronous decision took 2,443.7 ms; the largest main-thread call with asynchronous thinking took 58.8 ms under concurrent CPU load. These are smoke-test measurements, not a guaranteed frame-time bound. Thinking may still take additional time, but no longer blocks the UI for the duration of the search.

All **258 engine tests pass**. The Windows build succeeded in 30.0 seconds with no errors; its one compiler warning is the pre-existing `TMP_Text.enableWordWrapping` deprecation in `MarketView.cs:66`. Installed executable and AI/game assemblies were verified against the staged build by SHA-256. No existing game process was closed.

## Training experiment and safeguards

The new collector (`--search-curriculum`) produces public observations and search-supported action labels from ordinary games on a separate seed range. It also collects broad old-policy retention examples. No hand-authored tactical test scenarios or balance-view games were used to train the pilot. Search advice is an auxiliary policy target, not a fabricated terminal reward.

Forty balanced collection games produced 125 search labels and 656 retention examples. Whole seed pairs were held out: 96 training labels and 29 held-out labels. Hero coverage was highly uneven despite equal games: Decima 29, Ko Syn Wu 3, Rez 6, Tetra 30, Volos 28 training labels.

The bounded GPU pilot ran 800 updates in **13.44 seconds**, with equal hero contribution plus original-policy KL/value retention. It increased held-out teacher agreement, but the original tactical battery worsened from **1,436/4,608 to 1,276/4,608** completed goals. This candidate was **rejected**, and its weights were never installed. Lower supervised loss was insufficient evidence of stronger play.

`search_distillation.py` now deduplicates exact recorded inputs, separates held-out seed pairs, removes train/held-out input overlap, requires at least 200 distinct training labels per hero by default, limits repeated passes over the smallest hero dataset, checks native/GPU policy parity, and writes isolated checkpoints. A small-data diagnostic requires the explicit `--allow-small-pilot` flag. It never automatically deploys a model. This guards against generating a large apparent dataset by repeating a handful of situations.

Expensive search is not added to every PPO rollout. The implemented route for later training is sparse search-generated advice with broad retention and independent tactical/strength gates. A larger diverse curriculum is needed before another distillation attempt is justified; the successful change in this release is search, not a newly trained network.

## Evidence and reproduction

The inference profile and unchanged network hash are recorded in `Assets/Resources/AI/shards-inference.json`. Detailed artifacts are in [tactical-search-2026-09-27](tactical-search-2026-09-27/):

- `confirmation-tactics.json`, `confirmation-baseline.json`, and `confirmation-tactical-gate.json`: independent tactical confirmation and all traces.
- `release-paired.json`, `confirmation-games.json`, and the corresponding strength summaries: individual games, seat/hero coverage and paired uncertainty.
- `async-parity.json`, `distillation-report.json`, `distilled-network.json`, and `manifest.json`: integration, rejected training experiment and deployment provenance.

Frozen runnable binaries, weights and source snapshots are under `/home/lva/.local/share/shards-training/2026-09-26/tactical-search-20260927/frozen-confirmation`. Later edits to the workspace do not change those evaluations.

Example workspace reproduction:

```sh
dotnet build Tools/PolicyVerify -c Release
dotnet Tools/PolicyVerify/bin/Release/net8.0/PolicyVerify.dll Assets/Resources/AI/shards-policy.bytes --search-tactics /tmp/tactics.json 16 --perturb --sampled --fresh
python3 Tools/TrainingPreflight/hero_tactical_gate.py /tmp/tactics.json
dotnet Tools/PolicyVerify/bin/Release/net8.0/PolicyVerify.dll Assets/Resources/AI/shards-policy.bytes --search-evaluation /tmp/strength.json 160 --fresh
python3 Tools/TrainingPreflight/search_strength_report.py /tmp/strength.json
```

Use the frozen binary for exact historical reproduction. The strength-report validator rejects incomplete/duplicate games, repeated seeds, unbalanced heroes/seats, mismatched pairs and inconsistent outcome totals.
