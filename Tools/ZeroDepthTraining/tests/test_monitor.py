"""CPU-only truthful rates, bounded history, resume and read-only HTTP checks."""
import http.client
import json
import os
from pathlib import Path
import sys
import tempfile
import threading
import unittest
from unittest import mock

helper_directory = Path(__file__).resolve().parent
if not (helper_directory / "monitor.py").exists():
    helper_directory = helper_directory.parent
sys.path.insert(0, str(helper_directory))

import monitor


class ReviewTests(unittest.TestCase):
    def test_review_receipts_and_overdue_time_are_visible_without_gpu(self):
        with tempfile.TemporaryDirectory() as td:
            root=Path(td);run=root/'training';reviews=root/'reviews';run.mkdir();reviews.mkdir()
            (run/'review-service.json').write_text(json.dumps({'directory':str(reviews),'interval_seconds':1800}))
            (reviews/'state.json').write_text(json.dumps({'status':'waiting','next_review_wall':100}))
            (reviews/'active.json').write_text(json.dumps({'slot':2,'state':'intervention_incomplete','intervention_required':True}))
            value=monitor.Monitor(run,now=lambda:190).review_report()
            self.assertEqual(value['interval_seconds'],1800)
            self.assertEqual(value['overdue_seconds'],90)
            self.assertEqual(value['active']['state'],'intervention_incomplete')
            (reviews/'active.json').write_text('{broken')
            self.assertEqual(monitor.Monitor(run,now=lambda:190).review_report()['active'],{})

class RateTests(unittest.TestCase):
    def test_observed_completed_game_rate_includes_idle_wall_time(self):
        rows = [dict(event="start", wall=100, games=0), dict(event="generation", wall=110, games=100),
                dict(event="generation", wall=120, games=150)]
        value = monitor.observed_rate(rows, 130, running=True)
        self.assertEqual(value["games_per_second"], 5)
        self.assertEqual(value["estimated_games_per_hour"], 18_000)
        self.assertEqual(value["observed_seconds"], 30)
        self.assertEqual(monitor.observed_rate(rows, 260, running=True)["games_per_second"], 0)
        self.assertEqual(monitor.observed_rate(rows, 130, running=False)["games_per_second"], 0)

    def test_resume_counter_rollback_resets_rate_baseline(self):
        rows = [dict(event="generation", wall=100, games=50_000), dict(event="start", wall=120, games=49_000)]
        self.assertEqual(monitor.observed_rate(rows, 125, running=True)["games_per_second"], 0)
        rows.append(dict(event="generation", wall=130, games=49_100))
        self.assertEqual(monitor.observed_rate(rows, 140, running=True)["games_per_second"], 5)
        self.assertEqual(monitor.observed_rate([rows[-1]], 140, running=True)["games_per_second"], 0)


class RawRateTests(unittest.TestCase):
    def row(self, event, wall, mono, games, boot="boot-a"):
        return dict(event=event, wall=wall, monotonic=mono * .9, monotonic_raw=mono, boot_id=boot, games=games)

    def rate(self, rows, *, wall=100_000, mono=1030, running=True, boot="boot-a"):
        return monitor.observed_rate(rows, wall, running=running, raw_now=mono, boot_id=boot)

    def test_forward_and_backward_wall_jumps_do_not_change_elapsed_rate_or_series(self):
        rows = [self.row("start", 100, 1000, 0), self.row("generation", 113.6, 1010, 128),
                self.row("generation", 5000, 1015, 256), self.row("generation", 95, 1020, 384)]
        value = self.rate(rows)
        self.assertEqual(value["clock"], "monotonic_raw")
        self.assertEqual(value["clock_boot_id"], "boot-a")
        self.assertEqual(value["observed_seconds"], 30)
        self.assertEqual(value["games_per_second"], 384 / 30)
        self.assertEqual(value["estimated_games_per_hour"], 384 / 30 * 3600)
        self.assertEqual(monitor._generation_speed(rows[1], rows[2]), (128 / 5, "monotonic_raw"))
        self.assertEqual(monitor._generation_speed(rows[2], rows[3]), (128 / 5, "monotonic_raw"))

    def test_idle_checkpoint_ppo_and_reset_time_remain_in_denominator(self):
        rows = [self.row("start", 100, 1000, 0), self.row("generation", 120, 1020, 128)]
        # A one-second raw collection claim cannot erase the full elapsed gap.
        rows[1]["seconds"] = 1
        value = self.rate(rows, mono=1050)
        self.assertEqual(value["observed_seconds"], 50)
        self.assertEqual(value["games_per_second"], 128 / 50)
        self.assertEqual(self.rate(rows, mono=1140)["games_per_second"], 0)
        self.assertEqual(self.rate(rows, running=False)["games_per_second"], 0)

    def test_resume_resets_counter_baseline_independently_of_wall_order(self):
        rows = [self.row("generation", 5000, 900, 50_000), self.row("start", 120, 1000, 49_000),
                self.row("generation", 100, 1010, 49_128)]
        self.assertEqual(self.rate(rows, mono=1020)["games_per_second"], 128 / 20)
        self.assertEqual(self.rate(rows[:2], mono=1005)["games_per_second"], 0)

    def test_foreign_boot_and_invalid_new_clock_never_use_wall_fallback(self):
        rows = [self.row("start", 100, 1000, 0, "old-boot"),
                self.row("generation", 110, 1010, 128, "old-boot")]
        value = self.rate(rows)
        self.assertEqual(value["games_per_second"], 0)
        self.assertEqual(value["clock"], "monotonic_raw")
        self.assertEqual(self.rate(rows, boot=None)["games_per_second"], 0)
        rows[0]["boot_id"] = rows[1]["boot_id"] = "boot-a"
        rows[0]["monotonic_raw"] = rows[1]["monotonic_raw"] = float("nan")
        self.assertEqual(self.rate(rows)["games_per_second"], 0)
        self.assertEqual(self.rate(rows, mono=float("nan"))["games_per_second"], 0)

    def test_mixed_clocks_and_boot_transition_never_share_an_anchor(self):
        rows = [dict(event="start", wall=100, games=0), dict(event="generation", wall=110, games=128),
                self.row("generation", 120, 1000, 256)]
        self.assertEqual(self.rate(rows)["games_per_second"], 0)
        rows.append(self.row("generation", 130, 1010, 384))
        self.assertEqual(self.rate(rows, mono=1020)["games_per_second"], 128 / 20)
        self.assertEqual(monitor._generation_speed(rows[1], rows[2])[0], None)
        rows.append(self.row("generation", 140, 1020, 512, "other-boot"))
        self.assertEqual(self.rate(rows)["games_per_second"], 0)
        self.assertEqual(monitor._generation_speed(rows[3], rows[4])[0], None)

    def test_stalled_raw_clock_returns_exact_zero(self):
        rows = [self.row("start", 100, 1000, 0), self.row("generation", 110, 1000, 128)]
        self.assertEqual(self.rate(rows, mono=1000)["games_per_second"], 0)
        self.assertEqual(monitor._generation_speed(rows[0], rows[1])[0], None)

    def test_legacy_wall_fallback_is_explicit_and_unchanged(self):
        rows = [dict(event="start", wall=100, games=0), dict(event="generation", wall=110, games=128)]
        value = self.rate(rows, wall=120)
        self.assertEqual(value["clock"], "wall")
        self.assertIsNone(value["clock_boot_id"])
        self.assertEqual(value["games_per_second"], 128 / 20)
        self.assertEqual(monitor._generation_speed(rows[0], rows[1]), (128 / 10, "wall"))


    def test_no_raw_on_platform_falls_back_to_wall_without_scaled_monotonic(self):
        rows = [dict(event="start", wall=100, monotonic=1000, boot_id="boot-a", games=0),
                dict(event="generation", wall=110, monotonic=1009, boot_id="boot-a", games=128)]
        value = self.rate(rows, wall=120, mono=1018)
        self.assertEqual(value["clock"], "wall")
        self.assertEqual(value["games_per_second"], 128 / 20)
        self.assertEqual(monitor._generation_speed(rows[0], rows[1]), (128 / 10, "wall"))

    def test_raw_reader_unavailable_has_no_synthesized_counter(self):
        with mock.patch.object(monitor.time, "CLOCK_MONOTONIC_RAW", None):
            self.assertIsNone(monitor._raw_now())
        with mock.patch.object(monitor.time, "clock_gettime", side_effect=OSError("unsupported")):
            self.assertIsNone(monitor._raw_now())
        rows = [self.row("start", 100, 1000, 0), self.row("generation", 110, 1010, 128)]
        self.assertEqual(self.rate(rows, mono=None)["games_per_second"], 0)

    def test_raw_scale_is_used_while_ordinary_monotonic_is_diagnostic_only(self):
        rows = [self.row("start", 100, 1000, 0), self.row("generation", 110, 1011.111111, 128)]
        value = self.rate(rows, mono=1022.222222)
        self.assertAlmostEqual(value["games_per_second"], 128 / 22.222222)
        self.assertLess(value["games_per_second"], 128 / 20)

    def test_counter_cannot_gain_credit_during_raw_stall(self):
        rows = [self.row("start", 100, 1000, 0), self.row("generation", 110, 1010, 128),
                self.row("generation", 120, 1010, 256)]
        self.assertEqual(self.rate(rows, mono=1020)["games_per_second"], 0)


class MonitorTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.directory = Path(self.temporary.name)

    def tearDown(self):
        self.temporary.cleanup()

    def write(self, name, data):
        (self.directory / name).write_text(json.dumps(data))

    def metrics(self, rows):
        (self.directory / "metrics.jsonl").write_text("".join(json.dumps(row) + "\n" for row in rows))

    def test_accepted_learning_diagnostics_are_exposed_without_model_reads(self):
        diagnostics = dict(diagnostic_scope="row_weighted_accepted_minibatches_before_optimizer_step",
            accepted_minibatches=100, diagnostic_rows=102400, normalized_entropy=.45,
            mean_legal_actions=5.2, clip_fraction=.11, value_mse=.7, value_explained_variance=.3,
            rejected_kl=.04, rejected_minibatch_rows=1024, effective_epochs=2.1, update_coverage=.7)
        self.metrics([dict(event="generation",wall=100,games=128,loss=.2,**diagnostics)])
        view=monitor.Monitor(self.directory,now=lambda:110)
        payload=view.state()
        for key,value in diagnostics.items():self.assertEqual(payload["losses"][key],value)
        self.assertEqual(payload["losses"]["loss"],.2)
        self.assertFalse((self.directory/"latest.soicp").exists())

    def test_active_learning_rate_prefers_pinned_identity_and_filters_stale_change(self):
        self.write("config.json", {"learning_rate": .0003, "width": 512})
        self.write("identity.json", {"configuration": {"learning_rate": .0001}})
        marker = {"schema": "shards-zero-depth-learning-rate-improvement-v1", "before_learning_rate": .0003,
                  "after_learning_rate": .0001, "at_games": 1700000, "created_utc": "2026-10-03T15:00:00Z",
                  "reason": "Controlled reduction", "evidence_file": "/tmp/pilot.json", "unrelated_large_field": [1]}
        self.write("learning-rate-improvement.json", marker)
        view = monitor.Monitor(self.directory)
        payload = view.state()
        self.assertEqual(payload["model"]["learning_rate"], .0001)
        self.assertEqual(payload["model"]["learning_rate_source"], "identity")
        self.assertEqual(payload["learning_rate_improvement"]["before_learning_rate"], .0003)
        self.assertNotIn("unrelated_large_field", payload["learning_rate_improvement"])
        self.assertFalse((self.directory / "latest.soicp").exists())
        self.write("learning-rate-improvement.json", marker | {"after_learning_rate": .00015})
        self.assertIsNone(view.state()["learning_rate_improvement"])

    def test_learning_rate_fallback_and_bounded_marker_need_no_checkpoint(self):
        self.write("config.json", {"width": 512})
        view = monitor.Monitor(self.directory)
        self.assertEqual(view.state()["model"]["learning_rate"], .0003)
        self.assertEqual(view.state()["model"]["learning_rate_source"], "default")
        self.write("config.json", {"learning_rate": .0001})
        self.assertEqual(view.state()["model"]["learning_rate"], .0001)
        self.assertEqual(view.state()["model"]["learning_rate_source"], "config")
        (self.directory / "learning-rate-improvement.json").write_text('{"oversized":"' + 'x' * 16384 + '"}')
        self.assertIsNone(view.state()["learning_rate_improvement"])
        self.write("identity.json", {"configuration": {"learning_rate": True}})
        self.assertIsNone(view.state()["model"]["learning_rate"])

    def test_tail_keeps_only_latest_large_rolling_snapshot_and_complete_lines(self):
        path = self.directory / "metrics.jsonl"
        rows = [dict(event="generation", wall=index, games=index, rolling_game_stats={"games_in_window": index}) for index in range(100)]
        path.write_bytes(("".join(json.dumps(row) + "\n" for row in rows) + '{"event":"start"').encode())
        tail = monitor.JsonlTail(path, max_rows=50)
        tail.refresh()
        self.assertEqual(len(tail.rows), 50)
        self.assertEqual(sum("rolling_game_stats" in row for row in tail.rows), 1)
        self.assertEqual(tail.latest_rolling["games_in_window"], 99)
        with path.open("ab") as stream:
            stream.write(b',"wall":101,"games":80}\nnot-json\n{"value":NaN}\n')
        tail.refresh()
        self.assertEqual(tail.invalid_lines, 1)
        self.assertIsNone(tail.rows[-1]["value"])
        path.write_text('{"event":"generation","wall":102,"games":90}\n')
        tail.refresh()
        self.assertIsNone(tail.latest_rolling)
        self.assertEqual(len(tail.rows), 1)

    def test_cold_tail_starts_near_end_without_interpreting_partial_rows(self):
        path = self.directory / "metrics.jsonl"
        path.write_text("".join(json.dumps(dict(event="generation", wall=index, games=index,
                                              rolling_game_stats={"games_in_window": index})) + "\n" for index in range(1000)))
        tail = monitor.JsonlTail(path)
        tail.refresh(max_bytes=4096)
        self.assertEqual(tail.offset, path.stat().st_size)
        self.assertEqual(tail.rows[-1]["games"], 999)
        self.assertEqual(tail.latest_rolling["games_in_window"], 999)
        self.assertEqual(tail.invalid_lines, 0)

    def test_running_progress_prefers_live_totals_and_inactive_rates_are_zero(self):
        self.write("config.json", {"width": 512, "hero_mode": "balanced_random"})
        self.write("status.json", {"games": 0, "generations": 0, "optimizer_steps": 0, "model_parameters": 13_750_273})
        self.write("budget.json", {"limit_seconds": 43200, "charged_seconds": 10,
                   "active": {"pid": os.getpid(), "last_heartbeat_wall": 125, "last_heartbeat_monotonic": 1},
                   "collection_accounting": {"attempted_games": 130, "censored_games": 2}})
        self.metrics([dict(event="start", wall=100, games=0),
                      dict(event="generation", wall=120, games=128, generation=1, total_optimizer_steps=6,
                           total_decisions=2000, rolling_game_stats={"games_in_window": 128})])
        view = monitor.Monitor(self.directory, now=lambda: 130)
        value = view.state()
        self.assertEqual(value["phase"], "training")
        self.assertEqual(value["progress"]["games"], 128)
        self.assertEqual(value["progress"]["optimizer_steps"], 6)
        self.assertEqual(value["model"]["width"], 512)
        self.assertEqual(value["model"]["hero_mode"], "balanced_random")
        self.assertEqual(value["model"]["parameters"], 13_750_273)
        self.assertEqual(value["rolling_game_stats"]["games_in_window"], 128)
        self.assertAlmostEqual(value["rate"]["games_per_second"], 128 / 30)
        self.write("budget.json", {"limit_seconds": 43200, "charged_seconds": 10, "active": None})
        self.assertEqual(view.state()["rate"]["games_per_second"], 0)

    def test_new_resume_start_uses_checkpoint_counters_and_rolling_state(self):
        self.write("status.json", {"games": 1900, "generations": 19, "optimizer_steps": 300,
                                   "rolling_game_stats": {"games_in_window": 1900}})
        self.metrics([dict(event="generation", wall=100, games=2200, generation=22,
                           rolling_game_stats={"games_in_window": 2200}, total_optimizer_steps=400),
                      dict(event="start", wall=120, games=1900)])
        value = monitor.Monitor(self.directory, now=lambda: 125).state()
        self.assertEqual(value["progress"]["games"], 1900)
        self.assertEqual(value["progress"]["generations"], 19)
        self.assertEqual(value["progress"]["optimizer_steps"], 300)
        self.assertEqual(value["rolling_game_stats"]["games_in_window"], 1900)

    def test_same_boot_heartbeat_budget_rate_and_progress_ignore_wall_jumps(self):
        self.write("status.json", {"games": 0, "generations": 0})
        self.write("budget.json", {"limit_seconds": 43200, "charged_seconds": 10,
            "active": {"pid": os.getpid(), "boot_id": "boot-a", "last_heartbeat_wall": 90_000,
                       "last_heartbeat_monotonic": 35}})
        self.metrics([dict(event="start", wall=90_000, monotonic=0, monotonic_raw=0, boot_id="boot-a", games=100),
                      dict(event="generation", wall=85_000, monotonic=30, monotonic_raw=30, boot_id="boot-a", games=228,
                           generation=1)])
        value = monitor.Monitor(self.directory, now=lambda: 100_000, monotonic_now=lambda: 40, raw_now=lambda: 40,
                                boot_id="boot-a").state()
        self.assertEqual(value["phase"], "training")
        self.assertEqual(value["heartbeat_age_seconds"], 5)
        self.assertEqual(value["budget"]["charged_seconds"], 15)
        self.assertEqual(value["progress"]["games"], 228)
        self.assertEqual(value["rate"]["games_per_second"], 128 / 40)
        self.assertEqual(value["series"][0]["observed_games_per_second"], 128 / 30)
        self.assertEqual(value["series"][0]["wall"], 85_000)

    def test_new_resume_start_overrides_tail_even_when_wall_time_moves_backwards(self):
        self.write("status.json", {"games": 1900, "generations": 19, "optimizer_steps": 300,
                                   "rolling_game_stats": {"games_in_window": 1900}})
        self.metrics([dict(event="generation", wall=100, monotonic=10, boot_id="boot-a", games=2200,
                           generation=22, rolling_game_stats={"games_in_window": 2200}),
                      dict(event="start", wall=80, monotonic=20, boot_id="boot-a", games=1900)])
        value = monitor.Monitor(self.directory, now=lambda: 90, monotonic_now=lambda: 25,
                                boot_id="boot-a").state()
        self.assertEqual(value["progress"]["games"], 1900)
        self.assertEqual(value["progress"]["generations"], 19)
        self.assertEqual(value["rolling_game_stats"]["games_in_window"], 1900)

    def test_training_completion_with_live_launcher_is_evaluation_pending(self):
        self.write("status.json", {"state": "complete", "optimizer_steps": 1})
        self.write("budget.json", {"active": None})
        self.write("supervisor.json", {"state": "complete", "supervisor_pid": os.getpid(), "trainer_pid": 99999999})
        self.write("post-training-evaluation-example.plan.json", {"pairs": 2048})
        view = monitor.Monitor(self.directory)
        self.assertEqual(view.state()["phase"], "evaluation_pending")
        self.write("post-training-evaluation-example.json", {"purpose": "final_strength_evaluation",
                    "summary": {"evaluation_finished": False, "recorded_games": 2}, "results": [{"secret": "not sent"}]})
        value = view.state()
        self.assertEqual(value["phase"], "evaluating")
        self.assertNotIn("results", value["evaluation"])
        self.write("post-training-evaluation-example.json", {"purpose": "final_strength_evaluation",
                    "summary": {"evaluation_finished": True, "verdict": "not_demonstrated"}})
        self.assertEqual(view.state()["phase"], "complete")

    def test_http_serves_dashboard_and_state_without_control_or_file_access(self):
        self.write("prepared.json", {"training_started": False})
        original_files = sorted(path.name for path in self.directory.iterdir())
        server = monitor.make_server(monitor.Monitor(self.directory), port=0)
        worker = threading.Thread(target=server.serve_forever, daemon=True)
        worker.start()
        try:
            connection = http.client.HTTPConnection("127.0.0.1", server.server_address[1], timeout=2)
            for path, code in (("/", 200), ("/api/state", 200), ("/health", 200), ("/../config.json", 404), ("/stop", 404)):
                connection.request("GET", path)
                response = connection.getresponse()
                body = response.read()
                self.assertEqual(response.status, code)
                if path == "/api/state":
                    self.assertEqual(json.loads(body)["phase"], "prepared")
                if path == "/":
                    self.assertIn(b"Self-play outcome balance", body)
            connection.close()
            self.assertEqual(sorted(path.name for path in self.directory.iterdir()), original_files)
        finally:
            server.shutdown()
            server.server_close()
            worker.join(timeout=2)


if __name__ == "__main__":
    unittest.main()
