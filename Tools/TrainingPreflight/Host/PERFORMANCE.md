# Fresh host measurements, 2026-09-25

These are simulator and interface measurements, not learned-policy strength or
training throughput. The current all-DLC Duel rules were compiled headlessly;
no earlier AI implementation, weights, or training data were used.

The rates below measured observation schema v1. The 2026-09-26 training-support
update adds explicit schema v2 public entity records and candidate status, plus
opcode 5 selective stepping that skips held lanes. Its correctness checks pass
([v2 encoder/replay checks](selftest-v2.json), [selective-step parity](selective-step-selftest.json));
these changes were not timed in the CPU matrices below. Use later integrated
collection measurements for v2 performance rather than assuming the same rates.

## Repeated CPU experiments

Each CPU configuration ran the same 5,000 seeds starting at 17,000, three times,
after 32 excluded warm-up games. Worker counts were 1, 4, 8, and 12; modes were
bare staged adapter and adapter plus compact encoding. The action driver is a
weak exercise schedule. All configurations produced final-state fingerprint
`FA381489D47621A1`, the same action trace counts, and zero administrative caps.
Every matrix also included a separate paired 300-seed snapshot comparison.
Manifests record commands and source fingerprints.

Median **wrapper steps per second**, rounded to the nearest thousand:

| Workers | Original encoder | Value-type candidates only | Final encoder |
| ---: | ---: | ---: | ---: |
| 1 | 245,000 | 233,000 | 280,000 |
| 4 | 593,000 | 558,000 | 684,000 |
| 8 | 691,000 | 679,000 | 839,000 |
| 12 | 671,000 | 684,000 | 885,000 |

1. `Candidate` changed from a heap object to a value type stored in its reused
   list. Allocation fell by about 522 bytes per wrapper step. Timing did not
   improve consistently, so this change is justified by reduced GC pressure,
   not a claimed speedup.
2. Encoding had added about 1,083 bytes per wrapper step. Concrete List/array
   iteration replaced boxed interface enumerators, and explicit visible-card
   loops replaced capturing `Find` delegates. The measured extra encoding
   allocation fell to effectively zero. The final encoded adapter allocates
   about 1,436 bytes per wrapper step, approximately the bare-engine amount.

The best encoded median improved from 691,382 to 885,139 wrapper steps/s
(28.0%), with about 52.8% less allocation than the original encoded adapter.
At a fixed eight workers the improvement was 21.4%. This does **not** imply a
28% training improvement: transport, inference, optimization, and scheduling
are absent from these CPU measurements. Final twelve-worker encoded results
ranged from 825,380 to 889,373/s; eight-worker results ranged from 791,733 to
862,068/s. Short runs and tiered compilation still produce measurable noise.
Use the complete pipeline to select the worker count.

The original paired 300-seed, four-worker comparison measured 244,207 wrapper
steps/s with compact encoding versus 24,758 with presentation snapshots, about
9.9 times faster. Both include simulation, choice selection and initialization;
these are not isolated encoder or snapshot timings.
Presentation snapshots allocated about 186,815 bytes per wrapper step versus
3,045 for the original compact encoder. This smaller paired corpus has a
different duration/state sample than the 5,000-seed main corpus; do not combine
its rates as if they came from the same experiment.

Raw evidence:

* [Original adapter](cpu-baseline/manifest.json)
* [Value-type candidates](cpu-value-candidates/manifest.json)
* [Allocation-free encoding](cpu-low-allocation-encoder/manifest.json)

## Mapped-buffer publication

The first mapped transport used `MemoryMappedViewAccessor.WriteArray<byte>`.
A separate copy experiment held the source, destination, byte count and repeat
count constant: 4,263,936 bytes copied 256 times, three interleaved repetitions.
Every result was checked against every source byte.

| Implementation | Median cached copy rate | Three repetitions |
| --- | ---: | --- |
| Accessor WriteArray | 2.02 GB/s | 1.16, 2.02, 2.11 GB/s |
| Bounded Span.CopyTo | 47.13 GB/s | 21.26, 47.13, 53.18 GB/s |

This is about 23.3 times faster **for this cached memory-copy operation**. The
small buffers can fit in CPU cache; these numbers are not DRAM, PCIe, GPU, or
end-to-end training bandwidth. The optional Span path owns a pointer lease for
the mapped view's lifetime, checks exact lengths, and publishes the response
only after copying. An independent read-only review found no concrete bounds,
lifetime, or protocol issue. [Raw copy results](mapped-copy-benchmark.json).

The final host also reads action indices in one packed buffer and writes packed
header/trailer blocks, preserving the protocol while reducing scalar pipe I/O.
This transport-only change was validated by parity tests; its speed benefit is
left to the parent pipeline benchmark rather than inferred from source code.
`SHARDS_PROFILE_PUBLICATION=1` separately records mapped-copy and pipe-write
time on clean shutdown. The profile includes warm-up responses.

## Correctness and limits

`selftest` passes at split branching factors 2 and 64. It covers 32 seeded full
replays across one/four workers and encoded/bare modes, hidden-zone permutations
for both seats that actually change hidden card identities, all enabled options
in a 130-option paged request, disabled-option rejection, exhaustive small
ordered/optional selections and damage allocations, interval coverage through
1,000 damage, and mapped-copy bounds/disposal failures. The transport test
compares 65 batches of eight resident games byte for byte between pipes and
each mapped-copy implementation.

The final compact observation is intentionally incomplete; [CONTRACT.md](CONTRACT.md)
lists omitted public history, per-entity details, and compressed decision-title
identity. Every engine action and staged choice remains reachable, including
overkill, optional omissions and paging. Wrapper steps are not engine
submissions. No policy learning or strength claim follows from these tests.

No production rule files were changed by this host investigation. An additional
opt-in `SHARDS_FUSED_SERVE=1` experiment now combines step/reset/encoding in one
worker pass and accumulates counters in task-local values. The baseline remains
the default. Four-way baseline/fused × pipe/Span parity passed at one and four
workers with split branching [2](fused-serve-selftest-b2.json) and
[8](fused-serve-selftest-b8.json), each covering 129 responses and terminal resets.
Fused `step_ms` includes encoding and `encode_ms` is zero; only their sum is
comparable across schedules. No speed gain is credited here before the paired
pipeline timing experiment.
