# Frozen V5 Scry memory fixture

CPU-only, no policy inference or training. References the existing HostV5 DLL as an assembly, never builds the host. Uses its internal SelfTest fixture/encoding helpers through reflection and the actual Adapter for all three tested actions.

Run from the repository root:

```sh
/home/lva/.dotnet/dotnet run --project Tools/TrainingPreflight/experiments/rez_scry_memory_probe/Probe.csproj -c Release
```

The original execution built a copy in `/tmp/shards-rez-scry-probe` with an absolute DLL HintPath. Recorded evidence is `../../results/rez-scry-memory-alias-fixture-2026-09-26.json`; it identifies the frozen DLL hash. Exit status is nonzero if the tested post-Scry alias fails, the revealed menu inputs do not differ, or the same reroll candidate fails to expose distinct known top cards.

Fixture scope is deliberately narrow: same-seed, card-conserving constructed starting states, followed by actual legal actions. It does not claim complete seed-to-position reachability or quantify the strength cost of the omission.
