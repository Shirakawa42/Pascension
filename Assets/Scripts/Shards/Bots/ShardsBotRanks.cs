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
            // SILVER — re-spec 2026-07-22 to gen-0's net at HALF of GOLD's budget (a
            // clean iteration step below GOLD, same net). The old full-rollout-at-1.0s
            // config could not be BOTH fast and stronger than BRONZE (full rollouts
            // score ~48% vs BRONZE at 200 it, needing ~600 it ≈ 0.6-1.0s to be strong),
            // so the fast-below-MASTER reframe forces the net here too. gen-0 T2 @ 100 it
            // is fast (~30-40ms), beats BRONZE, and loses to GOLD @ 200 it. The archival
            // pre-neural full-rollout search survives as the "strong" tooling kind.
            Rank("silver", "SILVER", isSearch: true,
                (seed, engine) => new ShardsSearchBot(seed, engine,
                    NetConfig(SilverIterations), Model.Value, Gen0Net.Value)),
            // GOLD — generation-0 value net (narrow 512-256-128, 74.6% val acc,
            // 720k BOOTSTRAP positions): ISMCTS with 2-end-turn net-truncated rollouts
            // at the shared fixed budget. The weak-net neural rung; pairs with PLATINUM
            // at EQUAL iterations. PINNED to generation 0.
            Rank("gold", "GOLD", isSearch: true,
                (seed, engine) => new ShardsSearchBot(seed, engine,
                    NetConfig(RankIterations), Model.Value, Gen0Net.Value)),
            // PLATINUM — re-spec 2026-07-22 to the generation-8 NARROW net (512-256-128,
            // ~560K params, 76.7% val acc, gen-5's full 1.32M-position q-labeled mix).
            // The honest "better NET at EQUAL iterations" step over GOLD: 56.5%
            // [51.6-61.3] vs gen-0 at 200it (n=400) — gate passed. Same play strength as
            // the retired WIDE gen-5 (46.0% [40.4-51.7], a tie) at ~2.6x cheaper eval, so
            // GOLD and PLATINUM share ONE architecture and speed, differing only by
            // TRAINING DATA (bootstrap vs full mix). Wall-clock dropped for a fixed fast
            // budget (~50-80ms/decision vs the old 1.0-1.25s). PINNED to generation 8.
            Rank("platinum", "PLATINUM", isSearch: true,
                (seed, engine) => new ShardsSearchBot(seed, engine,
                    NetConfig(RankIterations), Model.Value, Gen8Net.Value)),
        };

        private static readonly Lazy<IShardsValueEvaluator> Gen0Net =
            new(() => ShardsNeuralEval.LoadGeneration(0));

        private static readonly Lazy<IShardsValueEvaluator> Gen8Net =
            new(() => ShardsNeuralEval.LoadGeneration(8));

        /// <summary>Shared fixed search budget for the GOLD/PLATINUM net pair — fast
        /// (~50-80ms/decision), deterministic, and where the promotion gates were
        /// proven (56.5% at 200 it, n=400). Each neural rank is a BETTER NET at this
        /// same budget, not more thinking time; larger budgets (EMERALD 400, DIAMOND
        /// 800) and wall-clock are reserved for the top ranks once the better-net
        /// method plateaus.</summary>
        private const int RankIterations = 200;

        /// <summary>SILVER = gen-0 at half GOLD's budget: a fast iteration step below it.</summary>
        private const int SilverIterations = 100;

        private static ShardsSearchConfig NetConfig(int iterations)
        {
            var config = ShardsSearchConfig.ForSims(iterations);
            config.RolloutEndTurns = 2;          // net-truncated rollouts (the deployed mode)
            config.EarlyStopBudgetFraction = 1.0; // EXACT: byte-identical move to the full
                                                  // budget, just faster on decided positions —
                                                  // keeps N an honest, gate-faithful ceiling.
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
