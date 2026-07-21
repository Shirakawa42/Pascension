using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using Newtonsoft.Json;
using Shards.Engine;

namespace SoiSim
{
    public static class RunCommand
    {
        public static int Run(Cli cli)
        {
            string bots = cli.GetStr("--bots", "heuristic");
            int budget = cli.GetInt("--budget", 0);
            int perMatchup = cli.GetInt("--games-per-matchup", 400);
            string matchupFilter = cli.GetStr("--matchups", "all");
            ulong seedBase = cli.GetULong("--seed-base", 1);
            int threads = cli.GetInt("--threads", Math.Max(1, Environment.ProcessorCount - 1));
            string outPath = cli.GetStr("--out", null);
            bool append = cli.Has("--append");
            string tag = cli.GetStr("--tag", "baseline");
            cli.RejectUnknown();

            var factory = new BotFactory(bots, budget);
            var rules = new ShardsRules();
            string configHash = SimConfig.ConfigHash((int)SimConfig.AllDlc, rules, bots, budget, factory.Descriptor);

            var matchups = SimScheduler.Matchups(SimConfig.AllDlc);
            if (matchupFilter != "all")
            {
                var wanted = new HashSet<string>(matchupFilter.Split(',').Select(m =>
                {
                    var parts = m.Split(':');
                    if (parts.Length != 2) throw new CliError($"--matchups entry '{m}' must be charA:charB");
                    Array.Sort(parts, StringComparer.Ordinal);
                    return string.Join(":", parts);
                }));
                matchups = matchups.FindAll(p =>
                {
                    var key = new[] { p.A, p.B };
                    Array.Sort(key, StringComparer.Ordinal);
                    return wanted.Contains(string.Join(":", key));
                });
                if (matchups.Count == 0) throw new CliError("--matchups matched nothing");
            }

            outPath ??= Path.Combine(SimConfig.FindRepoRoot(), "Tools", "ShardsData", "sim",
                $"run-{DateTime.Now:yyyyMMdd-HHmmss}-{bots}{(budget > 0 ? budget.ToString() : "")}-{tag}.jsonl");
            Directory.CreateDirectory(Path.GetDirectoryName(outPath));

            if (append && File.Exists(outPath))
            {
                using var reader = new StreamReader(outPath);
                string first = reader.ReadLine();
                var header = first == null ? null : JsonConvert.DeserializeObject<RunHeader>(first, SimJson.Settings);
                if (header?.ConfigHash != configHash)
                    throw new CliError($"--append config mismatch: file has {header?.ConfigHash ?? "no header"}, current is {configHash}");
            }

            var work = SimScheduler.BuildWorkList(matchups, perMatchup, seedBase);
            Console.WriteLine($"run: {work.Count} games ({matchups.Count} matchups × {perMatchup}), bots={factory.Descriptor}" +
                              (budget > 0 ? $" budget={budget}" : "") + $", threads={threads}");
            Console.WriteLine($"  -> {outPath}");

            using var cts = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) =>
            {
                e.Cancel = true;
                Console.WriteLine("  Ctrl-C: draining…");
                cts.Cancel();
            };

            var sw = Stopwatch.StartNew();
            using var writer = new StreamWriter(outPath, append: append && File.Exists(outPath));
            using var errors = new StreamWriter(outPath + ".errors.jsonl", append: true);
            if (!append || writer.BaseStream.Length == 0)
                writer.WriteLine(SimJson.Line(new RunHeader
                {
                    ConfigHash = configHash,
                    Date = DateTime.UtcNow.ToString("o"),
                    Dlc = (int)SimConfig.AllDlc,
                    Bots = bots,
                    Budget = budget,
                    BotVersion = factory.Descriptor,
                    GitRev = GitRev(),
                    SeedBase = seedBase,
                    Tag = tag,
                    Rules = new RunHeader.RulesSnapshot
                    {
                        StartingHealth = new ShardsRules().StartingHealth,
                        MaxHealth = new ShardsRules().MaxHealth,
                        HandSize = new ShardsRules().HandSize,
                        MasteryCap = new ShardsRules().MasteryCap,
                        CenterRowSize = new ShardsRules().CenterRowSize
                    }
                }));

            var scheduler = new SimScheduler(factory, threads);
            var (done, failures) = scheduler.Run(work, writer, errors, cts.Token,
                (d, total, perSec) =>
                {
                    Console.WriteLine(
                        $"  {d}/{total}  {perSec:F0} games/s  ETA {(total - d) / Math.Max(1.0, perSec):F0}s");
                    CampaignStatus.Update($"stats run ({factory.Descriptor})",
                        $"**Progress**: {d:N0} / {total:N0} games · {perSec:F1} games/s · " +
                        $"ETA {(total - d) / Math.Max(0.1, perSec) / 60:F0} min");
                });

            sw.Stop();
            Console.WriteLine($"  done: {done}/{work.Count} in {sw.Elapsed.TotalSeconds:F1}s " +
                              $"({done / sw.Elapsed.TotalSeconds:F0} games/s), {failures} failures" +
                              (failures > 0 ? $" — see {outPath}.errors.jsonl" : ""));
            CampaignStatus.Complete("stats run",
                $"stats run ({factory.Descriptor}): {done:N0} games, {failures} failures → {Path.GetFileName(outPath)}");
            return failures == 0 && done == work.Count ? 0 : 1;
        }

        private static string GitRev()
        {
            try
            {
                var psi = new ProcessStartInfo("git", "rev-parse --short HEAD")
                {
                    WorkingDirectory = SimConfig.FindRepoRoot(),
                    RedirectStandardOutput = true,
                    UseShellExecute = false
                };
                using var p = Process.Start(psi);
                string rev = p.StandardOutput.ReadToEnd().Trim();
                p.WaitForExit(3000);
                return string.IsNullOrEmpty(rev) ? "unknown" : rev;
            }
            catch
            {
                return "unknown";
            }
        }
    }
}
