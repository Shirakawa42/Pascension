# Exact one-pass Allegiance counter probe

The isolated candidate matches the frozen HostV3 counter results and is worth a future integrated Host benchmark. It was **not installed** in HostV3 or a training runtime. No live source, manifest, or Host DLL was modified/rebuilt. `Probe.csproj` references the existing DLL as a file, copies it into this probe's output, and verifies the original and copied DLL SHA-256 before/after execution:

`472f94ef69e265b1ab03249413275ccc088063dfa5f0fc2d9b57e806025a8520`

`FactionCounter.OnePass` scans Deck, Hand, Discard, PlayZone, and Champions once; queries Project Yggdrasil once; counts integer faction membership; and divides each final count by `20f`. Temporary fast-plays remain included. Prism's faction membership uses a bit mask to prevent double counts. Destinies and SetAside are excluded from owned card counts, and only Yggdrasil in Destinies activates its faction substitution. The candidate receives only the deciding player's object.

Correctness passed all 189 definitions, 2,646 definition/faction/Yggdrasil membership comparisons, 3,780 zone/temporary fixtures, 100 mixed collections, explicit Prism/Yggdrasil/SetAside expectations, 1,177 seeded legal replay rows, and opponent hidden composition/order perturbations for both seats. All seven normalized values match original encoded float bits. Replay checks verify that neither current encoding nor the candidate changes the rules state hash. The current full observation, candidates, and mask also remain identical under opponent hidden perturbations. Arbitrary card/zone combinations are unit fixtures, not claims of game reachability. The latest complete correctness result is embedded in [benchmark-fixed-jit.json](benchmark-fixed-jit.json); [selftest.json](selftest.json) records the earlier test-only pass before adding the explicit SetAside-Yggdrasil check.

Median microseconds per call, nine interleaved samples per method, one low-priority worker while another campaign could run:

| Owned cards | Yggdrasil | Original seven counts | Candidate one pass | Current full encoding | Counter speedup | Estimated encoding time saved |
| --- | --- | ---: | ---: | ---: | ---: | ---: |
| 10 | No | 1.180 | 0.173 | 2.625 | 6.84× | 38.4% |
| 10 | Yes | 1.063 | 0.174 | 2.755 | 6.12× | 32.3% |
| 20 | No | 1.885 | 0.320 | 3.832 | 5.88× | 40.8% |
| 20 | Yes | 2.248 | 0.327 | 3.876 | 6.86× | 49.6% |
| 40 | No | 3.685 | 0.622 | 5.665 | 5.93× | 54.1% |
| 40 | Yes | 3.967 | 0.634 | 5.916 | 6.25× | 56.3% |
| 100 | No | 8.737 | 1.494 | 11.510 | 5.85× | 62.9% |
| 100 | Yes | 9.862 | 1.595 | 13.170 | 6.18× | 62.8% |

All three measured methods allocated zero bytes per call. Full encoding invokes the existing HostV3 `Encoder.Encode` through a once-created typed dynamic-method delegate; there is no per-call reflection or output allocation. Baseline and candidate each return seven normalized floats. Output arrays are retained and consumed.

These are **contended CPU microbenchmarks**, not training throughput results. The last column is `(old count median − new count median) / original encoding median`; a replacement encoder was not built. The full-encoding fixtures have five hand cards, two played cards (one temporary), three discard cards, and the remaining cards in deck, with a repeated Crystal/Fungal Hermit/Shard Abstractor/Prism/Data Heretic mix. They have no owned champions and are not a measured training-state distribution. Timing varies with contention, legal menu, composition, champion conditions, code placement, and JIT behavior. In particular, the 10-card Yggdrasil counter appearing faster than the non-Yggdrasil case is within this noisy design's limits, not a semantic speed claim.

The first [benchmark.json](benchmark.json) used default tiered compilation and showed changing optimization tiers across sizes; retain it as exploratory evidence only. The table uses [benchmark-fixed-jit.json](benchmark-fixed-jit.json), a separate process with tiered compilation and tiered PGO disabled, 10,000 warmup calls per method, calibrated batches of at least 20 ms, and rotating method order. Disabling tiering stabilizes comparisons but differs from the long-running Host's default runtime; a real replacement must be tested under the campaign's settings.

The observed headroom justifies an isolated replacement encoder and byte-identical old/new Host replay comparison, followed by representative whole-encoding and pipeline timing. It does not by itself justify interrupting a campaign or predict a 32–63% training speedup: only part of training is encoding, and the measured fraction depends on fixture choice. Any future binary adoption needs an explicit reviewed identity migration even though observation semantics remain identical.

Reproduction from repository root (no project reference to HostV3):

```sh
DOTNET_PROCESSOR_COUNT=1 nice -n 10 /home/lva/.dotnet/dotnet build Tools/TrainingPreflight/experiments/faction_count_probe/Probe.csproj -c Release --nologo -m:1 -v:q
DOTNET_PROCESSOR_COUNT=1 DOTNET_TieredCompilation=0 DOTNET_TieredPGO=0 SHARDS_SPLIT_BRANCHES=8 nice -n 10 /home/lva/.dotnet/dotnet Tools/TrainingPreflight/experiments/faction_count_probe/bin/Release/net8.0/Probe.dll
```

Append `--test-only` to skip timing. The probe uses no GPU APIs or Python Torch imports.
