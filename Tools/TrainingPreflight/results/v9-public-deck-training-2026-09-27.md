# Public opponent composition and reroll investigation

The user clarified that the opponent's entire permanent collection is public. V8 did not represent that exact collection. A constructed pair with an Undergrowth opponent and an Order opponent produced identical complete observations and identical policy probabilities. This was a real information omission: training could not learn different responses to those two public positions. Earlier privacy tests that changed the opponent's composition had enforced the wrong contract. [Alias fixture](reroll-composition-alias-v8.json), [reroll audit](reroll-denial-v8-2026-09-27.md).

V9 adds exact current opponent card counts, faction/Allegiance counts and collection summaries. Purchases, kept fast-plays, free permanent recruits and banishment update the composition immediately. Temporary fast-plays count for Allegiance, as intended, but not permanent ownership. Opponent hand membership, draw-pile membership and order remain private. Public collection-sensitive defense is also corrected. [Information contract and tests](v9-public-deck-contract-2026-09-27.md).

Rerolling remains optional. It can deny a valuable opponent card, improve one's own market, waste gems, or expose a better replacement. A blanket faction-denial rule would confuse these cases. The frozen generation8672 audit found3,061rerolls in160games, costing3,405gems. Of these,1,866had no later own purchase or fast-play that turn, but that does not distinguish successful denial from unsuccessful shopping. The faction-affinity proxy did not establish systematic opponent denial. Opponent mastery was already visible and affected decisions. Raw frequency is not a strategic-quality metric.

The new representation lets the policy use exact opponent composition for rerolls, purchases and other decisions. It supplies public facts without supplying the opponent's current hand. A new projection starts with zero additional columns so migration preserves every learned weight region and Adam moment. All19retained policies, RNG, counters and the original12-hour budget survive migration from generation8762. Saved older policies keep zero timing prior. [Migration verification](v9-public-deck-migration.json).

V9 also retains the tested soft timing intervention: a1.5logit penalty for12exactly identified conditional exhausts whose public prerequisite is currently false. It is applied before exploration in both actor and PPO likelihood calculations. It removes no legal action and the learned score can overcome it. The earlier640game frozen ablation reduced observed same-turn missed gates from180to115, with a51.875%score and a wide44.28–59.47%bound. This supports a modest timing aid, not a claim of optimal hand sequencing. [Ablation](soft-timing-v8-2026-09-26.json).

## Validation before training adoption

- 2,903collection checks,1,544information checks, retained replay/worker/engine checks.
- 13,715reached-state privacy probes across64completegames and25contexts;33,536hand/draw swaps preserved the exact public collection. All4,928inputfloats remained identical.10,175fresh legal menus were also invariant. This is substantial tested coverage, not a proof over all possible game states. [Privacy sweep](v9-public-composition-privacy-2026-09-27.json).
- Six CPU policy contracts include zero-column preservation, nonzero learning gradients from the added public inputs, legal support, timing specificity and mixture ordering.
- 72GPUforward/backward comparisons: maximum log-probability error1.526e-5 and derivative error3.815e-6. [GPU parity](v9-public-deck-cuda-parity.json).
- 256complete frozenGPUgames, zero censors; maximum behavior likelihood error3.600e-5 and value error2.559e-6. No policy/checkpoint/ledger mutation. [Collector test](v9-public-deck-gpu-smoke.json).
- Interleaved forward microbenchmarks: usual batch256time is0.3%higher thanV8; batch2048is4.4%higher. This measures only captured inference, not full training. [Benchmark](v9-public-deck-gpu-microbench.json).
- Monitor recognizes the new trainer and host;22monitor tests and17watcher tests pass.

The checkpoint metadata field `zero_padded_columns` records each appended region’s starting column (512 for the information projection and640 for the mode head); each receives256new zero columns.

The updated prospective [adoption plan](v9-public-deck-adoption-gates.json) supersedes the earlier soft-timing-only plan because the public information contract changed. Training/evaluation outcomes are recorded in separate pilot and regression artifacts below after completion.

## Limits

Public composition is necessary for informed denial but does not establish good denial play. After training, sensitivity to composition is only a learnability check. A causal tactical evaluation needs fixed public positions, alternative legal reroll targets, and paired continuations averaged over hidden allocations consistent with the public information; it must not optimize using the actual hidden hand.

Remembered opponent hand reveals, complete nested-effect progress and unbounded public histories remain outside the current finite feed-forward representation. The user-visible game's deck window still uses a viewer-only full-deck snapshot; the clarified public-information rule is implemented here for training. No claim is made that every remaining information gap or strategic error is solved.

## Pilot result

The charged180second session completed49generations (8762→8811),12,544games and6,169accepted optimizer steps, with zero censored games and one guarded minibatch rejection. All model/Adam tensors remained finite. Maximum actor/PPO log-probability mismatch was7.391e-5 and value mismatch2.190e-6. Median generation time was2.915s versus2.827s in the precedingV8window (+3.1%); collected learning rows/s were28,689 versus29,883. These adjacent windows have different trajectories and are not an isolated performance experiment. [Pilot summary](v9-public-deck-pilot-summary.json).

An independent exact fixture confirms that the new opponent compositions differ only in the appended public inputs; private allocation/order perturbations remain invisible. The migrated source initially produces identical policy outputs across those compositions, as required by zero-column preservation. After the pilot, those compositions produce different value estimates and reroll target probabilities: conditional reroll-distribution total variation is0.00719. The new public features are being learned and used. This small constructed fixture does not establish that the direction is strategically correct. [Response audit](reroll-public-response-v9-2026-09-27.json).

## Adoption

The640game balanced screen completed with zero censors and scored46.09375%,95%paired bound38.50–53.69%. It passed the predeclared45%point-score coarse rejection floor, but establishes neither superiority nor noninferiority; the lower point estimate merits continued monitoring. [Balanced result](v9-public-deck-balanced-regression.json).

V9 continued from generation8811 in `main-v9`, with3.802hours remaining in the original12-hour ledger. The decision retains a demonstrated public-information correction plus the separately tested soft timing aid. All older checkpoints remain available. Frozen CPU champion/anchor comparisons and statistics are scheduled every100,000training games; the first cycle starts after300seconds. The final4096game champion and initial-policy evaluations run outside the training budget. The dashboard and card statistics remain at `http://localhost:8768/` and `/statistics`. [Launch record](v9-adoption.json).
