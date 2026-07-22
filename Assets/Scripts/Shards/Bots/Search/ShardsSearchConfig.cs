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

        /// <summary>-1 = roll every game to terminal (legacy default). 0 = NO rollout:
        /// the evaluator scores the expansion leaf directly (~4× cheaper iterations —
        /// the net was trained on exactly these pending-point positions). N&gt;0 = roll
        /// N end-turns first, then evaluate. Truncation modes need a 2-player game and
        /// an evaluator; otherwise legacy full rollouts apply.</summary>
        public int RolloutEndTurns = -1;

        /// <summary>Root-parallel worker trees (real games; sims stay 1 for
        /// bit-reproducibility). Each worker forks + searches independently; root
        /// visits merge by action key.</summary>
        public int RootWorkers = 1;

        /// <summary>Self-play exploration: while Round ≤ this, the ROOT action is
        /// SAMPLED ∝ visits^(1/RootTau) instead of argmax — game diversity without a
        /// policy prior to perturb. 0 (default) = always argmax.</summary>
        public int RootSampleTurns = 0;
        public double RootTau = 1.0;

        /// <summary>Stop a search the instant the most-visited root child's lead is
        /// insurmountable by the remaining budget — the argmax move can no longer
        /// change, so this is STRENGTH-NEUTRAL (bit-identical move to spending the
        /// whole budget) while skipping wasted iterations on decided positions.
        /// AUTO-DISABLED during temperature sampling (self-play exploration turns) and
        /// root-parallel search, where the full visit distribution is used rather than
        /// just the argmax. In wall-clock mode the remaining-iteration count is a rate
        /// projection. Default ON.</summary>
        public bool EarlyStopWhenDecided = true;

        /// <summary>Fraction of the remaining budget the runner-up is assumed able to
        /// capture, in (0,1]. 1.0 = EXACT and strength-neutral (stop only when the
        /// leader is literally uncatchable — the runner-up would need EVERY remaining
        /// iteration). Below 1.0 stops sooner by assuming the runner-up realistically
        /// captures only this share of what's left (UCB starves trailing arms), at a
        /// small, tunable chance of switching to a NEAR-TIED alternative — near-ties
        /// cost ≈0 strength, so this buys speed for free (validated by win-rate
        /// parity). 0.5 ≈ "the runner-up can't get more than half the rest".</summary>
        public double EarlyStopBudgetFraction = 0.5;

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
