using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Pascension.Core;
using Shards.Content;
using Shards.Engine;

namespace SoiSim
{
    /// <summary>Head-to-head strength + timing probe: mirrored games between two bot
    /// kinds. The timing numbers size the Phase-B stats runs.</summary>
    public static class ProbeCommand
    {
        public static int Run(Cli cli)
        {
            string kindA = cli.GetStr("--a", "strong");
            int budgetA = cli.GetInt("--budget-a", cli.GetInt("--budget", 200));
            string kindB = cli.GetStr("--b", "greedy");
            int budgetB = cli.GetInt("--budget-b", 0);
            int games = cli.GetInt("--games", 100);
            ulong seedBase = cli.GetULong("--seed-base", 9000);
            int threads = cli.GetInt("--threads", Math.Max(1, Environment.ProcessorCount - 1));
            double epsilon = cli.Has("--epsilon")
                ? double.Parse(cli.GetStr("--epsilon", "-1"), System.Globalization.CultureInfo.InvariantCulture)
                : -1;
            int truncateA = cli.GetInt("--truncate-a", 0);
            int truncateB = cli.GetInt("--truncate-b", 0);
            double wallclock = cli.Has("--wallclock")
                ? double.Parse(cli.GetStr("--wallclock", "0"), System.Globalization.CultureInfo.InvariantCulture)
                : 0;
            int workersA = cli.GetInt("--workers-a", 1);
            int workersB = cli.GetInt("--workers-b", 1);
            int netA = cli.GetInt("--net-a", -1);
            int netB = cli.GetInt("--net-b", -1);
            cli.RejectUnknown();

            ShardsCardDatabase.Clear();
            ShardsContentRegistry.EnsureRegistered();
            var factoryA = new BotFactory(kindA, budgetA)
                { Epsilon = epsilon, TruncateEndTurns = truncateA, WallClockSeconds = wallclock, RootWorkers = workersA, NetGeneration = netA };
            var factoryB = new BotFactory(kindB, budgetB)
                { Epsilon = epsilon, TruncateEndTurns = truncateB, WallClockSeconds = wallclock, RootWorkers = workersB, NetGeneration = netB };
            var chars = ShardsContentRegistry.CharactersFor(SimConfig.AllDlc);

            var work = new List<(ulong Seed, bool AFirst, string C0, string C1)>();
            var rng = new Pascension.Engine.Core.DeterministicRng(seedBase, 55);
            for (int p = 0; p < games / 2; p++)
            {
                ulong s = seedBase + (ulong)p;
                int a = rng.Next(chars.Count);
                int b = rng.Next(chars.Count - 1);
                if (b >= a) b++;
                work.Add((s, true, chars[a], chars[b]));
                work.Add((s, false, chars[b], chars[a]));
            }

            double totalScore = 0;
            long totalSubmits = 0, aDecisionMs = 0, aDecisions = 0;
            int done = 0, failures = 0;
            object sync = new();
            var sw = Stopwatch.StartNew();

            Parallel.ForEach(work, new ParallelOptions { MaxDegreeOfParallelism = threads }, item =>
            {
                var specs = new List<PlayerSpec>
                {
                    new() { Name = "S0", CharacterId = item.C0 },
                    new() { Name = "S1", CharacterId = item.C1 }
                };
                var adapter = new ShardsEngineAdapter(
                    ShardsContentRegistry.StandardConfig(item.Seed, specs, SimConfig.AllDlc));
                int aSeat = item.AFirst ? 0 : 1;
                var seats = new IBotAgent[2];
                seats[aSeat] = factoryA.Create(item.Seed, aSeat, adapter.Inner);
                seats[1 - aSeat] = factoryB.Create(item.Seed, 1 - aSeat, adapter.Inner);

                int guard = 0;
                long localAMs = 0, localADecisions = 0;
                var decisionSw = new Stopwatch();
                while (!adapter.GameOver && guard++ < SimGameRunner.GuardLimit)
                {
                    var pending = adapter.PendingInput;
                    if (pending == null) break;
                    bool isA = pending.PlayerIndex == aSeat;
                    if (isA) decisionSw.Restart();
                    var action = seats[pending.PlayerIndex].Choose(pending, null)
                                 ?? adapter.DefaultActionFor(pending);
                    if (isA)
                    {
                        localAMs += decisionSw.ElapsedMilliseconds;
                        localADecisions++;
                    }
                    if (!adapter.Submit(action).Accepted &&
                        !adapter.Submit(adapter.DefaultActionFor(adapter.PendingInput)).Accepted)
                        break;
                }

                double score = !adapter.GameOver ? -1
                    : adapter.WinnerIndex < 0 ? 0.5
                    : adapter.WinnerIndex == aSeat ? 1 : 0;
                lock (sync)
                {
                    if (score < 0) failures++;
                    else totalScore += score;
                    totalSubmits += guard;
                    aDecisionMs += localAMs;
                    aDecisions += localADecisions;
                    done++;
                    if (done % 10 == 0)
                    {
                        double perSec = done / sw.Elapsed.TotalSeconds;
                        CampaignStatus.Update($"probe: {factoryA.Descriptor} vs {factoryB.Descriptor}",
                            $"**Progress**: {done} / {work.Count} games · {perSec:F2} games/s · " +
                            $"ETA {(work.Count - done) / Math.Max(0.01, perSec) / 60:F0} min\n\n" +
                            $"**A running win rate**: {totalScore / Math.Max(1, done - failures):P1}");
                        if (done % 50 == 0)
                            Console.WriteLine($"  {done}/{work.Count}  {perSec:F2} games/s");
                    }
                }
            });
            sw.Stop();

            int decisive = done - failures;
            double wr = decisive == 0 ? 0 : totalScore / decisive;
            var (lo, hi) = Stats.Wilson((int)Math.Round(totalScore), Math.Max(1, decisive));
            Console.WriteLine($"probe: {factoryA.Descriptor} vs {factoryB.Descriptor}, {done} games" +
                              (failures > 0 ? $" ({failures} FAILURES)" : ""));
            Console.WriteLine($"  A win rate: {wr:P1} [{lo:P1}–{hi:P1}]");
            CampaignStatus.Log($"probe: {factoryA.Descriptor} vs {factoryB.Descriptor} → " +
                               $"{wr:P1} [{lo:P1}–{hi:P1}] over {done} games");
            Console.WriteLine($"  wall: {sw.Elapsed.TotalSeconds:F1}s ({done / sw.Elapsed.TotalSeconds:F2} games/s on {threads} threads)");
            Console.WriteLine($"  A think time: {(aDecisions == 0 ? 0 : aDecisionMs / (double)aDecisions):F0} ms/decision ({aDecisions} decisions)");
            return failures == 0 ? 0 : 1;
        }
    }
}
