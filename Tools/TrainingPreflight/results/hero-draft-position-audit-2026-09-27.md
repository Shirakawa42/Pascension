# Hero draft position audit — 2026-09-27

User reported selecting Decima followed by the native AI selecting Volos despite Tetra being available. The native policy samples its learned categorical distribution; uniform random hero assignment is used only by the final evaluation setup.

Read-only diagnostic matches were created separately inside Unity using the shipped model. No live match, training, or final evaluation data was modified.

Across seeds 100001–100032, after seat 1 selected Decima, seat 0 assigned Volos probability 1.0 at FP32 precision in all 32 positions. Mean Tetra probability was 4.8391e-10. This is not a rare stochastic pick.

The draft candidate encoder provides both compact menu ordinal (candidate column 25) and stable hero identity (column 31). The categorical decision head uses the compact ordinal. Removing Decima shifts Tetra from ordinal 1 to 0 and Volos from 2 to 1. The policy strongly favors ordinal 1: in seed 100001 it preferred Tetra at 99.8756% when drafting first, but Volos at effectively 100% after Decima was selected.

Diagnostic counterfactual: changing only candidate ordinal values to stable hero indices after Decima's selection changed the Tetra probability to 99.8241%. Hero identities, legal choices, market and all observation features remained the same. This demonstrates strong positional dependence. It does not validate blindly remapping the saved model: the same diagnostic makes the model choose Rez when Tetra is unavailable.

The final 50,000-game dataset bypassed learned hero drafting via balanced external assignments, so this draft defect did not choose its hero populations. Normal-draft strength comparisons do include the defective learned drafting behavior.

No production fix was applied by this diagnostic. A no-training remediation should isolate hero drafting, validate a matchup/seat-based choice rule using the frozen evaluation data, and retain the learned gameplay policy. A future learned solution needs a stable hero-aware action representation and coverage of all opponent first picks, rather than relying on compact menu positions.
