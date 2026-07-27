using System;
using System.Collections.Generic;
using Pascension.Core;
using Shards.Engine;

namespace Shards.Bots
{
    /// <summary>
    /// The SoI bot ladder, named after League ranks. IRON/BRONZE wrap the two instant
    /// agents; SILVER → DIAMOND are full-rollout ISMCTS at doubling iteration budgets.
    /// Unminted ranks are absent from <see cref="Minted"/> and hidden from the menu.
    /// DisplayName is the Loc.T key — FR entries live in LocFrench (FER, BRONZE,
    /// ARGENT, OR, PLATINE, ÉMERAUDE, DIAMANT).
    ///
    /// ⚠ NO NEURAL NETS ANYWHERE. The 2026-07-21→26 campaign minted GOLD→DIAMOND from
    /// trained nets and the result was a ladder INVERTED AT THE TOP: the shipped DIAMOND
    /// scored 8.5% [5.6-11.4] against BRONZE, an instant greedy policy — roughly −410
    /// Elo. Everything net-shaped was removed 2026-07-27. The replacement agent (the
    /// turn planner) is being built against a FROZEN external benchmark — see the
    /// `bench:*` kinds in <see cref="Create"/> — rather than against itself.
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

        /// <summary>Minted ranks in ascending strength.
        /// ⚠ SILVER→DIAMOND budgets above 4800 are EXTRAPOLATED, not measured — the
        /// crossover run stopped at 4800 (71.4%) plus a partial 6000 (n=40). Re-measure
        /// before citing them.</summary>
        public static readonly IReadOnlyList<RankSpec> Minted = new[]
        {
            Rank("iron", "IRON", isSearch: false,
                (seed, engine) => new ShardsHeuristicBot(seed, engine)),
            Rank("bronze", "BRONZE", isSearch: false,
                (seed, engine) => new ShardsGreedyEvalBot(seed, engine, Model.Value)),
            // SILVER → DIAMOND — RE-MINTED 2026-07-25 on FULL ROLLOUTS, no net.
            // The frozen nets are gone from the ladder because they were measured to be
            // worth LESS than nothing (see the block comment above Rollout()).
            Rank("silver", "SILVER", isSearch: true,
                (seed, engine) => new ShardsSearchBot(seed, engine,
                    Rollout(SilverIterations, SilverWorkers), Model.Value)),
            Rank("gold", "GOLD", isSearch: true,
                (seed, engine) => new ShardsSearchBot(seed, engine,
                    Rollout(GoldIterations, GoldWorkers), Model.Value)),
            Rank("platinum", "PLATINUM", isSearch: true,
                (seed, engine) => new ShardsSearchBot(seed, engine,
                    Rollout(PlatinumIterations, PlatinumWorkers), Model.Value)),
            Rank("emerald", "EMERALD", isSearch: true,
                (seed, engine) => new ShardsSearchBot(seed, engine,
                    Rollout(EmeraldIterations, EmeraldWorkers), Model.Value)),
            Rank("diamond", "DIAMOND", isSearch: true,
                (seed, engine) => new ShardsSearchBot(seed, engine,
                    Rollout(DiamondIterations, DiamondWorkers), Model.Value)),
        };

        // ------------------------------------------------------- rank budgets
        //
        // ⚠ THE CROSSOVER IS ~1200 ITERATIONS. Measured 2026-07-25 vs BRONZE (instant
        // V5 greedy), paired: 300 it → **21.2%**, 1200 it → **51.4%**, 4800 it → ~70%.
        // Below the crossover, MORE SEARCH IS WORSE THAN NO SEARCH: ε-greedy rollouts
        // produce value estimates noisier than simply trusting the tuned policy, so a
        // small budget actively talks the bot out of good greedy moves. Above it search
        // compounds fast (~115 Elo/doubling; a 4× step is worth 79.3%).
        //
        // So every search rank sits WELL above 1200, and the ladder is spaced by budget
        // doublings from there. Never set one of these from the scaling slope alone.
        //
        // Wall clock is calibrated on the reference box (16 logical cores) at ~365 µs per
        // full-rollout iteration (measured: 73 ms/decision at 200 it, single thread).
        // Root workers cut wall-clock ONLY — the merge is seeded and CPU-independent, so
        // the move is identical on any machine, which is the shipped guarantee.
        private const int SilverIterations = 2400, SilverWorkers = 8;          // ~110 ms
        private const int GoldIterations = 6000, GoldWorkers = 8;              // ~275 ms
        private const int PlatinumIterations = 12000, PlatinumWorkers = 8;     // ~550 ms
        private const int EmeraldIterations = 24000, EmeraldWorkers = 16;      // ~550 ms
        private const int DiamondIterations = 48000, DiamondWorkers = 16;      // ~1.1 s

        /// <summary>ISMCTS with FULL ε-greedy rollouts to terminal and NO neural net, at a
        /// FIXED iteration budget.
        ///
        /// Why no net (measured 2026-07-25, RunPod, paired):
        ///  · net (gen-8, truncate-2) vs this rollout agent at EQUAL 200 it —
        ///    **40.6% [38.1-43.0] over 1000 pairs**. The net loses by ~66 Elo. It is
        ///    worse than having no evaluator at all.
        ///  · 4× budget (800 vs 200): rollout agent **79.3%**, net agent **52.2%**.
        /// The frozen net was anchoring every leaf to a value function fit on a different
        /// game (no Duel bit, no hero identity, none of the 9 Duel flags), so extra
        /// iterations bought better play toward a worse target. That — not the game — is
        /// what produced the "~22 Elo per doubling" figure that retired MASTER→CHALLENGER
        /// and wrote off "more search" as a rung. Without it the same step is ~115
        /// Elo/doubling, which is what makes these budgets worth buying.
        ///
        /// A net returns to the ladder only when one beats this agent head-to-head at
        /// equal wall-clock. That is now the bar, and gen-8 is 66 Elo below it.
        ///
        /// FIXED iterations, never wall-clock: the move must not depend on the machine.
        /// EarlyStopBudgetFraction stays 1.0 (exact — byte-identical to spending the whole
        /// budget, just faster once the leader is literally uncatchable).</summary>
        private static ShardsSearchConfig Rollout(int totalIterations, int workers = 1)
        {
            // Root-parallel budgets are PER TREE, so divide to keep `totalIterations`
            // the honest total the rank is gated at.
            var config = ShardsSearchConfig.ForSims(Math.Max(1, totalIterations / Math.Max(1, workers)));
            config.EarlyStopBudgetFraction = 1.0;
            config.RootWorkers = workers;
            return config;
        }

        public static RankSpec Find(string kindString)
        {
            foreach (var rank in Minted)
                if (rank.KindString == kindString)
                    return rank;
            return null;
        }

        // ------------------------------------------------- the FROZEN benchmark ladder
        //
        // These four kinds are the fixed yardstick every future candidate is measured
        // against. THEY MUST NEVER CHANGE — not their weights, not their budgets, not
        // their worker counts. The previous campaign's central failure was gating
        // net-vs-net in mirror matches, which is structurally blind to a shared blind
        // spot: nine generations all read ~50% against each other while the shipped top
        // rank was losing to instant greedy 8.5% of the time. An external, immovable
        // reference is the only thing that can catch that.
        //
        // `bench:greedy-v5` pins V5 EXPLICITLY rather than following ShardsEvalWeights
        // .Current, because Current moves the moment the tuner runs and a moving
        // yardstick measures nothing.
        //
        // Single-threaded on purpose: root-parallel merges are CPU-independent but
        // single trees are bit-reproducible, which is what a benchmark wants.
        private static readonly Lazy<ShardsValueModel> FrozenV5 =
            new(() => new ShardsValueModel(ShardsEvalWeights.V5));

        /// <summary>Resolves rank kind strings, the frozen `bench:*` benchmark kinds, and
        /// the tooling kinds ("heuristic", "random", "greedy", "strong", "strong-fast").</summary>
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

                // RETIRED negative result: 1-step lookahead on a static evaluator. `soisim
                // rank` measured why it can never work — sibling end-of-turn leaves differ
                // by 0.013 true win-prob at the decision margin, below any evaluator's
                // resolution. Kept resolvable so the campaign log's probes stay
                // reproducible; never mint it.
                "planner" => new ShardsPlannerBot(seed, engine, Model.Value),
                "planner-w8" => new ShardsPlannerBot(seed, engine, Model.Value,
                    config: new ShardsPlannerConfig { Worlds = 8 }),

                // The Phase 2 candidate: whole-turn PURCHASE BASKETS scored by terminal
                // rollouts — the only selector `soisim rank` measured with positive
                // headroom (+0.012/turn; every static evaluator measured negative). NOT a
                // minted rank until it clears Gate A — beating full-rollout ISMCTS
                // head-to-head at equal WALL-CLOCK.
                "basket" => new ShardsBasketPlannerBot(seed, engine, Model.Value),
                "basket-96" => new ShardsBasketPlannerBot(seed, engine, Model.Value,
                    new ShardsBasketPlannerConfig { Worlds = 2, RolloutsPerWorld = 48 }),

                // --- frozen benchmark ladder (see the block comment above) ---
                "bench:heuristic" => new ShardsHeuristicBot(seed, engine),
                "bench:greedy-v5" => new ShardsGreedyEvalBot(seed, engine, FrozenV5.Value),
                // 1200 ≈ the measured crossover (51.4% vs instant greedy); 4800 ≈ 70%.
                "bench:rollout-1200" => new ShardsSearchBot(seed, engine,
                    Rollout(1200), FrozenV5.Value),
                "bench:rollout-4800" => new ShardsSearchBot(seed, engine,
                    Rollout(4800), FrozenV5.Value),

                _ => new ShardsHeuristicBot(seed, engine)
            };
        }

        /// <summary>Whether the kind needs the worker-thread seat (search ranks, the
        /// "strong"* tooling kinds, and the rollout benchmarks).</summary>
        public static bool IsSearchKind(string kind) =>
            Find(kind)?.IsSearch ??
            kind is "strong" or "strong-fast" or "bench:rollout-1200" or "bench:rollout-4800"
                or "basket" or "basket-96";
    }
}
