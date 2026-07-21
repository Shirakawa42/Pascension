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

        /// <summary>Shared read-only model for greedy seats (built once, thread-safe).</summary>
        private static readonly System.Lazy<ShardsValueModel> GreedyModel =
            new(() => new ShardsValueModel());

        /// <summary>Goes into the RunHeader — bump when a bot's behavior changes.</summary>
        public string Descriptor => Kind switch
        {
            "random" => "random-v1",
            "heuristic" => "heuristic-v1",
            "greedy" => "greedy-" + CurrentWeightsName(),
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

        public IBotAgent Create(ulong gameSeed, int seat, ShardsEngine engine) => Kind switch
        {
            "heuristic" => new ShardsHeuristicBot(gameSeed * 100 + (ulong)seat, engine),
            "random" => new ShardsHeuristicBot(gameSeed * 100 + (ulong)seat, engine, random: true),
            "greedy" => new ShardsGreedyEvalBot(gameSeed * 100 + (ulong)seat, engine, GreedyModel.Value),
            _ => throw new CliError($"unknown bot kind '{Kind}' (strong lands with the search bot milestone)")
        };
    }
}
