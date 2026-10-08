# Native Shards policy verification

The game uses the frozen generic-effect policy from generation 18,232. `Assets/Resources/AI/shards-policy.bytes` contains FP32 weights and precomputed shared card-effect embeddings. Inference runs in C# without Python, CUDA, ONNX, networking, or training.

The engine-facing adapter and observation encoder were ported from HostV10. `native-source-provenance.json` identifies the originals and port. Differences: namespaces, Unity-compatible bit counting, a supplied authoritative engine, fixed eight-way split choices, and accepted human/policy submissions routed through `GameHost`. Memory updates happen only after acceptance, using the pre-action public request and deck counts. No action is submitted recursively from an input notification.

`PolicyEngine.StepPolicy()` makes one wrapper decision. Its independent sampler uses the same policy distribution, including the saved legal-choice mixture. The engine owns all game randomness. Observations preserve the trained public-information contract; policy inference receives only numeric observations and legal candidates.

## Verified on 2026-09-27

- 200 real positions: maximum native/PyTorch probability error 0.00000138, value error 0.00000084.
- 30 complete games / 11,019 decisions: exact native/original observation and candidate equality throughout.
- Reactor cleanup regression: 82 assertions, six real action paths.
- Existing engine/host suite: 241 passing tests.
- Unity editor: clean compilation, actual menu-button launch, human Rez selection, AI drafting and full first turn, and a complete 882-event match.
- The automated Unity match was removed from the guest's personal history by its exact record GUID.

Run the native harness with a model export and real-position fixtures:

```sh
dotnet run --project Tools/PolicyVerify -- Assets/Resources/AI/shards-policy.bytes /path/to/native-parity.json
```

`Tools/TrainingPreflight/export_game_policy.py` exports a validated checkpoint and optional real-position fixtures from an immutable training-runtime snapshot. It performs no training. The immutable snapshot is necessary because the main game now incorporates HostV10's Reactor cleanup correction, which changes the old checkpoint source identity.

## Balance revisions

This is a versioned model/catalog export. Its semantic embeddings represent the current card effects. After changing card effects, refresh the descriptors and re-export with the same learned effect-network weights, then rerun parity and strength checks. The shipped bytes do not rewrite themselves when C# card definitions change. New card IDs or mechanics additionally require reviewing the observation/catalog contract; retained categorical residuals mean transfer strength must be measured, not assumed.

## Draft and destiny regression checks

`--draft-regression` checks real PolicyEngine first picks and responses to every hero over 32 seeds. HeroDraftPolicy bypasses the old compact-position mode head only for the hero draft, using the 50,000-game seat-specific matchup table. Refresh that table after a gameplay model or balance change. New match identity ends in `-draft2`.

`--destiny-audit <output.json>` runs isolated complete games with balanced hero assignments, testing candidate-order and row-order changes plus bonus-destiny decision option reordering. It writes no monitoring/training statistics. Its numerical permutation check does not assert optimal play or zero residual ordinal influence in bonus choices.
