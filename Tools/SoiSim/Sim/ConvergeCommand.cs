using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Pascension.Core;
using Pascension.Engine.Core;
using Shards.Bots;
using Shards.Content;
using Shards.Engine;

namespace SoiSim
{
    /// <summary>Convergence study: at sampled priority points, run the SAME search at a
    /// ladder of iteration budgets and record the chosen move at each. Because the
    /// search is seeded identically, budget b's search is a prefix of budget 2b's — so
    /// this measures how fast the argmax settles onto the deep-search ("final") move.
    /// Answers "how many iterations until the decision stops changing" — the budget the
    /// fixed-iteration ranks should use. Early stop is DISABLED so each budget is exact.</summary>
    public static class ConvergeCommand
    {
        public static int Run(Cli cli)
        {
            int netGen = cli.GetInt("--net", -1);
            int truncate = cli.GetInt("--truncate", 2);
            int games = cli.GetInt("--games", 40);
            int stride = cli.GetInt("--sample-stride", 3);
            ulong seedBase = cli.GetULong("--seed-base", 50000);
            int threads = cli.GetInt("--threads", Math.Max(1, Environment.ProcessorCount - 1));
            var budgets = cli.GetStr("--budgets", "16,32,64,128,256,512,1024,2048")
                .Split(',').Select(int.Parse).OrderBy(b => b).ToArray();
            cli.RejectUnknown();

            int maxBudget = budgets[^1];
            ShardsCardDatabase.Clear();
            ShardsContentRegistry.EnsureRegistered();
            var chars = ShardsContentRegistry.CharactersFor(SimConfig.AllDlc);
            var model = new ShardsValueModel();
            IShardsValueEvaluator eval = truncate >= 0
                ? (netGen >= 0 ? ShardsNeuralEval.LoadGeneration(netGen) : ShardsNeuralEval.LoadCurrent())
                : null;

            Console.WriteLine($"converge: net gen {(netGen >= 0 ? netGen : ShardsNetWeights.Generation)}, " +
                              $"truncate {truncate}, budgets [{string.Join(",", budgets)}], {games} games, " +
                              $"every {stride}th priority point");

            // agree[i] = # sampled points where budget[i]'s move == the max-budget move.
            long[] agree = new long[budgets.Length];
            long[] stable = new long[budgets.Length]; // move[i] == move[i-1]
            long sampled = 0;
            var sw = Stopwatch.StartNew();

            Parallel.For(0, games, new ParallelOptions { MaxDegreeOfParallelism = threads }, g =>
            {
                ulong seed = seedBase + (ulong)g;
                var rng = new DeterministicRng(seed * 31 + 7, 91);
                int a = rng.Next(chars.Count);
                int b = rng.Next(chars.Count - 1);
                if (b >= a) b++;
                var specs = new List<PlayerSpec>
                {
                    new() { Name = "S0", CharacterId = chars[a] },
                    new() { Name = "S1", CharacterId = chars[b] }
                };
                var adapter = new ShardsEngineAdapter(
                    ShardsContentRegistry.StandardConfig(seed, specs, SimConfig.AllDlc));
                // Drive with instant greedy to reach realistic positions cheaply; the
                // convergence measurement itself uses the real net-truncated search.
                var driver = new IBotAgent[]
                {
                    new ShardsGreedyEvalBot(seed * 100, adapter.Inner, model),
                    new ShardsGreedyEvalBot(seed * 100 + 1, adapter.Inner, model)
                };

                var localAgree = new long[budgets.Length];
                var localStable = new long[budgets.Length];
                long localSampled = 0;
                int priorityPoints = 0, guard = 0;
                while (!adapter.GameOver && guard++ < SimGameRunner.GuardLimit)
                {
                    var pending = adapter.PendingInput;
                    if (pending == null) break;
                    if (pending.Kind == PendingInputKind.Priority && priorityPoints++ % stride == 0)
                    {
                        string prev = null, final = null;
                        var moves = new string[budgets.Length];
                        for (int i = 0; i < budgets.Length; i++)
                        {
                            var cfg = ShardsSearchConfig.ForSims(budgets[i]);
                            cfg.RolloutEndTurns = truncate;
                            cfg.EarlyStopWhenDecided = false; // measure exact per-budget argmax
                            var bot = new ShardsSearchBot(seed * 977 + (ulong)priorityPoints,
                                adapter.Inner, cfg, model, eval);
                            moves[i] = bot.Choose(pending, null)?.Describe() ?? "";
                        }
                        final = moves[^1];
                        for (int i = 0; i < budgets.Length; i++)
                        {
                            if (moves[i] == final) localAgree[i]++;
                            if (prev != null && moves[i] == prev) localStable[i]++;
                            prev = moves[i];
                        }
                        localSampled++;
                    }
                    var action = driver[pending.PlayerIndex].Choose(pending, null)
                                 ?? adapter.DefaultActionFor(pending);
                    if (!adapter.Submit(action).Accepted &&
                        !adapter.Submit(adapter.DefaultActionFor(adapter.PendingInput)).Accepted)
                        break;
                }

                lock (agree)
                {
                    for (int i = 0; i < budgets.Length; i++)
                    {
                        agree[i] += localAgree[i];
                        stable[i] += localStable[i];
                    }
                    sampled += localSampled;
                    if (sampled > 0 && (g & 7) == 0)
                        CampaignStatus.Update("converge",
                            $"**Progress**: {g + 1}/{games} games · {sampled:N0} decisions sampled · " +
                            $"{sampled / sw.Elapsed.TotalSeconds:F0} decisions/s");
                }
            });
            sw.Stop();

            Console.WriteLine($"converge: {sampled:N0} decisions from {games} games in {sw.Elapsed.TotalSeconds:F1}s");
            Console.WriteLine($"  {"budget",8} | {"match final",12} | {"match prev",11}");
            for (int i = 0; i < budgets.Length; i++)
            {
                double mf = sampled == 0 ? 0 : 100.0 * agree[i] / sampled;
                double mp = sampled == 0 ? 0 : 100.0 * stable[i] / sampled;
                Console.WriteLine($"  {budgets[i],8} | {mf,10:F1} % | {(i == 0 ? "-" : mp.ToString("F1") + " %"),11}");
            }
            CampaignStatus.Complete("converge",
                $"converge net {(netGen >= 0 ? netGen : ShardsNetWeights.Generation)} T{truncate}: " +
                string.Join("  ", budgets.Select((bd, i) =>
                    $"{bd}:{(sampled == 0 ? 0 : 100.0 * agree[i] / sampled):F0}%")));
            return 0;
        }
    }
}
