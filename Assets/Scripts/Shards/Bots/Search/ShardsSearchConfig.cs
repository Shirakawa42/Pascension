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
