using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using Shards.Engine;

namespace Shards.Preflight
{
    internal static class StatisticsSelfTest
    {
        internal static void Run(string[] args)
        {
            string directory = Path.Combine(Path.GetTempPath(), "shards-statistics-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                string snapshot;
                using (var stats = new TrainingStatistics(directory, "training_pool", 123, 2))
                {
                    stats.Reset(2, 123); snapshot = stats.SnapshotPath;
                    var game = new Adapter(123); var engine = game.Engine;
                    engine.State.Players[0].CharacterId = engine.State.Players[1].CharacterId = "decima";
                    Buy(stats, 0, engine, 0, "fungal_hermit", false, 1, 3);
                    Buy(stats, 0, engine, 1, "fungal_hermit", false, 2, 4);
                    Buy(stats, 0, engine, 0, "fungal_hermit", false, 3, 2);
                    engine.State.GameOver = true; engine.State.WinnerIndex = 0; engine.State.Round = 9;
                    engine.State.Players[0].Mastery = 15; engine.State.Players[0].Health = 30;
                    engine.State.Players[1].Mastery = 6;
                    stats.Finish(0, engine.State, true, 123);
                    Buy(stats, 1, engine, 0, "fungal_hermit", true, 7, 1);
                    Buy(stats, 1, engine, 1, "fungal_hermit", true, 8, 2);
                    engine.State.GameOver = false; engine.State.WinnerIndex = -1; engine.State.Round = 401;
                    stats.Finish(1, engine.State, false, 124);
                    engine.State.Players[1].CharacterId = "tetra";
                    string relic = ShardsCardDatabase.All.First(c => c.Type == ShardsCardType.Relic).Id;
                    string destiny = ShardsCardDatabase.All.First(c => c.Type == ShardsCardType.Destiny).Id;
                    var mark = new TrainingStatistics.StepMark(engine.Log.Count, 5);
                    engine.Emit(new ShardsRelicRecruitedEvent { PlayerIndex = 0, DefId = relic });
                    engine.Emit(new ShardsDestinyTakenEvent { PlayerIndex = 1, DefId = destiny });
                    engine.Emit(new ShardsCardBoughtEvent { PlayerIndex = 0, DefId = "shard_abstractor", FastPlay = false, CostPaid = 0 });
                    engine.Emit(new ShardsCardBoughtEvent { PlayerIndex = 1, DefId = "fungal_hermit", FastPlay = true, CostPaid = 0 });
                    stats.ObserveStep(0, engine, mark);
                    engine.State.GameOver = true; engine.State.Round = 12;
                    stats.Finish(0, engine.State, true, 125);
                    // A discarded partial cohort contributes neither choices nor outcome rows.
                    Buy(stats, 1, engine, 0, "prism", false, 3, 3);
                    object lanes = typeof(TrainingStatistics).GetField("_lanes", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(stats);
                    stats.Reset(2, 900);
                    Check(ReferenceEquals(lanes, typeof(TrainingStatistics).GetField("_lanes", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(stats)), "Reset reuses fixed lane arrays");
                }
                using var doc = JsonDocument.Parse(File.ReadAllBytes(snapshot));
                var root = doc.RootElement; var totals = root.GetProperty("totals");
                Check(totals.GetProperty("completed_games").GetInt64() == 2 && totals.GetProperty("censored_games").GetInt64() == 1, "Real terminal/censor counts");
                Check(totals.GetProperty("draws").GetInt64() == 1 && totals.GetProperty("unfinished_discarded_games").GetInt64() == 1, "Draw/discard separation");
                var rows = root.GetProperty("rows").EnumerateArray().ToArray();
                var buy = rows.Single(r => r.GetProperty("choice_kind").GetString() == "buy");
                Check(Number(buy, "selected_player_games") == 2 && Number(buy, "selected_game_clusters") == 1, "Two player selections share one game cluster");
                Check(Number(buy, "wins") == 1 && Number(buy, "losses") == 1 && Number(buy, "pick_count") == 3, "Duplicate purchases do not duplicate outcome players");
                Check(Number(buy, "acquisition_round_sum") == 6 && Number(buy, "cost_paid_sum_known") == 9, "Actual purchase rounds/costs");
                var fast = rows.Single(r => r.GetProperty("choice_kind").GetString() == "fastplay");
                Check(Number(fast, "selected_player_games") == 0 && Number(fast, "draws") == 0 && Number(fast, "censored_player_games") == 2 && Number(fast, "censored_game_clusters") == 1 && Number(fast, "censored_pick_count") == 2, "Censors remain unknown, clustered once");
                Check(Number(fast, "censored_round_sum") == 15 && Number(fast, "censored_cost_paid_sum") == 3, "Censored acquisition summaries stay separate");
                foreach (string kind in new[] { "relic", "destiny", "effect_acquire", "effect_fastplay" })
                {
                    var row = rows.Single(r => r.GetProperty("choice_kind").GetString() == kind);
                    Check(Number(row, "draws") == 1 && Number(row, "pick_count") == 1 && Number(row, "acquisition_round_sum") == 5, "Actual event category " + kind);
                }
                var heroBuy = root.GetProperty("hero_choice_rows").EnumerateArray().Single(r => r.GetProperty("choice_kind").GetString() == "buy");
                Check(Number(heroBuy, "selected_game_clusters") == 1 && Number(heroBuy, "selected_player_games") == 2, "Same-hero synthetic fixture clusters once");
                Check(root.GetProperty("final_state_sums").GetProperty("resolved_decisive_games").GetInt64() == 1 && root.GetProperty("final_state_sums").GetProperty("winner_mastery_sum").GetInt64() == 15, "Terminal state sums");
                Check(root.GetProperty("final_round_histogram")[9].GetInt64() == 1 && root.GetProperty("final_round_histogram")[12].GetInt64() == 1 && root.GetProperty("final_round_histogram")[401].GetInt64() == 0, "Final round histogram excludes censor");
                Check(root.GetProperty("final").GetBoolean() && root.GetProperty("reset_count").GetInt64() == 2, "Final flush and reset metadata");
                var history = Directory.GetFiles(Path.Combine(directory, "history"), "*.json");
                Check(history.Length > 0 && history.Length <= 512, "Bounded immutable history produced");
                Check(history.Any(file => File.ReadAllBytes(file).SequenceEqual(File.ReadAllBytes(snapshot))), "Latest/history share identical serialized bytes");
                if (args.Length > 1) File.Copy(snapshot, args[1], true);
                PublicationThreshold(directory);
                ErrorIsolation(directory);
                Program.Print(new { passed = true, actual_event_fixtures = "buy/fastplay/relic/destiny/effect_acquire/effect_fastplay",
                    duplicate_player_and_game_cluster_checks = true, caps_not_draws = true,
                    incomplete_reset_omitted = true, reset_reuses_lane_arrays = true,
                    hero_choices = true, terminal_sums = true, final_flush = true,
                    publication_after_10000_resolved_games = true, error_sidecar_and_history_isolation = true,
                    fixture = args.Length > 1 ? args[1] : null,
                    scope = "Fixed expected typed-event/outcome unit fixtures; wire tests separately execute real legal games" });
            }
            finally { Directory.Delete(directory, true); }
        }

        private static void Buy(TrainingStatistics stats, int lane, ShardsEngine engine, int seat,
            string definition, bool fast, int round, int cost)
        {
            var mark = new TrainingStatistics.StepMark(engine.Log.Count, round, seat, definition, fast);
            engine.Emit(new ShardsCardBoughtEvent { PlayerIndex = seat, DefId = definition, FastPlay = fast, CostPaid = cost });
            stats.ObserveStep(lane, engine, mark);
        }

        private static void PublicationThreshold(string directory)
        {
            using var stats = new TrainingStatistics(Path.Combine(directory, "threshold"), "training_pool", 5, 1);
            stats.Reset(1, 5); var game = new Adapter(5);
            game.Engine.State.GameOver = true; game.Engine.State.WinnerIndex = 0;
            for (int i = 0; i < 10000; i++)
            {
                stats.BeginStep(0, game, 0);
                stats.Finish(0, game.Engine.State, true, (ulong)(5 + i));
            }
            stats.BatchBoundary();
            Check(SpinWait.SpinUntil(() =>
            {
                if (!File.Exists(stats.SnapshotPath)) return false;
                using var json = JsonDocument.Parse(File.ReadAllBytes(stats.SnapshotPath));
                return json.RootElement.GetProperty("totals").GetProperty("completed_games").GetInt64() == 10000;
            }, 2000), "10k completed threshold publishes before close");
        }

        private static void ErrorIsolation(string directory)
        {
            string broken = Path.Combine(directory, "history-failure"); Directory.CreateDirectory(broken);
            File.WriteAllText(Path.Combine(broken, "history"), "block directory creation");
            string snapshot;
            using (var stats = new TrainingStatistics(broken, "training_pool", 1, 1))
            { stats.Reset(1, 1); snapshot = stats.SnapshotPath; }
            Check(File.Exists(snapshot) && Directory.GetFiles(broken, "*.history-error.json").Length == 1, "History failure preserves regular snapshot");
            using (var stats = new TrainingStatistics(Path.Combine(directory, "routing-failure"), "training_pool", 1, 1))
            { stats.Reset(1, 2); snapshot = stats.ErrorPath; }
            Check(File.Exists(snapshot), "Routing failure emits sidecar without throwing");
        }
        private static long Number(JsonElement row, string name) => row.GetProperty(name).GetInt64();
        private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException("Statistics selftest: " + message); }
    }
}
