using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Pascension.Core;
using Pascension.Engine.Core;
using Shards.Bots;
using Shards.Content;
using Shards.Engine;

namespace SoiSim
{
    /// <summary>Generates value-net training positions from self-play. Two modes:
    /// --budget 0 = greedy bootstrap (thousands of games/min, flags bit0 set, labels
    /// z-only) for generation 0; --budget N = ISMCTS self-play at N iterations for
    /// later generations. Positions are sampled at priority points (reservoir, both
    /// seats' perspectives) and labeled with the final outcome z.</summary>
    public static class SelfplayCommand
    {
        public static int Run(Cli cli)
        {
            int games = cli.GetInt("--games", 20000);
            int budget = cli.GetInt("--budget", 0);
            int perGame = cli.GetInt("--positions-per-game", 12);
            int truncate = cli.GetInt("--truncate", 2);
            int tempTurns = cli.GetInt("--temp-turns", 8);
            ulong seedBase = cli.GetULong("--seed-base", 1);
            int threads = cli.GetInt("--threads", Math.Max(1, Environment.ProcessorCount - 1));
            string outDir = cli.GetStr("--out",
                Path.Combine(SimConfig.FindRepoRoot(), "Tools", "ShardsData", "selfplay", "gen0"));
            cli.RejectUnknown();

            ShardsCardDatabase.Clear();
            ShardsContentRegistry.EnsureRegistered();
            var chars = ShardsContentRegistry.CharactersFor(SimConfig.AllDlc);
            Directory.CreateDirectory(outDir);

            Console.WriteLine($"selfplay: {games} games, budget {(budget > 0 ? budget + " iters" : "greedy bootstrap")}, " +
                              $"{perGame} positions/game, threads={threads} -> {outDir}");

            long positions = 0;
            int done = 0;
            var sw = Stopwatch.StartNew();
            var model = new ShardsValueModel();
            // One shared evaluator (read-only weights, ThreadStatic scratch) for every
            // search seat; truncation only engages when a trained net exists.
            IShardsValueEvaluator sharedEval =
                budget > 0 && truncate > 0 && ShardsNetWeights.Available
                    ? ShardsNeuralEval.LoadCurrent()
                    : null;
            if (budget > 0)
                Console.WriteLine(sharedEval != null
                    ? $"  search selfplay: net gen {ShardsNetWeights.Generation} leaf eval, truncate {truncate}, temp turns {tempTurns}"
                    : "  search selfplay: FULL rollouts (no net embedded), temp turns " + tempTurns);

            Parallel.For(0, threads, worker =>
            {
                using var writer = new PositionWriter(Path.Combine(outDir, $"positions-{worker:D2}.soip"));
                var rng = new DeterministicRng(seedBase * 7919 + (ulong)worker, 271);
                var features = new float[ShardsStateEncoder.FeatureCount];

                for (int g = worker; g < games; g += threads)
                {
                    ulong seed = seedBase + (ulong)g;
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

                    var seats = new IBotAgent[2];
                    for (int s = 0; s < 2; s++)
                    {
                        if (budget > 0)
                        {
                            var config = ShardsSearchConfig.ForSims(budget);
                            config.RootSampleTurns = tempTurns;
                            if (sharedEval != null)
                                config.RolloutEndTurns = truncate;
                            seats[s] = new ShardsSearchBot(seed * 100 + (ulong)s, adapter.Inner,
                                config, model, sharedEval);
                        }
                        else
                        {
                            seats[s] = new ShardsGreedyEvalBot(seed * 100 + (ulong)s, adapter.Inner, model);
                        }
                    }

                    // Reservoir over priority points; encode AT the moment of sampling.
                    var reservoir = new List<(float[] X, ushort Move, byte Seat)>(perGame);
                    int priorityPoints = 0, guard = 0;
                    while (!adapter.GameOver && guard++ < SimGameRunner.GuardLimit)
                    {
                        var pending = adapter.PendingInput;
                        if (pending == null) break;
                        if (pending.Kind == PendingInputKind.Priority)
                        {
                            priorityPoints++;
                            if (reservoir.Count < perGame)
                            {
                                var x = new float[ShardsStateEncoder.FeatureCount];
                                ShardsStateEncoder.Encode(adapter.Inner.State, pending.PlayerIndex, x);
                                reservoir.Add((x, (ushort)priorityPoints, (byte)pending.PlayerIndex));
                            }
                            else
                            {
                                int slot = rng.Next(priorityPoints);
                                if (slot < perGame)
                                {
                                    var x = new float[ShardsStateEncoder.FeatureCount];
                                    ShardsStateEncoder.Encode(adapter.Inner.State, pending.PlayerIndex, x);
                                    reservoir[slot] = (x, (ushort)priorityPoints, (byte)pending.PlayerIndex);
                                }
                            }
                        }
                        var action = seats[pending.PlayerIndex].Choose(pending, null)
                                     ?? adapter.DefaultActionFor(pending);
                        if (!adapter.Submit(action).Accepted &&
                            !adapter.Submit(adapter.DefaultActionFor(adapter.PendingInput)).Accepted)
                            break;
                    }
                    if (!adapter.GameOver) continue; // stalls/guard-caps carry no label

                    int winner = adapter.WinnerIndex;
                    foreach (var (x, move, seat) in reservoir)
                    {
                        float z = winner < 0 ? 0.5f : winner == seat ? 1f : 0f;
                        writer.Write(x, z, q: -1f, seed, move, seat, flags: budget > 0 ? (byte)0 : (byte)1);
                    }
                    Interlocked.Add(ref positions, reservoir.Count);
                    int d = Interlocked.Increment(ref done);
                    if (d % 500 == 0 && worker == 0)
                    {
                        double perSec = d / sw.Elapsed.TotalSeconds;
                        CampaignStatus.Update($"selfplay → {Path.GetFileName(outDir)}",
                            $"**Progress**: {d:N0} / {games:N0} games ({100.0 * d / games:F0}%) · " +
                            $"{perSec:F1} games/s · ETA {(games - d) / Math.Max(0.1, perSec) / 60:F0} min\n\n" +
                            $"**Positions**: {Interlocked.Read(ref positions):N0} · " +
                            (budget > 0 ? $"search budget {budget} iters" : "greedy bootstrap"));
                        if (d % 5000 == 0)
                            Console.WriteLine($"  {d}/{games}  {positions} positions  {perSec:F0} games/s");
                    }
                }
            });

            sw.Stop();
            Console.WriteLine($"selfplay: {done} games, {positions} positions in {sw.Elapsed.TotalSeconds:F1}s " +
                              $"-> {outDir} (schema v{ShardsStateEncoder.SchemaVersion})");
            CampaignStatus.Complete("selfplay",
                $"selfplay {Path.GetFileName(outDir)}: {done:N0} games → {positions:N0} positions " +
                $"({(budget > 0 ? $"search {budget}it" : "greedy bootstrap")}, {sw.Elapsed.TotalMinutes:F0} min)");
            return 0;
        }
    }
}
