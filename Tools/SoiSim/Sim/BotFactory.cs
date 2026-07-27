using System.Collections.Concurrent;
using System.IO;
using Newtonsoft.Json.Linq;
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

        /// <summary>&gt;0: wall-clock budget in seconds (replaces the iteration budget).</summary>
        public double WallClockSeconds { get; set; }

        /// <summary>&gt;1: root-parallel worker trees (probe outer threads accordingly).</summary>
        public int RootWorkers { get; set; } = 1;

        /// <summary>RESEARCH ONLY — makes "strong" cheat by skipping determinization, so
        /// it plans against the real hidden state. Used to bound what hidden information
        /// costs. Never reachable from a minted rank.</summary>
        public bool PerfectInformation { get; set; }

        /// <summary>Early-stop budget fraction for "strong": -1 = config default,
        /// 0 = OFF, &gt;0 = that fraction (1.0 exact/neutral, lower = more aggressive).</summary>
        public double EarlyStopFraction { get; set; } = -1;

        /// <summary>Tuned weight vector for this side's value model (null = the current
        /// champion). Lets a probe pit two weight vectors against each other — the V(n)
        /// vs V(n-1) promotion gate, and the Duel-blind ablation.</summary>
        public double[] Weights { get; set; }

        /// <summary>Shared read-only model for greedy seats (built once, thread-safe).</summary>
        private static readonly System.Lazy<ShardsValueModel> GreedyModel =
            new(() => new ShardsValueModel());

        /// <summary>Models for explicit weight vectors, keyed by vector identity. Building
        /// one walks every def in the database, so this must not happen per game.</summary>
        private static readonly ConcurrentDictionary<double[], ShardsValueModel> WeightModels = new();

        /// <summary>Resolved vector → the name it was requested under. Needed because
        /// W.Pad returns a NEW array for any historical vector shorter than the current
        /// layout, so reference-matching against ShardsEvalWeights' fields would report
        /// every one of them as "custom" — and the campaign log is already full of probe
        /// lines that cannot be attributed after the fact.</summary>
        private static readonly ConcurrentDictionary<double[], string> WeightNames = new();

        private ShardsValueModel Model =>
            Weights == null ? GreedyModel.Value
                            : WeightModels.GetOrAdd(Weights, w => new ShardsValueModel(w));

        /// <summary>Named weight vectors for the probe flags: `current`, any historical
        /// `V1`…`Vn` from the generated file, or `duel-blind` — the current champion with
        /// both Duel actions pushed below END TURN, reproducing how the bots behaved
        /// before 2026-07-25. That ablation is the only honest way to measure what
        /// Duel-awareness is worth, since both sides otherwise share the blind spot.
        ///
        /// ⚠ `duel-blind` is a GREEDY-side ablation only. ShardsValueModel.ScoreAction
        /// also feeds ShardsIsmcts as the move prior (`score / 4000`) and as the rollout
        /// policy, so the sentinel magnitude poisons the search's UCB rather than cleanly
        /// removing the action. Measure search-side capability gaps some other way.</summary>
        public static double[] ResolveWeights(string name)
        {
            if (string.IsNullOrEmpty(name) || name == "current") return null;
            if (name == "duel-blind")
            {
                var blind = (double[])W.Pad(ShardsEvalWeights.Current).Clone();
                blind[W.HeroAbilityValueScale] = -1e9; // any net value scores far below END TURN
                blind[W.RerollBase] = -1e9;
                WeightNames[blind] = "duel-blind";
                return blind;
            }
            if (name == "scry-live")
            {
                // `soisim coverage` found that Rez's ENTIRE hero ability (Scry 2) is
                // unreachable under V5, exactly like the row reroll was before 2026-07-25:
                //   value = 2 x ScryPerCard(0.1958) = 0.392
                //   cost  = 1 gem x Gems(0.5370)    = 0.537   -> net -0.145 -> below END TURN
                // Break-even is ScryPerCard = 0.2685. This vector lifts it clear of that so
                // the ability actually fires, which answers the question the zero cannot:
                // did the tuner correctly price Scry as worthless, or did it simply never
                // explore a value where the action becomes reachable and could pay off?
                var live = (double[])W.Pad(ShardsEvalWeights.Current).Clone();
                live[W.ScryPerCard] = 0.40;
                WeightNames[live] = "scry-live";
                return live;
            }
            foreach (var field in typeof(ShardsEvalWeights).GetFields())
                if (field.FieldType == typeof(double[]) &&
                    string.Equals(field.Name, name, System.StringComparison.OrdinalIgnoreCase))
                {
                    var resolved = W.Pad((double[])field.GetValue(null));
                    WeightNames[resolved] = field.Name;
                    return resolved;
                }
            throw new CliError(
                $"unknown weights '{name}' (expected current, duel-blind, scry-live, or V1…Vn)");
        }

        /// <summary>Goes into the RunHeader — bump when a bot's behavior changes.</summary>
        public string Descriptor => Kind switch
        {
            "random" => "random-v1",
            "heuristic" => "heuristic-v1",
            "greedy" => "greedy-" + WeightsName(),
            "strong" => $"ismcts-{WeightsName()}-{(Budget > 0 ? Budget : 200)}it" +
                        (PerfectInformation ? "-ORACLE" : ""),
            _ => Kind
        };

        /// <summary>Names this side's weights so probe lines and run headers say WHICH
        /// vector played — the campaign log is full of `greedy-V4 vs greedy-V4` entries
        /// that are indistinguishable after the fact.</summary>
        private string WeightsName()
        {
            if (Weights == null) return CurrentWeightsName();
            if (WeightNames.TryGetValue(Weights, out string named)) return named;
            foreach (var field in typeof(ShardsEvalWeights).GetFields())
                if (field.FieldType == typeof(double[]) &&
                    ReferenceEquals(field.GetValue(null), Weights))
                    return field.Name;
            return "custom";
        }

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
            config.RootWorkers = RootWorkers;
            config.PerfectInformation = PerfectInformation;
            if (EarlyStopFraction == 0) config.EarlyStopWhenDecided = false;
            else if (EarlyStopFraction > 0) config.EarlyStopBudgetFraction = EarlyStopFraction;
            return config;
        }

        public IBotAgent Create(ulong gameSeed, int seat, ShardsEngine engine) => Kind switch
        {
            "heuristic" => new ShardsHeuristicBot(gameSeed * 100 + (ulong)seat, engine),
            "random" => new ShardsHeuristicBot(gameSeed * 100 + (ulong)seat, engine, random: true),
            "greedy" => new ShardsGreedyEvalBot(gameSeed * 100 + (ulong)seat, engine, Model),
            "strong" => new ShardsSearchBot(gameSeed * 100 + (ulong)seat, engine,
                StrongConfig(), Model),
            _ => ShardsBotRanks.Create(Kind, gameSeed * 100 + (ulong)seat, engine)
        };
    }
}
