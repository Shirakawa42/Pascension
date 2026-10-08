using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Security.Cryptography;
using System.Text.Json;
using Shards.Content;
using Shards.Engine;

namespace Shards.FactionCountProbe
{
    internal static class Program
    {
        private const BindingFlags PrivateStatic = BindingFlags.NonPublic | BindingFlags.Static;
        private const BindingFlags PrivateInstance = BindingFlags.NonPublic | BindingFlags.Instance;
        private static readonly Assembly Host = typeof(ShardsEngine).Assembly;
        private static readonly Type Adapter = Host.GetType("Shards.Preflight.Adapter");
        private static readonly Type Encoder = Host.GetType("Shards.Preflight.Encoder");
        private static readonly int[] Slots = { 317, 318, 319, 509, 510, 511, 701 };
        private delegate void EncodeDelegate(object game, float[] observation, float[] candidates, float[] mask);
        private static readonly EncodeDelegate Encode = MakeEncoder();
        private static int _nextId = 1;
        private static float _consume;

        private static void Main(string[] args)
        {
            string originalPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../HostV3/bin/Release/net8.0/TrainingHostV3.dll"));
            string hostHash = Hash(Host.Location);
            // The file reference copies the existing assembly; there is no project reference/build of HostV3.
            Check(Hash(originalPath) == hostHash, "Referenced copy matches the unchanged original HostV3 DLL");
            ShardsContentRegistry.EnsureRegistered();
            Encoder.GetMethod("Initialize", PrivateStatic).Invoke(null, null);
            Check(ShardsCardDatabase.All.Count() == 189, "Expected frozen 189-definition catalog");
            var tests = Verify();
            var timing = new List<object>();
            if (!args.Contains("--test-only"))
                foreach (int size in new[] { 10, 20, 40, 100 })
                    foreach (bool yggdrasil in new[] { false, true }) timing.Add(Benchmark(size, yggdrasil));
            Check(Hash(originalPath) == hostHash, "Original HostV3 DLL remains byte-identical");
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                schema = "shards-faction-count-probe-v1", passed = true,
                utc = DateTime.UtcNow.ToString("O"), hostSha256 = hostHash,
                referencedAssembly = Host.Location, originalHost = originalPath,
                processId = Environment.ProcessId, logicalProcessorsVisible = Environment.ProcessorCount,
                workers = 1, serverGc = System.Runtime.GCSettings.IsServerGC,
                tieredCompilationEnvironment = Environment.GetEnvironmentVariable("DOTNET_TieredCompilation"),
                tieredPgoEnvironment = Environment.GetEnvironmentVariable("DOTNET_TieredPGO"),
                timingContext = "Single-worker CPU microbench while another campaign may run; not quiet, end-to-end, or training-throughput measurements.",
                timingFixture = "Constructed mixed owned-zone collections; owned size includes temporary cards. Not seed-reachability or population-distribution evidence.",
                tests, timings = timing, consumed = _consume
            }, new JsonSerializerOptions { WriteIndented = true }));
        }

        private static object Verify()
        {
            int membershipChecks = 0, zoneFixtures = 0;
            var baseline = new float[7]; var candidate = new float[7];
            foreach (var def in ShardsCardDatabase.All)
                foreach (bool yggdrasil in new[] { false, true })
                {
                    var owner = new ShardsPlayer();
                    if (yggdrasil) owner.Destinies.Add(Card("project_yggdrasil", ShardsZone.SetAside));
                    uint membership = FactionCounter.Membership(def, yggdrasil);
                    for (int faction = 0; faction < 7; faction++)
                    {
                        Check(((membership >> faction) & 1) == (ShardsEngine.CountsAs(owner, def, (ShardsFaction)faction) ? 1u : 0u), "Definition membership " + def.Id);
                        membershipChecks++;
                    }
                    for (int zone = 0; zone < 5; zone++)
                        foreach (bool temporary in new[] { false, true })
                        {
                            var lists = OwnedZones(owner);
                            var card = Card(def.Id, ZoneNames[zone]); card.FastPlayed = temporary; card.BanishAtCleanup = temporary;
                            lists[zone].Add(card);
                            AssertCounts(owner, baseline, candidate);
                            lists[zone].Clear(); zoneFixtures++;
                        }
                }
            var random = new Random(63289); var definitions = ShardsCardDatabase.All.ToArray();
            for (int fixture = 0; fixture < 100; fixture++)
            {
                var owner = new ShardsPlayer(); var zones = OwnedZones(owner);
                for (int i = 0; i < fixture * 2; i++)
                {
                    int zone = random.Next(5);
                    var card = Card(definitions[random.Next(definitions.Length)].Id, ZoneNames[zone]);
                    card.FastPlayed = random.Next(2) == 0; zones[zone].Add(card);
                }
                if (fixture % 2 == 0) owner.Destinies.Add(Card("project_yggdrasil", ShardsZone.SetAside));
                owner.SetAside.Add(Card("prism", ShardsZone.SetAside));
                owner.Destinies.Add(Card("prism", ShardsZone.SetAside)); // excluded list, regardless of definition
                AssertCounts(owner, baseline, candidate);
            }
            // Explicit expected result pins wildcard + Yggdrasil + temporary inclusion, independently of the baseline.
            var special = new ShardsPlayer();
            special.Hand.Add(Card("prism", ShardsZone.Hand));
            var temporaryCard = Card("fungal_hermit", ShardsZone.PlayZone); temporaryCard.FastPlayed = true;
            special.PlayZone.Add(temporaryCard);
            special.Destinies.Add(Card("project_yggdrasil", ShardsZone.SetAside));
            special.SetAside.Add(Card("fungal_hermit", ShardsZone.SetAside));
            FactionCounter.OnePass(special, candidate);
            Check(candidate.SequenceEqual(new[] { 0f, 1f, 2f, 1f, 2f, 1f, 0f }.Select(x => x / 20f)), "Explicit special-card result");
            special.SetAside.Add(special.Destinies[0]); special.Destinies.Clear();
            FactionCounter.OnePass(special, candidate);
            Check(candidate.SequenceEqual(new[] { 0f, 1f, 2f, 1f, 1f, 1f, 0f }.Select(x => x / 20f)), "Yggdrasil in SetAside does not activate identity");
            int replayRows = 0;
            for (int seed = 0; seed < 4; seed++)
            {
                object game = NewGame((ulong)(1901 + seed)); var rng = new Random(seed + 88);
                var obs = new float[2048]; var actions = new float[2048]; var mask = new float[64];
                while (!Engine(game).State.GameOver && !(bool)Get(game, "Truncated", true) && replayRows < (seed + 1) * 300)
                {
                    var state = Engine(game).State; ulong before = state.ComputeHash();
                    Encode(game, obs, actions, mask);
                    FactionCounter.OnePass(state.Players[(int)Get(game, "Actor", true)], candidate);
                    Check(Slots.Select((slot, faction) => BitConverter.SingleToInt32Bits(obs[slot]) == BitConverter.SingleToInt32Bits(candidate[faction])).All(x => x), "Exact current encoded slots");
                    Check(state.ComputeHash() == before, "Encoding and candidate leave rules state unchanged");
                    int choice = (int)Adapter.GetMethod("ExerciseChoice", PrivateInstance).Invoke(game, new object[] { rng });
                    Adapter.GetMethod("Step", PrivateInstance).Invoke(game, new object[] { choice }); replayRows++;
                }
            }
            for (int seat = 0; seat < 2; seat++) VerifyPrivacy(seat);
            return new { catalogDefinitions = 189, membershipChecks, zoneFixtures, mixedCollectionFixtures = 100,
                specialExpectedCounts = new[] { 0, 1, 2, 1, 2, 1, 0 }, replayRows,
                hiddenOpponentPerturbationSeats = 2, outputsBitExact = true, noRulesMutation = true,
                scope = "All-definition unit fixtures plus bounded seeded legal replay and full-encoding hidden-zone differential; candidate not installed in a serving Host." };
        }

        private static void VerifyPrivacy(int seat)
        {
            object game = NewGame((ulong)(600 + seat)); var rng = new Random(7); int steps = 0;
            while ((int)Get(game, "Actor", true) != seat || Engine(game).PendingInput.Decision != null ||
                Engine(game).State.Players[1 - seat].Hand.Count == 0 || Engine(game).State.Players[1 - seat].Deck.Count == 0)
            {
                Check(++steps < 1000 && !Engine(game).State.GameOver, "Reach privacy fixture");
                int choice = (int)Adapter.GetMethod("ExerciseChoice", PrivateInstance).Invoke(game, new object[] { rng });
                Adapter.GetMethod("Step", PrivateInstance).Invoke(game, new object[] { choice });
            }
            var obs = new float[2048]; var actions = new float[2048]; var mask = new float[64];
            Encode(game, obs, actions, mask);
            var oldObs = (float[])obs.Clone(); var oldActions = (float[])actions.Clone(); var oldMask = (float[])mask.Clone();
            var candidate = new float[7]; FactionCounter.OnePass(Engine(game).State.Players[seat], candidate);
            var oldCandidate = (float[])candidate.Clone(); var enemy = Engine(game).State.Players[1 - seat];
            enemy.Hand[0].DefId = enemy.Hand[0].DefId == "prism" ? "crystal" : "prism";
            enemy.Deck[0].DefId = enemy.Deck[0].DefId == "fungal_hermit" ? "crystal" : "fungal_hermit";
            enemy.Hand.Reverse(); enemy.Deck.Reverse();
            Refresh(game); Encode(game, obs, actions, mask);
            FactionCounter.OnePass(Engine(game).State.Players[seat], candidate);
            Check(oldObs.SequenceEqual(obs) && oldActions.SequenceEqual(actions) && oldMask.SequenceEqual(mask) && oldCandidate.SequenceEqual(candidate), "Opponent hidden perturbation invariant");
        }

        private static object Benchmark(int size, bool yggdrasil)
        {
            object game = NewGame((ulong)(8000 + size));
            while (Engine(game).PendingInput.Decision?.Context == "soi.herodraft") Adapter.GetMethod("Step", PrivateInstance).Invoke(game, new object[] { 0 });
            var owner = Engine(game).State.Players[(int)Get(game, "Actor", true)];
            foreach (var zone in OwnedZones(owner)) zone.Clear(); owner.Destinies.Clear();
            string[] pool = { "crystal", "fungal_hermit", "shard_abstractor", "prism", "data_heretic_duel" };
            var zones = OwnedZones(owner);
            for (int i = 0; i < size; i++)
            {
                int zone = i < 5 ? 1 : i < 7 ? 3 : i < 10 ? 2 : 0;
                var card = Card(pool[i % pool.Length], ZoneNames[zone]); card.Owner = owner.Index; card.FastPlayed = zone == 3 && i % 2 == 0;
                zones[zone].Add(card);
            }
            if (yggdrasil) owner.Destinies.Add(Card("project_yggdrasil", ShardsZone.SetAside));
            Refresh(game);
            var baseline = new float[7]; var candidate = new float[7];
            AssertCounts(owner, baseline, candidate);
            var obs = new float[2048]; var actions = new float[2048]; var mask = new float[64];
            Action oldCounter = () => { FactionCounter.Baseline(owner, baseline); _consume = baseline[3]; };
            Action newCounter = () => { FactionCounter.OnePass(owner, candidate); _consume = candidate[3]; };
            Action encoder = () => { Encode(game, obs, actions, mask); _consume = obs[509]; };
            Action[] methods = { oldCounter, newCounter, encoder };
            foreach (var method in methods) for (int warm = 0; warm < 10000; warm++) method();
            int[] iterations = methods.Select(Calibrate).ToArray();
            var samples = methods.Select(_ => new List<double>()).ToArray();
            var allocations = methods.Select(_ => new List<double>()).ToArray();
            for (int repeat = 0; repeat < 9; repeat++)
                for (int order = 0; order < methods.Length; order++)
                {
                    int method = (order + repeat) % methods.Length;
                    long beforeBytes = GC.GetAllocatedBytesForCurrentThread(); long start = Stopwatch.GetTimestamp();
                    for (int i = 0; i < iterations[method]; i++) methods[method]();
                    long elapsed = Stopwatch.GetTimestamp() - start;
                    samples[method].Add(elapsed * 1e6 / Stopwatch.Frequency / iterations[method]);
                    allocations[method].Add((GC.GetAllocatedBytesForCurrentThread() - beforeBytes) / (double)iterations[method]);
                }
            double oldUs = Median(samples[0]), newUs = Median(samples[1]), encodeUs = Median(samples[2]);
            return new { ownedCards = size, yggdrasil, temporaryCards = owner.PlayZone.Count(c => c.FastPlayed),
                baselineCounterUs = oldUs, candidateCounterUs = newUs, currentFullEncodeUs = encodeUs,
                countOnlySpeedup = oldUs / newUs, baselineCounterShareOfEncode = oldUs / encodeUs,
                estimatedEncodeSavingsShare = (oldUs - newUs) / encodeUs,
                estimateCaveat = "Difference of separately measured medians, not a measured integrated replacement or end-to-end speedup.",
                iterations, samplesUs = samples, allocatedBytesPerCall = allocations.Select(Median).ToArray(),
                methods = new[] { "original_seven_OwnedCount_normalized", "candidate_one_pass_normalized", "original_full_Encoder.Encode" } };
        }

        private static int Calibrate(Action action)
        {
            int iterations = 1024;
            while (true)
            {
                long start = Stopwatch.GetTimestamp();
                for (int i = 0; i < iterations; i++) action();
                if ((Stopwatch.GetTimestamp() - start) / (double)Stopwatch.Frequency >= .02 || iterations >= 1048576) return iterations;
                iterations *= 2;
            }
        }
        private static double Median(List<double> values) => values.OrderBy(v => v).ElementAt(values.Count / 2);
        private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
        private static void AssertCounts(ShardsPlayer owner, float[] baseline, float[] candidate)
        {
            FactionCounter.Baseline(owner, baseline); FactionCounter.OnePass(owner, candidate);
            Check(baseline.Select(BitConverter.SingleToInt32Bits).SequenceEqual(candidate.Select(BitConverter.SingleToInt32Bits)), "All normalized faction outputs bit exact");
        }
        private static readonly ShardsZone[] ZoneNames = { ShardsZone.Deck, ShardsZone.Hand, ShardsZone.Discard, ShardsZone.PlayZone, ShardsZone.Champions };
        private static List<ShardsCard>[] OwnedZones(ShardsPlayer p) => new[] { p.Deck, p.Hand, p.Discard, p.PlayZone, p.Champions };
        private static ShardsCard Card(string id, ShardsZone zone) => new ShardsCard { InstanceId = _nextId++, DefId = id, Zone = zone, Owner = 0 };
        private static object NewGame(ulong seed) => Activator.CreateInstance(Adapter, PrivateInstance, null, new object[] { seed }, null);
        private static object Get(object game, string name, bool property = false) => property ? Adapter.GetProperty(name, PrivateInstance).GetValue(game) : Adapter.GetField(name, PrivateInstance).GetValue(game);
        private static ShardsEngine Engine(object game) => (ShardsEngine)Get(game, "Engine");
        private static void Refresh(object game)
        {
            Engine(game).State.InvalidateCardIndex();
            Engine(game).PendingInput.LegalActions = Engine(game).LegalActions((int)Get(game, "Actor", true));
            Adapter.GetMethod("Rebuild", PrivateInstance).Invoke(game, null);
        }
        private static EncodeDelegate MakeEncoder()
        {
            // Once-created typed trampoline avoids reflection/boxing/allocation inside measured encodes.
            var method = new DynamicMethod("CallOriginalHostEncoder", typeof(void),
                new[] { typeof(object), typeof(float[]), typeof(float[]), typeof(float[]) }, typeof(Program).Module, true);
            var il = method.GetILGenerator(); il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Castclass, Adapter);
            var span = typeof(Span<float>).GetMethod("op_Implicit", new[] { typeof(float[]) });
            for (short i = 1; i <= 3; i++) { il.Emit(OpCodes.Ldarg, i); il.Emit(OpCodes.Call, span); }
            il.Emit(OpCodes.Call, Encoder.GetMethod("Encode", PrivateStatic)); il.Emit(OpCodes.Ret);
            return (EncodeDelegate)method.CreateDelegate(typeof(EncodeDelegate));
        }
        private static void Check(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); }
    }
}
