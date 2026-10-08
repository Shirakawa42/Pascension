# Search-free Shards training

This is a separate, fresh 1v1 Duel campaign with all DLC enabled. The learner
chooses one legal action from the current observation. It performs no lookahead,
terminal search, hypothetical game copies, tactical probes, or CPU card scoring.
GPU batches run the policy, value head, sampling, owned float32 rollouts, and PPO.
The selected policy width is 512: exactly 13,746,737 trainable parameters.

The October 6 continuation tests a learned semantic representation of the first
three exactly known center-deck cards, in their current order. It reads the
existing public knowledge table; uncertain positions and unknown identities
produce no embedding. All original raw observations remain available. This
reuses the first layer's unused schema-v2 columns 178–255, preserving parameter
count, parameter order, and Adam state. An immutable anchor makes its initial
contribution zero. Checkpoints store `known_top_anchor` and `known_top_enabled`;
legacy policies load disabled, and archived opponents retain their behavior.
Enabling it is an explicit checkpoint preparation step, never an automatic
resume action. The Scry scorer has not been reset. Correctness and CUDA graph
parity passed; playing-strength improvement requires completed comparisons.

The authoritative C# rules engine, legal menus, and observation encoder still run
on CPU. Preparation and validation do **not** launch outcome learning.
The [historical preparation report](VALIDATION.md) records checks, measured frozen
throughput and information boundaries before the authorized training launch.
The [width-512 launch report](results/launch-512-validation.md) records the current
12-hour run, final action audit, source pins and verified learned checkpoint.
The [follow-up visibility checks](../TrainingPreflight/results/zero-depth-visibility-followup-2026-10-02.md)
cover personal/center reveals across turns, physical known-hand identities,
every zone, and parked monster rewards in observation schema v2.

## Information and decisions

The new observation has 24,576 floats and up to 64 visible action candidates with
48 features each. It includes separate card counts for 24 zones, complete owned
deck compositions, own hand and draw-pile composition, both players' public
resources and turn flags, market slot identities, all public card-instance
statuses, all pending options including disabled and off-page options, ordered
staged selections, played-card order, deferred damage, and known personal/shared
pile positions with remembered public identities. It retains public copied-effect
choices, recursion guards, and active replay scopes through nested decisions.
Publicly revealed opponent-hand cards remain visible in memory.
Identified copies remain distinct when another copy of the same card is played.
Static card effects are supplied as 512-feature semantic descriptors.

Unknown opposing hand contents, unknown draw order, and RNG state are hidden.
Previously revealed information is updated through accepted actions and typed
events. Small additive reveal metadata fixes missing hand/personal-top provenance
without changing card rules. Unobserved private movement produces remembered
position intervals; the representation is a state and fact ledger, rather than
an exact posterior over every possible history. Arbitrary effect-iterator locals
are not exposed. The field inventory and these limits are explicit in `catalog`
and the [observation audit](../TrainingPreflight/results/zero-depth-observation-audit-2026-10-02.md).

Definition IDs have one fixed alphabetical catalog order. Unordered visible
entities and actions are canonicalized in encoded copies; live engine lists are
never sorted. Actual instance links, ordered choices, and revealed pile order
remain distinct. No card/action is removed merely because it shares a definition
with another. Numeric input transforms use signed `log1p` without clipping.
Every fixed table fails on overflow instead of dropping information.

Damage assignment uses binary interval choices and preserves every engine-legal
integer allocation. Larger menus retain every choice through pages, while the
observation already includes all pending options. Training uses
`hero_mode=balanced_random`: all five heroes and all 20 legal ordered matchups
appear once per shuffled cycle. Duel forbids duplicate heroes. The sampler is
deterministic from the game seed, uses randomness independent of the rules RNG,
and resolves both initial draft choices through accepted game actions. Drafting
adds no policy experience; hero powers remain learned decisions. Resets, worker
counts, and batch boundaries cannot change a seed's matchup. Exact balance
applies to whole attempted cycles; discarded or censored games can affect
completed-game frequencies. `hero_mode=policy` retains the original learned draft
for legacy diagnostics and the fixed incumbent evaluation.
Automation is optional and applies only when there is exactly one
wrapper choice. Play order, focus, buys, hero abilities, shields, and concession
remain learned choices whenever alternatives exist.

## Prepare and validate

Use the existing local Python/CUDA environment:

```sh
/home/lva/.dotnet/dotnet build Tools/ZeroDepthTraining/Host -c Release -t:Rebuild --nologo
/home/lva/.dotnet/dotnet Tools/ZeroDepthTraining/Host/bin/Release/net8.0/ZeroDepthHost.dll selftest
/home/lva/.venvs/shards-preflight/bin/python -m unittest discover -s Tools/ZeroDepthTraining -p 'test_learning.py'
/home/lva/.venvs/shards-preflight/bin/python -m unittest discover -s Tools/ZeroDepthTraining/tests
/home/lva/.venvs/shards-preflight/bin/python Tools/ZeroDepthTraining/prepare.py \
  --output /home/lva/.local/share/shards-zero-depth/2026-10-02/training-512 --no-build
```

Preparation creates the configuration, source/binary/catalog identity, observation
catalog, and immutable incumbent policy/settings bundle. It creates no training
ledger, learned checkpoint, or training session. Existing campaign budgets and
Unity AI resources are untouched.

`stress.py` audits bounded frozen cohorts without a learner or optimizer. It
checks consumed GPU input bits, legal actions, eager likelihoods, frozen bucket
weights and deciding-seat outcome ownership. For example:

```sh
/home/lva/.dotnet/dotnet Tools/ZeroDepthTraining/Host/bin/Release/net8.0/ZeroDepthHost.dll \
  stress-selftest 1024 240200 Tools/ZeroDepthTraining/results 8000
/home/lva/.venvs/shards-preflight/bin/python Tools/ZeroDepthTraining/stress.py \
  --games 1024 --seed 2305843009213693952 --policy-seeds 20261002 \
  --rollout-capacity 131072 --output Tools/ZeroDepthTraining/results/frozen-owned-validation.json
```

The C# stress oracle checks actual zone conservation, remembered facts and their
tensor rows, fresh/reused buffers, and hidden-information invariance across all
heroes and DLC masks. `stress-replay CAPTURE.json` reproduces its failures.
GPU stress fails on a censor by default. `--allow-censors` continues a diagnostic
sweep, reports every cutoff separately, and saves exact action traces; it gives
them no learned outcome. These audits are recorded in the
[stress follow-up](../TrainingPreflight/results/zero-depth-stress-followup-2026-10-02.md).

`benchmark.py --help` exposes frozen real-engine throughput sweeps. It first
checks automation on/off for identical final state hashes, winners, and real
submissions. GPU reports distinguish completed, censored, and unresolved games;
a prefix ending at the first completion is not a steady whole-cohort rate. Its
optional optimizer smoke uses a disposable model and explicitly fabricated
targets, then discards it. It is not a training pilot or strength result.
`--packed` uploads only occupied schema-declared table prefixes in float32 and
reconstructs the complete inputs with a custom Triton CUDA kernel. `--compiled`
fuses the frozen actor forward inside its manual CUDA graph. Transport preserves
the raw input bits; fusion is checked against eager action likelihoods and values,
including weight and semantic-cache refresh. The
[CUDA investigation](../TrainingPreflight/results/zero-depth-cuda-followup-2026-10-02.md)
records transfer, cold compilation and completed-game measurements separately.

## Learning and recovery

Each generation freezes behavior weights and drains one real episode per lane.
On archive lanes, only the assigned learner seat contributes experience. Other
lanes learn from both seats, with each decision credited to its actual deciding
seat rather than an assumed alternation. Completed games supply terminal
zero-sum utilities; draws supply zero utility. Capped games are unknown and every
row from them is excluded before normalization or updates. The persistent censor
guard stops learning after four caps in the conservative recent 4,096 attempts.

The training path uses undiscounted TD(lambda) value targets and generalized
advantages with lambda=0.95. Traces follow each lane and deciding seat separately,
including consecutive actions by one seat and archive games with opponent rows
omitted. Final win/loss utilities remain unchanged. `Rollout.seal` defaults to
lambda=1 for Monte Carlo reference tests; the training caller explicitly selects
0.95. Censored trajectories are excluded before computing either estimator.
Bootstrapped value MSE/explained variance cannot be compared directly with older
Monte Carlo-target diagnostics. Reduced advantage variance alone does not prove
playing-strength improvement: use independent paired-game evaluations.

PPO checks all stored behavior likelihoods before updating, uses shuffled
minibatches, entropy, gradient clipping, KL early stopping, and finite
parameter/Adam-state checks.

`archive_strategy="recent"` preserves the original opponent selection: the
initial policy plus the newest snapshots, bounded by `archive_limit`.
`archive_strategy="historical"` uses the same slot limit; with twelve slots it
keeps one fixed anchor, three recent snapshots and eight older snapshots sampled
uniformly by reservoir sampling as they leave the recent slots. On migration
from a legacy checkpoint, the anchor is the current trained policy; the newest
three available opponents become recent and the other eight become the initial
historical pool. Previously discarded opponents cannot be recovered. The general
historical strategy requires at least five slots and reserves `archive_limit-4`
slots for history. Snapshot cadence, opponent-game fraction, model size and PPO
settings stay independently configured.

Recent and historical strategies select one frozen opponent uniformly per cohort.
`archive_strategy="prioritized"` instead favors opponents the learner struggles
against, with a uniform exploration component. With twelve slots, it protects
four historical slots for measured challengers and reserves four for random
history, alongside the anchor and three recent snapshots. An outgoing recent
opponent qualifies for protection after at least 32 decayed effective games,
an estimated learner score below 45%, and a score at least five percentage
points below the easiest protected opponent. Estimates retain the existing
neutral prior and decay; random replacement cannot evict protected slots.
These checks run only when adding snapshots and add no model evaluations.

Checkpointed Python RNG governs both selection and reservoir replacement.
Checkpoints retain the compatible `archive` weight list plus strict
`opponent_archive` metadata for generation IDs, pool membership and the number
of eligible historical snapshots seen. Status and generation logs expose
`archive_pool` ages/counts and `selected_opponent`; these are diagnostics, never
learning inputs. Prioritized logs also expose `protected_historical_ids` and the
retention policy. Archive strength remains a hypothesis until fixed-opponent
evaluation demonstrates improvement.

The actor caches semantic embeddings;
all zone sums share one matrix multiply, and CUDA graphs retain stable buffers
across policy refreshes. Active lanes are packed into pre-captured GPU batches
of 32, 64, or 128 rows as a cohort drains. Finished lanes and archive-opponent
padding never enter the learner's experience. Bucket changes preserve action
probabilities; they need not preserve identical random sampled trajectories.
Lossless table-prefix transport and fused CUDA Adam are enabled in the prepared
configuration. Both have ordinary fallbacks through `packed_inputs` and
`fused_optimizer`. CPU runs keep ordinary transport and Adam; packed CUDA input
requires Triton. Optimizer fusion was compared on disposable fabricated targets,
including parameter/moment parity, finite-state checks and checkpoint recovery.
`compiled_actor` remains optional: the isolated GPU forward is faster, while
bounded whole-game timings do not establish a consistent additional benefit.

Default GPU rollout capacity is 131,072 owned decisions. Memory is checked before
allocation; unresolved experience never wraps or gets overwritten. Reduce batch
or increase capacity if a full cohort exceeds it. Checksummed atomic checkpoints
include model, optimizer, archive, RNG, next seed, configuration, and counters.
Recovery restarts at a saved episode boundary, not inside C# iterators. Source,
rules, host binary, and catalog drift are rejected.

The [eight-core follow-up](results/8core-upgrade-research-2026-10-02.md) replaces
per-step scheduling with eight persistent game workers, gathers only selected
input rows, batches table-count validation, and avoids empty histogram writes.
Checkpoints temporarily use eight CPU threads for finite snapshots and write
header/payload chunks without a large concatenation. All observation values,
action choices and terminal results retain their original meaning and bits.
The complete Python suite passed 109 tests; the production host matched every
publication in a 1,396-step learned-policy replay and passed worker lifecycle,
visibility and action checks.

The current campaign resumed from 50,048 games and 58,544 optimizer updates.
The guarded [runtime migration](performance_upgrade.py) preserves model, Adam,
archive, RNG, game statistics, counters and next seed. It permits only an exact
reviewed execution-source change with the same configuration and catalog, or the
explicitly approved switch from policy drafting to balanced random heroes, keeps
immutable original artifacts, and retains the original campaign allocation and
absolute deadline. Ordinary resume continues to reject source drift.
The [live verification](results/8core-live-runtime-verification-2026-10-02.json)
records continued finite learning and the observed rate including PPO and saves.

Rules-host exceptions and administrative caps save exact engine seeds and action vectors
under `diagnostics/`. Replay runs only the CPU rules engine:

```sh
/home/lva/.venvs/shards-preflight/bin/python Tools/ZeroDepthTraining/replay.py TRACE.npz
```

Use `--allow-drift` explicitly when validating a fix against a newer host or
catalog. A replay reports natural completions, censors, and still-live lanes;
it performs no inference or learning.

## Explicit launch and final comparison

Training starts only with an explicit allocation:

```sh
/home/lva/.venvs/shards-preflight/bin/python Tools/ZeroDepthTraining/launch.py \
  --campaign /home/lva/.local/share/shards-zero-depth/2026-10-02/training-512 \
  --seconds 43200
```

The separate supervisor enforces the allocation and heartbeat, preserves the
latest valid checkpoint, and cleans only its owned processes. `--games` adds a
completed-game target; `--resume` uses the same persistent allocation and the same
`--seconds` value as the original allocation. Training
uses seeds below `2^63`; held-out evaluation uses the high-bit namespace.

After successful learning, the launcher automatically plays 2,048 held-out seed
pairs (4,096 games) against the frozen **current packaged AI with its deployed
hybrid search settings**. The challenger continues to use zero search. Every seed
is played with both learner seats, and evaluation performs no updates.

Each checkpoint gets a separate `post-training-evaluation-<hash>.json` report,
so resuming training preserves earlier comparisons. The result includes
wins/losses/draws/caps by seat, artifact hashes, and a
conservative 95% bound over paired seed scores. Only a complete predeclared
evaluation whose lower bound exceeds 50% can report `stronger_than_current`.
Censors stay unknown; losses are retained. There is no automatic Unity AI
replacement. A one-pair untrained pipeline smoke explicitly sets
`strength_evidence=false` and cannot pass this strength gate.

## Live monitoring and rolling game statistics

The read-only local dashboard shows completed games/s and projected games/hour
from game deltas over the raw monotonic clock in the current boot, including
collection, PPO and checkpoint gaps. Legacy logs use wall time. System-clock
corrections cannot inflate or deflate the current elapsed-time rate. Rates
fall to zero when no games complete. It also displays the allocation, progress,
learning health, checkpoint status and the final incumbent comparison.

```sh
/home/lva/.venvs/shards-preflight/bin/python Tools/ZeroDepthTraining/monitor.py \
  --campaign /home/lva/.local/share/shards-zero-depth/2026-10-02/training-512 \
  --port 8766
```

Open <http://localhost:8766/>. Monitoring runs separately and never accesses CUDA
or loads model weights. Rolling statistics retain the latest 100,000 natural
completions in cohort/lane order, remove older results, and survive checkpoint
recovery. Administrative censors are counted separately. They include game
length, mastery, final collection sizes, hero results, ordered matchup coverage,
and card-presence
associations. Historical-opponent scores distinguish assigned learner seats;
self-play seat balance and card associations do not establish model strength.

`Tools/TrainingWatchdog/hourly_review.py` runs persistent, nonoverlapping background
Codex reviews. The current eight-hour allocation schedules them every **30
minutes**, with a 25-minute limit per active review. Startup and final audits are
read-only. The dashboard shows the active receipt, next scheduled review and
missed-review warnings. The review service restarts after Linux boot under the
same fixed authorization, and a durable STOP cancels its child.

For this allocation, two fresh unsuccessful champion trials and at least 50,000
new games beyond the retained champion/last actual learning intervention trigger
a required diagnostic experiment or tested repair. An unchanged report without
an existing evidence artifact is marked `intervention_incomplete`. The previous
hourly/500,000-game settings remain defaults for older configurations.

Champion challenges now use **3,200 games** while preserving fresh paired seeds,
all 20 legal ordered hero matchups, and the existing alpha-spending rule. The
former 400-game sample was too underpowered to recognize modest gains after many
trials. The current league plan allows a shared **600-second** evaluation window;
champion comparisons use batches of 64. The previous 120-second window truncated
the first expanded test. Evaluation remains bounded by the training owner's
original deadline and stops when that owner exits. The fixed baseline, retained champion, training archive, and final
4,096-game deployed-incumbent comparison keep their separate purposes. No
exploratory champion result automatically replaces the Unity AI.

For an 8,000-game intervention follow-up, use
`Tools/TrainingWatchdog/paired_followup.py:run`. The ordinary arena caps each call
at 4,000 games and 600 seconds. The follow-up composes two such blocks against
the same frozen policies, using disjoint contiguous seed ranges and both seats.
Larger confirmations may predeclare `games` in 4,000-game increments, up to
32,000; they retain the same per-block caps and shared caller deadline.
Evaluation `batch` and `workers` can be specified independently of training
(defaults: 40 and 2). Both are validated before any games, recorded in every
block, and checked against the requested plan. A larger evaluation batch can
reduce inference overhead without changing the training batch or sample count.
It checks both output reservations and seed schedules before playing, forwards
the caller's owner/resource/STOP/deadline guards, and recomputes paired statistics
from all raw outcomes. Missing games, censors, changed policies, incomplete
blocks, or missing initial-state mirror checks cannot produce a complete result.
Reports also compare both policies playing each of the five heroes on mirrored
seeds. The hero-difference intervals use a Bonferroni correction across the five
heroes. These are head-to-head comparisons: swapping models also changes the
opposing policy, so an individual hero's difference does not isolate that
hero's improvement. The combined intervals are exploratory; champion promotion
keeps its own gate.

For a hero-specific progress measurement, run each policy version against the
same frozen opponent with identical engine seeds, seats, sampling seeds and
batching. `Tools/TrainingWatchdog/fixed_opponent_comparison.py` validates those
conditions, checks matching initial-state publications, and recomputes outcomes
from complete balanced arenas. Its declared primary Rez interval and jointly
adjusted five-hero intervals measure changes with the opponent held fixed.
Declare the primary hero before evaluating. Older policy manifests remain
unchanged: compatible execution requires identical inference/engine sources,
verified policy and Host hashes, and the ordinary inference `model.Policy`.
Training-only source compatibility never authorizes an inference or rules change.

Completed checks can be published as dated snapshots on the dashboard without
restarting training. The publisher recomputes both comparisons from their raw
arena blocks and rejects changed summaries, plans or incomplete games:

```bash
python Tools/TrainingWatchdog/progress_panel.py --campaign CAMPAIGN_NAME \
  --fixed FIXED_OPPONENT_CHECK_DIRECTORY --recent RECENT_REFERENCE_CHECK_DIRECTORY \
  --dashboard Tools/ZeroDepthTraining/dashboard.html
```

Either comparison argument may be omitted. Refresh the browser after publication.
Use `--deadline-wall` with the original watchdog Unix deadline to display its cap;
unused charged-time allocation can exceed the time left before that cap.
The panel labels checkpoint counts, confidence intervals and its publication time;
it is a completed snapshot, separate from the live statistics. Hero intervals
crossing zero do not establish improvement. This display never promotes a model.

The resumed learner retains undiscounted same-seat GAE/TD(lambda=.95), the
512-width model, Adam moments, prioritized archive, RNG and rolling game history.
The cancelled depth prototype is inactive and its pilot weights are not used.

The [throughput research](../TrainingPreflight/results/zero-depth-throughput-research-2026-10-02.md)
explains the CPU/GPU boundary, safe automation, and measured historical context.
