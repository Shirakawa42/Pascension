using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using Shards.Bots;
using Shards.Content;
using Shards.Engine;

namespace SoiSim
{
    /// <summary>CMA-ES self-play tuning of the ShardsValueModel weight vector.
    /// Optimizes in per-dimension normalized space (weight magnitudes span 0.02…2000);
    /// fitness = winrate vs the reference pool on a generation-shared schedule
    /// (common random numbers + mirrored seat pairs).</summary>
    public static class TuneCommand
    {
        public static int Run(Cli cli)
        {
            int generations = cli.GetInt("--generations", 300);
            int gamesPerCandidate = cli.GetInt("--games-per-candidate", 240);
            int lambda = cli.GetInt("--lambda", 16);
            double sigma = double.Parse(cli.GetStr("--sigma", "0.15"),
                System.Globalization.CultureInfo.InvariantCulture);
            int threads = cli.GetInt("--threads", Math.Max(1, Environment.ProcessorCount - 1));
            int seed = cli.GetInt("--seed", 1);
            bool emit = !cli.Has("--no-emit");
            string emitPath = cli.GetStr("--out", WeightsEmitter.DefaultPath);
            cli.RejectUnknown();

            ShardsCardDatabase.Clear();
            ShardsContentRegistry.EnsureRegistered();

            var start = ShardsEvalWeights.Current;
            int n = start.Length;
            var scale = new double[n];
            for (int i = 0; i < n; i++)
                scale[i] = Math.Max(Math.Abs(start[i]), 0.05);
            var mean0 = new double[n];
            for (int i = 0; i < n; i++)
                mean0[i] = start[i] / scale[i];

            var cma = new CmaEs(mean0, sigma, lambda, seed);
            var tournament = new Tournament(threads);
            var championWeights = (double[])start.Clone();
            var championHistory = new List<double[]> { championWeights };
            double championVsHeuristic = -1;

            Console.WriteLine($"tune: {generations} generations × λ{lambda} × {gamesPerCandidate} games " +
                              $"(~{(long)generations * lambda * gamesPerCandidate / 1000}k games), threads={threads}");
            using var cts = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) =>
            {
                e.Cancel = true;
                Console.WriteLine("  Ctrl-C: finishing this generation, then emitting the current champion…");
                cts.Cancel();
            };

            var sw = Stopwatch.StartNew();
            int gen = 0;
            for (; gen < generations && !cts.IsCancellationRequested; gen++)
            {
                var population = cma.Ask();
                var models = new List<ShardsValueModel>(lambda + 1);
                foreach (var x in population)
                    models.Add(new ShardsValueModel(Denormalize(x, scale)));
                // The champion rides along under the same seeds — fair comparison.
                models.Add(new ShardsValueModel(championWeights));

                var championModel = new ShardsValueModel(championWeights);
                var historicalModel = new ShardsValueModel(
                    championHistory[Math.Max(0, championHistory.Count - 5)]);
                var schedule = tournament.BuildSchedule(gamesPerCandidate, (ulong)(seed * 1_000_003 + gen));

                double[] fitness;
                try
                {
                    fitness = tournament.Evaluate(models, championModel, historicalModel, schedule, cts.Token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                cma.Tell(population, fitness.Take(lambda).ToArray());

                int best = 0;
                for (int k = 1; k < lambda; k++)
                    if (fitness[k] > fitness[best])
                        best = k;
                double championFitness = fitness[lambda];
                if (fitness[best] > championFitness)
                {
                    championWeights = Denormalize(population[best], scale);
                    championHistory.Add(championWeights);
                }

                if (gen % 10 == 0 || gen == generations - 1)
                {
                    championVsHeuristic = MeasureVsHeuristic(championWeights, tournament, threads, 200,
                        (ulong)(seed * 7_000_017 + gen));
                    Console.WriteLine($"  gen {gen,4}  best {fitness[best]:F3}  champ {championFitness:F3}  " +
                                      $"σ {cma.Sigma:F3}  vsHeuristic {championVsHeuristic:P1}  " +
                                      $"{sw.Elapsed.TotalSeconds:F0}s");
                }
            }
            sw.Stop();

            championVsHeuristic = MeasureVsHeuristic(championWeights, tournament, threads, 1000,
                (ulong)(seed * 9_000_041));
            Console.WriteLine($"tune: done after {gen} generations in {sw.Elapsed.TotalMinutes:F1} min; " +
                              $"champion vs heuristic-v1: {championVsHeuristic:P1} (gate ≥ 65%)");

            if (emit)
            {
                string version = WeightsEmitter.NextVersionName();
                WeightsEmitter.Emit(emitPath, championWeights, version,
                    $"{version}: sep-CMA-ES self-play, {gen} gens × λ{lambda} × {gamesPerCandidate} games, " +
                    $"seed {seed}, vs-heuristic {championVsHeuristic:P1} ({DateTime.Now:yyyy-MM-dd}).");
            }
            return championVsHeuristic >= 0.65 ? 0 : 1;
        }

        private static double[] Denormalize(double[] x, double[] scale)
        {
            var w = new double[x.Length];
            for (int i = 0; i < x.Length; i++)
                w[i] = x[i] * scale[i];
            return w;
        }

        /// <summary>Mirrored candidate-vs-heuristic winrate on fresh seeds.</summary>
        public static double MeasureVsHeuristic(double[] weights, Tournament tournament, int threads,
            int games, ulong seedBase)
        {
            var model = new ShardsValueModel(weights);
            var schedule = new List<Tournament.Matchup>();
            var chars = ShardsContentRegistry.CharactersFor(SimConfig.AllDlc);
            var rng = new Pascension.Engine.Core.DeterministicRng(seedBase, 313);
            for (int p = 0; p < games / 2; p++)
            {
                ulong s = seedBase * 100000 + (ulong)p;
                int a = rng.Next(chars.Count);
                int b = rng.Next(chars.Count - 1);
                if (b >= a) b++;
                schedule.Add(new Tournament.Matchup { Seed = s, Opponent = Tournament.OpponentKind.Heuristic, CandidateFirst = true, CharA = chars[a], CharB = chars[b] });
                schedule.Add(new Tournament.Matchup { Seed = s, Opponent = Tournament.OpponentKind.Heuristic, CandidateFirst = false, CharA = chars[b], CharB = chars[a] });
            }
            double total = 0;
            object sync = new();
            System.Threading.Tasks.Parallel.ForEach(schedule,
                new System.Threading.Tasks.ParallelOptions { MaxDegreeOfParallelism = threads },
                m =>
                {
                    double score = tournament.PlayOne(model, model, model, m);
                    lock (sync) total += score;
                });
            return total / schedule.Count;
        }
    }
}
