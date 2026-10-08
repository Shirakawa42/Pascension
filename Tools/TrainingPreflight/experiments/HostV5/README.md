# Isolated hero-curriculum host

HostV5 keeps the frozen V4 rules, legal-action adapter, observation schema,
encoder and catalog. Its only game-population change is an explicit initial
hero setup, before the first policy observation. It does not modify V4 files or
binaries. This is preparation for a separately identified continuation, not a
claim of improved strength.

`serve` defaults to natural drafting. Training must explicitly launch
`serve --hero-setup curriculum-75-25`. Each actual episode seed determines either
the ordinary draft (probability 25%) or one of all 20 ordered distinct hero
assignments (combined probability 75%). A versioned, independent seed-derived
SplitMix64 stream uses rejection sampling; neither planning nor the two legal
draft submissions consumes engine RNG. Natural evaluation must keep the default
host mode, even when evaluating a checkpoint trained with the curriculum.

The separate `hero-setup-descriptor` command describes this intervention. The
normal `catalog` output remains byte-identical to V4, including observation
schema `shards-observation-v3`. The Python variant identity must include the
setup descriptor and pinned V5 executable sources/binary.

## Learning and replay boundary

Forced drafts submit the two actual original legal candidate slots, first seat1
then seat0. Hero effects and relic setup run normally. The host publishes only
the resulting ordinary starting observation. These two external setup choices
have no actor probability, PPO row or loss. The natural quarter retains ordinary
draft learning; this alone does not guarantee meaningful exploration of a
currently nearly deterministic draft head.

The Adapter's counters and administrative caps include the two actual setup
actions, just as if a policy had chosen them. Wire wrapper/submission counters
count only subsequent policy actions. Initial wire counters remain zero, and
each automatic reset derives its new setup from the unchanged seed formula
`seed_base + lane + episode_index * batch`. Held lanes do not advance.

For any seed, run:

```sh
dotnet TrainingHostV5.dll hero-setup 123 --hero-setup curriculum-75-25
```

This emits `shards-hero-setup-replay-v1`, the mode/seed, actual legal prefix
slots/option IDs/heroes and setup offsets. Capped games emit the same metadata
to stderr with their final Adapter and policy-suffix counters. When training
statistics routing is active, an immutable atomic JSON is also saved under
`D/hero-setup-replay/`; diagnostic write failure leaves gameplay unchanged.
Existing Python
censor traces contain only policy actions: recreate the same V5 mode and seed,
then replay that suffix without applying the setup twice. A natural branch of
the curriculum has zero setup offset and retains its draft in the suffix.

## Statistics populations

The raw schema stays `shards-training-pool-stats-v1`. Statistics requested at
directory `D` are routed to independent outcome pools:

| Directory | `hero_setup.cohort` | Rows included |
| --- | --- | --- |
| `D/natural-draft/` | `natural-draft` | Ordinary draft games only |
| `D/forced-random/` | `forced-random` | Externally assigned hero games only |

Every snapshot includes `hero_setup.schema`, `requested_mode`, `cohort`,
`forced_probability`, `setup_wrapper_steps_per_game` and
`policy_actions_include_setup:false`. Both pools know the actual reset
batch/seed, but only the matching pool receives a lane's actions and outcome.
Completed games, caps, unfinished lanes, card outcomes, hero rows and pairing
histograms are never duplicated or silently pooled. Each pool retains its own
10,000-completion publication cadence and bounded history. A natural-only host
does not create the forced pool. The natural pool will usually publish more
slowly under the 75/25 curriculum.

## Bounded verification

`hero-selftest.json` records actual-engine tests of all 20 ordered pairs,
unchanged engine RNG, exact manual-prefix equivalence, four legal suffix steps
per pair, natural reset preservation, replay goldens and malformed routing
rejection. Synthetic typed-event/outcome fixtures separately verify that
completed, censored and unfinished games enter exactly one statistics pool.

`wire-prefix-check.json` covers pipe/shared transports and fused/nonfused host
loops. Each case compares natural V4/V5 observations and manually drafted
V4/curriculum V5 observations bit-for-bit, exercises four partial-lane suffix
steps and exact reset, and uses one legal concession solely to test automatic
reset seed/setup and response counters. These are prefix and lifecycle fixtures;
no full policy matches, training updates, ledger operations or CUDA workloads
were run.

The equal-time quality experiment and predeclared balanced/natural evaluation
criteria are owned by the root protocol in
`results/hero-curriculum-protocol-2026-09-26.json`. Coverage and strength are
separate outcomes. Runtime/migration validation and exclusive GPU trials remain
additional readiness requirements.
