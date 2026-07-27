using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Pascension.Core;
using Pascension.Engine.Actions;
using Pascension.Engine.Core;
using Shards.Bots;
using Shards.Content;
using Shards.Engine;

namespace SoiSim
{
    /// <summary>Options for <see cref="RankCommand"/> — kept as a struct-of-knobs so the
    /// smoke tests can call <see cref="RankCommand.RunCore"/> directly with tiny sizes.</summary>
    public sealed class RankOptions
    {
        public int Games = 100;
        public ulong SeedBase = 880000;
        public int Threads = Math.Max(1, Environment.ProcessorCount - 1);
        public int MinRound = 4;
        /// <summary>Sample every Nth eligible turn (both seats — the game is a V5 mirror).</summary>
        public int Stride = 2;
        /// <summary>Cap on sampled decision points per game.</summary>
        public int MaxPointsPerGame = 6;
        /// <summary>Ground-truth rollouts per candidate leaf (split in half for the CV
        /// selector — see the headroom block below).</summary>
        public int Rollouts = 64;
        /// <summary>0 = evaluate every legal candidate; else only the K best by the
        /// policy's own action score (mirrors a top-k reranking planner).</summary>
        public int TopK = 0;
        /// <summary>true = the shipping planner's tail (no further gem spends);
        /// false = full greedy completion (the 4% version's tail).</summary>
        public bool FrozenTail = true;
        /// <summary>false = siblings are candidate FIRST ACTIONS (the shipped planner's
        /// granularity). true = siblings are complete PURCHASE BASKETS — the coarser unit
        /// the ablation says carries 84-92% of the strength. Measured before any basket
        /// planner is built, because the action-granularity result (headroom ≈ 0) shows
        /// granularity is exactly what decides whether a planner can win at all.</summary>
        public bool Baskets;
    }

    /// <summary>Aggregate result of a rank run — what the smoke tests pin.</summary>
    public sealed class RankResult
    {
        public int Points;
        public long Pairs;
        public double MeanAbsDelta;       // |Δtruth| over sibling pairs — what is at stake
        public double MeanTopGap;         // truth gap between best and second-best sibling
        public double PolicyBestShare;    // how often V5's pick is also the truth-best leaf
        public double MeanRawRegret;      // truthBest − truth(policy pick); selection-biased UP
        /// <summary>Truth uplift per decision if the pick were made by each scorer instead
        /// of the policy. Index: 0=rollout-CV, 1=L1 fitted, 2=L0 baseline, 3=clock.
        /// Unbiased: selection uses one half of the rollouts (or the scorer, which never
        /// sees rollouts), valuation uses the other half.</summary>
        public readonly double[] Headroom = new double[4];
        /// <summary>[scorer, bucket] pairwise sign-agreement with truth. Scorers:
        /// 0=L1, 1=L0, 2=clock, 3=policy root score. Buckets by |Δtruth|.</summary>
        public readonly long[,] Agree = new long[4, RankCommand.Buckets];
        public readonly long[,] Total = new long[4, RankCommand.Buckets];

        public double Agreement(int scorer, int bucket) =>
            Total[scorer, bucket] == 0 ? 0.5 : (double)Agree[scorer, bucket] / Total[scorer, bucket];
    }

    /// <summary>`rank` — does the evaluator RANK SIBLING candidate turns correctly?
    ///
    /// Holdout accuracy on random mid-game positions is a screening tool; a planner never
    /// compares random positions. It compares SIBLINGS: the end-of-turn leaves of the
    /// candidate actions available at one decision point, which differ by a card or two.
    /// Whether an evaluator can see THOSE differences is a different and much harder
    /// question than 64% holdout accuracy, and it was never measured — the planner was
    /// built on the assumption and scored 12.7%.
    ///
    /// Method: drive real bench:greedy-v5 mirror games; at sampled turn starts, build each
    /// candidate's end-of-turn leaf exactly the way the planner does (same CompleteTurn
    /// code path, common rng reseed across siblings), then estimate each leaf's TRUE value
    /// with N policy rollouts to terminal (common random numbers across siblings, hidden
    /// zones re-determinized per rollout). Leaves are built from the true state — the
    /// oracle experiment bounds hidden information at +38 Elo, so determinizing here would
    /// only add noise to a question about the evaluator.
    ///
    /// Two families of numbers come out:
    ///  · pairwise sign agreement per scorer, bucketed by |Δtruth| — can the scorer see a
    ///    5/10/20-point true difference between siblings at all;
    ///  · HEADROOM per decision — the truth uplift of replacing the policy's pick with each
    ///    scorer's pick, cross-validated so rollout noise cannot flatter it. This is the
    ///    number that decides whether ANY reranking planner can work: if it is ~0 for every
    ///    scorer, nothing steered by leaf values can beat the policy at this granularity,
    ///    and the search has to move to a coarser unit (purchase baskets) or a different
    ///    signal (full rollouts, as ISMCTS above the crossover already proves works).</summary>
    public static class RankCommand
    {
        public const int Buckets = 4;
        // |Δtruth| bucket edges. With 32-rollout half-samples the paired-diff noise floor
        // is ~0.07, so the bottom bucket is mostly attenuation — read the upper three.
        private static readonly double[] BucketLo = { 0.05, 0.10, 0.20, 0.35 };

        private sealed class PointRecord
        {
            public double RawRegret, TopGap, AbsDeltaSum;
            public long Pairs;
            public bool PolicyBest;
            public readonly double[] Headroom = new double[4];
            public readonly long[,] Agree = new long[4, Buckets];
            public readonly long[,] Total = new long[4, Buckets];
        }

        public static int Run(Cli cli)
        {
            var opt = new RankOptions
            {
                Games = cli.GetInt("--games", 100),
                SeedBase = cli.GetULong("--seed-base", 880000),
                Threads = cli.GetInt("--threads", Math.Max(1, Environment.ProcessorCount - 1)),
                MinRound = cli.GetInt("--min-round", 4),
                Stride = Math.Max(1, cli.GetInt("--stride", 2)),
                MaxPointsPerGame = cli.GetInt("--points-per-game", 6),
                Rollouts = Math.Max(4, cli.GetInt("--rollouts", 64)),
                TopK = cli.GetInt("--topk", 0),
                FrozenTail = cli.GetStr("--tail", "frozen") switch
                {
                    "frozen" => true,
                    "greedy" => false,
                    var t => throw new CliError($"--tail expects frozen|greedy, got '{t}'")
                },
                Baskets = cli.GetStr("--siblings", "actions") switch
                {
                    "actions" => false,
                    "baskets" => true,
                    var s => throw new CliError($"--siblings expects actions|baskets, got '{s}'")
                }
            };
            cli.RejectUnknown();

            var sw = Stopwatch.StartNew();
            var result = RunCore(opt, line => Console.WriteLine(line));
            Console.WriteLine($"  {sw.Elapsed.TotalSeconds:F0}s");
            CampaignStatus.Complete("rank",
                $"rank {result.Points} points / {result.Pairs} sibling pairs: policy pick truth-best " +
                $"{result.PolicyBestShare:P1}, headroom/decision rolloutCV {result.Headroom[0]:+0.000;-0.000} · " +
                $"L1 {result.Headroom[1]:+0.000;-0.000} · L0 {result.Headroom[2]:+0.000;-0.000} · " +
                $"clock {result.Headroom[3]:+0.000;-0.000}; L1 pair-agreement " +
                $"{result.Agreement(0, 1):P1} at |Δ|∈[0.10,0.20)");
            return 0;
        }

        public static RankResult RunCore(RankOptions opt, Action<string> log)
        {
            ShardsCardDatabase.Clear();
            ShardsContentRegistry.EnsureRegistered();
            var chars = ShardsContentRegistry.CharactersFor(SimConfig.AllDlc);
            // The frozen benchmark policy drives the games, the leaves AND the rollouts —
            // the question is about the evaluators, so the policy is held at the reference.
            var model = new ShardsValueModel(W.Pad(ShardsEvalWeights.V5));
            var evalL1 = new ShardsLinearEval(ShardsEvalLinearWeights.Current);
            var evalL0 = new ShardsLinearEval(ShardsEvalLinearWeights.L0);
            var evalClock = new ShardsClockEval();

            log($"rank: {opt.Games} games, {opt.Rollouts} rollouts/candidate, " +
                (opt.Baskets
                    ? "siblings BASKETS, "
                    : $"siblings actions, tail {(opt.FrozenTail ? "frozen" : "greedy")}, " +
                      $"topk {(opt.TopK > 0 ? opt.TopK.ToString() : "all")}, ") +
                $"dlc mask {(int)SimConfig.AllDlc}");

            // Deterministic collection: one slot per game, merged in game order — the same
            // discipline that fixed `fit`. Nothing downstream may see the scheduler.
            var perGame = new List<PointRecord>[opt.Games];
            Parallel.For(0, opt.Games, new ParallelOptions { MaxDegreeOfParallelism = opt.Threads },
                g => { perGame[g] = PlayGame(opt, chars, model, evalL1, evalL0, evalClock, g); });

            var points = new List<PointRecord>();
            foreach (var bucket in perGame)
                if (bucket != null)
                    points.AddRange(bucket);

            var r = new RankResult { Points = points.Count };
            if (points.Count == 0) { log("  no points sampled"); return r; }
            foreach (var p in points)
            {
                r.Pairs += p.Pairs;
                r.MeanAbsDelta += p.AbsDeltaSum;
                r.MeanTopGap += p.TopGap;
                r.MeanRawRegret += p.RawRegret;
                if (p.PolicyBest) r.PolicyBestShare++;
                for (int s = 0; s < 4; s++)
                {
                    r.Headroom[s] += p.Headroom[s];
                    for (int b = 0; b < Buckets; b++)
                    {
                        r.Agree[s, b] += p.Agree[s, b];
                        r.Total[s, b] += p.Total[s, b];
                    }
                }
            }
            r.MeanAbsDelta /= Math.Max(1, r.Pairs);
            r.MeanTopGap /= points.Count;
            r.MeanRawRegret /= points.Count;
            r.PolicyBestShare /= points.Count;
            for (int s = 0; s < 4; s++) r.Headroom[s] /= points.Count;

            log($"  {r.Points} points · {r.Pairs} sibling pairs · mean |Δtruth| {r.MeanAbsDelta:F3} · " +
                $"top-2 truth gap {r.MeanTopGap:F3}");
            log($"  policy pick is the truth-best leaf at {r.PolicyBestShare:P1} of points · " +
                $"raw regret {r.MeanRawRegret:F3}/decision (selection-biased up — headroom below is the honest number)");
            log("  headroom per decision (truth uplift of replacing the policy pick, cross-validated):");
            log($"    rollout-CV({opt.Rollouts / 2,3})  {r.Headroom[0]:+0.0000;-0.0000}   ← what a rollout reranker would gain");
            log($"    L1 fitted        {r.Headroom[1]:+0.0000;-0.0000}");
            log($"    L0 baseline      {r.Headroom[2]:+0.0000;-0.0000}");
            log($"    clock            {r.Headroom[3]:+0.0000;-0.0000}");
            log("  pairwise ranking agreement vs truth (siblings, by |Δtruth|):");
            log("    bucket        pairs      L1       L0     clock    policy");
            for (int b = 0; b < Buckets; b++)
            {
                string lo = BucketLo[b].ToString("F2");
                string hi = b + 1 < Buckets ? BucketLo[b + 1].ToString("F2") : "1.00";
                log($"    {lo}-{hi}    {r.Total[0, b],7}   {r.Agreement(0, b),6:P1}  {r.Agreement(1, b),6:P1}  " +
                    $"{r.Agreement(2, b),6:P1}  {r.Agreement(3, b),6:P1}");
            }
            return r;
        }

        private static List<PointRecord> PlayGame(RankOptions opt, IReadOnlyList<string> chars,
            ShardsValueModel model, IShardsValueEvaluator evalL1, IShardsValueEvaluator evalL0,
            IShardsValueEvaluator evalClock, int g)
        {
            ulong seed = opt.SeedBase + (ulong)g;
            var rng = new DeterministicRng(seed * 31 + 7, 91);
            int a = rng.Next(chars.Count);
            int b = rng.Next(chars.Count - 1);
            if (b >= a) b++;
            var adapter = new ShardsEngineAdapter(ShardsContentRegistry.StandardConfig(
                seed, new List<PlayerSpec>
                {
                    new() { Name = "S0", CharacterId = chars[a] },
                    new() { Name = "S1", CharacterId = chars[b] }
                }, SimConfig.AllDlc));
            var seats = new IBotAgent[]
            {
                new ShardsGreedyEvalBot(seed * 100, adapter.Inner, model),
                new ShardsGreedyEvalBot(seed * 100 + 1, adapter.Inner, model)
            };

            var records = new List<PointRecord>();
            var leafArena = new ShardsCloneArena();
            var rolloutArena = new ShardsCloneArena();
            (int Round, int Player) lastTurnKey = (-1, -1);
            int turnCounter = 0, pointIndex = 0;
            int guard = 0;
            while (!adapter.GameOver && guard++ < SimGameRunner.GuardLimit)
            {
                var pending = adapter.PendingInput;
                if (pending == null) break;
                var state = adapter.Inner.State;
                if (pending.Kind == PendingInputKind.Priority &&
                    state.TurnPlayerIndex == pending.PlayerIndex &&
                    state.Round >= opt.MinRound &&
                    records.Count < opt.MaxPointsPerGame)
                {
                    var key = (state.Round, state.TurnPlayerIndex);
                    if (key != lastTurnKey)
                    {
                        lastTurnKey = key;
                        if (turnCounter++ % opt.Stride == 0)
                        {
                            var record = SamplePoint(opt, adapter.Inner, pending.PlayerIndex,
                                model, evalL1, evalL0, evalClock,
                                Mix(seed, (ulong)pointIndex), leafArena, rolloutArena);
                            pointIndex++;
                            if (record != null) records.Add(record);
                        }
                    }
                }
                var action = seats[pending.PlayerIndex].Choose(pending, null)
                             ?? adapter.DefaultActionFor(pending);
                if (!adapter.Submit(action).Accepted &&
                    !adapter.Submit(adapter.DefaultActionFor(adapter.PendingInput)).Accepted)
                    break;
            }
            return records;
        }

        /// <summary>Per-point accumulator: one entry per surviving sibling leaf.</summary>
        private sealed class LeafSet
        {
            public readonly List<double> TruthA = new();  // selection half (CV)
            public readonly List<double> TruthB = new();  // valuation half
            public readonly List<double> L1 = new();
            public readonly List<double> L0 = new();
            public readonly List<double> Clock = new();
            /// <summary>Scorer 3 — the policy's own preference. Action mode: the root
            /// action score. Basket mode: summed item CardValue (NaN for the natural
            /// basket, which has no prescription; NaN pairs are skipped).</summary>
            public readonly List<double> Prior = new();
        }

        /// <summary>One decision point: build every sibling's end-of-turn leaf, score it
        /// with each evaluator, ground-truth it with rollouts, and tally the comparisons.</summary>
        private static PointRecord SamplePoint(RankOptions opt, ShardsEngine engine, int me,
            ShardsValueModel model, IShardsValueEvaluator evalL1, IShardsValueEvaluator evalL0,
            IShardsValueEvaluator evalClock, ulong pointSeed, ShardsCloneArena leafArena,
            ShardsCloneArena rolloutArena)
        {
            var leaves = new LeafSet();
            int policyPick;
            if (opt.Baskets)
            {
                if (!SampleBasketSiblings(opt, engine, me, model, evalL1, evalL0, evalClock,
                        pointSeed, leafArena, rolloutArena, leaves))
                    return null;
                policyPick = 0; // the natural (unconstrained V5) turn is always added first
            }
            else
            {
                if (!SampleActionSiblings(opt, engine, me, model, evalL1, evalL0, evalClock,
                        pointSeed, leafArena, rolloutArena, leaves))
                    return null;
                policyPick = ArgMax(leaves.Prior);
            }
            return Tally(leaves, policyPick);
        }

        /// <summary>Siblings = candidate first actions + the shared planner tail — the
        /// shipped planner's exact granularity.</summary>
        private static bool SampleActionSiblings(RankOptions opt, ShardsEngine engine, int me,
            ShardsValueModel model, IShardsValueEvaluator evalL1, IShardsValueEvaluator evalL0,
            IShardsValueEvaluator evalClock, ulong pointSeed, ShardsCloneArena leafArena,
            ShardsCloneArena rolloutArena, LeafSet leaves)
        {
            var player = engine.State.Players[me];
            var legal = engine.LegalActions(me);
            var candidates = new List<(PlayerAction Action, double RootScore)>();
            foreach (var action in legal)
            {
                if (action is ConcedeAction) continue;
                candidates.Add((action, model.ScoreAction(engine, player, action)));
            }
            if (candidates.Count < 2) return false;
            if (opt.TopK > 0 && candidates.Count > opt.TopK)
                candidates = candidates.OrderByDescending(c => c.RootScore).Take(opt.TopK).ToList();

            ulong worldSeed = Mix(pointSeed, 0xC0FFEE) | 1UL;
            foreach (var (candidate, rootScore) in candidates)
            {
                // The planner's exact leaf: common reseed across siblings (CRN), the shared
                // CompleteTurn tail, evaluation at the end-of-turn position.
                var leaf = engine.Fork(rngReseed: worldSeed, quiet: true, arena: leafArena);
                if (!leaf.Submit(candidate).Accepted) continue;
                ShardsPlannerBot.CompleteTurn(leaf, me, model, opt.FrozenTail, 400);
                ScoreLeaf(opt, leaf, me, model, evalL1, evalL0, evalClock, pointSeed,
                    rolloutArena, leaves, rootScore);
            }
            return leaves.TruthB.Count >= 2;
        }

        /// <summary>Siblings = complete PURCHASE BASKETS: the natural V5 turn plus
        /// prescribed spend-sets (row defs / focus / hero ability) executed by a
        /// constrained tail. Duplicate outcomes (a basket the turn could not fill
        /// collapses into a smaller one) are deduplicated by full state hash, so every
        /// surviving sibling is a genuinely different turn.</summary>
        private static bool SampleBasketSiblings(RankOptions opt, ShardsEngine engine, int me,
            ShardsValueModel model, IShardsValueEvaluator evalL1, IShardsValueEvaluator evalL0,
            IShardsValueEvaluator evalClock, ulong pointSeed, ShardsCloneArena leafArena,
            ShardsCloneArena rolloutArena, LeafSet leaves)
        {
            var player = engine.State.Players[me];
            // The bot's own enumeration and turn execution — the measured thing IS the
            // shipped thing (ShardsBasketPlannerBot shares this exact code path).
            var baskets = ShardsBasketPlannerBot.EnumerateBaskets(engine, me, model);
            ulong worldSeed = Mix(pointSeed, 0xC0FFEE) | 1UL;
            var leafHashes = new HashSet<ulong>();
            for (int i = 0; i < baskets.Count; i++)
            {
                var basket = baskets[i];
                var leaf = engine.Fork(rngReseed: worldSeed, quiet: true, arena: leafArena);
                ShardsBasketPlannerBot.RunToTurnEnd(leaf, me, model, basket, 400);
                double prior;
                if (basket.Defs == null)
                {
                    prior = double.NaN; // natural turn: no prescription to price
                }
                else
                {
                    prior = 0;
                    foreach (string id in basket.Defs)
                        if (ShardsCardDatabase.TryGet(id, out var def))
                            prior += model.CardValue(def, player.Mastery);
                    if (basket.Focus || basket.Hero) prior += 0.4; // nominal — diagnostic only
                }
                if (!leafHashes.Add(leaf.State.ComputeFullHash()))
                {
                    // Identical turn — but the INCUMBENT must survive: without index 0 the
                    // headroom comparison has no baseline, so never dedup the natural leaf.
                    if (i != 0) continue;
                }
                bool scored = ScoreLeaf(opt, leaf, me, model, evalL1, evalL0, evalClock,
                    pointSeed, rolloutArena, leaves, prior);
                if (i == 0 && !scored)
                    return false; // no incumbent, no headroom — drop the point
            }
            return leaves.TruthB.Count >= 2;
        }

        /// <summary>Ground-truths one leaf with CRN rollouts and appends every scorer's
        /// view of it. Returns false when the leaf cannot be scored (parked mid-decision,
        /// so the engine cannot fork it).</summary>
        private static bool ScoreLeaf(RankOptions opt, ShardsEngine leaf, int me,
            ShardsValueModel model, IShardsValueEvaluator evalL1, IShardsValueEvaluator evalL0,
            IShardsValueEvaluator evalClock, ulong pointSeed, ShardsCloneArena rolloutArena,
            LeafSet leaves, double prior)
        {
            int half = opt.Rollouts / 2;
            double sumA = 0, sumB = 0;
            if (leaf.State.GameOver)
            {
                double terminal = leaf.State.WinnerIndex < 0 ? 0.5
                    : leaf.State.WinnerIndex == me ? 1 : 0;
                sumA = terminal * half;
                sumB = terminal * (opt.Rollouts - half);
            }
            else if (leaf.PendingInput?.Kind != PendingInputKind.Priority)
            {
                return false; // cannot fork mid-decision
            }
            else
            {
                for (int j = 0; j < opt.Rollouts; j++)
                {
                    // Same rollout seed for every sibling (CRN — the shuffle cancels).
                    ulong rs = Mix(pointSeed, 0xB00 + (ulong)j) | 1UL;
                    var rf = leaf.Fork(rngReseed: rs, quiet: true, arena: rolloutArena);
                    ShardsDeterminizer.Sample(rf.State, me, rf.State.Rng);
                    double outcome = Rollout(rf, me, model);
                    if (j < half) sumA += outcome; else sumB += outcome;
                }
            }
            leaves.TruthA.Add(sumA / Math.Max(1, half));
            leaves.TruthB.Add(sumB / Math.Max(1, opt.Rollouts - half));
            leaves.L1.Add(evalL1.Evaluate(leaf.State, me));
            leaves.L0.Add(evalL0.Evaluate(leaf.State, me));
            leaves.Clock.Add(evalClock.Evaluate(leaf.State, me));
            leaves.Prior.Add(prior);
            return true;
        }

        private static PointRecord Tally(LeafSet leaves, int policyPick)
        {
            var truthA = leaves.TruthA;
            var truthB = leaves.TruthB;
            var l1 = leaves.L1;
            var l0 = leaves.L0;
            var clock = leaves.Clock;
            var rootScores = leaves.Prior;
            int n = truthB.Count;
            if (n < 2) return null;

            // Truth for comparisons = the FULL sample (A+B) — pairwise agreement wants the
            // least-noisy reference. Headroom uses the A/B split so selection can't peek.
            var truth = new double[n];
            for (int i = 0; i < n; i++) truth[i] = (truthA[i] + truthB[i]) / 2;

            var record = new PointRecord();
            int truthBest = ArgMax(truth);
            record.PolicyBest = truthBest == policyPick;
            record.RawRegret = truth[truthBest] - truth[policyPick];
            double second = double.NegativeInfinity;
            for (int i = 0; i < n; i++)
                if (i != truthBest) second = Math.Max(second, truth[i]);
            record.TopGap = truth[truthBest] - second;

            // Headroom, cross-validated: pick with a signal that never saw half B, then
            // value BOTH that pick and the policy pick on half B alone.
            record.Headroom[0] = truthB[ArgMax(truthA)] - truthB[policyPick];
            record.Headroom[1] = truthB[ArgMax(l1)] - truthB[policyPick];
            record.Headroom[2] = truthB[ArgMax(l0)] - truthB[policyPick];
            record.Headroom[3] = truthB[ArgMax(clock)] - truthB[policyPick];

            var scorers = new[] { l1, l0, clock, rootScores };
            for (int i = 0; i < n; i++)
                for (int k = i + 1; k < n; k++)
                {
                    double delta = truth[i] - truth[k];
                    record.AbsDeltaSum += Math.Abs(delta);
                    record.Pairs++;
                    int bucket = BucketOf(Math.Abs(delta));
                    if (bucket < 0) continue;
                    for (int s = 0; s < 4; s++)
                    {
                        double d = scorers[s][i] - scorers[s][k];
                        // NaN = no prescription to price (the natural basket); 0 = no
                        // opinion — count neither way.
                        if (double.IsNaN(d) || d == 0) continue;
                        record.Total[s, bucket]++;
                        if (d > 0 == delta > 0) record.Agree[s, bucket]++;
                    }
                }
            return record;
        }

        private static double Rollout(ShardsEngine rf, int me, ShardsValueModel model) =>
            ShardsBasketPlannerBot.RolloutToTerminal(rf, me, model, SimGameRunner.GuardLimit);

        private static int BucketOf(double absDelta)
        {
            for (int b = Buckets - 1; b >= 0; b--)
                if (absDelta >= BucketLo[b])
                    return b;
            return -1;
        }

        private static int ArgMax(IReadOnlyList<double> values)
        {
            int best = 0;
            for (int i = 1; i < values.Count; i++)
                if (values[i] > values[best])
                    best = i;
            return best;
        }

        /// <summary>splitmix64-style deterministic mixer for derived seeds.</summary>
        private static ulong Mix(ulong a, ulong b)
        {
            ulong z = a + 0x9E3779B97F4A7C15UL * (b + 1);
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            return z ^ (z >> 31);
        }
    }
}
