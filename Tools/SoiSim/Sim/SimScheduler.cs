using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Shards.Content;
using Shards.Engine;

namespace SoiSim
{
    /// <summary>Builds the matchup work list and runs it in parallel, appending one
    /// JSON line per finished game. Line-atomic (single lock around the writer), so a
    /// killed run leaves a valid JSONL prefix; Ctrl-C drains and closes cleanly.</summary>
    public sealed class SimScheduler
    {
        private readonly BotFactory _bots;
        private readonly int _threads;

        public SimScheduler(BotFactory bots, int threads)
        {
            _bots = bots;
            _threads = threads;
        }

        /// <summary>All unordered character pairs (mirrors included) for the DLC set:
        /// 5 characters → 15 matchups.</summary>
        public static List<(string A, string B)> Matchups(ShardsDlc dlc)
        {
            var chars = ShardsContentRegistry.CharactersFor(dlc);
            var pairs = new List<(string, string)>();
            for (int i = 0; i < chars.Count; i++)
                for (int j = i; j < chars.Count; j++)
                    pairs.Add((chars[i], chars[j]));
            return pairs;
        }

        /// <summary>Seat-balanced work list: per matchup, even game indexes seat (A,B),
        /// odd seat (B,A) — exactly N/2 each. Seeds are sequential from seedBase.</summary>
        public static List<GameSpec> BuildWorkList(
            List<(string A, string B)> matchups, int gamesPerMatchup, ulong seedBase)
        {
            var work = new List<GameSpec>(matchups.Count * gamesPerMatchup);
            ulong seed = seedBase;
            foreach (var (a, b) in matchups)
                for (int g = 0; g < gamesPerMatchup; g++)
                    work.Add(new GameSpec
                    {
                        Seed = seed++,
                        Characters = g % 2 == 0 ? new[] { a, b } : new[] { b, a }
                    });
            return work;
        }

        /// <summary>Runs the work list; returns (completed, failures). Records stream to
        /// writer; termination error/stall/guard_cap games also get a line in errors.</summary>
        public (int Done, int Failures) Run(
            List<GameSpec> work, TextWriter writer, TextWriter errors, CancellationToken ct,
            Action<int, int, double> progress = null)
        {
            ShardsCardDatabase.Clear();
            ShardsContentRegistry.EnsureRegistered(); // once, BEFORE parallelism (not thread-safe)

            int done = 0, failures = 0;
            object writeLock = new();
            var sw = Stopwatch.StartNew();
            long lastProgress = 0;

            try
            {
                Parallel.ForEach(work,
                    new ParallelOptions { MaxDegreeOfParallelism = _threads, CancellationToken = ct },
                    spec =>
                    {
                        var record = SimGameRunner.RunOne(spec, _bots);
                        bool ok = record.Termination is "kill" or "overwhelm" or "tie";
                        string line = SimJson.Line(record);
                        lock (writeLock)
                        {
                            writer.WriteLine(line);
                            if (!ok)
                            {
                                failures++;
                                errors?.WriteLine(line);
                            }
                            done++;
                            if (done % 100 == 0)
                                writer.Flush();
                        }
                        long elapsed = sw.ElapsedMilliseconds;
                        if (progress != null && elapsed - Interlocked.Read(ref lastProgress) > 5000)
                        {
                            Interlocked.Exchange(ref lastProgress, elapsed);
                            progress(done, work.Count, done / sw.Elapsed.TotalSeconds);
                        }
                    });
            }
            catch (OperationCanceledException)
            {
                // Ctrl-C: fall through — everything already written is a valid prefix.
            }

            lock (writeLock)
            {
                writer.Flush();
                errors?.Flush();
            }
            return (done, failures);
        }
    }
}
