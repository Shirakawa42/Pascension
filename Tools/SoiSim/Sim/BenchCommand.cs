using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Pascension.Core;
using Shards.Bots;
using Shards.Content;
using Shards.Engine;

namespace SoiSim
{
    /// <summary>Measures headless heuristic self-play throughput — the number every
    /// tuning/stats wall-clock estimate calibrates from.</summary>
    public static class BenchCommand
    {
        private const ShardsDlc AllDlc =
            ShardsDlc.RelicsOfTheFuture | ShardsDlc.ShadowOfSalvation | ShardsDlc.IntoTheHorizon;

        public static int Run(Cli cli)
        {
            int games = cli.GetInt("--games", 200);
            int players = cli.GetInt("--players", 2);
            ulong seedBase = cli.GetULong("--seed-base", 1);
            int threads = cli.GetInt("--threads", Math.Max(1, Environment.ProcessorCount - 1));
            cli.RejectUnknown();

            ShardsCardDatabase.Clear();
            ShardsContentRegistry.EnsureRegistered();

            // JIT warmup — a few games, unmeasured.
            for (ulong s = 0; s < 3; s++)
                RunOne(seedBase + 1_000_000 + s, players);

            Console.WriteLine($"bench: {games} games, {players} players, all DLC, heuristic bots");
            Measure("single-thread", games, seedBase, players, 1);
            if (threads > 1)
                Measure($"{threads} threads", games, seedBase, players, threads);
            return 0;
        }

        private static void Measure(string label, int games, ulong seedBase, int players, int threads)
        {
            long totalSubmits = 0, totalRounds = 0, failures = 0;
            var sw = Stopwatch.StartNew();
            Parallel.For(0, games, new ParallelOptions { MaxDegreeOfParallelism = threads }, i =>
            {
                var r = RunOne(seedBase + (ulong)i, players);
                Interlocked.Add(ref totalSubmits, r.Submits);
                Interlocked.Add(ref totalRounds, r.Rounds);
                if (!r.Finished)
                    Interlocked.Increment(ref failures);
            });
            sw.Stop();

            double perSec = games / sw.Elapsed.TotalSeconds;
            Console.WriteLine(
                $"  {label,-14} {sw.Elapsed.TotalSeconds,7:F2}s  {perSec,8:F1} games/s  " +
                $"avg {totalSubmits / (double)games,6:F0} submits  avg {totalRounds / (double)games,5:F1} rounds  " +
                (failures == 0 ? "0 failures" : $"{failures} FAILURES"));
        }

        private static (long Submits, int Rounds, bool Finished) RunOne(ulong seed, int players)
        {
            var chars = ShardsContentRegistry.CharactersFor(AllDlc);
            var specs = new List<PlayerSpec>();
            for (int i = 0; i < players; i++)
                specs.Add(new PlayerSpec
                {
                    Name = "Bot" + i,
                    CharacterId = chars[(int)((seed + (ulong)i) % (ulong)chars.Count)]
                });

            var adapter = new ShardsEngineAdapter(ShardsContentRegistry.StandardConfig(seed, specs, AllDlc));
            var bots = new IBotAgent[players];
            for (int i = 0; i < players; i++)
                bots[i] = new ShardsHeuristicBot(seed * 100 + (ulong)i, adapter.Inner);

            int guard = 0;
            while (!adapter.GameOver && guard++ < 30000)
            {
                var pending = adapter.PendingInput;
                if (pending == null)
                    return (guard, adapter.Inner.State.Round, false);
                var action = bots[pending.PlayerIndex].Choose(pending, null)
                             ?? adapter.DefaultActionFor(pending);
                if (!adapter.Submit(action).Accepted &&
                    !adapter.Submit(adapter.DefaultActionFor(adapter.PendingInput)).Accepted)
                    return (guard, adapter.Inner.State.Round, false);
            }
            return (guard, adapter.Inner.State.Round, adapter.GameOver);
        }
    }
}
