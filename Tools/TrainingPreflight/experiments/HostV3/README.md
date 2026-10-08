# Isolated own-Allegiance observation extension

This variant adds seven **own** authorized faction counts and no other features.
It links unchanged Adapter, MappedBody, SelfTest, Core, Shards engine and content
sources. Its separate `Encoder.cs` and `Program.cs` retain old observation
meanings and add the schema-v3 feature/catalog metadata. It does not overwrite
or rebuild the live Host project.

Schema: `shards-observation-v3`. Binary:
`bin/Release/net8.0/TrainingHostV3.dll`. Wire framing and tensor dimensions remain
version1 / 2048 observation /64 candidates ×32 features.

| Slot | Name | Faction | Value |
| --- | --- | --- | --- |
|317|own_allegiance_none|None|own rule count /20|
|318|own_allegiance_homodeus|Homodeus|own rule count /20|
|319|own_allegiance_undergrowth|Undergrowth|own rule count /20|
|509|own_allegiance_order|Order|own rule count /20|
|510|own_allegiance_wraethe|Wraethe|own rule count /20|
|511|own_allegiance_aion|Aion|own rule count /20|
|701|own_allegiance_monster|Monster|own rule count /20|

The current catalog contains189 definitions (indices0..188). V2 writes counts
at128+192×channel+definitionIndex, making the selected tails of channels0,1,2
alwayszero. V3 fails initialization for a catalog exceeding189 definitions,
because adding definitions190–192 would collide with the new features.
Scalars, other counts, entity records, staged traces, candidates and masks are
otherwise unchanged. Values are not clipped by the encoder. Counts use
`AllegianceEffect.OwnedCount` for the current pending-input owner; this honors
Prism, Project Yggdrasil and temporary fast-plays, exactly as the rules do.
There is no opponent collection query.

Catalog and startup diagnostics add `maxCardDefinitions:189` and
`extraObservationFeatures`, a seven-element list of slot/name/faction/
normalization/source records. Catalog card order and dimensions are unchanged.

```sh
DOTNET_PROCESSOR_COUNT=1 dotnet build Tools/TrainingPreflight/experiments/HostV3/TrainingHostV3.csproj -c Release -m:1
DOTNET_PROCESSOR_COUNT=1 dotnet Tools/TrainingPreflight/experiments/HostV3/bin/Release/net8.0/TrainingHostV3.dll v3selftest
python Tools/TrainingPreflight/experiments/HostV3/check_differential.py
```

`v3selftest` checks the demonstrated temporary-card alias, exact old-column
agreement across that fixture, both-seat opponent hidden membership/privacy,
Prism/Yggdrasil behavior, and catalog overflow rejection. `selftest` separately
runs the original host regression suite in the isolated variant.
`check_differential.py` compares old/new wire observations outside the seven
slots, complete candidate/mask/reward/done/seat bytes, and engine counters on
identical real action sequences through pipe and mapped transports, including
holds and terminal auto-resets. These are correctness checks, not performance
measurements or evidence of improved playing strength.

Do not directly load old weights onto the populated new features: their old
first-layer columns were irrelevant but may contain random nonzero weights.
An explicit checkpoint migration must zero those seven columns and validate
matching optimizer moments, all frozen archive policies, schema/source lineage,
behavior parity, RNG/seed preservation and the shared campaign budget.
