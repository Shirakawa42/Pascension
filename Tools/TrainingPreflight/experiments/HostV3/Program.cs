using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Tasks;
using Shards.Content;
using Shards.Engine;

namespace Shards.Preflight
{
    internal static class Program
    {
        private static void Main(string[] args)
        {
            if (!BitConverter.IsLittleEndian) throw new NotSupportedException("The preflight wire requires a little-endian host");
            ShardsContentRegistry.EnsureRegistered();
            Encoder.Initialize();
            switch (args.FirstOrDefault() ?? "selftest")
            {
                case "serve": Serve(); break;
                case "catalog": Print(new { Encoder.ObsDim, Encoder.MaxActions, Encoder.ActionDim,
                    observationSchema = Encoder.SchemaVersion, cards = Encoder.CardIds,
                    maxCardDefinitions = Encoder.MaxCardDefinitions,
                    extraObservationFeatures = Encoder.ExtraObservationFeatures }); break;
                case "selftest": SelfTest.Run(); break;
                case "v3selftest": V3SelfTest.Run(); break;
                case "benchmark": Benchmark(args); break;
                case "copybench": CopyBenchmark(); break;
                default: throw new ArgumentException("Expected serve/catalog/selftest/benchmark");
            }
        }

        internal static void Print(object value) => Console.WriteLine(JsonSerializer.Serialize(value,
            new JsonSerializerOptions { WriteIndented = true }));

        private static void Serve()
        {
            using var input = new BinaryReader(Console.OpenStandardInput());
            using var output = new BinaryWriter(Console.OpenStandardOutput());
            string sharedPath = Environment.GetEnvironmentVariable("SHARDS_SHARED_BUFFER");
            MappedBody sharedBody = null;
            bool spanCopy = Environment.GetEnvironmentVariable("SHARDS_SHARED_COPY") == "span";
            bool profilePublication = Environment.GetEnvironmentVariable("SHARDS_PROFILE_PUBLICATION") == "1";
            bool fusedServe = Environment.GetEnvironmentVariable("SHARDS_FUSED_SERVE") == "1";
            long mapTicks = 0, pipeTicks = 0, responseCount = 0, responseBytes = 0;
            Console.Error.WriteLine(JsonSerializer.Serialize(new { host = "fresh-staged-training-host-v1",
                observationSchema = Encoder.SchemaVersion, selectiveStepOpcode = 5,
                maxCardDefinitions = Encoder.MaxCardDefinitions,
                extraObservationFeatures = Encoder.ExtraObservationFeatures,
                serverGc = System.Runtime.GCSettings.IsServerGC, splitBranches = Adapter.SplitBranches,
                sharedBuffer = sharedPath != null, sharedCopy = spanCopy ? "span" : "accessor",
                fusedServe, stepTiming = fusedServe ? "combined-step-reset-encode" : "step-reset-only" }));
            Adapter[] games = null;
            int workers = 1, count = 0;
            byte[] payload = null;
            int obsBytes = 0, candidateBytes = 0, maskBytes = 0, rewardBytes = 0;
            long wrapper = 0, submissions = 0, completed = 0, truncated = 0;
            ulong seedBase = 0;
            long[] episodes = null;
            int[] actions = null;
            byte[] actionBytes = null;
            var envelope = new byte[96];
            var stepTimes = new double[8];
            while (true)
            {
                uint op;
                try { op = input.ReadUInt32(); } catch (EndOfStreamException) { return; }
                if (op == 3)
                {
                    sharedBody?.Dispose();
                    if (profilePublication) Console.Error.WriteLine(JsonSerializer.Serialize(new {
                        publicationProfile = true, responses = responseCount, payloadBytes = responseBytes,
                        mappedCopyMs = mapTicks * 1000.0 / Stopwatch.Frequency,
                        pipeWriteMs = pipeTicks * 1000.0 / Stopwatch.Frequency }));
                    return;
                }
                if (op == 1)
                {
                    count = checked((int)input.ReadUInt32());
                    workers = checked((int)input.ReadUInt32());
                    seedBase = input.ReadUInt64();
                    if (count < 1 || count > 32768 || workers < 1 || workers > 64)
                        throw new ArgumentException("Invalid batch/worker count");
                    games = new Adapter[count]; episodes = new long[count]; actions = new int[count];
                    actionBytes = new byte[count * 4];
                    RunWorkers(count, workers, i => games[i] = new Adapter(seedBase + (ulong)i));
                    obsBytes = count * Encoder.ObsDim * 4;
                    candidateBytes = count * Encoder.MaxActions * Encoder.ActionDim * 4;
                    maskBytes = count * Encoder.MaxActions * 4;
                    rewardBytes = count * 2 * 4;
                    payload = new byte[obsBytes + candidateBytes + maskBytes + rewardBytes + count * 8];
                    if (sharedPath != null)
                    {
                        sharedBody?.Dispose();
                        sharedBody = new MappedBody(sharedPath, payload.Length);
                    }
                    wrapper = submissions = completed = truncated = 0;
                }
                else if (games == null) throw new InvalidOperationException("Reset before stepping");
                else if (op != 2 && op != 4 && op != 5) throw new ArgumentException("Unknown opcode");
                bool stepping = op == 2 || op == 5;
                int rewardOffset = obsBytes + candidateBytes + maskBytes;
                int doneOffset = rewardOffset + rewardBytes;
                int actorOffset = doneOffset + count * 4;
                // A held lane keeps its already encoded observation and actor seat.
                // Terminal rewards/done are response-local, including after auto-reset.
                Array.Clear(payload, rewardOffset, rewardBytes + count * 4);
                var clock = Stopwatch.StartNew();
                if (stepping)
                {
                    // One bulk pipe read instead of a scalar stream read for every game.
                    input.BaseStream.ReadExactly(actionBytes);
                    Buffer.BlockCopy(actionBytes, 0, actions, 0, actionBytes.Length);
                    int activeCount = 0;
                    for (int i = 0; i < count; i++)
                    {
                        if (actions[i] < (op == 5 ? -1 : 0)) throw new ArgumentException("Invalid negative action index");
                        if (actions[i] >= 0) activeCount++;
                    }
                    // Exclude time blocked waiting for Python to send its actions.
                    clock.Restart();
                    if (fusedServe)
                    {
                        var batch = RunFusedWorkers(count, workers, i =>
                        {
                            if (actions[i] < 0) return new BatchTotals();
                            var game = games[i];
                            long before = game.Submissions;
                            game.Step(actions[i]);
                            var totals = new BatchTotals { Submissions = game.Submissions - before };
                            bool terminal = game.Engine.State.GameOver;
                            bool cap = game.Truncated;
                            if (terminal || cap)
                            {
                                var doneSpan = MemoryMarshal.Cast<byte, int>(payload.AsSpan(doneOffset, count * 4));
                                doneSpan[i] = terminal ? 1 : 2;
                                if (terminal)
                                {
                                    totals.Completed = 1;
                                    int winner = game.Engine.State.WinnerIndex;
                                    if (winner >= 0)
                                    {
                                        var rewards = MemoryMarshal.Cast<byte, float>(payload.AsSpan(rewardOffset, rewardBytes));
                                        rewards[i * 2 + winner] = 1;
                                        rewards[i * 2 + 1 - winner] = -1;
                                    }
                                }
                                else totals.Truncated = 1;
                                episodes[i]++;
                                games[i] = new Adapter(seedBase + (ulong)i + (ulong)episodes[i] * (ulong)count);
                            }
                            Encoder.Encode(games[i],
                                MemoryMarshal.Cast<byte, float>(payload.AsSpan(i * Encoder.ObsDim * 4, Encoder.ObsDim * 4)),
                                MemoryMarshal.Cast<byte, float>(payload.AsSpan(obsBytes + i * Encoder.MaxActions * Encoder.ActionDim * 4,
                                    Encoder.MaxActions * Encoder.ActionDim * 4)),
                                MemoryMarshal.Cast<byte, float>(payload.AsSpan(obsBytes + candidateBytes + i * Encoder.MaxActions * 4,
                                    Encoder.MaxActions * 4)));
                            MemoryMarshal.Cast<byte, int>(payload.AsSpan(actorOffset, count * 4))[i] = games[i].Actor;
                            return totals;
                        });
                        submissions += batch.Submissions;
                        completed += batch.Completed;
                        truncated += batch.Truncated;
                    }
                    else
                    {
                        RunWorkers(count, workers, i =>
                        {
                            if (actions[i] < 0) return;
                            var game = games[i];
                            long before = game.Submissions;
                            game.Step(actions[i]);
                            System.Threading.Interlocked.Add(ref submissions, game.Submissions - before);
                            bool terminal = game.Engine.State.GameOver;
                            bool cap = game.Truncated;
                            if (!terminal && !cap) return;
                            var doneSpan = MemoryMarshal.Cast<byte, int>(payload.AsSpan(doneOffset, count * 4));
                            doneSpan[i] = terminal ? 1 : 2;
                            if (terminal)
                            {
                                System.Threading.Interlocked.Increment(ref completed);
                                int winner = game.Engine.State.WinnerIndex;
                                if (winner >= 0)
                                {
                                    var rewards = MemoryMarshal.Cast<byte, float>(payload.AsSpan(rewardOffset, rewardBytes));
                                    rewards[i * 2 + winner] = 1;
                                    rewards[i * 2 + 1 - winner] = -1;
                                }
                            }
                            else System.Threading.Interlocked.Increment(ref truncated);
                            episodes[i]++;
                            games[i] = new Adapter(seedBase + (ulong)i + (ulong)episodes[i] * (ulong)count);
                        });
                    }
                    wrapper += activeCount;
                }
                stepTimes[0] = stepping ? clock.Elapsed.TotalMilliseconds : 0;
                if (stepping && fusedServe)
                {
                    // The previous timer includes one combined step/reset/encode pass.
                    stepTimes[1] = 0;
                }
                else
                {
                    clock.Restart();
                    RunWorkers(count, workers, i =>
                    {
                        if (op == 5 && actions[i] < 0) return;
                        Encoder.Encode(games[i],
                            MemoryMarshal.Cast<byte, float>(payload.AsSpan(i * Encoder.ObsDim * 4, Encoder.ObsDim * 4)),
                            MemoryMarshal.Cast<byte, float>(payload.AsSpan(obsBytes + i * Encoder.MaxActions * Encoder.ActionDim * 4,
                                Encoder.MaxActions * Encoder.ActionDim * 4)),
                            MemoryMarshal.Cast<byte, float>(payload.AsSpan(obsBytes + candidateBytes + i * Encoder.MaxActions * 4,
                                Encoder.MaxActions * 4)));
                        MemoryMarshal.Cast<byte, int>(payload.AsSpan(actorOffset, count * 4))[i] = games[i].Actor;
                    });
                    stepTimes[1] = clock.Elapsed.TotalMilliseconds;
                }
                stepTimes[2] = wrapper; stepTimes[3] = submissions;
                stepTimes[4] = completed; stepTimes[5] = truncated;
                stepTimes[6] = games.Sum(g => (long)g.Engine.Log.Count);
                stepTimes[7] = GC.GetTotalMemory(false);
                var header = MemoryMarshal.Cast<byte, uint>(envelope.AsSpan(0, 32));
                header[0] = 0x534F4931u; header[1] = 1; header[2] = (uint)count;
                header[3] = Encoder.ObsDim; header[4] = Encoder.MaxActions; header[5] = Encoder.ActionDim;
                header[6] = (uint)payload.Length; header[7] = 64;
                stepTimes.AsSpan().CopyTo(MemoryMarshal.Cast<byte, double>(envelope.AsSpan(32, 64)));
                // Synchronous writes finish before the response header publishes the new buffer.
                long publicationStart = profilePublication ? Stopwatch.GetTimestamp() : 0;
                sharedBody?.Write(payload, spanCopy);
                if (profilePublication) mapTicks += Stopwatch.GetTimestamp() - publicationStart;
                publicationStart = profilePublication ? Stopwatch.GetTimestamp() : 0;
                if (sharedBody == null)
                {
                    output.Write(envelope, 0, 32);
                    output.Write(payload);
                    output.Write(envelope, 32, 64);
                }
                else output.Write(envelope);
                output.Flush();
                if (profilePublication) pipeTicks += Stopwatch.GetTimestamp() - publicationStart;
                responseCount++; responseBytes += payload.Length;
            }
        }

        private struct BatchTotals
        {
            internal long Submissions;
            internal long Completed;
            internal long Truncated;
            internal void Add(BatchTotals other)
            {
                Submissions += other.Submissions;
                Completed += other.Completed;
                Truncated += other.Truncated;
            }
        }

        private static BatchTotals RunFusedWorkers(int count, int workers, Func<int, BatchTotals> action)
        {
            var aggregate = new BatchTotals();
            if (workers == 1)
            {
                for (int i = 0; i < count; i++) aggregate.Add(action(i));
            }
            else
            {
                // Parallel.For retains dynamic load balancing. Counters accumulate in
                // task-local structs and merge only when each local partition completes.
                Parallel.For(0, count, new ParallelOptions { MaxDegreeOfParallelism = workers },
                    () => new BatchTotals(),
                    (i, loop, local) => { local.Add(action(i)); return local; },
                    local =>
                    {
                        System.Threading.Interlocked.Add(ref aggregate.Submissions, local.Submissions);
                        System.Threading.Interlocked.Add(ref aggregate.Completed, local.Completed);
                        System.Threading.Interlocked.Add(ref aggregate.Truncated, local.Truncated);
                    });
            }
            return aggregate;
        }

        private static void CopyBenchmark()
        {
            const int bytes = 256 * (Encoder.ObsDim + Encoder.MaxActions * Encoder.ActionDim + Encoder.MaxActions + 4) * 4;
            const int iterations = 256;
            string path = Path.Combine("/dev/shm", "shards-copybench-" + Environment.ProcessId);
            var payload = new byte[bytes];
            new Random(1).NextBytes(payload);
            try
            {
                using (var file = new FileStream(path, FileMode.CreateNew)) file.SetLength(bytes);
                using var body = new MappedBody(path, bytes);
                var results = new System.Collections.Generic.List<object>();
                for (int repeat = 0; repeat < 3; repeat++)
                    foreach (bool span in new[] { false, true })
                    {
                        for (int i = 0; i < 8; i++) body.Write(payload, span);
                        var clock = Stopwatch.StartNew();
                        for (int i = 0; i < iterations; i++) body.Write(payload, span);
                        clock.Stop();
                        if (!File.ReadAllBytes(path).SequenceEqual(payload)) throw new InvalidOperationException("Mapped byte mismatch");
                        results.Add(new { copy = span ? "span" : "accessor", repeat, bytes, iterations,
                            seconds = clock.Elapsed.TotalSeconds, gigabytesPerSecond = (double)bytes * iterations / clock.Elapsed.TotalSeconds / 1e9 });
                    }
                Print(new { workload = "mapped-publication-copy-only", results });
            }
            finally { if (File.Exists(path)) File.Delete(path); }
        }

        private static void RunWorkers(int count, int workers, Action<int> action)
        {
            if (workers == 1) { for (int i = 0; i < count; i++) action(i); }
            else Parallel.For(0, count, new ParallelOptions { MaxDegreeOfParallelism = workers }, action);
        }

        internal sealed class Result
        {
            public long Wrapper { get; set; }
            public long Submissions { get; set; }
            public long Bytes { get; set; }
            public int Winner { get; set; }
            public bool Capped { get; set; }
            public int Round { get; set; }
            public int Events { get; set; }
            public ulong Hash { get; set; }
        }

        internal static Result RunGame(ulong seed, string mode)
        {
            long bytes = GC.GetAllocatedBytesForCurrentThread();
            var game = new Adapter(seed);
            var rng = new Random(unchecked((int)seed * 31 + 17));
            var obs = new float[Encoder.ObsDim];
            var candidates = new float[Encoder.MaxActions * Encoder.ActionDim];
            var mask = new float[Encoder.MaxActions];
            while (!game.Engine.State.GameOver && !game.Truncated)
            {
                if (mode == "encode") Encoder.Encode(game, obs, candidates, mask);
                else if (mode == "snapshot") GC.KeepAlive(ShardsSnapshotBuilder.Build(game.Engine, game.Actor));
                game.Step(game.ExerciseChoice(rng));
            }
            return new Result { Wrapper = game.WrapperSteps, Submissions = game.Submissions,
                Capped = game.Truncated, Winner = game.Engine.State.WinnerIndex, Round = game.Engine.State.Round,
                Events = game.Engine.Log.Count, Hash = game.Engine.State.ComputeHash(),
                Bytes = GC.GetAllocatedBytesForCurrentThread() - bytes };
        }

        private static void Benchmark(string[] args)
        {
            int count = args.Length > 1 ? int.Parse(args[1]) : 1000;
            int workers = args.Length > 2 ? int.Parse(args[2]) : 1;
            string mode = args.Length > 3 ? args[3] : "encode";
            ulong seed = args.Length > 4 ? ulong.Parse(args[4]) : 0;
            if (mode != "engine" && mode != "encode" && mode != "snapshot") throw new ArgumentException("Unknown benchmark mode");
            for (int i = 0; i < 32; i++) RunGame((ulong)i + 1_000_000, mode);
            GC.Collect(); GC.WaitForPendingFinalizers();
            int[] before = { GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2) };
            var results = new Result[count];
            var clock = Stopwatch.StartNew();
            RunWorkers(count, workers, i => results[i] = RunGame(seed + (ulong)i, mode));
            clock.Stop();
            long wrapper = results.Sum(r => r.Wrapper), submissions = results.Sum(r => r.Submissions);
            ulong hash = 14695981039346656037UL;
            foreach (var r in results) hash = unchecked((hash ^ r.Hash) * 1099511628211UL);
            Print(new { workload = "fresh-staged-training-host-v1", runtime = Environment.Version.ToString(),
                serverGc = System.Runtime.GCSettings.IsServerGC,
                count, workers, mode, seed, splitBranches = Adapter.SplitBranches, seconds = clock.Elapsed.TotalSeconds,
                wrapperSteps = wrapper, engineSubmissions = submissions,
                wrapperStepsPerSecond = wrapper / clock.Elapsed.TotalSeconds,
                engineSubmissionsPerSecond = submissions / clock.Elapsed.TotalSeconds,
                allocatedBytesPerWrapperStep = (double)results.Sum(r => r.Bytes) / wrapper,
                completed = results.Count(r => !r.Capped), capped = results.Count(r => r.Capped),
                wins = new[] { results.Count(r => !r.Capped && r.Winner == 0), results.Count(r => !r.Capped && r.Winner == 1) },
                meanRounds = results.Average(r => r.Round), maxRounds = results.Max(r => r.Round),
                meanEvents = results.Average(r => r.Events), finalStateFingerprint = hash.ToString("X16"),
                collections = new[] { GC.CollectionCount(0) - before[0], GC.CollectionCount(1) - before[1], GC.CollectionCount(2) - before[2] } });
        }
    }
}
