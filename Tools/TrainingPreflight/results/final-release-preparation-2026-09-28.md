# Final release preparation

The independent 800-game comparison is running. No new model, runtime settings, build or statistics have been installed/published by this preparation step.

## Evidence reviewed

The selected weights remain `c9d751fc2987199ab77ef4b38facc77cad54278822b14dd8493f672d36eb3102`, the semantic value adaptation on 4,000 games. Neither subsequent actor fit nor subsequent hybrid-value fit established an improvement over it. Both later value pilots scored 155–165 over 320 games.

The final candidate passes 512 tactical cases. Nine recorded natural positions pass explicit checks of the actual default-profile continuation, including Rez before Longshot, resolving a healing line before lethal monster attacks, deploying World Piercer before Unconditional Conscription, retaining Brute while winning, immediate winning End Turn, taking the free Crystal banish, and reaching at least 20 mastery before Infinity Shard. Alternative search settings remain diagnostics, not guarantees. See `final-candidate-natural-regression-assessment.json` in the campaign directory.

The new native game-facing AI assembly passes a complete synchronous/background parity game, 174 decisions and 168 background searches, with no source mutation. Unity was not used for any simulation or tactical test.

## Packaging defect found

Read-only inspection of the currently installed `pascension_Data/data.unity3d` finds **two ResourceManager entries with the exact key `ai/shards-policy`**: one resolves to the model bytes and the other to JSON metadata. The staged release names the metadata `shards-policy-metadata.json`, retaining its existing `.meta` GUID when promoted. Promotion must rename/remove the old metadata path; merely copying the new name would leave the duplicate behind.

`Tools/TrainingPreflight/verify_packaged_ai.py` checks uniqueness and exact SHA-256 bytes for the four required resource paths. It runs without an editor or player. Tested with UnityPy 1.25.3, installed in the separate `/home/lva/.venvs/shards-package-inspection` environment. [UnityPy's documented binary TextAsset decoding](https://github.com/K0lb3/UnityPy#textasset) is used to preserve the original model bytes.

The old package fails this verifier as expected: duplicate model path, no explicit runtime settings or renamed metadata, and stale inference metadata. This does not establish that the duplicate caused the earlier Windows console/focus problem.

## Prepared artifacts

- `prepare_candidate_release.py` stages the selected model, exact full search configuration and truthful adaptation/inference metadata outside Assets. It verifies selected-weight hashes, the 512-case tactical gate and local-build gameplay-source hashes.
- `build-ai-candidate.ps1` packages a complete matching Windows game into a new directory, using one hidden editor process, eight CPU-affinity slots and no Unity tests. It checks model identity, rejects the colliding metadata name and refuses to overwrite an existing output. Windows PowerShell syntax was checked; no build has been launched yet.
- `final-statistics-command.json` prepares 2,500 new frozen games, balanced over the 20 ordered distinct-hero pairs, both players using the selected search profile. Batch 160 matches the previous completed statistics cohort; inference is on GPU with eight CPU cores. It has not been launched.

Current staged resources are under `/home/lva/.local/share/shards-training/2026-09-28/ten-hour-improvement/prepared-release-unique-resources/AI`. The earlier `prepared-release/AI` draft retains the old metadata name and is superseded; do not promote it.

Before completion: inspect the independent comparison and fresh natural games, launch and verify the new statistics cohort, refresh the draft table from that cohort, promote the uniquely named resources, build matching assemblies, verify the packaged resource bytes, and install with a rollback copy. All remain required; preparation alone is not deployment evidence.
