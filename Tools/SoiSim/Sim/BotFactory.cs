using Pascension.Core;
using Shards.Bots;
using Shards.Engine;

namespace SoiSim
{
    /// <summary>Creates bot seats for sim games. Bot seed derivation follows the
    /// engine-test idiom: gameSeed * 100 + seat.</summary>
    public sealed class BotFactory
    {
        public string Kind { get; }
        public int Budget { get; }
        /// <summary>Optional rollout-ε override for "strong" (-1 = config default).</summary>
        public double Epsilon { get; set; } = -1;

        /// <summary>&gt;0: truncate rollouts after N end-turns and score with the
        /// baseline evaluator (the B2 experiment knob).</summary>
        public int TruncateEndTurns { get; set; }

        /// <summary>&gt;0: wall-clock budget in seconds (replaces the iteration budget).</summary>
        public double WallClockSeconds { get; set; }

        /// <summary>&gt;1: root-parallel worker trees (probe outer threads accordingly).</summary>
        public int RootWorkers { get; set; } = 1;

        /// <summary>Shared read-only model for greedy seats (built once, thread-safe).</summary>
        private static readonly System.Lazy<ShardsValueModel> GreedyModel =
            new(() => new ShardsValueModel());

        /// <summary>Goes into the RunHeader — bump when a bot's behavior changes.</summary>
        public string Descriptor => Kind switch
        {
            "random" => "random-v1",
            "heuristic" => "heuristic-v1",
            "greedy" => "greedy-" + CurrentWeightsName(),
            "strong" => $"ismcts-{CurrentWeightsName()}-{(Budget > 0 ? Budget : 200)}it",
            _ => Kind
        };

        private static string CurrentWeightsName()
        {
            foreach (var field in typeof(ShardsEvalWeights).GetFields())
                if (field.FieldType == typeof(double[]) &&
                    ReferenceEquals(field.GetValue(null), ShardsEvalWeights.Current))
                    return field.Name;
            return "custom";
        }

        public BotFactory(string kind, int budget)
        {
            Kind = kind;
            Budget = budget;
        }

        private ShardsSearchConfig StrongConfig()
        {
            var config = WallClockSeconds > 0
                ? ShardsSearchConfig.ForRealGames(WallClockSeconds)
                : ShardsSearchConfig.ForSims(Budget > 0 ? Budget : 200);
            if (Epsilon >= 0)
                config.RolloutEpsilon = Epsilon;
            if (TruncateEndTurns > 0)
                config.RolloutEndTurns = TruncateEndTurns;
            config.RootWorkers = RootWorkers;
            return config;
        }

        public IBotAgent Create(ulong gameSeed, int seat, ShardsEngine engine) => Kind switch
        {
            "heuristic" => new ShardsHeuristicBot(gameSeed * 100 + (ulong)seat, engine),
            "random" => new ShardsHeuristicBot(gameSeed * 100 + (ulong)seat, engine, random: true),
            "greedy" => new ShardsGreedyEvalBot(gameSeed * 100 + (ulong)seat, engine, GreedyModel.Value),
            "strong" => new ShardsSearchBot(gameSeed * 100 + (ulong)seat, engine,
                StrongConfig(), GreedyModel.Value),
            _ => throw new CliError($"unknown bot kind '{Kind}'")
        };
    }
}
