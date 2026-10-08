# Isolated benchmark diagnostics

This retained diagnostic harness compares the packaged incumbent implementation
with the sparse implementation used by `../MatchupOptimizedHost`. It is not a
training host and must not replace the packaged game policy.

Build with `dotnet build -c Release`. Run the resulting DLL with
`inference-perf <frozen-incumbent-directory>` or
`search-parity <frozen-incumbent-directory>` (see `Program.cs` for dispatch).
The first checks bitwise probabilities and values on actual public game states;
the second checks searched actions, resulting engine hashes, submission counts,
and search branch counts. Both run the original and proposed implementation.

October 6 results are retained alongside the active benchmark as
`inference-perf.json` and `search-parity.json`, referenced by `performance.json`.
Full searched decisions measured 2.07 times faster in the controlled comparison.
That figure is not a claim about whole-benchmark throughput; use completed
32-game cohort timings for that measurement.
