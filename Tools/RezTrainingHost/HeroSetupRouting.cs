using System;
using System.Globalization;
using System.IO;
using System.Text.Json;

namespace Shards.Preflight
{
    internal static class HeroSetupRouting
    {
        internal const string Natural = "natural", Curriculum = "curriculum-75-25";
        internal static object Catalog => new {
            schema = HeroCurriculum.Schema, defaultMode = Natural, trainingMode = Curriculum,
            forcedProbability = 1.0, orderedPairCount = 20,
            heroOrder = new[] { "decima", "tetra", "volos", "kosynwu", "rez" },
            selection = "separate-seed-derived-splitmix64-rejection-v1",
            setupPolicyActions = false, adapterCapCountersIncludeSetup = true };

        internal static string ParseServe(string[] args)
        {
            if (args.Length == 1) return Natural;
            if (args.Length != 3 || args[1] != "--hero-setup")
                throw new ArgumentException("serve accepts only --hero-setup natural|curriculum-75-25");
            return Validate(args[2]);
        }

        internal static string Validate(string mode)
        {
            if (mode != Natural && mode != Curriculum) throw new ArgumentException("Invalid explicit hero setup mode");
            return mode;
        }

        internal static Adapter Create(ulong seed, string mode, out HeroCurriculum.Setup setup)
        {
            Validate(mode);
            var game = new Adapter(seed);
            // Each consecutive block of 20 seeds receives a fresh shuffled permutation
            // of the 20 ordered distinct-hero pairs. No engine RNG is consumed.
            ulong origin = ulong.Parse(Environment.GetEnvironmentVariable("SHARDS_STATS_EXPECTED_SEED") ?? "0");
            ulong index = seed - origin;
            bool rezOnly = Environment.GetEnvironmentVariable("SHARDS_REZ_TRAINING") == "1";
            var order = rezOnly ? new[] { 3, 7, 11, 15, 16, 17, 18, 19 } : new int[20];
            if (!rezOnly) for (int i = 0; i < 20; i++) order[i] = i;
            var random = new Random(unchecked((int)(index / (ulong)order.Length) ^ 9271957));
            for (int i = order.Length - 1; i > 0; i--) { int j = random.Next(i + 1); int saved = order[i]; order[i] = order[j]; order[j] = saved; }
            setup = HeroCurriculum.ApplyPlanAtGameStart(game, seed, new HeroCurriculum.Plan(order[index % (ulong)order.Length]));
            return game;
        }

        internal static object Population(string requestedMode, bool forced) => new {
            schema = HeroCurriculum.Schema, requested_mode = Validate(requestedMode),
            cohort = forced ? "forced-random" : "natural-draft",
            forced_probability = 1.0,
            setup_wrapper_steps_per_game = forced ? 2 : 0,
            policy_actions_include_setup = false,
            rez_training = Environment.GetEnvironmentVariable("SHARDS_REZ_TRAINING") == "1",
            scope = "Patched-rule self-play with random heroes. Each ordered distinct-hero pair gets one game per randomly permuted block of 20." };

        internal static object Replay(string mode, HeroCurriculum.Setup setup, Adapter game) => new {
            schema = "shards-hero-setup-replay-v1", hero_setup_mode = Validate(mode), hero_setup_schema = HeroCurriculum.Schema,
            engine_seed_hex = setup.EngineSeed.ToString("x16"), setup,
            adapter_wrapper_steps = game.WrapperSteps, adapter_engine_submissions = game.Submissions,
            policy_suffix_wrapper_steps = game.WrapperSteps - setup.SetupWrapperSteps,
            policy_suffix_engine_submissions = game.Submissions - setup.SetupEngineSubmissions,
            round = game.Engine.State.Round,
            instruction = "Recreate the same HostV5 hero setup mode and engine seed, then replay the recorded policy suffix. Do not apply the setup twice." };

        internal static void LogCensor(string mode, HeroCurriculum.Setup setup, Adapter game)
        {
            object record = Replay(mode, setup, game);
            Console.Error.WriteLine(JsonSerializer.Serialize(new { hero_setup_censor_replay = record }));
            string directory = Environment.GetEnvironmentVariable("SHARDS_STATS_DIRECTORY");
            if (directory == null || Environment.GetEnvironmentVariable("SHARDS_STATS_PURPOSE") != "final_evaluation") return;
            try { PersistReplay(directory, setup.EngineSeed, record); }
            catch (Exception error)
            {
                // Optional diagnostics must never alter an accepted game action.
                Console.Error.WriteLine(JsonSerializer.Serialize(new {
                    hero_setup_replay_error = true, engine_seed_hex = setup.EngineSeed.ToString("x16"),
                    error = error.Message, game_continues = true }));
            }
        }

        internal static string PersistReplay(string root, ulong seed, object record)
        {
            string directory = Path.Combine(root, "hero-setup-replay");
            Directory.CreateDirectory(directory);
            string destination = Path.Combine(directory, "censored-setup-" + seed.ToString("x16") +
                "-" + Guid.NewGuid().ToString("N") + ".json");
            string temporary = destination + ".tmp";
            try
            {
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    stream.Write(JsonSerializer.SerializeToUtf8Bytes(record));
                    stream.Flush(true);
                }
                File.Move(temporary, destination, false);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
            return destination;
        }

        internal static void PrintSetup(string[] args)
        {
            if (args.Length != 4 || args[2] != "--hero-setup")
                throw new ArgumentException("hero-setup SEED --hero-setup natural|curriculum-75-25");
            ulong seed = ulong.Parse(args[1], CultureInfo.InvariantCulture);
            var game = Create(seed, args[3], out var setup);
            Program.Print(Replay(args[3], setup, game));
        }
    }
}
