# Soft activation timing investigation — 2026-09-26

This is a bounded, evaluation-only intervention on immutable V8 generation **8541**, rather than a change to the running trainer. The candidate subtracts **1.5 logits** from exhaust actions whose precisely identified immediate payout prerequisite is currently false. All original legal actions remain available. The score subtraction precedes the saved relic/destiny exploration mixture, so it does not accidentally change that mixture's declared probability mass. The learned value and all weights remain unchanged.

A 1.5-logit penalty multiplies that action's base-policy odds relative to an unaffected alternative by `exp(-1.5) ≈ 0.2231`. A learned preference can overcome it. This gives the requested encouragement to wait while avoiding a blanket prohibition on strategic sequencing. It adds no HP penalty or shaped outcome reward.

## Source proof and narrow scope

The intervention uses only `immediate_gate(card, observation)` from the earlier audit. It recognizes the following exact predicates; every other exhaust action remains unchanged. Public counters belong to the acting player. No opponent hidden condition is evaluated.

| Active card | False prerequisite penalized | Definition source |
|---|---|---|
| Datic Secrets | Fewer than two Order allies played | [Duel](../../../Assets/Scripts/Shards/Content/ShardsDuelSet.cs#L474) |
| Paradigm Shift | Missing either Order or Wraethe play | [Duel](../../../Assets/Scripts/Shards/Content/ShardsDuelSet.cs#L484) |
| Forged in Flame | Missing either Wraethe or Homodeus play | [Horizon](../../../Assets/Scripts/Shards/Content/ShardsHorizonSet.cs#L455) |
| Biotech Enhancements | Missing either Homodeus or Undergrowth play | [Horizon](../../../Assets/Scripts/Shards/Content/ShardsHorizonSet.cs#L473) |
| The Crystal Gate | Missing either Order or Undergrowth play | [Horizon](../../../Assets/Scripts/Shards/Content/ShardsHorizonSet.cs#L449) |
| True Leader | No faction has at least three plays | [Horizon](../../../Assets/Scripts/Shards/Content/ShardsHorizonSet.cs#L563) |
| Synthesis | Mastery below 15 | [Horizon](../../../Assets/Scripts/Shards/Content/ShardsHorizonSet.cs#L574) |
| Stolen Futures | Mastery below 10 | [Horizon](../../../Assets/Scripts/Shards/Content/ShardsHorizonSet.cs#L531) |
| Primus Pilus | Fewer than three owned champions | [Duel](../../../Assets/Scripts/Shards/Content/ShardsDuelSet.cs#L341) |
| War Bound | Fewer than two owned champions | [Horizon](../../../Assets/Scripts/Shards/Content/ShardsHorizonSet.cs#L558) |
| Strategic Mastermind | Health below 40 | [Horizon](../../../Assets/Scripts/Shards/Content/ShardsHorizonSet.cs#L536) |
| Nature Dominance | Zero Undergrowth plays | [Horizon](../../../Assets/Scripts/Shards/Content/ShardsHorizonSet.cs#L444) |

A true prerequisite does not necessarily guarantee a useful final effect: a recruit target can be absent, drawing can be irrelevant near a win, and healing can be capped. This intervention deliberately makes no claim about those cases. It does not alter flag-setting abilities that must activate before later plays.

The raw-prefix predicate implementation also recognizes the inactive original Datic Secrets and Paradigm Shift definitions; the active all-DLC catalog offers their Duel replacements. The proposed V9 lookup explicitly maps the 12 active definitions to V8's public readiness slots **2064–2075**.

## Independent contracts

[The evaluation script](../experiments/soft_timing_probe.py) copies the frozen V8 forward calculation into an isolated subclass and subtracts the finite penalty from raw scores before the CPU mixture. It does not edit production sources. It checks:

- Exact baseline logit/value parity when penalty is zero, including 64 actual visited rows.
- An independent synthetic probability calculation with a false-gate exhaust and a legal relic, proving the subtraction happens before the mixture.
- An unchanged true-gate row and retained positive support for all legal actions in that contract.
- Unchanged saved policy tensors before/after evaluation and no initialized CUDA context.

An independent comparison of the proposed V9 implementation covered all **12 cards × false/true = 24 synthetic consistent states**, each including a relic mixture. Catalog codes, feature slots, logits and values matched this raw-predicate implementation exactly. Source inspection confirms matching Boolean expressions in the encoder. [Independent review evidence](v9-soft-prior-independent-review.json).

The V9 GPU kernel adds only a constant, public-state-dependent score subtraction. Its derivative with respect to the unadjusted score remains one, so the previous mixture chain rule remains applicable. That is a source-level review; actual GPU parity and performance belong to the separate adoption validation.

## Predeclared evaluation

[The plan](soft-timing-v8-2026-09-26.plan.json) reserves **640 games**, all 20 ordered distinct-hero assignments, 16 paired seeds per assignment, both seats, seed namespace `0x8500000000000000`, sampling seed **850926**. Both policies have the same immutable generation-8541 weights; only policy A receives the soft timing prior. The preliminary screen requires completion, zero censors and point score at least **0.45**. This is a coarse rejection screen, not a formal noninferiority or superiority test.

The first startup attempt failed before any game outcomes because the old forced-panel helper had captured a 2048-feature host constructor in a default argument. V8 correctly rejected its undersized shared-memory buffer. The new script was corrected to pass the explicit V8 host factory. [The aborted startup plan is retained](soft-timing-v8-2026-09-26-startup-aborted.plan.json); no failed-attempt outcomes enter the evaluation. The running trainer was unaffected.

Counts use only active rows and the acting policy. A missed-timing observation requires a false activation followed by a true gate **at a later normal priority state of the same own turn**, while the card remains owned and its exhaust is unavailable. It demonstrates a foregone activation opportunity, not its causal effect on winning. Different policies visit different states, so aggregate action counts are descriptive rather than matched counterfactuals. Ending with a playable hand card is likewise not automatically an error.

## Result

The panel completed all **640 games**, with **zero censors**, in **132.13 seconds**. Soft timing scored **51.875%**, with paired 95% bound **[44.283%, 59.467%]**. It passed the predeclared 45% point-score floor. The interval crosses 50%; this does **not** establish greater strength or formal noninferiority. [Full results](soft-timing-v8-2026-09-26.json).

| Actual visited-action statistic | Soft timing | Unchanged V8 |
|---|---:|---:|
| Acting decisions | 131,083 | 129,774 |
| False-gate exhaust selections | 1,546 | 1,707 |
| False-gate exhaust candidate opportunities | 20,357 | 18,367 |
| True-gate exhaust selections | 1,107 | 1,024 |
| True-gate exhaust candidate opportunities | 7,847 | 7,699 |
| False activation followed by same-turn usable gate while unavailable | **115** | **180** |
| End actions with a legal hand play | 553 / 8,434 | 580 / 8,429 |

The soft prior produced about **36% fewer observed missed-gate sequences** in this panel, with more true-gate activations. Opportunity selection rates were approximately 7.59% versus 9.29% for false prerequisites, but these are different visited-state distributions and cannot be treated as a controlled causal per-action effect. Rare-card differences, such as which policy happened to own Forged in Flame, are especially noisy.

The prior **does not solve timing completely**: 115 missed sequences remain, and many false-gate actions remain selected. That is consistent with its finite, overridable nature and makes a longer learning pilot necessary. The end-with-hand-play counts do not establish a separate improvement because those endings may be strategically valid and this intervention does not target them.

All contracts passed, including exact zero-penalty parity on 64 real rows, preserved tensors and no CUDA initialization. The evidence supports considering a **bounded, separately identified V9 training pilot**, conditional on the parent's GPU numerical/performance checks and budget-preserving migration. No training adoption was performed by this investigation, and the existing V8 trainer remained active.
