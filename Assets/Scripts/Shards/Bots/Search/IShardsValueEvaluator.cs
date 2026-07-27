using Shards.Engine;

namespace Shards.Bots
{
    /// <summary>Win-probability estimate for a position. Implementations must be pure
    /// reads (called on cloned states inside a search) and cheap (µs-scale).
    ///
    /// NOTHING IMPLEMENTS THIS TODAY, on purpose. Both previous implementations were
    /// removed 2026-07-27:
    ///  · ShardsNeuralEval (the trained nets) measured 40.6% against a full-rollout
    ///    agent at equal budget — worse than having no evaluator at all — and 8.5%
    ///    against instant greedy as shipped.
    ///  · ShardsBaselineEvaluator was a hand-coefficient logistic whose LARGEST term was
    ///    linear health, which four independent expert reviews each named as its single
    ///    biggest error: health is only meaningful through the kill clock
    ///    (TTK = health / damage-per-turn), never as a linear term.
    ///
    /// The replacement is the clock evaluator (Phase 2): analytic ratio features
    /// (N, D, killClock, ascendClock, TTK, burst) computed exactly, a learned
    /// per-(defId, masteryBucket) card-value table, and a learned residual composed
    /// MULTIPLICATIVELY with the sigmoid base.
    ///
    /// ⚠ The bar for shipping any evaluator: it must beat full-rollout ISMCTS
    /// head-to-head at equal WALL-CLOCK, measured paired with SPRT at n≥2000. Running
    /// that probe first — rather than after nine training generations — is the single
    /// process change this rewrite exists to enforce.</summary>
    public interface IShardsValueEvaluator
    {
        /// <summary>P(playerIndex wins) in [0,1] for a 2-player state.</summary>
        double Evaluate(ShardsState state, int playerIndex);
    }
}
