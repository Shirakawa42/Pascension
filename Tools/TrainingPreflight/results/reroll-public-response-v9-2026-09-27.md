# Public opponent collection: independent V9 representation and response check

This check isolates the previously reproduced reroll composition alias. It uses a separately compiled probe referencing the published HostV9 assembly; no pinned V9 source, binary, training process, or GPU was modified.

## Representation and privacy

The paired fixtures keep the market, acting player's state, heroes, resources, and opponent hand/deck counts fixed. Only the opponent's publicly known three-card ownership changes between an Undergrowth collection and an Order collection. These are constructed sparse positions, not full seeded legal replays or claims about optimal rerolls.

V9 now changes **11 observation floats**, exclusively within the appended public-information region **2560–2815**. The entire original **2,560-float prefix** and all candidate/mask values are identical to the V8 fixture. The earlier V8 test encoded these two compositions identically.

Two additional privacy perturbations pass exact equality across every encoded input:

- Exchange an opponent hand card with a deck card while preserving hand/deck counts and total collection.
- Reverse the opponent's hidden draw order.

Thus, this bounded fixture demonstrates both the required public-composition distinction and invariance to those hidden details. It does not prove the absence of every possible information leak in the game. [Fixture and equality results](reroll-composition-alias-v9.json), [isolated source](reroll-composition-v9-probe/Program.cs), [original V8 contrast](reroll-composition-alias-v8.json).

## Frozen policy comparison

[The CPU-only scorer](../experiments/reroll_public_response_v9.py) compares the migrated generation-8762 learner against the completed bounded public-deck pilot. Each checkpoint is loaded through the normal strict, checksummed loader from an isolated copy. It scores the same exact fixture inputs, including all legal actions, and reports changes in reroll probability, conditional reroll-target distribution, and value.

The migration's new public-information columns must be exactly zero. Identical initial responses across this new composition contrast are therefore expected and tested: preservation must not invent an immediate reroll strategy. Nonzero sensitivity after training would demonstrate that the policy uses the information, not that it chooses the correct denial target.

## Results

The completed pilot reached generation **8811**, following **12,544 games** from the migrated generation **8762**. This diagnostic performed no additional games or optimizer updates.

| Measurement across the two public compositions | Migrated initial | After pilot |
|---|---:|---:|
| New public-information projection weight L2 norm | 0 | 9.7872 |
| New public columns of decision head, L2 norm | 0 | 1.0297 |
| Total variation of full action distribution | 0 | 0.007390 |
| Total variation of reroll-target distribution conditional on reroll | 0 | 0.007189 |
| Change in total reroll probability, Order minus Undergrowth | 0 | −0.000801 |
| Change in predicted own outcome value, Order minus Undergrowth | 0 | −0.084969 |

The initial exact equality passed as required. After the pilot, the model responds to the public composition contrast. With the Undergrowth collection it assigns **96.3093%** total reroll probability and value **0.39997**; with the Order collection those become **96.2292%** and **0.31500**. Conditional reroll-target probability mass changes by about **0.72 percentage points** in total variation. The overall shift is measurable but modest in this fixture.

This establishes that the new public features are represented and have begun influencing learned action/value outputs. It does **not** establish that the preferred target is strategically correct, that denial has been mastered, or that the pilot is stronger. The deliberately sparse fixture is not suitable for calibrated strength conclusions. The parent's balanced evaluation remains the appropriate separate strength screen.

[Raw policy responses and strict loader provenance](reroll-public-response-v9-2026-09-27.json): initial checkpoint payload SHA-256 `e2c2502949199550bbc5c6c8406ecbec666682ebe4a54c6d0ed08b0609ee06b9`; completed pilot payload `24d36d9a2881867db6f10d538285dbdfd235fbc7a944c33b739a7f742cead1fc`. Policy tensors were unchanged by this diagnostic and CUDA remained uninitialized.
