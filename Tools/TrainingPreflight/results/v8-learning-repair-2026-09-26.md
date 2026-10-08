# Learning and mechanical-choice repair

The investigation found real representation defects, not evidence that all the remaining errors are legitimate strategy. Two authorized information states were indistinguishable, opposite modes differed mainly by a tiny ordinal, and the value function ignored the cards in choice menus. Outcome credit and optimizer updates passed the audited contracts. Millions of games cannot recover information that the observation omits.

## Changes

- Champion assignments are zero or at least the publicly announced lethal threshold. Taunts are handled first; a skipped taunt blocks its protected targets; unblocked residual damage automatically goes to the opponent. Ordinary champions use exact lethal amounts. Testudo permits overkill because hidden shield reveals can prevent a nominally lethal hit. This changes the AI adapter, not the underlying game rules.
- A copied Reactor effect automatically takes three gems only when an authorized public non-Reactor source proves there is no banish cost. Physical Reactor and unproven contexts retain their choices.
- Conditional exhausts remain legal. Thirty-two explicit public/own readiness predicates expose their conditions; future-effect setters can still activate early. A prior blanket-deferral ablation was inconclusive, so it was not adopted.
- A 512-value supplement preserves the original2048-value prefix and adds authorized effect source, public reveal memory, actual played-card summaries, public acquisition history, and individual entity states through64.
- Sixteen independent categorical scores cover typed modes/yes-no choices and Volos. Ordinary card-list positions are not assigned these mode scores. A masked legal-card embedding summary now informs both policy and value. New information enters before the existing final nonlinearity, allowing it to interact with board context.
- All new projections initialize at zero. Migration preserves all old policy tensors, exploration buffers, Adam moments, RNG, counters and the original43200second campaign allocation. No old checkpoint is overwritten.

## Evidence

- [Learning diagnosis](learning-signal-diagnosis-v8-2026-09-26.md): a controlled Reactor-label task reached95% probability in101 categorical-head updates versus288 legacy updates. This is a trainability diagnostic, not game-strength evidence. Early-game value error remains high; terminal-only feedback is noisy.
- [Damage audit](v8-damage-abstraction-audit-2026-09-26.md):28,064 allocation checks and45 copied-Reactor automation checks, with red reproductions againstV6.
- [Information audit](v8-information-audit-2026-09-26.md):1,544 information/readiness/privacy checks, plus the retained20 hero and general host regression checks.
- [Hidden-state sweep](v8-hidden-invariance-2026-09-26.json):13,715 reached states in64 complete games,25 contexts, both seats;4672 input floats invariant under hidden identity/deck/RNG perturbations.10,175 freshly recomputed legal-action menus were also invariant. This is tested noninterference, not a proof over every reachable state.
- [GPU parity](v8-cuda-choice-parity.json):72 FP32 forward/backward cases; maximum derivative error5.722e-6 and selected-distribution log-probability error6.104e-5 against the Torch reference.
- [Frozen GPU collection](v8-gpu-smoke.json):256 complete games, zero censors; maximum behavior likelihood mismatch3.994e-5 and value mismatch1.461e-6; policy/checkpoint/budget unchanged.
- [Migration](v8-migration.json): generation8454,19 policies, all existing tensors and continuation state preserved.

## Performance and adoption

[Predeclared gates](v8-adoption-gates.json) require a300second charged pilot, numerical/censor checks and a640game balanced-hero regression screen. The initial interleaved forward benchmark is about45–50% slower at small batches and28% slower at2048; it does not measure end-to-end training speed. The pilot completed87generations and22,272games with zero censors and finite weights/Adam state. Median generation time was2.996s (16.5% above the adjacentV7window; not controlled identical trajectories). The640game balanced screen scored49.84%,95% bound42.25–57.44%; the4096game frozen champion comparison scored54.39%, bound51.39–57.40%. V8 was adopted at generation8541. See [pilot metrics](v8-pilot-training-summary.json) and [balanced screen](v8-pilot-balanced-regression.json).

## What is not established

This does not establish optimal play, causal hero/card balance, or absence of every bug. [Remaining information gaps](v8-information-audit-2026-09-26.md#remaining-information-gaps-and-bounded-compression) include remembered opponent hand reveals, complete nested-effect progress and bounded public entity/top/ordered-selection histories. The new representation fixes demonstrated aliases while retaining a finite feed-forward approximation. These gaps must not be described as solved merely because the tested hidden-state perturbations pass.

Raw training win rates still mix policy competence, acquisition decisions and evolving opponents. Preserve controlled paired evaluations and treat rare card selections as exposure observations, not proof that a card is bad.

## Subsequent public-information correction

The user clarified that the opponent’s whole permanent collection is public. V8 incorrectly omitted that exact composition and tested invariance under changing it. Those identity-changing tests are not a valid privacy contract for this game. [V9 corrects the contract](v9-public-deck-contract-2026-09-27.md): full composition is visible, while private hand/draw allocation and draw order remain hidden. The damage and categorical-choice improvements above are retained.
