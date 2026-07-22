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
            int truncateA = cli.GetInt("--truncate-a", -1);
            int truncateB = cli.GetInt("--truncate-b", -1);
            double wallclock = cli.Has("--wallclock")
                ? double.Parse(cli.GetStr("--wallclock", "0"), System.Globalization.CultureInfo.InvariantCulture)
                : 0;
            // Per-side budgets for cross-budget rank duels (e.g. a 1.25s candidate
            // vs the 1.0s rank below, both AS SHIPPED). Fall back to the shared value.
            double wallclockA = cli.Has("--wallclock-a")
                ? double.Parse(cli.GetStr("--wallclock-a", "0"), System.Globalization.CultureInfo.InvariantCulture)
                : wallclock;
            double wallclockB = cli.Has("--wallclock-b")
                ? double.Parse(cli.GetStr("--wallclock-b", "0"), System.Globalization.CultureInfo.InvariantCulture)
                : wallclock;
            int workersA = cli.GetInt("--workers-a", 1);
            int workersB = cli.GetInt("--workers-b", 1);
            int netA = cli.GetInt("--net-a", -1);
            int netB = cli.GetInt("--net-b", -1);
            // Early-stop budget fraction applied to BOTH search seats: -1 default,
            // 0 off, >0 the fraction (1.0 exact, lower = more aggressive).
            double earlyStop = cli.Has("--earlystop")
                ? double.Parse(cli.GetStr("--earlystop", "-1"), System.Globalization.CultureInfo.InvariantCulture)
                : -1;
            // Optionally SAVE the played positions as v1 training data — the deep-search
            // test games are champion-quality, so reuse them instead of discarding.
            string recordDir = cli.GetStr("--record", null);
            int recordPerGame = cli.GetInt("--record-per-game", 20);
            // Machine-readable result (score/decisive/games) for cross-worker aggregation.
            string resultPath = cli.GetStr("--result", null);
            cli.RejectUnknown();

            ShardsCardDatabase.Clear();
            ShardsContentRegistry.EnsureRegistered();
            var factoryA = new BotFactory(kindA, budgetA)
                { Epsilon = epsilon, TruncateEndTurns = truncateA, WallClockSeconds = wallclockA, RootWorkers = workersA, NetGeneration = netA, EarlyStopFraction = earlyStop };
            var factoryB = new BotFactory(kindB, budgetB)
                { Epsilon = epsilon, TruncateEndTurns = truncateB, WallClockSeconds = wallclockB, RootWorkers = workersB, NetGeneration = netB, EarlyStopFraction = earlyStop };
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

            // Shared recorder (v1 schema, one file; per-game writes under a brief lock —
            // ~20 positions once per game, so contention is negligible).
            PositionWriter recorder = null;
            long recorded = 0;
            object recordSync = new();
            if (recordDir != null)
            {
                Directory.CreateDirectory(recordDir);
                recorder = new PositionWriter(Path.Combine(recordDir, $"probe-{seedBase}.soip"),
                    1, ShardsStateEncoder.V1FeatureCount);
                Console.WriteLine($"  recording v1 positions ({recordPerGame}/game) -> {recordDir}");
            }

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
                var reservoir = recorder != null
                    ? new List<(float[] X, byte Seat, float Q)>(recordPerGame) : null;
                var recRng = recorder != null
                    ? new DeterministicRng(item.Seed * 6367 + 11, 29) : null;
                int priorityPoints = 0;
                while (!adapter.GameOver && guard++ < SimGameRunner.GuardLimit)
                {
                    var pending = adapter.PendingInput;
                    if (pending == null) break;
                    bool isA = pending.PlayerIndex == aSeat;
                    // Reservoir-sample priority points; encode the v1 information set of
                    // the acting seat BEFORE its move, then backfill the search's root Q.
                    int sampledSlot = -1;
                    if (reservoir != null && pending.Kind == PendingInputKind.Priority)
                    {
                        priorityPoints++;
                        int slot = reservoir.Count < recordPerGame ? reservoir.Count : recRng.Next(priorityPoints);
                        if (slot < recordPerGame)
                        {
                            var x = new float[ShardsStateEncoder.V1FeatureCount];
                            ShardsStateEncoder.EncodeV1(adapter.Inner.State, pending.PlayerIndex, x);
                            var entry = (x, (byte)pending.PlayerIndex, -1f);
                            if (slot < reservoir.Count) reservoir[slot] = entry; else reservoir.Add(entry);
                            sampledSlot = slot;
                        }
                    }
                    if (isA) decisionSw.Restart();
                    var actingBot = seats[pending.PlayerIndex];
                    var action = actingBot.Choose(pending, null) ?? adapter.DefaultActionFor(pending);
                    if (sampledSlot >= 0 && actingBot is ShardsSearchBot searchBot)
                    {
                        var e = reservoir[sampledSlot];
                        reservoir[sampledSlot] = (e.X, e.Seat, (float)searchBot.LastRootQ);
                    }
                    if (isA)
                    {
                        localAMs += decisionSw.ElapsedMilliseconds;
                        localADecisions++;
                    }
                    if (!adapter.Submit(action).Accepted &&
                        !adapter.Submit(adapter.DefaultActionFor(adapter.PendingInput)).Accepted)
                        break;
                }

                if (reservoir != null && adapter.GameOver)
                {
                    int w = adapter.WinnerIndex;
                    lock (recordSync)
                    {
                        foreach (var (x, seat, q) in reservoir)
                            recorder.Write(x, w < 0 ? 0.5f : w == seat ? 1f : 0f, q, item.Seed, 0, seat, flags: 0);
                        recorded += reservoir.Count;
                    }
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
            recorder?.Dispose();
            if (recordDir != null)
                Console.WriteLine($"  recorded {recorded:N0} positions -> {recordDir}");
            if (resultPath != null)
            {
                var inv = System.Globalization.CultureInfo.InvariantCulture;
                var full = Path.GetFullPath(resultPath);
                Directory.CreateDirectory(Path.GetDirectoryName(full));
                File.WriteAllText(full,
                    $"{{\"score\":{totalScore.ToString(inv)},\"decisive\":{decisive}," +
                    $"\"games\":{done},\"failures\":{failures},\"wr\":{wr.ToString(inv)}}}");
            }
            return failures == 0 ? 0 : 1;
        }
    }
}
