using System;
using System.Collections.Generic;
using Pascension.Core;
using Shards.Engine;

namespace Shards.Bots
{
    /// <summary>
    /// The SoI bot ladder, named after League ranks. IRON/BRONZE/SILVER wrap the three
    /// shipped agents unchanged; GOLD → CHALLENGER are MINTED by the neural training
    /// campaign (each promoted checkpoint pins the next rank; CHALLENGER always tracks
    /// the newest champion at the full think budget). Unminted ranks are absent from
    /// <see cref="Minted"/> and therefore hidden from the menu.
    /// DisplayName is the Loc.T key — FR entries live in LocFrench (FER, BRONZE,
    /// ARGENT, OR, PLATINE, ÉMERAUDE, DIAMANT, MAÎTRE, GRAND MAÎTRE, CHALLENGER).
    /// </summary>
    public static class ShardsBotRanks
    {
        public sealed class RankSpec
        {
            public string Id;                                   // "iron"
            public string KindString;                           // "rank:iron"
            public string DisplayName;                          // "IRON" (Loc key)
            /// <summary>True = needs the worker-thread SearchBotSeat.</summary>
            public bool IsSearch;
            public Func<ulong, ShardsEngine, IBotAgent> Factory;
        }

        /// <summary>Shared read-only value model (weights static, statics immutable).
        /// Initialized lazily on first bot creation — by then an engine exists, so the
        /// card database is guaranteed populated (Shards.Bots must not reference
        /// Shards.Content; the asmdef boundary keeps bots content-free).</summary>
        private static readonly Lazy<ShardsValueModel> Model = new(() => new ShardsValueModel());

        private static RankSpec Rank(string id, string display, bool isSearch,
            Func<ulong, ShardsEngine, IBotAgent> factory) => new()
        {
            Id = id,
            KindString = "rank:" + id,
            DisplayName = display,
            IsSearch = isSearch,
            Factory = factory
        };

        /// <summary>Minted ranks in ascending strength. The neural campaign appends
        /// GOLD, PLATINUM, EMERALD, DIAMOND, MASTER, GRANDMASTER, CHALLENGER here as
        /// checkpoints pass their promotion gates.</summary>
        public static readonly IReadOnlyList<RankSpec> Minted = new[]
        {
            Rank("iron", "IRON", isSearch: false,
                (seed, engine) => new ShardsHeuristicBot(seed, engine)),
            Rank("bronze", "BRONZE", isSearch: false,
                (seed, engine) => new ShardsGreedyEvalBot(seed, engine, Model.Value)),
            // SILVER is the pre-upgrade "MASTER" exactly as shipped 2026-07-21:
            // full-rollout SO-ISMCTS at 1.0s wall-clock.
            Rank("silver", "SILVER", isSearch: true,
                (seed, engine) => new ShardsSearchBot(seed, engine,
                    ShardsSearchConfig.ForRealGames(1.0), Model.Value)),
            // GOLD — minted 2026-07-21 from the generation-0 value net (74.6% val acc,
            // trained on 720k bootstrap positions): ISMCTS with net-truncated rollouts
            // (2 end-turns) at the same 1.0s budget. Promotion probe: 78.3%
            // [66.4–86.9] vs SILVER's search at EQUAL iterations (and ~2× cheaper
            // per iteration on top). PINNED to generation 0 forever — newer nets mint
            // newer ranks instead of drifting this one.
            Rank("gold", "GOLD", isSearch: true,
                (seed, engine) => new ShardsSearchBot(seed, engine,
                    NetConfig(1.0), Model.Value, Gen0Net.Value)),
            // PLATINUM — minted 2026-07-22 from the generation-5 value net (wide
            // 1024→512→256, 76.8% val acc, 1.32M mixed bootstrap+search positions
            // with q-labels): GOLD's net-truncated-rollout search armed with the
            // stronger net at the ladder's first budget step (1.25s). Promotion:
            // 57.8% [52.9–62.5] vs GOLD's method at equal 200-iteration budget
            // (n=400); **60.7% [54.9–66.3] vs GOLD AS SHIPPED** (1.25s vs 1.0s,
            // n=280 wall-clock); guards 100% vs random, 66% vs BRONZE at 200it —
            // identical to GOLD's 66%. Eval-at-leaf was probed and REJECTED for play (rollouts
            // resolve the tactical state the pooled encoding can't see — same for
            // the schema-2 tactical encoder, rejected in both modes). PINNED to
            // generation 5 forever.
            Rank("platinum", "PLATINUM", isSearch: true,
                (seed, engine) => new ShardsSearchBot(seed, engine,
                    NetConfig(1.25), Model.Value, Gen5Net.Value)),
        };

        private static readonly Lazy<IShardsValueEvaluator> Gen0Net =
            new(() => ShardsNeuralEval.LoadGeneration(0));

        private static readonly Lazy<IShardsValueEvaluator> Gen5Net =
            new(() => ShardsNeuralEval.LoadGeneration(5));

        private static ShardsSearchConfig NetConfig(double seconds)
        {
            var config = ShardsSearchConfig.ForRealGames(seconds);
            config.RolloutEndTurns = 2; // net-truncated rollouts
            return config;
        }

        public static RankSpec Find(string kindString)
        {
            foreach (var rank in Minted)
                if (rank.KindString == kindString)
                    return rank;
            return null;
        }

        /// <summary>Resolves rank kind strings AND the legacy/tooling kinds
        /// ("heuristic", "random", "greedy", "strong", "strong-fast").</summary>
        public static IBotAgent Create(string kind, ulong seed, ShardsEngine engine)
        {
            var rank = Find(kind);
            if (rank != null)
                return rank.Factory(seed, engine);
            return kind switch
            {
                "random" => new ShardsHeuristicBot(seed, engine, random: true),
                "greedy" => new ShardsGreedyEvalBot(seed, engine, Model.Value),
                "strong" => new ShardsSearchBot(seed, engine,
                    ShardsSearchConfig.ForRealGames(1.0), Model.Value),
                "strong-fast" => new ShardsSearchBot(seed, engine,
                    ShardsSearchConfig.ForRealGames(0.25), Model.Value),
                _ => new ShardsHeuristicBot(seed, engine)
            };
        }

        /// <summary>Whether the kind needs the worker-thread seat (search ranks and
        /// the legacy "strong"* tooling kinds).</summary>
        public static bool IsSearchKind(string kind) =>
            Find(kind)?.IsSearch ?? kind is "strong" or "strong-fast";
    }
}
