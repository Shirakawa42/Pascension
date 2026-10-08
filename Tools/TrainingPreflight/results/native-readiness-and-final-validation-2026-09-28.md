# Native release readiness and final validation pipeline

The fresh hybrid collection completed **2,000 independent games / 109,677 sampled public observations**. Fits use 1,600 training games and 400 held-out games; no rows from a held-out game enter training. Collection/training parity error is 4.77e-7. Both actor candidates completed eight epochs. Their lower imitation loss is not evidence of improved playing strength; paired matches remain the gate.

The updated AI requires the rebuilt Engine and Content assemblies because it consumes the new public-event metadata. Separate .NET Standard 2.1 projects now build all three against the existing game Core binary. The old single-DLL installer explicitly refuses to install an AI with mismatching rebuilt Engine/Content dependencies, preventing a partial incompatible update. No installation occurred.

The headless native compatibility harness constructs the preserved four-argument ABI, then exercises the explicit candidate profile in synchronous and background modes. A full **174-decision game / 168 background searches** passes with identical states after every accepted action and no background mutation of the live source. Under .NET 8 on this machine, background planning averaged **46.75 ms**, median **35.27 ms**, maximum **316.19 ms**. This is one complete game under concurrent evaluation load, not a Unity/Mono performance guarantee. Earlier 64-decision coverage also passed.

`native-candidate-readiness.json` records all three assembly hashes, the Core hash, exact profile and test result. The profile uses the current semantic teacher pending final model selection; no selected final model is implied by this build.

The existing actor pipeline remains running, followed by the already queued fresh-hybrid value fits and paired tests. A final supervisor checks live predecessor ownership and waits without duplicating GPU work. It selects a candidate only if its paired bootstrap lower bound exceeds 50% and its paired sign test is below .05 against the teacher; otherwise it retains the teacher. This rule is conservative and does not establish an exact ranking among statistically indistinguishable models.

The selected model then receives 512 tactical cases, seven recorded natural-position probes across five cohorts, a separate **800-game / 400-pair** comparison against the original-weight C4/D24/W2 hybrid baseline, and 20 new natural games. Both sides share the corrected engine and sampler; it is not an installed-binary-versus-binary benchmark. Seeds are independent of candidate-selection pilots. No automatic deployment or statistics publication is authorized by the supervisor itself; the agent will inspect the evidence and continue the task.

The original `Assets/Resources/AI/shards-policy.bytes` must remain unchanged until the final comparison has snapshotted its reference. Final selected weights/profile and game installation still need completion. All tests so far are headless, with the same eight-core affinity; no Unity tests were run.

Artifacts: `/home/lva/.local/share/shards-training/2026-09-28/ten-hour-improvement`, especially `native-candidate-*`, `fresh-expert-*`, `fresh-hybrid-value-*` and `final-candidate-*`. Ten-hour goal remains active, deadline 09:46:50 UTC.

## Additional public-memory stress audit

`KnowledgeFuzzAudit` checks public hand facts, personal deck-top memory and each seat's known center prefix against an offline truth oracle while a separate seeded random action chooser executes varied legal games. Hidden truth never influences those choices or repairs production memory. Passing checks cover **200 completed games, 90,943 transitions**, 9,380 hand-fact checks, 2,106 personal-top checks and 20,276 center-prefix checks. Every game terminated within the 2,000-action diagnostic cap; none was censored. Public sampling ran 3,027 times with source-state fingerprints preserved. There were zero false facts or unexpected hand invalidations. The action contexts include 2,347 Scry, 1,146 reveal, 410 discard, 388 return and 126 copy decisions. This is additional correctness evidence, not a strength or balance cohort.

The game source now optionally loads `AI/shards-search-settings` as a complete `PolicySearchSettings` object and passes it explicitly to the AI. The resource is intentionally not written until model/profile selection; absence preserves the prior defaults. Native release project files are explicitly exempted from Unity's broad `*.csproj` ignore rule so the reproducible build definitions can be retained.
