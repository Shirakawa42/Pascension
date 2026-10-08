using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using Shards.Engine;

namespace Shards.Preflight
{
    internal static class HeroRoutingSelfTest
    {
        internal static void Run()
        {
            Check(HeroSetupRouting.ParseServe(new[] { "serve" }) == "natural", "Evaluation defaults natural");
            Check(HeroSetupRouting.ParseServe(new[] { "serve", "--hero-setup", "curriculum-75-25" }) == "curriculum-75-25", "Explicit curriculum option");
            foreach (var invalid in new[] { new[] { "serve", "--hero-setup" },
                new[] { "serve", "--hero-setup", "random" }, new[] { "serve", "natural" },
                new[] { "serve", "--hero-setup", "natural", "extra" } })
            {
                bool rejected = false;
                try { HeroSetupRouting.ParseServe(invalid); } catch (ArgumentException) { rejected = true; }
                Check(rejected, "Malformed explicit setup rejected");
            }
            for (ulong seed = 0; seed < 24; seed++)
            {
                var normal = HeroSetupRouting.Create(seed, "natural", out var natural);
                var original = new Adapter(seed);
                Check(!natural.Plan.Forced && natural.Prefix.Count == 0 && normal.Engine.State.ComputeHash() == original.Engine.State.ComputeHash(), "Natural leaves state unchanged");
                var mixed = HeroSetupRouting.Create(seed, "curriculum-75-25", out var a);
                var replay = HeroSetupRouting.Create(seed, "curriculum-75-25", out var b);
                Check(JsonSerializer.Serialize(a) == JsonSerializer.Serialize(b) && mixed.Engine.State.ComputeHash() == replay.Engine.State.ComputeHash(), "Seed reset exact replay");
                Check(mixed.WrapperSteps == a.SetupWrapperSteps && mixed.Submissions == a.SetupEngineSubmissions, "Zero policy suffix at initialization");
            }
            StatisticsRouting();
            ReplayPersistence();
        }

        private static void ReplayPersistence()
        {
            string directory = Path.Combine(Path.GetTempPath(), "shards-v5-replay-" + Guid.NewGuid().ToString("N"));
            try
            {
                var game = HeroSetupRouting.Create(0, "curriculum-75-25", out var setup);
                game.Step(0);
                var record = HeroSetupRouting.Replay("curriculum-75-25", setup, game);
                string first = HeroSetupRouting.PersistReplay(directory, 0, record);
                string second = HeroSetupRouting.PersistReplay(directory, 0, record);
                Check(first != second && File.ReadAllBytes(first).SequenceEqual(File.ReadAllBytes(second)), "Same seed never overwrites a prior trace");
                using var doc = JsonDocument.Parse(File.ReadAllBytes(first));
                Check(doc.RootElement.GetProperty("hero_setup_mode").GetString() == "curriculum-75-25" &&
                    doc.RootElement.GetProperty("policy_suffix_wrapper_steps").GetInt64() == 1 &&
                    doc.RootElement.GetProperty("adapter_wrapper_steps").GetInt64() == 3, "Persistent replay has mode and exact setup offset");
                Check(Directory.GetFiles(directory, "*.tmp", SearchOption.AllDirectories).Length == 0, "Atomic trace leaves no temporary file");
            }
            finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
        }

        private static void StatisticsRouting()
        {
            string directory = Path.Combine(Path.GetTempPath(), "shards-v5-routing-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                var natural = new TrainingStatistics(Path.Combine(directory, "natural-draft"), "training_pool", 0, 2,
                    HeroSetupRouting.Population("curriculum-75-25", false));
                var forced = new TrainingStatistics(Path.Combine(directory, "forced-random"), "training_pool", 0, 2,
                    HeroSetupRouting.Population("curriculum-75-25", true));
                using (var stats = new CurriculumStatistics(natural, forced))
                {
                    stats.Reset(2, 0);
                    for (int lane = 0; lane < 2; lane++)
                    {
                        var game = HeroSetupRouting.Create((ulong)lane, "curriculum-75-25", out var setup);
                        Check(setup.Plan.Forced == (lane == 0), "Golden seed cohort fixture");
                        stats.SetSetup(lane, setup);
                        var mark = stats.BeginStep(lane, game, 0);
                        game.Engine.Emit(new ShardsCardBoughtEvent { PlayerIndex = lane, DefId = "fungal_hermit", CostPaid = 0 });
                        stats.ObserveStep(lane, game.Engine, mark);
                        // Typed terminal/censor unit fixtures, no full matches or policy outcomes.
                        game.Engine.State.GameOver = lane == 1;
                        game.Engine.State.WinnerIndex = lane == 1 ? 1 : -1;
                        game.Engine.State.Round = lane == 1 ? 9 : 401;
                        stats.Finish(lane, game.Engine.State, lane == 1, (ulong)lane);
                    }
                    // Each lane switches/reinitializes from the actual subsequent seed.
                    // Both next seeds are forced; only that pool can count unfinished lanes.
                    for (int lane = 0; lane < 2; lane++)
                    {
                        var game = HeroSetupRouting.Create((ulong)(lane+2), "curriculum-75-25", out var setup);
                        Check(setup.Plan.Forced, "Auto-reset cohort fixture");
                        stats.SetSetup(lane, setup); stats.BeginStep(lane, game, 0);
                    }
                    stats.Reset(2, 100);
                }
                using var n = JsonDocument.Parse(File.ReadAllBytes(natural.SnapshotPath));
                using var f = JsonDocument.Parse(File.ReadAllBytes(forced.SnapshotPath));
                CheckSnapshot(n.RootElement, "natural-draft", 1, 0, 0);
                CheckSnapshot(f.RootElement, "forced-random", 0, 1, 2);
                Check(n.RootElement.GetProperty("rows").GetArrayLength() == 1 && f.RootElement.GetProperty("rows").GetArrayLength() == 1, "No cross-population duplicate rows");
                Check(n.RootElement.GetProperty("rows")[0].GetProperty("censored_player_games").GetInt64() == 0 &&
                    f.RootElement.GetProperty("rows")[0].GetProperty("selected_player_games").GetInt64() == 0, "Censored and resolved populations stay separate");
                Check(Directory.GetFiles(directory, "session-*.json").Length == 0, "No pooled parent snapshot");
            }
            finally { Directory.Delete(directory, true); }
        }

        private static void CheckSnapshot(JsonElement snapshot, string cohort, int completed, int censored, int unfinished)
        {
            Check(snapshot.GetProperty("schema").GetString() == "shards-training-pool-stats-v1", "Raw stats schema preserved");
            Check(snapshot.GetProperty("hero_setup").GetProperty("cohort").GetString() == cohort, "Explicit cohort metadata");
            var totals = snapshot.GetProperty("totals");
            Check(totals.GetProperty("completed_games").GetInt64() == completed &&
                totals.GetProperty("censored_games").GetInt64() == censored &&
                totals.GetProperty("unfinished_discarded_games").GetInt64() == unfinished, "No doubled games, censors or unfinished lanes");
        }

        private static void Check(bool condition, string message)
        { if (!condition) throw new InvalidOperationException("Hero routing test: " + message); }
    }
}
