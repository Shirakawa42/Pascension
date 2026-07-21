using System.Collections.Generic;
using System.Threading.Tasks;
using Pascension.Engine.Actions;
using Shards.Engine;

namespace Shards.Bots
{
    /// <summary>Root-parallel SO-ISMCTS for real games: K independent trees, each on
    /// its own Fork of the live engine (concurrent Forks are pure reads — safe under
    /// the seat-holds-pending contract), merged by summing root-child visits per
    /// canonical action key. No locks, no virtual loss; deterministic given
    /// (seed, K, iteration budgets). Sims keep K=1 for bit-reproducibility.</summary>
    public static class ShardsRootParallelSearch
    {
        public static PlayerAction Search(ShardsEngine live, int viewer, ShardsValueModel model,
            ShardsSearchConfig config, ulong seed, IShardsValueEvaluator evaluator,
            out ShardsIsmcts.PlanCursor plan, out int totalIterations, out double rootQ)
        {
            int workers = System.Math.Max(1, config.RootWorkers);
            var searches = new ShardsIsmcts[workers];
            for (int w = 0; w < workers; w++)
                searches[w] = new ShardsIsmcts(live, viewer, model, config,
                    seed ^ ((ulong)(w + 1) * 0x9E3779B97F4A7C15UL), evaluator);

            var roots = new ShardsIsmcts.Node[workers];
            Parallel.For(0, workers, w => roots[w] = searches[w].RunSearch());

            totalIterations = 0;
            foreach (var search in searches)
                totalIterations += search.IterationsRun;

            // Merge: summed visits per action key; the winning key's plan cursor comes
            // from the worker holding the most visits on that child.
            var merged = new Dictionary<string, (int Visits, double Reward, ShardsIsmcts.Child Best)>();
            foreach (var root in roots)
                foreach (var kv in root.Children)
                {
                    if (kv.Value.Visits == 0) continue;
                    double reward = kv.Value.Rewards == null ? 0 : kv.Value.Rewards[viewer];
                    if (merged.TryGetValue(kv.Key, out var cur))
                        merged[kv.Key] = (cur.Visits + kv.Value.Visits, cur.Reward + reward,
                            kv.Value.Visits > cur.Best.Visits ? kv.Value : cur.Best);
                    else
                        merged[kv.Key] = (kv.Value.Visits, reward, kv.Value);
                }

            string bestKey = null;
            int bestVisits = -1;
            foreach (var kv in merged)
                if (kv.Value.Visits > bestVisits ||
                    (kv.Value.Visits == bestVisits && string.CompareOrdinal(kv.Key, bestKey) < 0))
                {
                    bestVisits = kv.Value.Visits;
                    bestKey = kv.Key;
                }

            if (bestKey == null)
            {
                plan = null;
                rootQ = -1;
                return model.ChooseAction(live, viewer);
            }
            var (mergedVisits, mergedReward, winner) = merged[bestKey];
            rootQ = mergedVisits > 0 ? mergedReward / mergedVisits : -1;
            plan = new ShardsIsmcts.PlanCursor { Node = winner.Node };
            return winner.Action ?? model.ChooseAction(live, viewer);
        }
    }
}
