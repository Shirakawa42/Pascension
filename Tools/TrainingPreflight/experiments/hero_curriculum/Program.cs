using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Shards.Content;

namespace Shards.Preflight
{
    internal static class Program
    {
        private static void Main(string[] args)
        {
            ShardsContentRegistry.EnsureRegistered();
            Encoder.Initialize();
            if (args.Length > 0 && args[0] != "selftest")
                throw new ArgumentException("Only bounded prefix selftest is supported; no match/training mode");
            var pairs = new HashSet<string>();
            for (int pair = 0; pair < 20; pair++)
            {
                ulong seed = (ulong)(39000 + pair);
                var plan = new HeroCurriculum.Plan(pair);
                Check(plan.Seat0 != plan.Seat1 && pairs.Add(plan.Seat0 + "/" + plan.Seat1), "Distinct ordered pair mapping");
                var automatic = new Adapter(seed);
                var manual = new Adapter(seed);
                ulong rng = automatic.Engine.State.Rng.State;
                var setup = HeroCurriculum.ApplyPlanAtGameStart(automatic, seed, plan);
                Check(setup.Prefix.Count == 2 && setup.Prefix.Select(p => p.Seat).SequenceEqual(new[] { 1, 0 }), "Seat1 then seat0 setup");
                foreach (var step in setup.Prefix)
                {
                    Check(manual.Actor == step.Seat && manual.Visible(step.CandidateSlot).Option.DefId == step.Hero,
                        "Recorded prefix uses actual original legal slots");
                    manual.Step(step.CandidateSlot);
                }
                Check(Encode(automatic).SequenceEqual(Encode(manual)), "Setup equals manual legal prefix byte for byte");
                Check(automatic.Engine.State.Rng.State == rng && manual.Engine.State.Rng.State == rng, "No engine RNG consumed");
                Check(automatic.WrapperSteps == 2 && automatic.Submissions == 2, "Setup wrapper/submission counters");
                Check(automatic.Engine.State.Players.SelectMany(p => p.SetAside).Select(c => c.DefId)
                    .SequenceEqual(manual.Engine.State.Players.SelectMany(p => p.SetAside).Select(c => c.DefId)), "Relics set aside normally");
                int offset = (int)automatic.WrapperSteps;
                for (int suffix = 0; suffix < 4; suffix++)
                {
                    Check(Encode(automatic).SequenceEqual(Encode(manual)), "Replay suffix observation parity");
                    int action = 0;
                    automatic.Step(action); manual.Step(action);
                }
                Check(automatic.WrapperSteps-offset == 4 && automatic.Submissions == manual.Submissions,
                    "Four suffix choices separate from two setup choices");
                Check(Encode(automatic).SequenceEqual(Encode(manual)), "Full prefix replay parity");
                bool duplicateRejected = false;
                try { HeroCurriculum.ApplyAtGameStart(automatic, seed); }
                catch (InvalidOperationException) { duplicateRejected = true; }
                Check(duplicateRejected, "Cannot apply setup twice or midgame");
            }
            var histogram = new int[21];
            var firstSeed = new ulong[21];
            var seen = new bool[21];
            for (ulong seed = 0; seed < 16000; seed++)
            {
                var plan = HeroCurriculum.PlanForSeed(seed);
                int index = plan.PairIndex + 1;
                histogram[index]++;
                if (!seen[index]) { seen[index] = true; firstSeed[index] = seed; }
                var again = HeroCurriculum.PlanForSeed(seed);
                Check(plan.Forced == again.Forced && plan.PairIndex == again.PairIndex, "Deterministic seed plan");
            }
            Check(seen.All(x => x), "Natural mode and all20 pairs occur");
            Check(firstSeed.SequenceEqual(new ulong[] { 1, 0, 30, 3, 36, 5, 24, 64, 97, 31, 111,
                17, 2, 28, 7, 77, 38, 19, 11, 20, 6 }), "Versioned seed-mapping golden vectors");
            Check(Math.Abs(histogram[0]-4000) < 300, "Natural fraction is near25% in the fixed seed probe");
            Check(histogram.Skip(1).All(count => Math.Abs(count-600) < 130), "Fixed-seed pair frequencies show no gross bias");
            for (int index = 0; index < firstSeed.Length; index++)
            {
                ulong seed = firstSeed[index];
                var first = new Adapter(seed); var second = new Adapter(seed);
                var before = Encode(first); ulong rng = first.Engine.State.Rng.State;
                var a = HeroCurriculum.ApplyAtGameStart(first, seed);
                var b = HeroCurriculum.ApplyAtGameStart(second, seed);
                Check(Encode(first).SequenceEqual(Encode(second)), "Deterministic reset/seed observation replay");
                Check(JsonSerializer.Serialize(a) == JsonSerializer.Serialize(b), "Deterministic setup trace");
                Check(first.Engine.State.Rng.State == rng, "Planning/setup never changes engine RNG");
                if (!a.Plan.Forced)
                {
                    Check(before.SequenceEqual(Encode(first)), "Natural branch is completely untouched");
                    Check(first.WrapperSteps == 0 && first.Submissions == 0 && a.Prefix.Count == 0,
                        "Natural branch has zero forced actions and ordinary draft stays visible");
                }
                else Check(a.SetupWrapperSteps == 2 && a.Prefix.Count == 2, "Forced setup explicitly recorded");
            }
            bool malformedRejected = false;
            try { HeroCurriculum.ApplyPlanAtGameStart(new Adapter(1), 1, default); }
            catch (ArgumentException) { malformedRejected = true; }
            Check(malformedRejected, "Default/uninitialized explicit plan is rejected");
            Console.WriteLine(JsonSerializer.Serialize(new { passed = true, schema = HeroCurriculum.Schema,
                observationSchema = Encoder.SchemaVersion, actualOrderedPairPrefixes = 20,
                suffixStepsPerPair = 4, seedPlanProbe = 16000, naturalSeeds = histogram[0],
                forcedPairCounts = histogram.Skip(1).ToArray(), firstSeedForNaturalThenPairs = firstSeed,
                prefixOnly = true, completeMatches = 0, optimizerUpdates = 0,
                engineRngUnchanged = true, naturalInputsUnchanged = true,
                forcedSetupCountedSeparatelyFromPolicySuffix = true }, new JsonSerializerOptions { WriteIndented = true }));
        }

        private static float[] Encode(Adapter game)
        {
            var values = new float[Encoder.ObsDim + Encoder.MaxActions*Encoder.ActionDim + Encoder.MaxActions];
            Encoder.Encode(game, values.AsSpan(0, Encoder.ObsDim),
                values.AsSpan(Encoder.ObsDim, Encoder.MaxActions*Encoder.ActionDim),
                values.AsSpan(Encoder.ObsDim+Encoder.MaxActions*Encoder.ActionDim, Encoder.MaxActions));
            return values;
        }
        private static void Check(bool condition, string message)
        { if (!condition) throw new InvalidOperationException("Hero curriculum selftest: " + message); }
    }
}
