using System;
using System.Collections.Generic;
using System.Linq;
using Shards.Bots;
using Shards.Content;
using Shards.Engine;

namespace SoiSim
{
    /// <summary>Strength report: Current weights vs the whole reference pool (random,
    /// heuristic-v1, every stored weight version), mirrored pairs, Wilson intervals.
    /// The release gates: ≥65% vs heuristic (greedy), never <95% vs random.</summary>
    public static class EvaluateCommand
    {
        public static int Run(Cli cli)
        {
            int games = cli.GetInt("--games", 1000);
            int threads = cli.GetInt("--threads", Math.Max(1, Environment.ProcessorCount - 1));
            ulong seedBase = cli.GetULong("--seed-base", 424242);
            cli.RejectUnknown();

            ShardsCardDatabase.Clear();
            ShardsContentRegistry.EnsureRegistered();

            var tournament = new Tournament(threads);
            var current = ShardsEvalWeights.Current;
            var versions = WeightsEmitter.ExistingVectors();
            string currentName = versions.LastOrDefault(v => ReferenceEquals(v.Vector, current)).Name ?? "Current";

            Console.WriteLine($"evaluate: {currentName} vs pool, {games} mirrored games each");
            Console.WriteLine();
            Console.WriteLine("| Opponent | Win rate | 95% CI |");
            Console.WriteLine("|---|---|---|");

            bool gatesOk = true;
            foreach (var (name, opponent) in Pool(versions, currentName))
            {
                double wr = Play(tournament, current, opponent, games, seedBase, threads);
                var (lo, hi) = Stats.Wilson((int)Math.Round(wr * games), games);
                Console.WriteLine($"| {name} | {wr:P1} | [{lo:P1}–{hi:P1}] |");
                if (name == "heuristic-v1" && wr < 0.65) gatesOk = false;
                if (name == "random" && wr < 0.95) gatesOk = false;
            }
            Console.WriteLine();
            Console.WriteLine(gatesOk ? "gates: OK" : "gates: FAILED (heuristic ≥65% / random ≥95%)");
            return gatesOk ? 0 : 1;
        }

        private static IEnumerable<(string, Func<ulong, ShardsEngine, Pascension.Core.IBotAgent>)> Pool(
            List<(string Name, double[] Vector)> versions, string currentName)
        {
            yield return ("random", (s, e) => new ShardsHeuristicBot(s, e, random: true));
            yield return ("heuristic-v1", (s, e) => new ShardsHeuristicBot(s, e));
            foreach (var (name, vector) in versions)
            {
                if (name == currentName) continue;
                var model = new ShardsValueModel(vector);
                yield return ($"greedy-{name}", (s, e) => new ShardsGreedyEvalBot(s, e, model));
            }
        }

        private static double Play(Tournament tournament, double[] currentWeights,
            Func<ulong, ShardsEngine, Pascension.Core.IBotAgent> opponentFactory,
            int games, ulong seedBase, int threads)
        {
            var model = new ShardsValueModel(currentWeights);
            var chars = ShardsContentRegistry.CharactersFor(SimConfig.AllDlc);
            var rng = new Pascension.Engine.Core.DeterministicRng(seedBase, 77);
            var work = new List<(ulong Seed, bool CandidateFirst, string CharA, string CharB)>();
            for (int p = 0; p < games / 2; p++)
            {
                ulong s = seedBase + (ulong)p;
                int a = rng.Next(chars.Count);
                int b = rng.Next(chars.Count - 1);
                if (b >= a) b++;
                work.Add((s, true, chars[a], chars[b]));
                work.Add((s, false, chars[b], chars[a]));
            }

            double total = 0;
            object sync = new();
            System.Threading.Tasks.Parallel.ForEach(work,
                new System.Threading.Tasks.ParallelOptions { MaxDegreeOfParallelism = threads },
                item =>
                {
                    var specs = new List<Pascension.Core.PlayerSpec>
                    {
                        new() { Name = "A", CharacterId = item.CharA },
                        new() { Name = "B", CharacterId = item.CharB }
                    };
                    var adapter = new ShardsEngineAdapter(
                        ShardsContentRegistry.StandardConfig(item.Seed, specs, SimConfig.AllDlc));
                    int candidateSeat = item.CandidateFirst ? 0 : 1;
                    var seats = new Pascension.Core.IBotAgent[2];
                    for (int seat = 0; seat < 2; seat++)
                        seats[seat] = seat == candidateSeat
                            ? new ShardsGreedyEvalBot(item.Seed * 100 + (ulong)seat, adapter.Inner, model)
                            : opponentFactory(item.Seed * 100 + (ulong)seat, adapter.Inner);

                    int guard = 0;
                    double score = 0;
                    while (!adapter.GameOver && guard++ < SimGameRunner.GuardLimit)
                    {
                        var pending = adapter.PendingInput;
                        if (pending == null) break;
                        var action = seats[pending.PlayerIndex].Choose(pending, null)
                                     ?? adapter.DefaultActionFor(pending);
                        if (!adapter.Submit(action).Accepted &&
                            !adapter.Submit(adapter.DefaultActionFor(adapter.PendingInput)).Accepted)
                            break;
                    }
                    if (adapter.GameOver)
                        score = adapter.WinnerIndex < 0 ? 0.5 : adapter.WinnerIndex == candidateSeat ? 1 : 0;
                    lock (sync) total += score;
                });
            return total / work.Count;
        }
    }
}
