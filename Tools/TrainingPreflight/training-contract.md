# Contract for the 12-hour learning campaign

## Schema-v3 continuation amendment, 2026-09-26

The explicit [variant entry](experiments/variant_entry.py) composes the reviewed
v2 trainer with a separately built [v3 host](experiments/HostV3/README.md).
Its identity is `shards-real-selfplay-v3`. All original rules, legal choices,
episode/censor semantics, shared-budget enforcement and numerical rejection
thresholds below remain in force. The runtime hash additionally pins every
executed helper, execution profile, new host sources/binary and audit wrapper.
Original v2 sources and binary remain available for v2 checkpoint verification.

Seven reserved zero columns (317, 318, 319, 509, 510, 511, 701) now encode the
deciding player's exact Allegiance counts, divided by20. Temporary fast-played
cards retain their intended rule behavior. No opponent hidden-collection
predicate is exposed. The catalog is guarded at189 definitions to prevent new
card IDs colliding with these reserved columns. All other observation fields,
candidates, masks, rewards and transport framing remain unchanged.

The reviewed migration zeroes only the first-layer weights for those seven
formerly zero inputs in learner, champion, initial policy and all archives.
Their Adam moments were verified already zero and are preserved, along with
all other parameters, optimizer steps, RNG, seeds, counters and checkpoint
budget. It publishes a new file under an explicit source/target manifest and
requires an inactive, exclusively locked existing ledger. No identity check is
bypassed and no training time is refunded. CPU function tests and a256-game
GPU differential check established exact pre-update behavior at IEEE FP32.

V3 uses IEEE FP32 matmuls for acting, verification and learning. This follows a
v2 stop at generation1996: selected-log-probability mismatch0.01294 exceeded the
existing0.01 guard. New frozen tests measured maximum IEEE error0.000031;
the exact failed batch was not retained. The guard remains unchanged.
The last valid v2 checkpoint is generation1977; uncheckpointed work was
accounted as discarded and its elapsed training time remains charged.

The selected performance profile uses exact contiguous owned copies and
equivalent input validation. Queued dual-policy inference is tested but remains
disabled because its incremental timing gain was inconsistent. Frozen paired
cohorts measured a12.6% collection-plus-verification gain from the selected
copy/validation combination against the IEEE baseline; this excludes optimizer
time and does not by itself establish playing strength.

Optional CPU shadow evaluations retain frozen snapshots, use separate paired
seeds, and report CPU precision and hero/seat coverage explicitly. They never
update weights or the training ledger, and stop with the trainer. Their
decision-weighted value errors and conditional hero usage are diagnostic;
they do not constitute causal card/hero balance estimates.

Updated 2026-09-26. **The real trainer is implemented.** Reviewed revision 2 uses identity `shards-real-selfplay-v2`, current 1v1 all-DLC Duel rules, complete per-seat outcomes from retained terminal episodes, matching behavior/learner probabilities, guarded PPO, a frozen opponent archive, atomic recovery and one supervised 12-hour allocation. Host-reported capped episodes are explicitly censored under the persistent guard below; no rule or host limit changes. The earlier preflight remains a separate set of bounded systems experiments with fabricated optimizer targets. No deleted AI implementation or results are prerequisites. Readiness tests and throughput probes do not establish strongest play, a solved meta, or multi-hour stability; pilot and frozen evaluation outcomes must supply that evidence.

## What is established, and what remains

| Area | Implemented contract | Remaining evidence/limits |
| --- | --- | --- |
| Rules and actions | Pinned current engine/catalog/host; exact priority actions, staged subsets/orders, integer allocation and paging; no dropped legal choices | More tactical and rare-card coverage; factorization affects the learning problem |
| Observation | Authorized schema v2, hidden-zone checks, count channels, candidate zone/temporary status and bounded public entity records | Explicit omissions below; no recurrent memory or sufficient-statistic claim |
| Learning | Owned completed episodes, deciding-seat terminal credit, same behavior/learner distribution, actual stored actions and PPO updates; all capped-lane rows excluded | Conditional-on-completion selection bias, learning strength and sustained behavior must be measured |
| Operations | Shared locked ledger, monotonic deadlines, atomic finite checkpoints, strict identities, external supervisor and read-only monitor | Tests cannot prove absence of every crash or long-run resource issue |
| Evaluation/balance | Frozen seat-swapped matches, initial/champion roles, legal-opportunity/selection telemetry and automated final audits | A wider payoff matrix, matchup coverage and causal balance interventions remain necessary |

Exact action coverage and complete information are different properties. The wrapper can express every supported choice while the encoder deliberately merges strategically different states. GPU utilization and optimizer example-passes do not establish either playing strength or unbiased balance conclusions. See [the executable interface](Host/CONTRACT.md) and [benchmark methodology](methodology.md).

## Observation schema v2 and remaining ablations

The [host contract](Host/CONTRACT.md) and [Encoder.cs](Host/Encoder.cs) define `obs[2048]`, `candidates[64,32]` and a binary 64-slot mask. The binary header remains version 1, but the observation semantics are **shards-observation-v2**. Matching dimensions do not make historical v1 fixtures or weights compatible. Preserve the pending-input owner's visibility boundary; hidden opposing cards, deck order and RNG are not available to the learner.

| Field | Current implementation | Remaining limitation |
| --- | --- | --- |
| Candidate identity/status | Exact card-ID feature16; visible zone, `FastPlayed`, `BanishAtCleanup`, ready/damage fields, effective cost and authorized defense/public shield | Integer choices retain low/high fields instead; hero choices reuse field31. Opposing hidden-condition-dependent defense remains omitted unless explicitly announced to the current player |
| Public entities | Up to 24 six-value records for champions, monsters, destinies and play zones, with definition, relative owner, exhausted, damage, defense and shield; total/omitted counts expose overflow | Records beyond 24 and per-entity attack-pending flags are omitted; aggregate counts and every legal candidate remain available |
| Current decision | Authorized context/title hashes, numeric title cues, candidate details and the last 16 staged choices plus aggregate selections | No collision-free resolving-source/mode schema; longer staging loses older ordered details |
| Defense | Authorized visible fields and incoming face-damage cues | Deferred champion-hit allocations are not fully represented; Testudo/taunt/shield planning remains an information ablation |
| Private knowledge/history | Current authorized decision and own unordered permanent collection | No persistent Scry/reorder/reveal memory, full event history or banished-card identities |

Candidate card identity stays categorical through the learning model's transform. Numeric observations and noncategorical candidate features are clipped to ±1000 and transformed with signed `log1p`; this compresses large encoded values and explicitly merges values beyond that bound. Candidate kind bits and card ID are unchanged. The value head is bounded by `tanh`. These design choices improve numerical conditioning; they do not prove the compressed state contains all strategically relevant information. Schema/source hashes prevent silently mixing these ablations across checkpoints.

## Outcome and credit assignment

[learning_rollout.py](learning_rollout.py) uses terminal utilities `+1` win, `-1` loss and `0` genuine rule-defined draw. Rows belong to the **pending-input owner's seat**, including defense during the other seat's turn. Their episode lane and seat are stored with a generation-level behavior version. Both current-policy seats can contribute; frozen-opponent decisions cannot. Values are never negated merely because another wrapper action or engine submission occurred.

Retained complete episodes use **gamma = 1, lambda = 1**. Every retained decision for seat `s` receives `G = terminal_utility[s]`; raw advantage is `G-V_old`, then normalized across retained completed rows. Entire censored trajectories are removed first and receive no target. This is the finite-episode Monte Carlo form of the GAE target and avoids attenuating terminal credit because allocation or paging added wrapper substeps. It trades simpler undiscounted credit for higher variance; it does not guarantee better learning. Earlier research's `lambda = 0.95` was a tentative alternative, not the implemented setting. [GAE paper](https://arxiv.org/abs/1506.02438).

The current trainer fixes split branching at eight and clips **individual wrapper subchoices**, not joint multi-choice decision probabilities. It reports wrapper/engine counts and action-kind histograms; it does not yet persist a separate logical-decision ID or entropy by choice family. Per-substep entropy, factor-wise clipping and minibatch weighting remain sensitive to factorization even with gamma/lambda one. Shuffled epochs retain all rows; uniformly sampled extra rows fill a final fixed-size minibatch and count toward reported example-passes/sample reuse.

Paging still exposes NEXT_PAGE alongside 63 candidates and preserves all choices. The real policy gives page a fixed −1 prior, but its entropy term includes every legal choice. Scoring all candidate blocks under one normalization, or excluding navigation-specific entropy incentives, are future algorithm changes requiring separate identity and validation; neither is silently assumed here.

## GPU rollout ownership and lifetime

Historical overwriting GPU rings are synthetic storage/scheduling probes. The real `EpisodeStore` is different: it owns observations, candidates, masks, executed actions, behavior log probabilities/values, episode lanes and seats until the whole generation resolves. It never wraps over unfinished rows. Capacity exhaustion stops collection; unresolved data is not turned into a fabricated training target.

One collection freezes both behavior and selected opponent weights. Opcode 5 holds terminal or capped lanes at their next episode's initial observation until the other attempts resolve. Capped-lane rows are removed as whole episodes before sealing. Every retained row must have true terminal credit; learner sampling cannot access unsealed rows, and another generation cannot overwrite a batch while updates are consuming it. Collection, storage and learning are synchronous on the current CUDA stream. No actor/learner overlap or asynchronous storage-lifetime guarantee is claimed.

Adaptive actors pack only selected authorized lanes into buckets, repeat one valid row for padding, and expose GPU views cropped to actual selected rows. The store preserves the original episode mapping and action slots; padding and frozen opponents never enter experience. Actor outputs are copied before graph replay or host-buffer reuse. Behavior likelihoods remain immutable across epochs, and the selected frozen opponent version is recorded with generation metrics. A model/storage snapshot does not serialize partially finished C# iterator state.

## Implemented PPO correctness gates

1. **Owned behavior records.** Observations, masks, actual sampled subchoices, old log probabilities/values and episode/seat metadata are copied before their producer buffers are reused. Unresolved storage never wraps.
2. **One probability definition.** `LearningPolicy` is shared by actor, learner and evaluator. Its fixed kind-logit prior is play +1, focus +0.5, end −1, concede −12, page −1 and other kinds zero; no legal choice is removed. There is no exercise `logits*0.02` path. Every generation recomputes all retained selected probabilities and values before updates; current TF32 acceptance thresholds are absolute log-probability error ≤0.01 and value error ≤0.005. Short real-engine readiness probes observed a much smaller maximum log-probability error, `4.77e-7`.
3. **Frozen collection.** Actor clones refresh only between completed generations; updates cannot mutate the acting or archived parameters. PPO consumes stored old probabilities throughout each epoch. There is no asynchronous stale-policy replay. [PPO paper](https://arxiv.org/abs/1707.06347).
4. **Fixed match opponents.** A configured fraction of lanes uses one frozen archive/champion selection for the whole match; other lanes use current-policy self-play. The current default archive fraction is 0.25, learner seats alternate across lanes/generations, and opponent versions are logged. Archive retention preserves initial/recent policies and spreads older versions across history. This is a small league, not a comprehensive strength baseline.
5. **Ownership tests.** Model, adaptive, credit and collector tests cover consecutive/defending decisions, terminal ownership, held reset lanes, stored likelihoods, buffer reuse, original slots and frozen-opponent exclusion. These focused traces do not establish exhaustive card coverage.
6. **Guarded updates.** Stored masks/actions, categorical IDs, targets, logits, values and log probabilities must be finite and valid. Defaults stop the remaining update epochs if absolute raw log-ratio exceeds 10 or approximate KL exceeds 0.03. Clipped policy/value losses use FP32 reductions; defaults are policy/value clip 0.2, entropy coefficient 0.01, value coefficient 0.5 and maximum gradient norm 0.5. A finite stale-ratio/KL rejection leaves weights and Adam state unchanged. Nonfinite objective/gradient/state is a failure requiring a known-good checkpoint, not a sanitized continuation.

The optional captured-backward path computes speculative derivatives using `exp(clamp(raw_log_ratio,-20,20))`, then checks its compact report before the **eager fused AdamW** update. With the accepted default bound ±10, that clamp is exactly inactive in both forward values and derivatives. Invalid raw actions use safe indices only to prevent a device assertion; their validity flag still rejects the batch. Rejected gradients never reach Adam and are overwritten on the next replay. Accepted gradients/metrics/updates and rejected model/Adam immutability were compared with eager execution. A second captured graph only reads parameter/optimizer finiteness after the update; capture warmup performs no dummy optimizer steps.

The [matched synthetic-target comparison](results/learning-ppo-capture-comparison-h128.json) measured width128/batch2048 at 14.36 ms eager versus 1.65 ms captured, and [width256](results/learning-ppo-capture-comparison-h256.json) at 15.41 versus 1.68 ms. These 8.72×/9.18× gains remove dispatch overhead; they are not fresh-data rates or outcome-learning results. [Adaptive subset inference](results/adaptive-actor-paired.json) improved host256/selected32 by 1.90× and selected64 by 1.50× including packing, while full256 added about 6% overhead. The [README readiness table](README.md#readiness-evidence-and-recent-optimizations) distinguishes these measurements from the test inventory and budgeted pilots.

The original sustained synthetic optimizer load exposed this exact failure: a selected log-probability difference of `+100.99609375` overflowed FP32 `exp`, producing infinite loss, a NaN gradient norm and corrupted parameters. The inputs, selected actions, logits, values and returns were finite before that update. Earlier updates already had nonfinite gradient norms despite finite losses and parameters. Replaying the saved model/optimizer snapshot eagerly reproduced the failure, and bounded replacement returns on the same snapshot did not prevent it. This is evidence for ratio and pre-step gradient gates, not a graph-specific defect or proof that target bounding alone repairs the workload. The revised disposable workload uses its own legal argmax and fixed fabricated returns to control the reference ratio; substituting learner-chosen actions for collected actions would invalidate real PPO. Do not silently sanitize nonfinite values or change the ratio objective and report the result as the same algorithm. [Failure record](results/soak-learner-failure.json), [exact-snapshot diagnosis](results/learner-diagnosis.json).

## Truncation, auto-reset and revision-2 censoring

The wire protocol returns a new episode's observation immediately after `done=1` or `done=2`, while reward/done refer to the old episode. The collector consumes that old episode's terminal utility before holding its reset lane. It never bootstraps from a reset observation. Administrative action/round/paging limits are **not draws or losses**. [Termination versus truncation](https://gymnasium.farama.org/tutorials/gymnasium_basics/handling_time_limits/).

Revision 1 failed on the first host administrative truncation. The first budgeted width128 pilot hit that gate and retained an earlier valid checkpoint, but its exact trigger was not captured. It was **not reproduced** by 5,376 resumed-training games or a 15,360-game targeted-seed frozen probe. A separate deterministic legal always-end-turn [fixture](results/round-cap-fixture.json), seed9026, reached the round guard after 802 wrapper actions with pre-cap round400 and both health values50. This proves legal nonprogress can reach a host limit; it does not identify the original learned failure's threshold or cause. Both differ from the earlier synthetic ratio-overflow diagnosis.

A later revision-2 pilot produced a separately captured learned cap. Its [exact CPU replay](results/learned-cap-replay.json) accepted all 3713 recorded actions, reached no earlier terminal/cap, and matched the round400 pre-cap scalars and selected candidate. Volos retained only Limiter Drones/Unknown God and Tetra only Cinder Scars, without gem generators; health remained 50/26 from round42 until the cap. This records an undertrained policy's degenerate resource composition, not evidence that those cards are weak or that a meta has been discovered. The revised collector excluded that episode and continued correctly. The original revision-1 incident remains unclassified. An independent [real-engine lifecycle check](results/censor-lifecycle.json) completed one game and censored another, retaining three genuine-outcome rows and removing all 802 capped-episode rows.

The reviewed revision-2 trainer selects `censor_truncated=True`; the standalone collector/evaluator API retains a strict default of false. On host `done=2`, it saves the complete seed/action prefix and diagnostics, holds the lane, and drains the remaining attempts. It then removes **all learning rows for every capped lane** before returns, normalization, likelihood verification or updates. It does not keep earlier apparently useful turns of a capped game. Other completed games retain their actual outcomes. No capped game receives a win/loss/draw, zero return, synthetic advantage or reset-observation bootstrap. Collector wrapper-step exhaustion, invalid rewards/counters and storage exhaustion remain failures.

Collector metrics distinguish `attempted_games`, `completed_games`, `censored_games`, `attempted_learning_rows`, `retained_learning_rows` and `censored_learning_rows`; `learning_rows` means retained rows. Censor reports include original lane, behavior version, engine seed, deciding/learning seat, archive assignment, length, pre-step round/scalars, last candidate/action and the full action prefix. Full traces live in the run's `censored-episodes` directory, with paths and bounded summaries in metrics. The keyed `censored_episodes` reports are authoritative; parallel lane/seat/archive arrays use sorted lane order. Retained action-kind histograms exclude censored rows. Archive metrics expose attempted/completed/censored counts and unknown-outcome bounds; `archive_score` is explicitly completed-only.

An all-censored cohort is drained but not an update batch: `completed=True`, `update_ready=False`, unsealed store. Budget/signal stops still discard and account for the entire interrupted collection. Censoring changes the training sample distribution to conditional-on-completion and can bias learning; diagnostics and a fixed low censor ceiling make that loss visible, not unbiased.

Before any optimizer update, the trainer durably calls `CampaignBudget.record_collection(attempted_games, completed_games, censored_games, learning_rows, censored_rows, details)`. Attempted games must equal true completed plus censored games. It then stops when `recent_censored >= 4` in the conservative trailing **4096 finalized attempts**. Before 4096 attempts exist, the window includes all finalized attempts so far. The oldest batch overlapping the boundary is included whole, so a window can exceed 4096 and never understates its included censor count. The guard persists across pilots/resume and is not reset by a model checkpoint. A cap seen in a collection later discarded at deadline remains in its trace/discarded-collection metrics but is outside this finalized-cohort window; no row from that discarded cohort is trained. Crash/deadline discard accounting remains separate.

The ledger's `collection_accounting` stores lifetime finalized attempts/completions/censors, retained/excluded rows, collection count, introduction timestamp and recent batches. Old ledgers migrate these new counters from zero at `started_wall`; earlier untracked game totals are not inferred. All prior charge/discard history remains. Revision 2 starts fresh pinned pilots rather than overriding old identity checks; the **313.0874 seconds charged before revision 2** remain consumed. The [readiness inventory](README.md#readiness-evidence-and-recent-optimizations) records 91 tests verified incrementally: changed revision-2 units passed, with earlier passing model/adaptive evidence retained. It does not claim a fresh combined-suite rerun or long-run stability.

## Identity, recovery and the training clock

[train_campaign.py](train_campaign.py) pins the complete training configuration, observation schema, ordered catalog hash, Core/Shards rules source hash, built host hash and selected model/rollout/evaluation/persistence source hash in `identity.json`. A run directory with a different identity is rejected. Resume requires the exact identity and the same campaign allocation; documentation/monitor display changes are not model revisions, while hashed implementation changes are.

[campaign_state.py](campaign_state.py) writes an owned finite CPU snapshot of learner/Adam state, archive/champion/initial weights, RNG and counters into one checksummed `.soicp` file. It fsyncs, atomically replaces, then fsyncs the directory. Checksum/length, identity, campaign membership, finite tensors and charged-time consistency are checked before recovery. Failure to persist a checkpoint or ledger stops the process. CPU recovery tests verify the next optimizer step and RNG stream; model tests verify optimizer snapshot ownership. The constant learning-rate configuration is checkpointed; no separate scheduler is currently used.

Checkpoints occur at episode-free boundaries. They do not serialize C# iterators or unfinished trajectories. Recovery starts fresh engine games using the saved next-seed boundary and records uncommitted/discarded experience; it does not claim exact mid-game continuation. Loading an older checkpoint never refunds work performed since that checkpoint. Failed sessions are not automatically retried.

The single ledger is `/home/lva/.local/share/shards-training/2026-09-26/budget.json`. A separate exclusive lock prevents concurrent trainers; an existing allocation cannot be enlarged or reset. Every actual outcome-learning pilot, failed run, resumed run and trained challenger consumes the **same 43,200 seconds**. Inline collection, updates, validation and checkpoint work are charged by session monotonic elapsed time. Systems-only inference/profiling and discarded optimizer probes with fabricated targets remain preparation.

Normal shutdown records actual elapsed time, including any overrun as an error. For an unclean session on the same boot, recovery charges through the recovery time, including uncertain downtime, up to its original grant. A changed boot or inconsistent monotonic clock consumes the full outstanding grant. Reported unresolved experience is recorded as discarded, with unknown unreported tails marked; time is never refunded. A checkpoint cannot replace the authoritative ledger.

## Supervisor, monitor and frozen evaluation

[supervise_training.py](supervise_training.py) runs outside the trainer's Python/CUDA call path. It watches startup, heartbeats and the published hard deadline, first signals the trainer to close/checkpoint, and kills only its owned process group if it cannot exit in time. Defaults are a 180-second startup timeout, a 120-second heartbeat timeout and at most 20 seconds of termination grace, bounded by the hard deadline. It cleans owned descendants after exit and never automatically retries a failure.

[monitor_training.py](monitor_training.py) serves [localhost:8768](http://127.0.0.1:8768), reads campaign logs and persists resource samples every three seconds. It has no training-control endpoint or external upload. It displays budget, generation, numerical, evaluation, process and checkpoint status, including post-training audit reports, censor rates and unresolved score intervals, and provides allowlisted trace downloads. Alerts expose failures, missing/stale processes, stale heartbeats/checkpoints and rejected minibatches; active-run alerts are separated from collapsed historical incidents and do not automatically repair training. Display-series sampling does not discard raw downloaded JSONL records. GPU memory is driver-global; CPU/RAM metrics cover WSL rather than the entire Windows machine.

Use [run_campaign_with_audit.py](run_campaign_with_audit.py) for the main launch. It wraps the supervisor and, only after successful training exit, requests two frozen 4096-game audits: final learner versus saved champion and versus initial policy. Each swaps seats on held-out seed pairs beginning in the `0x4000...` namespace, uses independent sampling seeds and collects legal candidate/selection telemetry. [evaluate_checkpoints.py](evaluate_checkpoints.py) verifies checkpoint/current identities, loads frozen roles, makes no optimizer update and opens no training-ledger session. These final audits are outside the charged training budget; inline champion evaluations during training remain charged.

Each automatic audit has a one-hour process limit and owned-process cleanup. `post-evaluation.json` distinguishes waiting/running/completed audit state from training state; audit failures retain trained checkpoints and already completed reports, and trigger no retry. The confidence bound is conservative over paired seeds. Initial/champion comparisons are useful checks, not an exhaustive policy payoff matrix. Further hero/matchup/tactical coverage and causal interventions are required before balance conclusions; candidate opportunity and selection counts describe policy usage, not intrinsic card strength.

Revision-2 frozen evaluations preserve all attempted games in the denominator. With `N` planned games, `C` capped games and resolved score sum `S` (win1, draw0.5, loss0), `score_identification_interval=[S/N,(S+C)/N]`. `score_bound_95` expands those endpoints by the conservative Hoeffding margin over `N/2` planned seat-swapped seed pairs, clipped to `[0,1]`. A censored outcome is unknown, never a midpoint/draw. `score_a` is null whenever `C>0`; `score_a_resolved_only` is explicitly conditional on completion and is null if none resolved. Counts include `resolved_games`, `censored_games` and `all_terminal`; a completed evaluation means all attempts were resolved or censored, not that all games terminated naturally. Opportunity/selection telemetry includes censored trajectories and states that scope. Champion promotion uses the conservative lower bound, not the resolved-only mean.
