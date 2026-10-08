# Both-player lookahead throughput — 2026-09-27

The matched 40-game benchmark improved from **0.523 to 1.155 games/s (2.21×)**. Every game retained its winner, final round, decision count and override count. Search depth, candidates, worlds, finishing budget, frozen weights and rules stayed unchanged.

## Execution constraints

Headless .NET engine, RTX 5090 batched FP32 policy/value inference, no Unity, no presentation delays, no training. The Python process and its child host inherit affinity to CPU IDs 0,2,4,6,8,10,12,14: one hardware thread on each of eight Ryzen 7 5800X cores. .NET processor count and host workers are also capped at eight. Server GC remains enabled.

## Matched measurements

All rows below use the same 40 games, batch 40 and eight workers. These paired-seed timing games are **not balance statistics**.

| Implementation | Seconds | Games/s | Finishing-search seconds |
|---|---:|---:|---:|
| Previous implementation | 76.48 | 0.523 | 56.17 |
| Typed copies + linear beam pruning | 48.04 | 0.833 | 29.67 |
| Reused identity tables | 42.46 | 0.942 | 25.48 |
| Skip identical beam retries | 42.45 | 0.942 | 25.24 |
| Compiled remaining field access | 40.83 | 0.980 | 23.16 |
| Cached exact action prefixes | 34.62 | 1.155 | 18.07 |

A separate 160-game/batch-160 probe reached **1.351 games/s before the prefix cache**. Its first 40 game records exactly matched the 40-game cohort. The final statistics run uses batch 160 with the prefix cache; its measured throughput is reported separately once complete. Timings exclude CUDA graph setup, which manifests record separately. Small differences between adjacent rows are not independent statistical estimates of each optimization's benefit.

## Changes

- Typed card/container copying avoids repeated reflection and boxed array operations. Mutable objects retain reference identity inside each cloned graph; list versions are preserved for paused enumerators.
- Per-thread identity tables reuse capacity and release all object references after each synchronous copy.
- Linear pruning preserves stable score ties and the existing first-move diversity rule without sorting/allocating dictionaries per child.
- A wider beam is skipped only when the narrower attempt pruned nothing: its identical node budget would revisit exactly the same prefix.
- Narrow/wide attempts reuse immutable states for **identical action prefixes**. Different sequences are never merged using incomplete state hashes. Logical expansion order and budgets are unchanged.
- The optional compiled field copier is headless-only. The portable game implementation retains reflection for remaining fields.
- The statistics collector is owned by the real-game host. Hypothetical rollouts never generate statistics. Unique seeds and a separately shuffled balanced hero schedule replace timing mode's intentionally duplicated seeds.

## Verification

- 258/258 engine tests passed without Unity.
- 15,289 transition comparisons over 40 complete games, 128 tactical positions and 23 decision contexts passed against the frozen pre-optimization copier. Observations, public memory, legal actions and source preservation were checked, including compiled copies.
- All 4,608 tactical scenario traces and outcomes exactly matched the prior accepted implementation: **4,602 successes**, with the same six sampled Rez Star Seeker threshold failures. These optimizations do not fix those existing policy/search limitations.
- Native/GPU prediction parity remained within 1e-4; observed errors were much smaller.
- A 40-unique-game statistics smoke test passed full outcome/hero-pair/strategy reconciliation, with no censored or discarded games and no unattributed wins.
- The portable netstandard2.1 game AI assembly builds successfully. This task does not replace the running game's installed DLL.

## Requested statistics cohort

Run directory: `/home/lva/.local/share/shards-training/2026-09-27/lookahead-statistics-5000`.

The run uses generation 19062, identical all-turn lookahead on both seats, 5,000 unique seeds starting at 8793210000000000000, and exactly 250 games per ordered distinct-hero pair. Full card/relic/destiny acquisition data updates every 100 completed games. Strategy, milestone, mode-use and exclusive win-cause evidence is reconciled at completion. The monitored campaign displays only this cohort; benchmark and previous evaluation games are excluded.

Frozen exported policy SHA-256: `f15c9d40644f6ef7e84ebf44d325ec4d6f3bae771e3f6ac7f7904a52baf4a177`.

Status: **stopped at the user’s revised target; exactly 1,400 completed games finalized**. The next progress checkpoint held 1,409 completions; nine extras and in-flight games were excluded. Original execution time was 935.7 seconds (about 1.506 completed games/s, including the nine extras). The retained cohort and full strategy/victory reconciliation are in `lookahead-statistics-1400`. No training updates.

The requested manual review subsequently found existing decision-quality defects, including premature end-turn overrides from the eight-decision horizon and AI concessions. The throughput changes preserve those behaviors; matching old decisions was not proof of good decisions. See [manual review](manual-game-review-2026-09-27.md).

Machine-readable timing and verification artifacts: `lookahead-throughput-2026-09-27/` beside this report.
