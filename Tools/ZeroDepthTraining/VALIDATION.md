# Historical prepared campaign validation — 2026-10-02, stress follow-up

This report describes the earlier width-256 preparation. The subsequent selected
width-512 training campaign is documented separately in
[the launch and action validation report](results/launch-512-validation.md).

The earlier zero-depth campaign was prepared and validated. **Outcome learning
had not started at this audit.** Its active preparation was
`/home/lva/.local/share/shards-zero-depth/2026-10-02/prepared-stress-audit`.
Its source, observation schema v2, configuration, host and incumbent bundle were
verified again after the visibility, ownership and recovery fixes. There is no campaign budget ledger,
training session, learned checkpoint, supervisor state or optimizer update.
The earlier `prepared` and `prepared-visibility-audit` directories remain
historical preparations; their source pins differ and they should not be launched.
The [machine-readable summary](results/stress-audit-final-summary.json) records
the final pins, counts, parity checks and zero-training isolation evidence.

Defaults are 128 lanes, eight CPU workers, captured active-lane GPU buckets,
lossless packed float32 input transport, GPU-owned experience and fused CUDA
Adam. Compiled actor inference is validated and optional (`compiled_actor=false`
by default). The 131,072-row rollout requires about 13.51 GiB. CPU rules, legal
menus and encoding remain authoritative; every decision has zero search depth.

## Verified behavior

| Check | Final result |
|---|---|
| Host and existing engine rebuilds | Zero warnings/errors. |
| Existing engine tests | 278 passed, zero failures/skips. |
| Python suites | 75 passed: 11 learning tests and 64 transport, actor, optimizer, host, recovery, orchestration and evaluation tests. The separate shared persistence suite also passed all 27 checks. |
| Real-engine visibility | 14 focused reveal groups, eight broad zone groups, 4,140 zone-oracle states, plus the original 1,166 states/four naturally completed games. |
| Lossless GPU transport | Exact float32 bit equality across 32/64/128 buckets, changing table extents, padding, signed zero and selected lanes; 120 additional whole-tensor comparisons on 40 real host batches. |
| Compiled actor | Eager likelihood/value parity, nonzero refreshed weights, semantic-cache refresh, stable graph addresses and raw GPU rollout ownership pass. |
| Fused optimizer | Normal-rate parameter/moment parity, finite state, ordinary CPU fallback, likelihood guards and state restoration followed by a bit-identical next fabricated update pass. |
| Rare-failure replay | Both new GPU cutoff traces reproduce exactly: 2,424×128 and 4,095×128 vectors, each with 127 natural finishes and one living game at round 401. Earlier recorded engine-defect traces passed the previous visibility audit. No utilities are invented. |
| Adversarial CPU games | 1,560 natural completions, zero censors; 596,637 real decision boundaries, 35,808 buffer comparisons and 9,684 hidden permutations across every hero and DLC mask. Remembered facts and tensor rows pass independent oracles. |
| Frozen GPU games | Four diversified fixed policies: 4,094 natural finishes / two administrative cutoffs / zero unresolved. Default initial policy: 1,024 natural finishes / zero cutoffs. All consumed input-bit and eager likelihood checks pass. |
| Complete GPU-owned rollouts | Repeat the default 1,024 games with 131,072-row owned storage: 490,943 retained decisions, zero cutoffs, exact same action/reward digests, largest cohort 64,041 rows. All frozen inference-bucket weights stay unchanged. |
| Automation parity | 128 games per setting; identical final-state/outcome digest and 46,251 real submissions. Exactly 308 of 51,765 wrapper choices were forced (0.595%). |
| Incumbent parity | Six decisions, four deployed searches, 168 branches and seven shared-engine submissions match the production helper. |
| Paired evaluation pipeline | Two natural held-out games with learner seats swapped; actual incumbent search; zero caps/missing games. Untrained learner lost both; validation cannot demonstrate strength. |

The paired check used a separate temporary untrained artifact and validation
ledger: zero sessions, zero charged seconds, zero optimizer steps. The prepared
campaign contains no checkpoint or ledger. Its
[report](results/stress-paired-validation.json) and
[predeclared plan](results/stress-paired-validation.plan.json) explicitly record
`strength_evidence=false`, `stronger_than_current=false`, and `pipeline_validated`.
The [isolation record](results/stress-paired-validation-isolation.json) confirms
zero charged seconds, zero sessions and zero outcome-learning updates.

## Additional bugs and optimizations

The new failure tests exposed shared-memory/descriptor leaks on startup, blocked
command writes and cleanup, a stale inherited incumbent setting, omitted
partial-cohort/held-terminal discard counts, and duplicate discard accounting
after a logging failure. Each case now has a regression. Behavior verification
also rejected neither NaN log probabilities nor NaN values reliably; device
maxima now retain NaNs for the final finite guard. Removing verifier readbacks
exposed an asynchronous pinned-staging race in the optional CPU-to-CUDA fallback;
an H2D completion event now protects source reuse.

GPU rollout append copies packed learner prefixes directly into owned buffers,
or gathers reordered rows directly into their destinations. A real-size test
reduced peak temporary allocation from 12,583,936 bytes to below 65,536 bytes,
with bit-exact inputs and independent storage. The isolated movement benchmark
measured 0.1752→0.0477 ms for 128 contiguous rows (about 73% lower).
Generation-wide PPO targets/indices are uploaded once; verification reduces
errors on-device, and unused pinned staging is lazy. A controlled 60,000-row
fabricated-target comparison measured 597.1→570.8 ms median for verification plus
one PPO epoch (about 4.4% lower, with overlapping variation). This is not a
whole-campaign speed estimate. The normal GPU path avoids 113,332,224 bytes of
unused pinned staging. All probability, finite-state, KL and gradient guards remain.

The six-configuration worker/batch sweep finished 2,688 games naturally with
matching worker-independent digests. Its 256-lane cohorts used up to 124,604
decisions before archive filtering. The small collection gain does not justify
reducing rollout headroom, so the prepared default stays 128 lanes/eight workers.
The [stress follow-up](../TrainingPreflight/results/zero-depth-stress-followup-2026-10-02.md)
records raw reports, reproduction commands, bounded cutoffs and remaining limits.

One GPU probe also exercises a disposable optimizer on real packed/compiled
inputs with **fabricated** targets: behavior log-probability error `1.19e-7`,
value error zero, finite weights/moments, and unchanged frozen original policy.
Those updates were discarded and never entered a campaign or comparison model.

## Information coverage

The three requested examples now have actual-engine tensor regressions:

| Situation | Verified information |
|---|---|
| Duplication Fabricator shows personal deck tops | Public identities and exact positions survive unrelated actions and turns. Draw/movement consumes or shifts the fact; a shuffle clears order. A known opposing draw becomes a known hand fact. |
| Three center tops are seen | The viewing seat retains the prefix across turns. A normal paid/free refill consumes one and leaves the other two at positions zero/one. Monster bypass can consume additional cards. Private looks/reorders reveal no hidden answer to the other seat. |
| Opponent full deck and public zones | Complete unordered permanent collection counts, separate public discard/play/champion/destiny zones, banished owner/status, market/monsters/shared destinies, inferred set-aside relics, and known hand identities reach the policy. Unknown hand/draw allocation and unseen order stay private. |

The deeper review fixed public refill identity retirement, private range
refinement, same-definition known-hand removal, Longshot return identity/order,
monster reward sources after their public return to the center bottom, and
repeated Legion windows. Legion release metadata now retires temporary records
chronologically before reshuffling, including partial repeats and a champion
selected into hand. This prevents duplicate entities, false temporary zones and
collection overcounts. Card amounts, gameplay order and RNG calls are unchanged.

The [follow-up audit](../TrainingPreflight/results/zero-depth-visibility-followup-2026-10-02.md)
links each regression and event contract. Broader checks perturb every public
player primitive for both relative seats, rule constants, mutable card statuses,
70 ordered monsters, 330 pending options, deferred sources and Doom Gate floods.
Whole observations, candidates and masks remain invariant under unauthorized
hidden-order/allocation/RNG permutations. Canonical order never sorts live lists;
values are not clipped and table overflow fails instead of truncating.

No remaining concrete omission among the audited current public fields was
identified. The representation summarizes public history into current state and
remembered facts; uncertain position ranges do not encode every joint historical
correlation. Arbitrary effect-iterator locals outside annotated public copy/replay
scopes are not a complete continuation graph. These actual boundaries are
explicit in the catalog and evaluation plan; this audit does not claim a formal
proof of every deduction from unlimited history. The
[original field inventory](../TrainingPreflight/results/zero-depth-observation-audit-2026-10-02.md)
remains available as historical context.

## Earlier visibility performance measurements

These historical measurements used host SHA beginning `9ad06881`; the new host
only adds stress oracles/commands, with no rules/encoder/knowledge behavior changes
in this hunting pass. These are bounded frozen-policy collection measurements on the RTX 5090/Ryzen
5800X, with 128 lanes, eight workers, CUDA graphs and singleton automation.
Startup, learning and incumbent evaluation are excluded from collection time.
They are not a forecast for a multi-million-game learned campaign.

| Transport/forward | Natural games | Collection seconds | Games/s |
|---|---:|---:|---:|
| Dense/eager graph | 896 | 20.39 | 43.95 |
| Packed/eager graph | 1,024 | 22.08 | 46.39 |
| Packed/compiled graph | 1,024 | 22.25 | 46.02 |
| Packed/eager graph, repeat | 896 | 20.52 | 43.66 |
| Dense/eager graph, repeat | 896 | 21.21 | 42.24 |

All five comparisons have identical wrapper-action, terminal-reward and decision
count digests for every shared seed cohort. The two dense measurements aggregate
to 43.08 games/s; the two packed measurements to 45.07 games/s (about 4.6%
higher). Small samples and system variation limit precision. Packed uploads are
**73.7% smaller** than the same dense bucket payload while preserving input bits.
An isolated retained-input upload plus expansion was about 0.18 ms versus about
0.55 ms for dense upload. Compiler fusion roughly halves isolated forward/sampling
time, but its whole-game improvement was inconsistent, so it remains optional.
Setup was about 1.2–1.4 seconds ordinarily and 3.9 seconds with cached compiler
artifacts; the separate cold compiler probe took 6.17 seconds.

The sixth probe includes disposable optimizer validation before collection.
Its extra sampling call changes subsequent trajectories, so its 1,024 games at
47.58 games/s are excluded from the matched-workload comparison above.
[Raw final-host reports](results/) contain exact configuration, source/binary/
catalog hashes, cohort digests, resource samples and GPU/CPU timing scopes.
The [CUDA investigation](../TrainingPreflight/results/zero-depth-cuda-followup-2026-10-02.md)
records precision rejection, kernel/fusion tests, fused Adam measurements and
remaining research opportunities. A full GPU rules-engine port is a separate
implementation/equivalence project; it has not been measured or implemented.

Fused Adam warm isolated GPU steps measured 0.720→0.266 ms at 128 rows and
0.441→0.268 ms at 1,024 rows on disposable fabricated targets. These are optimizer
step costs, not overall training rates. Strategic automation remains disabled;
only singleton choices are automated. Its repeated paired exercise runs provide
no evidence that broader heuristics would improve this training problem.

## Pinned preparation and eventual comparison

Host SHA-256:
`fb3fd1191865a8a7a328a183fe5b2643b4f23bd8b46205eae2e8ee94d3413aa1`.
Prepared source fingerprint:
`fdc31ec3e8424263a15c6e7b1bf08acb958d79fbec32d197dec7017cb69f3df8`.
Frozen incumbent policy SHA-256:
`2e9d7dc1b3c65d9ac57629abfdbd7e7d0c9cd34467dddfb4e4e0fca795ff286d`.

The [guide](README.md) points to the fresh preparation and explicit launch command.
After successful authorized learning, the launcher evaluates the fixed checkpoint
against the frozen current AI with its deployed hybrid search on 2,048 held-out
seed pairs (4,096 games). Only a complete comparison whose censor-conservative
paired 95% lower score bound exceeds 50% can report a stronger challenger.
Each checkpoint keeps its own report. Unity's packaged AI is not replaced by
this preparation or by evaluation.
