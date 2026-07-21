namespace Shards.Bots
{
    /// <summary>Budget and tuning knobs for the SO-ISMCTS search bot. Sims use
    /// Iterations mode (deterministic, reproducible); real games use WallClock.</summary>
    public sealed class ShardsSearchConfig
    {
        public enum BudgetMode { Iterations, WallClock }

        public BudgetMode Mode = BudgetMode.Iterations;
        /// <summary>Search iterations per decision (Iterations mode).</summary>
        public int Iterations = 200;
        /// <summary>Wall-clock budget per decision (WallClock mode), checked every
        /// few iterations.</summary>
        public double WallClockSeconds = 1.0;
        /// <summary>UCB1 exploration constant (rewards are in [0,1]).</summary>
        public double Ucb = 0.8;
        /// <summary>Progressive-bias strength: prior/(1+visits) added to UCB.</summary>
        public double ProgressiveBias = 0.3;
        /// <summary>ε for the ε-greedy rollout policy (uniform-random mixing).
        /// Deliberately LOW: full-game rollouts amplify random blunders into estimator
        /// noise the iteration budget can't average away — measured 27% vs greedy at
        /// ε=0.15/200it, 48% at ε=0.03/200it, 77% at ε=0.03/600it.</summary>
        public double RolloutEpsilon = 0.03;
        /// <summary>Hard safety cap on submits per iteration (descent + rollout).</summary>
        public int MaxIterationSubmits = 3000;

        /// <summary>0 = roll every game to terminal (legacy). N&gt;0 = stop the rollout
        /// after N end-turns and score the leaf with the evaluator — 3-6× cheaper
        /// iterations, and the leaf estimate replaces the noisiest part of the search.
        /// Only applies to 2-player games with an evaluator wired.</summary>
        public int RolloutEndTurns = 0;

        /// <summary>Root-parallel worker trees (real games; sims stay 1 for
        /// bit-reproducibility). Each worker forks + searches independently; root
        /// visits merge by action key.</summary>
        public int RootWorkers = 1;

        /// <summary>Self-play exploration: while Round ≤ this, the ROOT action is
        /// SAMPLED ∝ visits^(1/RootTau) instead of argmax — game diversity without a
        /// policy prior to perturb. 0 (default) = always argmax.</summary>
        public int RootSampleTurns = 0;
        public double RootTau = 1.0;

        public static ShardsSearchConfig ForSims(int iterations) => new()
        {
            Mode = BudgetMode.Iterations,
            Iterations = iterations
        };

        public static ShardsSearchConfig ForRealGames(double seconds) => new()
        {
            Mode = BudgetMode.WallClock,
            WallClockSeconds = seconds,
            Iterations = int.MaxValue
        };
    }
}
