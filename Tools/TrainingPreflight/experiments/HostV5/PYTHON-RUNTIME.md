# V5 Python continuation and frozen comparisons

The isolated V5 runtime changes the training start population: 75% of seed-derived plans impose an ordered pair of distinct heroes through the two actual legal draft submissions; 25% retain ordinary policy drafting. Setup happens before the first published policy observation. It contributes no PPO rows or behavior probabilities. Model, Adam state, RNG state, archive, objective, full observation/card catalog, rules, and shared training allocation remain unchanged. Coverage improvement is not a strength result.

`variant_v5_runtime.py` validates the exact frozen V4 source/binary, the final V5 binary, the unchanged entire catalog, and the separate `hero-setup-descriptor` result. The explicit training population participates in the new identity. Only `TrainingCurriculumHost` enters the curriculum context. Inline evaluation, external evaluation, and CPU monitoring always launch `serve --hero-setup natural`, with inherited statistics variables removed. Forced/natural training telemetry is separated under `training-statistics/forced-random/` and `training-statistics/natural-draft/`.

The new `migrate_runtime_v5.py` accepts only the pinned V4 identity. Its preview reads one open checkpoint inode on CPU. It preserves the complete original checkpoint payload, including earlier observation/runtime migration provenance, and adds `hero_curriculum_migration`. A canonical typed digest protects every original field except the explicitly changed identity. Publishing a new checkpoint requires the existing inactive campaign ledger lock, reconciled campaign/time allocation, a new destination, and strict target reload. It neither opens a training session nor creates an allocation. No publication into the actual campaign was performed by the preparation tests.

`test_runtime_v5.py` verifies identity rejection, unchanged policy/Adam/RNG/archive/metadata/budget values, temporary synthetic publication and no overwrite, active/locked ledger rejection, actual cross-variant role loading, context cleanup, and real host prefix replay. Seed0's forced prefix is reconstructed with the two ordinary legal draft choices in a natural host; its following five CPU-policy actions produce identical observations. Seed1's natural branch matches directly. Initial published policy counters are zero and become five after five policy submissions. Tests initialize no CUDA context and play no complete policy game.

The new frozen evaluator accepts both strict V4 and V5 checkpoints, including different supported widths, and evaluates both on the same reviewed natural V5 host. Natural mode reuses the existing paired evaluation loop. Balanced mode uses twenty distinct ordered hero-pair cells, disjoint engine-seed blocks, and both policy seat assignments per seed. It imposes only the two legal draft choices, then samples the ordinary frozen policies. The complete plan is inlined in the identity-hashed evaluator. CPU mode reuses the existing balanced-matrix helper after asserting that its plan matches exactly; CUDA mode applies the pinned plan to twenty ordinary `evaluate_match` calls. Reports fingerprint every invoked evaluation helper. The CPU monitoring helpers remain outside training identity; their code hashes are separate evaluation provenance. Eight fake-host tests verify plan equivalence, all hero prefixes, original action slots, paired allocation, unknown cap outcomes, incomplete-cell reporting, trace routing, and cleanup. CUDA execution and real-game strength remain for the coordinator's separate validation.

Every balanced cell has an output-specific `setup.json`; censored action traces contain the policy suffix. Reconstruct the initial natural host at the recorded seed, impose the recorded policy heroes in seat1 then seat0 order according to the trace's `policy_a_seat`, then replay the suffix. V5 training cap traces instead use the seed-derived curriculum setup and the host's `hero-setup-replay/` metadata. These are different evaluation/training populations and must not be interchanged.

Examples from the repository root (execution starts only when explicitly invoked):

```sh
# Both policies retain their own strict saved training identities.
python Tools/TrainingPreflight/experiments/evaluate_variants.py \
  --a V5_RUN/latest.soicp --b V4_RUN/latest.soicp \
  --mode natural --device cuda --games 4096 --batch 128 --workers 4 \
  --seed 0x6400000000000000 --sampling-seed 98391 --max-seconds 3600 \
  --output NEW_NATURAL_REPORT.json

# 20 cells × 100 disjoint paired seeds × 2 seats = 4,000 games.
python Tools/TrainingPreflight/experiments/evaluate_variants.py \
  --a V5_RUN/latest.soicp --b V4_RUN/latest.soicp \
  --mode balanced --device cuda --pairs-per-cell 100 --batch 100 --workers 4 \
  --seed 0x6300000000000000 --sampling-seed 98311 --max-seconds 3600 \
  --output NEW_BALANCED_REPORT.json

# CPU-only correctness; temporary synthetic checkpoint/ledger fixtures only.
PYTHONDONTWRITEBYTECODE=1 DOTNET_PROCESSOR_COUNT=1 OMP_NUM_THREADS=1 MKL_NUM_THREADS=1 \
OPENBLAS_NUM_THREADS=1 PYTHONPATH=Tools/TrainingPreflight:Tools/TrainingPreflight/experiments \
  python -m unittest test_runtime_v5 test_evaluate_variants -v
```

Balanced mode uses `--pairs-per-cell`, not `--games`. CPU mode fixes both Torch and host execution to one worker; device kernels and RNG differ from CUDA, so score series should be labeled. `--max-seconds` is cooperative between host replies; an external process-group timeout is still needed to bound a stalled pipe or device call. Outputs must be new paths. Every report retains both training identities, checkpoint/policy hashes, actual evaluation host/setup, device, paired uncertainty and censored outcomes.

The unpinned CPU monitor helpers additionally accept `--variant v5` and bind the strict V5 loader while retaining natural setup. A single-variant monitor's anchor must have the same strict runtime identity; the cross-variant evaluator is the separate comparison tool.
