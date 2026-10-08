# Fresh training host, binary version 1 / observation schema v2

This is a profiling/correctness adapter, not a trained policy. It compiles current
Core + Shards rules directly, uses Duel (which enables all DLC), two seats, the
real hero draft and real `Submit`. It does not load earlier AI code or data.

## Process interface

Run `dotnet TrainingHost.dll serve`. Standard output is binary only; diagnostics
go to standard error. All integers and floats are little endian. Commands:

* `uint32 1, uint32 n, uint32 workers, uint64 seed`: reset n resident games.
* `uint32 2, int32 action_index[n]`: one wrapper action per resident game.
* `uint32 3`: close without a response.
* `uint32 4`: observe again without stepping (transport/encoding experiment).
* `uint32 5, int32 action_index[n]`: selective wrapper step. `-1` holds a lane;
  nonnegative indices behave exactly as opcode 2. Values below `-1` are invalid.

Opcode 5 preserves the batch shape. A held lane keeps its existing observation,
candidates, mask, actor seat and engine state; reward/done are cleared. It does
not simulate or re-encode that lane, and increments no action/outcome counter.
This permits a complete-episode collector to hold a terminal lane at its fresh
auto-reset state while the remaining episodes finish. The client owns episode
IDs and must consume the terminal response before holding the new episode.
An all-held command is equivalent to observe for payload and cumulative counters.
Opcode 2 still rejects negative indices.

Optional `SHARDS_SHARED_BUFFER=/dev/shm/path`: the parent creates and truncates a
file to the exact payload byte count before reset. The host maps it read/write,
writes the identical body there, then publishes header+trailer on stdout,
omitting the pipe body. Header lengths are unchanged; the caller knows this
transport from its launch configuration. One synchronous outstanding command
owns the buffer; the parent must finish reading before sending the next command.
Startup stderr reports the actual GC mode, split branching, transport,
`selectiveStepOpcode: 5` and `observationSchema: "shards-observation-v2"`.
The `catalog` command also emits this observation schema identifier. Pin it
alongside the ordered card mapping and host binary hash before learning. The
wire header remains version 1; equal tensor dimensions do not imply observation
compatibility with historical v1 benchmark fixtures or model weights.
`SHARDS_SHARED_COPY=span` selects a bounded Span copy into the mapped view;
`accessor` (the initial implementation, still the host default for comparison)
uses `MemoryMappedViewAccessor.WriteArray`. `SHARDS_PROFILE_PUBLICATION=1`
emits a second stderr JSON object on clean close containing mapped-copy and
pipe-write times. These timers include warm-up responses and should not be
subtracted directly from a separately timed steady-state interval.
`dotnet TrainingHost.dll copybench` measures both mapped-copy implementations
on identical 4.26 MB buffers, verifies all bytes, and removes its temporary file.

Every response has eight uint32 header words:
`[0x534F4931, 1, n, 2048, 64, 32, payload_bytes, 64]`.
The body, in this exact order, contains contiguous arrays:

| Array | Type/shape |
| --- | --- |
| observation | float32[n,2048] |
| candidates | float32[n,64,32] |
| legal mask | float32[n,64], 1 means legal |
| terminal reward | float32[n,2], seat-indexed win +1 / loss -1 / draw 0 |
| done | int32[n]: 0 ongoing, 1 terminal, 2 administrative truncation |
| next actor seat | int32[n] |

The 64-byte trailer is eight float64 values:
`[batch_step_ms, batch_encode_ms, accumulated_wrapper_steps,
accumulated_engine_submissions, accumulated_completed_games,
accumulated_truncated_games, total_logged_events_in_resident_games,
managed_heap_bytes]`.
Step and encode timings are wall time, including worker scheduling, excluding
the output write. Optional `SHARDS_FUSED_SERVE=1` combines each game's step,
terminal/reset handling, and encoding in one worker pass. It also accumulates
submission/outcome counters in task-local values before merging them. The
initialization and observe-only paths retain their ordinary encoding pass.
Startup stderr explicitly reports `fusedServe` and `stepTiming`. For opcodes 2/5
in fused mode, `batch_step_ms` measures the combined step/reset/encode wall time
and `batch_encode_ms` is zero; comparing step/encode individually across modes
is invalid. Sum them to compare total environment service time. This scheduling
experiment is opt-in; the default remains the original two-pass serve path. Observe reports zero step time and clears reward/done. Reset
reports zero counters. Auto-reset occurs immediately after terminal/truncation;
the returned observation and actor belong to the new episode, while reward/done
describe the old episode. A learner must preserve its prior episode/seat state.
Truncation is not a draw. The preflight caps 20,000 submitted engine actions or
400 rounds; wrapper count additionally caps at 100,000 to detect paging loops.

## Action adapter

Every advertised priority action, including concede, is retained. Ordinary
decisions are exact staged subset/permutation selection: disabled options are
excluded, no repeats, FINISH appears once Min is met, Max submits automatically.
No engine state changes until the complete answer is submitted. Single-choice
decisions submit immediately. Damage splits process targets in source order,
choosing each integer amount by interval subdivision. `SHARDS_SPLIT_BRANCHES`
selects 2..64 branches (default 2). This preserves
every allocation from zero through remaining power, including overkill and
wasted allocations; the final target receives the remaining amount if Min
requires it. The engine remains authoritative for taunt and shield effects.

More than 64 candidates are paged into 63 slots plus NEXT_PAGE. Pages cycle;
no option is dropped. Wrapper actions are not equivalent to engine submissions,
and these counts must always be reported separately. Rejected index or engine
submission is fatal, never replaced with a safe default.

## Observation boundary and intentional compression

The actor is always the current pending-input owner, which can differ from the
turn owner. The dedicated encoder reads authorized fields directly instead of
constructing a presentation snapshot. It does not expose RNG state, center deck
contents/order, opponent hand/deck identities, raw card instance IDs, hidden
condition probes, or other-seat pending choices. Own permanent deck composition
is an unordered count, matching the existing snapshot's FullDeck permission.
Decision DefIds are revealed to their owner by the engine's decision contract.

Card indices are alphabetical definition IDs, zero-based, with 192 reserved
slots. The current catalog must fit; overflow fails loudly. `catalog` prints the
mapping. Scalars occupy [0,128); count channels occupy 128+192*channel:
0 own hand, 1 own permanent owned collection, 2 own discard, 3 own face-up
play/champions/destinies, 4 opposing discard, 5 opposing face-up cards, 6 market,
7 shared destiny/monster spaces, 8 staged selected cards. Counts are divided by
10. Scalars [62] and [63] report total and omitted public entity records /64.
[1856,2000) contains up to 24 public entity records, six values each:
definition index+1 /192, relative owner (-1 shared, 0 self, 1 opponent), exhausted,
marked damage /50, authorized defense /50, public shield value /20. Ordering is
own champions, opposing champions, active monsters, own destinies, opposing
destinies, own play zone, opposing play zone; each uses its existing ordered
public list. Champions take priority when the 24-record capacity overflows.
Overflow compresses observation only: full aggregate counts and all legal
candidates remain present.

[2000,2048) contains the last 16 staged choices as triplets
(definition index+1, public option ordinal+1, amount), divided by (192,128,1000).
Longer staging retains aggregate counts and the last 16 choices; action coverage
is not truncated. The compact observation intentionally omits full event
history, banished-card identities, public entity records beyond the capacity,
per-entity monster attack-pending flags, short Scry/reorder memory, deferred
champion-hit allocations during defense, and some identity-free mode text. This is an
explicit partial-observation benchmark, not a claim of a sufficient statistic.

Candidate features: [0,16) one-hot kind (play,buy,fastplay,focus,exhaust,monster,
destiny,relic,reroll,hero,end,concede,select,finish,page,integer); [16] definition
index+1 /192; [17] faction/6; [18] type/6; [19] printed cost/13; [20] authorized defense/50;
[21] public shield/20; [22] exhausted; [23] marked damage/50; [24] effective cost/13;
[25] public option ordinal/128 or row slot/6; [26] amount/1000; [27] Required;
[28] owner relative to actor (-1 unknown, 0 self, 1 opponent). For integer
choices (kind 15), [29] and [30] retain interval low/high /1000. Other candidates
use [29] `(ShardsZone+1)/16` for a visible card (0 unavailable), and [30]
`FastPlayed`. [31] is hero index+1 /5 for a hero choice, otherwise
`BanishAtCleanup` for a visible card. Revealed definition-only options have
printed defense/shield, zero zone and zero temporary flags.

Authorized defense means effective defense for an owned visible champion;
printed defense for an opposing champion unless the current deciding player's
`soi.split` option explicitly announces its remaining defense, in which case
that amount plus marked damage is reused. The encoder never calls the
opponent's hidden-collection-dependent Ferrata defense condition. This is an
intentional information omission outside an announced split. Public shield
values use the current registered engine function, whose modifiers inspect only
public mastery, destinies and shield-doubling state; pin/re-audit this assumption
if those functions change. Shared/unowned cards retain printed values.
Padding is all zero. The definitive scalar layout is `Encoder.EncodeScalars`.
Scalars [112,116) contain the context FNV-1a hash as four bytes /255;
[120,124) similarly identify the authorized current decision title. [124,128)
contain its first four unsigned integer substrings /1000 (including damage and
cost cues). Current title templates were inspected: they contain public names
and numeric requirements, never raw instance IDs. This is a compact identity
hint, not a natural-language representation or a collision-free semantic schema.

## Reproduction

`dotnet build Tools/TrainingPreflight/Host/TrainingHost.csproj -c Release`

`dotnet TrainingHost.dll selftest` runs bounded replay/parallel and observation
privacy checks. `benchmark GAMES WORKERS MODE SEED` uses the fresh exercise
schedule: MODE is engine, encode, or snapshot. This is not a strength baseline.
All modes traverse the same staged decisions and use the same action schedule.
Changing split branching changes the exercise policy's sampling distribution;
compare throughput and wrapper/submission ratios, not outcome strength, between
these configurations. Its coverage is exact for every branch count.

`python3 Tools/TrainingPreflight/Host/check_transport.py` compares 65 real
reset/step response batches between pipe and mapped transport byte for byte.

`python3 Tools/TrainingPreflight/Host/check_selective_step.py` compares 99 batches
at workers 1/4 across pipe/shared and baseline/fused modes, including opcode-2/5
equivalence, held observations/seats, active-only counters, mixed active/held
lanes, and terminal auto-reset holds. `selftest` additionally verifies that
equal aggregate board status can encode distinct public entities, entity
overflow keeps legal actions, and changing an opponent's hidden Ferrata
threshold does not alter its unauthorized public observation.
