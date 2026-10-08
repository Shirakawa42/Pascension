"""CPU-only monitor parser, publication, and loopback HTTP regressions."""
import http.client
import json
import os
from pathlib import Path
import tempfile
import threading
import time
import unittest
from unittest.mock import patch

import monitor_training as m


class Fixture(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.root = Path(self.temp.name)

    def tearDown(self):
        self.temp.cleanup()

    def write(self, path, data):
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(json.dumps(data))


class TailTests(Fixture):
    def test_final_dataset_excludes_training_even_before_first_publication(self):
        self.write(self.root / "balance-statistics.json", {"schema": "shards-balance-statistics-v1", "state": "ready", "snapshot_id": "old-training"})
        self.write(self.root / "balance-statistics-frozen.json", {"schema": "shards-balance-statistics-v1", "state": "ready", "snapshot_id": "old-evaluation"})
        self.write(self.root / "final-statistics-mode.json", {"enabled": True})
        monitor = m.Monitor(self.root)
        monitor.refresh()
        self.assertEqual(monitor.balance_sources, {"final": None})
        final = {"schema": "shards-balance-statistics-v1", "state": "ready", "snapshot_id": "final-only", "totals": {"resolved_games": 50000}}
        self.write(self.root / "balance-statistics-final.json", final)
        monitor.refresh()
        payload = json.loads(monitor.statistics_response()[0])
        self.assertEqual(payload["balance_statistics_sources"], {"final": final})
        self.assertEqual(payload["balance_statistics"]["totals"]["resolved_games"], 50000)

    def test_final_snapshot_supplies_its_own_patched_card_rules(self):
        self.write(self.root / "final-statistics-mode.json", {"enabled": True})
        catalog = {"cards": [{"id": "doom_gate", "defense": 7}], "heroes": []}
        final = {"schema": "shards-balance-statistics-v1", "state": "ready",
                 "snapshot_id": "patched-final", "card_catalog": catalog}
        self.write(self.root / "balance-statistics-final.json", final)
        monitor = m.Monitor(self.root)
        monitor.refresh()
        payload = json.loads(monitor.statistics_response()[0])
        self.assertEqual(payload["catalog"], catalog)
        self.assertEqual(set(payload["balance_statistics_sources"]), {"final"})

    def test_enriched_final_snapshot_exceeds_generic_status_limit(self):
        self.write(self.root / "final-statistics-mode.json", {"enabled": True})
        final = {"snapshot_id": "large-final", "balance_analysis": {"evidence": "x" * (5 * 1024 * 1024)}}
        self.write(self.root / "balance-statistics-final.json", final)
        monitor = m.Monitor(self.root)
        monitor.refresh()
        self.assertEqual(monitor.balance_sources["final"]["snapshot_id"], "large-final")
        self.assertIsNone(monitor.balance_error)
        (self.root / "balance-statistics-final.json").write_text("invalid")
        monitor.refresh()
        self.assertIsNone(monitor.balance_sources["final"])
        self.assertIn("could not be loaded", monitor.balance_error)

    def test_incremental_partial_invalid_and_nonfinite_rows(self):
        path = self.root / "metrics.jsonl"
        path.write_bytes(b'{"event":"first","wall":1}\n{"event":')
        tail = m.JsonlTail(path)
        tail.refresh()
        self.assertEqual(len(tail.rows), 1)
        offset = tail.offset
        tail.refresh()
        self.assertEqual(tail.offset, offset)
        self.assertEqual(len(tail.rows), 1)
        with path.open("ab") as stream:
            stream.write(b'"second","wall":2}\nnot-json\n{"value":NaN}\n')
        tail.refresh()
        self.assertEqual([r.get("event") for r in tail.rows], ["first", "second", None])
        self.assertIsNone(tail.rows[-1]["value"])
        self.assertEqual(tail.invalid_lines, 1)
        self.assertFalse(tail.pending)

    def test_rotation_truncation_and_bounded_retention(self):
        path = self.root / "metrics.jsonl"
        path.write_text(''.join(json.dumps({"wall": i}) + '\n' for i in range(10)))
        tail = m.JsonlTail(path, max_rows=3)
        tail.refresh()
        self.assertEqual([r["wall"] for r in tail.rows], [7, 8, 9])
        path.write_text('{"wall":11}\n')
        tail.refresh()
        self.assertEqual(list(tail.rows), [{"wall": 11}])
        replacement = self.root / "new"
        replacement.write_text('{"wall":12}\n')
        replacement.replace(path)
        tail.refresh()
        self.assertEqual(list(tail.rows), [{"wall": 12}])

    def test_oversized_unterminated_line_does_not_grow_unbounded(self):
        path = self.root / "metrics.jsonl"
        path.write_bytes(b'x' * 300000)
        tail = m.JsonlTail(path)
        tail.refresh()
        self.assertTrue(tail.dropping)
        self.assertEqual(tail.pending, b'')
        with path.open("ab") as stream:
            stream.write(b'junk\n{"wall":3}\n')
        tail.refresh()
        self.assertEqual(list(tail.rows), [{"wall": 3}])
        self.assertEqual(tail.invalid_lines, 1)

    def test_sampling_keeps_endpoints_and_time_filter(self):
        rows = [{"wall": i} for i in range(100)]
        result = m.sampled(rows, 50, 10)
        self.assertEqual(len(result), 10)
        self.assertEqual(result[0]["wall"], 50)
        self.assertEqual(result[-1]["wall"], 99)


class ProcessIdentityTests(Fixture):
    def test_v9_training_and_curriculum_host_are_recognized(self):
        command = ["python", str(m.HERE/"experiments/variant_v9_entry.py"), "train", "--run-dir", str(self.root)]
        self.assertTrue(m.process_argv_matches(command, "trainer", run_dir=self.root))
        host = ["dotnet", str(m.HERE/"experiments/HostV9/bin/Release/net8.0/TrainingHostV9.dll"),
                "serve", "--hero-setup", "curriculum-75-25"]
        self.assertTrue(m.process_argv_matches(host, "host"))
        host[-1] = "natural"
        self.assertFalse(m.process_argv_matches(host, "host"))

    def test_v8_training_and_curriculum_host_are_recognized(self):
        command = ["python", str(m.HERE/"experiments/variant_v8_entry.py"), "train", "--run-dir", str(self.root)]
        self.assertTrue(m.process_argv_matches(command, "trainer", run_dir=self.root))
        host = ["dotnet", str(m.HERE/"experiments/HostV8/bin/Release/net8.0/TrainingHostV8.dll"),
                "serve", "--hero-setup", "curriculum-75-25"]
        self.assertTrue(m.process_argv_matches(host, "host"))
        host[-1] = "natural"
        self.assertFalse(m.process_argv_matches(host, "host"))

    def test_v7_policy_training_entry_is_recognized(self):
        command = ["python", str(m.HERE/"experiments/variant_v7_entry.py"), "train", "--run-dir", str(self.root)]
        self.assertTrue(m.process_argv_matches(command, "trainer", run_dir=self.root))
        command[2] = "evaluate"
        self.assertFalse(m.process_argv_matches(command, "trainer", run_dir=self.root))

    def test_v6_training_and_curriculum_host_are_recognized(self):
        command = ["python", str(m.HERE/"experiments/variant_v6_entry.py"), "train", "--run-dir", str(self.root)]
        self.assertTrue(m.process_argv_matches(command, "trainer", run_dir=self.root))
        command[2] = "evaluate"
        self.assertFalse(m.process_argv_matches(command, "trainer", run_dir=self.root))
        host = ["dotnet", str(m.HERE/"experiments/HostV6/bin/Release/net8.0/TrainingHostV6.dll"),
                "serve", "--hero-setup", "curriculum-75-25"]
        self.assertTrue(m.process_argv_matches(host, "host"))

    def argv(self, mode="train", script="experiments/variant_entry.py", run=None):
        command = ["/home/example/venv/bin/python", str(m.HERE/script)]
        if script in ("experiments/variant_entry.py", "experiments/variant_v4_entry.py"):
            command.append(mode)
        return command+["--run-dir", str(run or self.root)]

    def test_current_and_legacy_training_entries_with_correct_run(self):
        for script in ("train_campaign.py", "experiments/variant_entry.py", "experiments/variant_v4_entry.py"):
            with self.subTest(script=script):
                self.assertTrue(m.process_argv_matches(self.argv(script=script), "trainer", run_dir=self.root))
        command = self.argv()
        command.insert(1, "-u")
        self.assertTrue(m.process_argv_matches(command, "trainer", run_dir=self.root))
        self.assertTrue(m.process_argv_matches(["python3", "experiments/variant_entry.py", "train",
                        "--run-dir="+str(self.root)], "trainer", cwd=m.HERE, run_dir=self.root))

    def test_evaluation_supervisors_and_argument_mentions_are_not_trainers(self):
        for mode in ("supervise", "evaluate", "identity"):
            command = self.argv(mode)+["--extra", str(m.HERE/"train_campaign.py")]
            self.assertFalse(m.process_argv_matches(command, "trainer", run_dir=self.root))
        for script in ("supervise_training.py", "evaluate_checkpoints.py", "run_campaign_with_audit.py"):
            self.assertFalse(m.process_argv_matches(self.argv(script=script), "trainer", run_dir=self.root))
        command = ["python", str(self.root/"unrelated.py"), str(m.HERE/"train_campaign.py"),
                   "--run-dir", str(self.root)]
        self.assertFalse(m.process_argv_matches(command, "trainer", run_dir=self.root))

    def test_wrong_script_and_run_directory_are_rejected(self):
        fake = self.root/"variant_entry.py"
        fake.write_text("unrelated file with the same basename")
        command = ["python", str(fake), "train", "--run-dir", str(self.root)]
        self.assertFalse(m.process_argv_matches(command, "trainer", run_dir=self.root))
        self.assertFalse(m.process_argv_matches(self.argv(run=self.root/"other"), "trainer", run_dir=self.root))
        self.assertFalse(m.process_argv_matches(self.argv()+["--run-dir", str(self.root)], "trainer", run_dir=self.root))

    def test_both_real_host_entries_require_serve_mode(self):
        for binary in (m.HERE/"Host/bin/Release/net8.0/TrainingHost.dll",
                       m.HERE/"experiments/HostV3/bin/Release/net8.0/TrainingHostV3.dll",
                       m.HERE/"experiments/HostV4/bin/Release/net8.0/TrainingHostV4.dll"):
            self.assertTrue(m.process_argv_matches(["dotnet", str(binary), "serve"], "host"))
            self.assertFalse(m.process_argv_matches(["dotnet", str(binary), "catalog"], "host"))
        self.assertFalse(m.process_argv_matches(["echo", "TrainingHost.dll", "serve"], "host"))

    def test_pid_start_wall_guard_remains_in_force(self):
        sampler = m.ResourceSampler(self.root)
        sampler.boot_wall, sampler.hz = 1000., 100
        pid = os.getpid()
        fields = ["0"]*20
        fields[0], fields[11], fields[12], fields[19] = "S", "100", "20", "400"
        original_text, original_bytes = Path.read_text, Path.read_bytes
        def text(path, *args, **kwargs):
            if str(path) == f"/proc/{pid}/stat":
                return f"{pid} (python) "+" ".join(fields)
            if str(path) == f"/proc/{pid}/statm":
                return "100 20"
            return original_text(path, *args, **kwargs)
        def data(path, *args, **kwargs):
            if str(path) == f"/proc/{pid}/cmdline":
                return b"\0".join(os.fsencode(value) for value in self.argv())+b"\0"
            return original_bytes(path, *args, **kwargs)
        with patch.object(Path, "read_text", text), patch.object(Path, "read_bytes", data):
            current = sampler.process(pid, "trainer", 1100., published_wall=1004., run_dir=self.root)
            reused = sampler.process(pid, "trainer", 1101., published_wall=1000., run_dir=self.root)
        self.assertTrue(current["alive"])
        self.assertEqual(current["start_wall"], 1004.)
        self.assertFalse(reused["alive"])
        self.assertFalse(reused["identity_matches"])


class MonitorTests(Fixture):
    def seed(self):
        now = time.time()
        run = self.root / "pilot128"
        self.write(run / "status.json", {"state": "running", "trainer_pid": 2147483647,
                                        "updated_wall": now, "generation": 1})
        self.write(run / "identity.json", {"configuration": {"width": 128}})
        (run / "latest.soicp").write_bytes(b'checkpoint metadata only; never deserialized by monitor')
        generation = {"wall": now, "event": "generation", "generation": 1, "games": 256,
                      "learning_rows": 123, "metrics": {"loss": .2}}
        (run / "metrics.jsonl").write_text(json.dumps(generation) + '\n')
        self.write(self.root / "budget.json", {"limit_seconds": 43200, "charged_seconds": 12,
                                              "active": None, "sessions": []})
        return m.Monitor(self.root), generation

    def test_progress_watch_is_exposed_without_reading_checkpoint(self):
        monitor,_=self.seed()
        progress={'schema':'shards-progress-watch-v1','proof':'inconclusive','comparisons':[]}
        self.write(self.root/'pilot128/progress-watch.json',progress)
        monitor.refresh()
        self.assertEqual(monitor.snapshot()['runs'][0]['progress_watch'],progress)

    def test_waiting_snapshot_is_valid_json(self):
        monitor = m.Monitor(self.root)
        monitor.refresh()
        result = monitor.snapshot()
        self.assertEqual(result["runs"], [])
        self.assertEqual(result["budget"]["remaining_seconds"], 43200)
        json.dumps(result, allow_nan=False)

    def test_resources_persist_and_process_failure_is_visible(self):
        monitor, generation = self.seed()
        with patch.object(m, "gpu_sample", return_value={"available": False, "error": "test fixture"}):
            monitor.sample_once()
        result = monitor.snapshot()
        self.assertEqual(result["runs"][0]["latest_generation"], generation)
        self.assertEqual(result["runs"][0]["state"], "stale_process")
        self.assertIsNotNone(result["runs"][0]["checkpoint_age_seconds"])
        self.assertEqual(len(result["resources"]), 1)
        self.assertTrue((self.root / "resource.jsonl").is_file())
        restarted = m.Monitor(self.root)
        restarted.refresh()
        self.assertEqual(len(restarted.snapshot()["resources"]), 1)

    def test_discovery_and_download_allowlist_exclude_symlinks_and_checkpoints(self):
        monitor, _ = self.seed()
        nested = self.root / "family" / "main"
        self.write(nested / "status.json", {"state": "session_complete"})
        (self.root / "linked").symlink_to(self.root / "pilot128", target_is_directory=True)
        (self.root / "pilot128" / "failure.json").symlink_to(self.root / "budget.json")
        monitor.refresh()
        self.assertEqual(set(monitor.runs), {"pilot128", "family/main"})
        for run, name in (("../pilot128", "status.json"), ("pilot128", "latest.soicp"),
                          ("pilot128", "../budget.json"), ("pilot128", "failure.json"),
                          ("", "identity.json")):
            self.assertIsNone(monitor.artifact(run, name))
        self.assertEqual(monitor.artifact("pilot128", "status.json").name, "status.json")

    def test_external_evaluations_and_post_training_audit_are_separate(self):
        monitor, _ = self.seed()
        self.write(self.root / "evaluations" / "pilot-comparison.json",
                   {"checkpoint_a": "pilot128/latest.soicp", "checkpoint_b": "pilot256/latest.soicp",
                    "result": {"complete": True, "games": 256, "score_a": .55}})
        self.write(self.root / "pilot128" / "post-evaluation.json", {"state": "pending"})
        (self.root / "evaluations" / "outside.json").symlink_to(self.root / "budget.json")
        monitor.refresh()
        result = monitor.snapshot()
        self.assertEqual(len(result["runs"]), 1)
        self.assertEqual(result["runs"][0]["post_evaluation"]["state"], "pending")
        self.assertEqual([row["name"] for row in result["external_evaluations"]], ["pilot-comparison.json"])
        self.assertEqual(monitor.evaluation_artifact("pilot-comparison.json").name, "pilot-comparison.json")
        self.assertIsNone(monitor.evaluation_artifact("../budget.json"))
        self.assertIsNone(monitor.evaluation_artifact("outside.json"))
        self.assertIsNone(monitor.evaluation_artifact("latest.soicp"))

    def test_revision2_counters_and_unknown_evaluation_outcomes_are_preserved(self):
        monitor, _ = self.seed()
        counts = {"attempted_games": 256, "completed_games": 255, "censored_games": 1,
                  "learning_rows": 100000, "censored_rows": 2000}
        self.write(self.root / "budget.json", {"limit_seconds": 43200, "charged_seconds": 313,
            "collection_accounting": {"schema": "shards-collection-accounting-v1", "started_wall": 1234,
                "collections": 1, "window_games": 4096, **counts, "recent_batches": [counts]}})
        unknown = {"complete": True, "all_terminal": False, "games": 256, "resolved_games": 255,
            "censored_games": 1, "score_a": None, "score_identification_interval": [.5, .50390625],
            "score_bound_95": [.38, .624], "draws": 0}
        self.write(self.root / "evaluations" / "unknown.json", unknown)
        with (self.root / "pilot128" / "metrics.jsonl").open("a") as stream:
            stream.write(json.dumps({"event": "champion_evaluation", "wall": time.time(), **unknown})+'\n')
        monitor.refresh()
        result = monitor.snapshot()
        accounting = result["budget"]["collection_accounting"]
        self.assertEqual(accounting["recent_censored"], 1)
        self.assertEqual(accounting["recent_attempted"], 256)
        self.assertEqual(accounting["censored_rows"], 2000)
        self.assertEqual(accounting["started_wall"], 1234)
        self.assertEqual(result["runs"][0]["state"], "running")
        self.assertIsNone(result["external_evaluations"][0]["data"]["score_a"])
        self.assertFalse(result["runs"][0]["latest_evaluation"]["all_terminal"])
        self.assertEqual(result["runs"][0]["latest_evaluation"]["draws"], 0)

    def test_trace_downloads_are_confined_to_known_censor_folders(self):
        monitor, _ = self.seed()
        run_trace = self.root / "pilot128" / "censored-episodes" / "censored-v1-lane0.json"
        eval_trace = self.root / "evaluations" / "match-censored" / "eval-1-seat0.json"
        self.write(run_trace, {"outcome": None, "actions": [1, 2]})
        self.write(self.root / "evaluations" / "match.json", {"complete": True, "all_terminal": False})
        self.write(eval_trace, {"outcome": None, "actions": [3, 4]})
        monitor.refresh()
        self.assertEqual(monitor.trace_artifact(run_trace.name, run_id="pilot128"), run_trace)
        self.assertEqual(monitor.trace_artifact(eval_trace.name, evaluation="match.json"), eval_trace)
        result = monitor.snapshot()
        self.assertEqual(result["runs"][0]["censor_traces"][0]["name"], run_trace.name)
        self.assertEqual(result["external_evaluations"][0]["censor_traces"][0]["name"], eval_trace.name)
        self.assertIsNone(monitor.trace_artifact("../budget.json", run_id="pilot128"))
        self.assertIsNone(monitor.trace_artifact(run_trace.name, run_id="../pilot128"))
        self.assertIsNone(monitor.trace_artifact(run_trace.name, run_id="pilot128", evaluation="match.json"))
        self.assertIsNone(monitor.trace_artifact(eval_trace.name, evaluation="../match.json"))
        link = run_trace.parent / "linked.json"
        link.symlink_to(self.root / "budget.json")
        self.assertIsNone(monitor.trace_artifact(link.name, run_id="pilot128"))
        moved = self.root / "moved-traces"
        run_trace.parent.rename(moved)
        run_trace.parent.symlink_to(moved, target_is_directory=True)
        self.assertIsNone(monitor.trace_artifact(run_trace.name, run_id="pilot128"))

    def test_loopback_http_json_html_raw_download_and_rejections(self):
        monitor, _ = self.seed()
        self.write(self.root / "pilot128" / "censored-episodes" / "trace.json", {"outcome": None})
        monitor.refresh()
        server = m.make_server(monitor, port=0)
        thread = threading.Thread(target=server.serve_forever, daemon=True)
        thread.start()
        try:
            self.assertEqual(server.server_address[0], "127.0.0.1")
            def request(path, headers=None):
                connection = http.client.HTTPConnection(*server.server_address, timeout=3)
                connection.request("GET", path, headers=headers or {})
                response = connection.getresponse()
                result = response.status, response.read(), dict(response.getheaders())
                connection.close()
                return result
            status, body, headers = request("/api/state?window=0&points=20")
            self.assertEqual(status, 200)
            self.assertEqual(json.loads(body)["runs"][0]["id"], "pilot128")
            self.assertEqual(headers["Cache-Control"], "no-store")
            status, body, _ = request("/")
            self.assertEqual(status, 200)
            self.assertIn(b'Shards training', body)
            self.assertNotIn(b'https://', body)
            status, body, headers = request("/api/artifact?run=pilot128&name=metrics.jsonl")
            self.assertEqual(status, 200)
            self.assertEqual(json.loads(body)["games"], 256)
            self.assertIn("attachment", headers["Content-Disposition"])
            self.assertEqual(request("/api/state?points=1")[0], 400)
            self.assertEqual(request("/api/state?window=-1")[0], 400)
            self.assertEqual(request("/api/artifact?run=..&name=budget.json")[0], 404)
            self.assertEqual(request("/api/artifact?run=pilot128&name=latest.soicp")[0], 404)
            status, body, _ = request("/api/trace?run=pilot128&name=trace.json")
            self.assertEqual(status, 200)
            self.assertIsNone(json.loads(body)["outcome"])
            self.assertEqual(request("/api/trace?run=pilot128&name=../budget.json")[0], 404)
            self.assertEqual(request("/api/state", {"Host": "evil.example"})[0], 403)
            self.assertEqual(request("/not-a-route")[0], 404)
        finally:
            server.shutdown()
            server.server_close()
            thread.join(3)


class BalancePublicationTests(Fixture):
    def test_large_game_records_stay_in_download_and_cache_skips_reparse(self):
        report = self.root/"evaluations/panel.json"
        raw = {"complete": True, "games": 32, "balance_observations": {
            "schema": "shards-balance-observations-v1", "totals": {"resolved_games": 32},
            "games": [{"private_to_download": "x"*200}]*25000}}
        self.write(report, raw)
        original = report.read_bytes()
        monitor = m.Monitor(self.root)
        with patch.object(m, "read_json", wraps=m.read_json) as read:
            monitor.refresh()
            monitor.refresh()
            report_reads = [call for call in read.call_args_list if call.args[1] == "panel.json"]
        self.assertEqual(len(report_reads), 1)
        shown = monitor.snapshot()["external_evaluations"][0]["data"]
        self.assertNotIn("games", shown["balance_observations"])
        self.assertEqual(shown["balance_observations"]["totals"]["resolved_games"], 32)
        self.assertEqual(monitor.evaluation_artifact("panel.json").read_bytes(), original)
        self.write(report, {"complete": True, "games": 64})
        monitor.refresh()
        self.assertEqual(monitor.snapshot()["external_evaluations"][0]["data"]["games"], 64)

    def test_sources_remain_separate_and_live_games_update_cadence(self):
        run = self.root/"main-v3"
        self.write(run/"status.json", {"state": "session_complete", "games": 1200})
        frozen = {"schema": "shards-balance-statistics-v1", "state": "ready",
            "scope": {"run_id": "main-v3"}, "refresh": {"snapshot_training_games": 1000},
            "totals": {"resolved_games": 32}}
        self.write(self.root/"balance-statistics-frozen.json", frozen)
        monitor = m.Monitor(self.root)
        monitor.refresh()
        snapshot = monitor.snapshot()
        self.assertEqual(snapshot["balance_statistics"]["refresh"]["games_since_snapshot"], 200)
        self.assertIsNone(snapshot["balance_statistics_sources"]["training_pool"])
        pool = {"schema": "shards-balance-statistics-v1", "state": "ready",
                "scope": {"run_id": "main-v3"}, "totals": {"resolved_games": 10000}}
        self.write(self.root/"balance-statistics.json", pool)
        with patch.object(monitor.pool_statistics, "refresh", return_value=pool):
            monitor.refresh()
        snapshot = monitor.snapshot()
        self.assertEqual(snapshot["balance_statistics"]["totals"]["resolved_games"], 10000)
        self.assertEqual(snapshot["balance_statistics_sources"]["frozen"]["totals"]["resolved_games"], 32)
        self.assertEqual(json.loads((self.root/"balance-statistics-frozen.json").read_text()), frozen)
        monitor.refresh()
        self.assertIsNone(monitor.snapshot()["balance_statistics_sources"]["training_pool"])


if __name__ == "__main__":
    unittest.main()
