using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Pascension.Core;
using Pascension.Engine.Core;
using Shards.Bots;
using Shards.Content;
using Shards.Engine;

namespace SoiSim
{
    /// <summary>`fit` — learns the position evaluator's weights from game outcomes by
    /// logistic regression (the method chess engines call Texel tuning).
    ///
    /// This is the training loop the project was missing. The previous campaign fitted a
    /// 560k-parameter net to summed bags of card vectors and shipped an agent 410 Elo below
    /// an instant policy; the replacement fits ~22 weights over ANALYTIC features, where every
    /// ratio a value function needs is supplied rather than discovered. Fitting is a convex
    /// problem, so it converges to the global optimum in seconds — no generations, no
    /// self-play collapse, no gates on a proxy metric.
    ///
    /// It also answers "can the AI find its own playstyle?" — yes, because nothing here
    /// encodes one. Aggression, economy, board development and the mastery race are all just
    /// coordinates; whichever actually wins gets weight.
    ///
    /// Why this loop and not CMA-ES on win rate: a candidate here costs one gradient step over
    /// cached samples, against thousands of games for a win-rate evaluation. Gradient tuning
    /// fits all 22 at once; evolution is reserved for the handful of knobs that have no
    /// gradient (search widths, risk scalars), exactly as the plan specifies.</summary>
    public static class FitCommand
    {
        private sealed class Sample
        {
            public double[] X;
            public double Y;    // 1 = the viewer went on to win
            public ulong Game;  // every position from one game shares a label
        }

        public static int Run(Cli cli)
        {
            int games = cli.GetInt("--games", 4000);
            ulong seedBase = cli.GetULong("--seed-base", 990000);
            int threads = cli.GetInt("--threads", Math.Max(1, Environment.ProcessorCount - 1));
            int epochs = cli.GetInt("--epochs", 4000);
            double lr = cli.GetDouble("--lr", 0.5);
            double l2 = cli.GetDouble("--l2", 1e-4);
            int minRound = cli.GetInt("--min-round", 4);
            bool emit = !cli.Has("--no-emit");
            cli.RejectUnknown();

            ShardsCardDatabase.Clear();
            ShardsContentRegistry.EnsureRegistered();
            var chars = ShardsContentRegistry.CharactersFor(SimConfig.AllDlc);
            var model = new ShardsValueModel(W.Pad(ShardsEvalWeights.Current));

            Console.WriteLine($"fit: {games} games, dlc mask {(int)SimConfig.AllDlc}, min round {minRound}");
            var sw = Stopwatch.StartNew();
            // One slot per game, merged in game order below. The first version used a
            // ConcurrentBag, whose enumeration order depends on thread scheduling; game
            // CONTENT was already reproducible (the fixed-weight baseline accuracy was
            // identical across every run), but the ORDER the samples reached the gradient
            // accumulator was not, and floating-point addition is not associative. With
            // lr held at 0.5 for 4000 epochs those last-ulp differences are amplified
            // rather than damped, which is exactly the 63.4/64.3/64.1/65.6 spread the
            // campaign log records. Distinct array slots need no lock, and the merge
            // order is the game index — nothing downstream sees the scheduler.
            var perGame = new List<Sample>[games];

            Parallel.For(0, games, new ParallelOptions { MaxDegreeOfParallelism = threads }, g =>
            {
                ulong seed = seedBase + (ulong)g;
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

                var local = new List<double[]>();
                int guard = 0;
                while (!adapter.GameOver && guard++ < SimGameRunner.GuardLimit)
                {
                    var pending = adapter.PendingInput;
                    if (pending == null) break;
                    // Start-of-turn positions past the opening: the play zone is empty and
                    // ResetTurn has zeroed gems and power, so what remains is exactly the
                    // durable state an evaluator is asked to judge. Opening rounds are
                    // near-even and would only teach the model to predict the seat advantage.
                    if (pending.Kind == PendingInputKind.Priority &&
                        adapter.Inner.State.Round >= minRound &&
                        adapter.Inner.State.Players[0].PlayZone.Count == 0 &&
                        adapter.Inner.State.Players[1].PlayZone.Count == 0)
                    {
                        var x = new double[ShardsEvalFeatures.Count];
                        ShardsEvalFeatures.Extract(adapter.Inner.State, 0, x);
                        local.Add(x);
                    }
                    var action = seats[pending.PlayerIndex].Choose(pending, null)
                                 ?? adapter.DefaultActionFor(pending);
                    if (!adapter.Submit(action).Accepted &&
                        !adapter.Submit(adapter.DefaultActionFor(adapter.PendingInput)).Accepted)
                        break;
                }
                if (!adapter.GameOver || adapter.WinnerIndex < 0) return;
                double y = adapter.WinnerIndex == 0 ? 1 : 0;
                var bucket = new List<Sample>(local.Count);
                foreach (var x in local) bucket.Add(new Sample { X = x, Y = y, Game = seed });
                perGame[g] = bucket;
            });

            var merged = new List<Sample>();
            foreach (var bucket in perGame)
                if (bucket != null)
                    merged.AddRange(bucket);
            var data = merged.ToArray();
            Console.WriteLine($"  {data.Length:N0} positions in {sw.Elapsed.TotalSeconds:F1}s");
            if (data.Length < 1000) { Console.Error.WriteLine("fit: too few samples"); return 2; }

            // Hold out by GAME, not by position. Every position from one game carries that
            // game's single outcome as its label, so a position-wise split puts near-identical
            // rows on both sides and the model scores itself on answers it has already seen.
            //
            // The first version sliced the ConcurrentBag directly. Bag order depends on thread
            // scheduling, so successive runs over IDENTICAL data reported 67.5% and then
            // 62.3% — the instability is what exposed the leak. Splitting on the game seed is
            // deterministic and leak-free.
            var train = data.Where(s => s.Game % 5 != 0).ToArray();
            var test = data.Where(s => s.Game % 5 == 0).ToArray();

            var w = new double[ShardsEvalFeatures.Count];
            var grad = new double[ShardsEvalFeatures.Count];
            for (int epoch = 0; epoch < epochs; epoch++)
            {
                Array.Clear(grad, 0, grad.Length);
                foreach (var s in train)
                {
                    double z = 0;
                    for (int i = 0; i < w.Length; i++) z += w[i] * s.X[i];
                    double p = 1.0 / (1.0 + Math.Exp(-Math.Max(-30, Math.Min(30, z))));
                    double err = p - s.Y;
                    for (int i = 0; i < w.Length; i++) grad[i] += err * s.X[i];
                }
                for (int i = 0; i < w.Length; i++)
                    w[i] -= lr * (grad[i] / train.Length + l2 * w[i]);
            }

            Console.WriteLine($"  train acc {Accuracy(train, w):P1} · holdout acc {Accuracy(test, w):P1}");
            Console.WriteLine($"  baseline (health+mastery only) holdout {Accuracy(test, Baseline()):P1}");
            Console.WriteLine();
            var ranked = Enumerable.Range(0, w.Length)
                .OrderByDescending(i => Math.Abs(w[i])).ToArray();
            foreach (int i in ranked)
                Console.WriteLine($"  {ShardsEvalFeatures.Names[i],-18} {w[i],9:F3}");

            if (emit) Emit(w, Accuracy(test, w));
            CampaignStatus.Complete("fit",
                $"fit {data.Length:N0} positions: holdout {Accuracy(test, w):P1} " +
                $"vs baseline {Accuracy(test, Baseline()):P1}");
            return 0;
        }

        private static double[] Baseline()
        {
            var b = new double[ShardsEvalFeatures.Count];
            b[0] = 2.0; b[1] = 2.0;
            return b;
        }

        private static double Accuracy(Sample[] data, double[] w)
        {
            int correct = 0;
            foreach (var s in data)
            {
                double z = 0;
                for (int i = 0; i < w.Length; i++) z += w[i] * s.X[i];
                if (z == 0) continue;
                if (z > 0 == s.Y > 0.5) correct++;
            }
            return data.Length == 0 ? 0 : (double)correct / data.Length;
        }

        private static void Emit(double[] w, double accuracy)
        {
            string path = Path.Combine(SimConfig.FindRepoRoot(), "Assets", "Scripts", "Shards",
                "Bots", "ShardsEvalLinearWeights.g.cs");
            var sb = new StringBuilder();
            sb.AppendLine("// <auto-generated>");
            sb.AppendLine("// Position-evaluator weights, FITTED to game outcomes. Regenerate with:");
            sb.AppendLine("//   dotnet run -c Release --project Tools/SoiSim -- fit");
            sb.AppendLine($"// L1: logistic regression, holdout accuracy {accuracy:P1}.");
            sb.AppendLine("// L0: hand-set health+mastery baseline, kept as the control.");
            sb.AppendLine("// </auto-generated>");
            sb.AppendLine("namespace Shards.Bots");
            sb.AppendLine("{");
            sb.AppendLine("    public static class ShardsEvalLinearWeights");
            sb.AppendLine("    {");
            sb.AppendLine("        /// <summary>The vector the linear evaluator uses by default.</summary>");
            sb.AppendLine("        public static double[] Current => L1;");
            sb.AppendLine();
            sb.AppendLine("        /// <summary>Hand-set control: the two terms measured to work.</summary>");
            sb.AppendLine("        public static readonly double[] L0 =");
            sb.AppendLine("        {");
            sb.AppendLine("            2.0, 2.0, 0, 0, 0, 0, 0, 0, 0, 0,");
            sb.AppendLine("            0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0");
            sb.AppendLine("        };");
            sb.AppendLine();
            sb.AppendLine("        public static readonly double[] L1 =");
            sb.AppendLine("        {");
            for (int i = 0; i < w.Length; i++)
                sb.AppendLine($"            {w[i].ToString("F6", System.Globalization.CultureInfo.InvariantCulture)}," +
                              $" // {ShardsEvalFeatures.Names[i]}");
            sb.AppendLine("        };");
            sb.AppendLine("    }");
            sb.AppendLine("}");
            File.WriteAllText(path, sb.ToString());
            Console.WriteLine($"  emitted L1 -> {path}");
        }
    }
}
