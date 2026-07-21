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

        /// <summary>Goes into the RunHeader — bump when a bot's behavior changes.</summary>
        public string Descriptor => Kind switch
        {
            "random" => "random-v1",
            "heuristic" => "heuristic-v1",
            _ => Kind
        };

        public BotFactory(string kind, int budget)
        {
            Kind = kind;
            Budget = budget;
        }

        public IBotAgent Create(ulong gameSeed, int seat, ShardsEngine engine) => Kind switch
        {
            "heuristic" => new ShardsHeuristicBot(gameSeed * 100 + (ulong)seat, engine),
            "random" => new ShardsHeuristicBot(gameSeed * 100 + (ulong)seat, engine, random: true),
            _ => throw new CliError($"unknown bot kind '{Kind}' (strong lands with the search bot milestone)")
        };
    }
}
