"""Filesystem/supervision tests only; never launches Torch, a host or training."""
import argparse
import copy
import json
import os
from pathlib import Path
import tempfile
import sys
import unittest
from unittest.mock import patch

import shadow_watch as watch


class FakeProcess:
    pid = 987654
    def __init__(self, returncode=None):
        self.returncode = returncode
    def poll(self):
        return self.returncode
    def wait(self, timeout=None):
        return self.returncode


class WatchTests(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory()
        self.addCleanup(self.directory.cleanup)
        self.root = Path(self.directory.name)
        self.run_dir = self.root/"main-v3"
        self.run_dir.mkdir()
        self.ledger = self.root/"budget.json"
        watch.atomic_json(self.ledger, {"charged_seconds": 123., "limit_seconds": 43200.})
        watch.atomic_json(self.run_dir/"status.json", {"state": "running", "trainer_pid": os.getpid()})
        (self.run_dir/"latest.soicp").write_bytes(b"fixture-current")
        watch.atomic_json(self.run_dir/"identity.json", {"schema": "fixture-v3"})
        self.anchor = self.root/"anchor"/"migrated1977.soicp"
        self.anchor.parent.mkdir()
        self.anchor.write_bytes(b"fixture-fixed-anchor")
        watch.atomic_json(self.anchor.parent/"identity.json", {"schema": "fixture-v3"})
        self.output_dir = self.root/"evaluations"
        self.output_dir.mkdir()
        self.args = argparse.Namespace(run_dir=self.run_dir, ledger=self.ledger, anchor=self.anchor,
            anchor_role="learner", variant="v3", output_dir=self.output_dir, games=4, batch=2,
            interval=1200., initial_delay=0., child_timeout=300., max_runtime=43200.,
            step_delay_ms=3.,
            balance_telemetry=False, every_training_games=0, game_min_interval=60.,
            sampling_seed=70926, seed=watch.DEFAULT_SEED, once=True)

    def test_terminal_checks_are_read_only_and_distinguish_live_pid(self):
        before = self.ledger.read_bytes()
        self.assertIsNone(watch.terminal_reason(self.run_dir, self.ledger))
        with patch.object(watch, "process_alive", return_value=False):
            self.assertEqual(watch.terminal_reason(self.run_dir, self.ledger), "trainer_pid_not_alive")
        watch.atomic_json(self.run_dir/"status.json", {"state": "failed"})
        self.assertEqual(watch.terminal_reason(self.run_dir, self.ledger), "trainer_failed")
        self.assertEqual(before, self.ledger.read_bytes())
        watch.atomic_json(self.run_dir/"status.json", {"state": "starting"})
        watch.atomic_json(self.ledger, {"charged_seconds": 43200., "limit_seconds": 43200.})
        self.assertEqual(watch.terminal_reason(self.run_dir, self.ledger), "training_budget_exhausted")

    def test_reservation_survives_reopen_and_never_reuses_failed_seeds(self):
        path = self.root/"watch.json"
        state = {"next_seed": 100, "jobs_reserved": 0}
        first = watch.reserve_job(state, path, games=256, kind="champion", output=self.output_dir/"first.json")
        reopened = watch.read_json(path)
        self.assertEqual(reopened["active_job"]["seed"], 100)
        second = watch.reserve_job(reopened, path, games=256, kind="anchor", output=self.output_dir/"second.json")
        self.assertEqual((first["sequence"], second["sequence"], second["seed"]), (0, 1, 228))
        self.assertEqual(watch.read_json(path)["next_seed"], 356)

    def test_snapshot_pins_open_inode_and_refuses_overwrite(self):
        original_copy = watch.shutil.copyfileobj
        def replacement(source, target, *args, **kwargs):
            if str(source.name).endswith("latest.soicp"):
                replacement_path = self.run_dir/"replacement.soicp"
                replacement_path.write_bytes(b"newer-current")
                replacement_path.replace(self.run_dir/"latest.soicp")
            return original_copy(source, target, *args, **kwargs)
        with patch.object(watch.shutil, "copyfileobj", side_effect=replacement):
            saved = watch.snapshot_checkpoint(self.run_dir, self.root/"snapshot")
        self.assertEqual(saved.read_bytes(), b"fixture-current")
        self.assertEqual((self.run_dir/"latest.soicp").read_bytes(), b"newer-current")
        with self.assertRaises(FileExistsError):
            watch.snapshot_checkpoint(self.run_dir, self.root/"snapshot")

    def test_timeout_kills_only_owned_child_group_and_marks_no_retry(self):
        output = self.output_dir/"timeout.json"
        job = {"sequence": 0, "kind": "anchor", "seed": 100, "output": str(output)}
        process = FakeProcess()
        def terminate(child):
            self.assertIs(child, process)
            child.returncode = -15
        with patch.object(watch.subprocess, "Popen", return_value=process) as spawn, \
             patch.object(watch, "terminate_group", side_effect=terminate), \
             patch.object(watch.time, "monotonic", side_effect=[0., 301., 302.]):
            result = watch.run_job(self.args, job, self.run_dir/"latest.soicp")
        self.assertEqual(result["reason"], "shadow_child_timeout")
        self.assertEqual(result["state"], "failed")
        self.assertFalse(watch.read_json(output)["watch_failure"]["automatic_retry"])
        self.assertEqual(spawn.call_args.kwargs["env"]["CUDA_VISIBLE_DEVICES"], "")
        self.assertEqual(spawn.call_args.kwargs["env"]["OMP_NUM_THREADS"], "1")
        self.assertTrue(spawn.call_args.kwargs["start_new_session"])
        self.assertIn("--kill-after=5s", spawn.call_args.args[0])
        self.assertIn("295s", spawn.call_args.args[0])

    def test_missing_independent_timeout_fails_before_child_launch(self):
        with patch.object(watch.shutil, "which", return_value=None):
            with self.assertRaisesRegex(RuntimeError, "System timeout is required"):
                watch.bounded_command(self.args, {}, self.run_dir/"latest.soicp")

    def test_stopped_trainer_does_not_launch_a_child(self):
        watch.atomic_json(self.run_dir/"status.json", {"state": "session_complete"})
        with patch.object(watch.subprocess, "Popen") as spawn:
            result = watch.run_job(self.args, {"output": str(self.output_dir/"unused.json")},
                                   self.run_dir/"latest.soicp")
        spawn.assert_not_called()
        self.assertEqual(result["state"], "cancelled")

    def test_cleanup_kills_remaining_owned_group_even_after_parent_exit(self):
        process = FakeProcess(returncode=0)
        with patch.object(watch.os, "killpg") as kill:
            watch.terminate_group(process)
        self.assertEqual(kill.call_args_list[-1].args, (process.pid, watch.signal.SIGKILL))

    def test_one_cycle_uses_same_snapshot_distinct_seeds_and_fixed_anchor(self):
        jobs = []
        def completed(args, job, snapshot, **kwargs):
            jobs.append((copy.copy(job), snapshot, args.anchor))
            return {**job, "state": "completed", "seconds": 1.}
        before = self.ledger.read_bytes()
        with patch.object(watch, "run_job", side_effect=completed), \
             patch.object(watch.signal, "signal"), patch.object(watch.os, "getpriority", return_value=10):
            self.assertEqual(watch.run(copy.copy(self.args)), 0)
        state = watch.read_json(self.run_dir/"shadow-watch.json")
        self.assertEqual([job[0]["kind"] for job in jobs], ["champion", "anchor"])
        self.assertEqual([job[0]["seed"] for job in jobs], [watch.DEFAULT_SEED, watch.DEFAULT_SEED+2])
        self.assertEqual(jobs[0][1], jobs[1][1])
        self.assertEqual(jobs[0][2].read_bytes(), b"fixture-fixed-anchor")
        self.assertNotEqual(jobs[0][2], self.anchor)
        self.assertEqual(state["state"], "stopped")
        self.assertEqual(before, self.ledger.read_bytes())

    def test_failed_job_stops_without_second_job_or_automatic_retry(self):
        def failed(args, job, snapshot, **kwargs):
            return {**job, "state": "failed", "reason": "fixture_failure"}
        with patch.object(watch, "run_job", side_effect=failed) as runner, \
             patch.object(watch.signal, "signal"), patch.object(watch.os, "getpriority", return_value=10):
            self.assertEqual(watch.run(copy.copy(self.args)), 1)
            self.assertEqual(runner.call_count, 1)
            with self.assertRaisesRegex(RuntimeError, "failed or was interrupted"):
                watch.run(copy.copy(self.args))

    def test_game_cadence_first_delay_then_counter_and_wall_floor(self):
        args = copy.copy(self.args)
        args.every_training_games = 100000
        state = {"cycles": 0, "next_eligible_wall": 500.}
        status = {"state": "running", "games": 1200}
        self.assertFalse(watch.cadence(args, state, status, 499.)[0])
        self.assertTrue(watch.cadence(args, state, status, 500.)[0])
        watch.record_snapshot_cadence(args, state, status, 500.)
        state["cycles"] = 1
        watch.complete_cycle_cadence(args, state, 530.)
        self.assertEqual(state["next_training_games_target"], 101200)
        self.assertFalse(watch.cadence(args, state, {**status, "games": 101199}, 999.)[0])
        self.assertFalse(watch.cadence(args, state, {**status, "games": 101200}, 589.)[0])
        self.assertTrue(watch.cadence(args, state, {**status, "games": 101200}, 590.)[0])
        # A recovered older checkpoint must not reset/advance the target.
        due, progress = watch.cadence(args, state, {**status, "games": 1000}, 999.)
        self.assertFalse(due)
        self.assertEqual(progress["games_since_snapshot"], 0)
        self.assertEqual(progress["next_training_games_target"], 101200)
        self.assertFalse(watch.cadence(args, state, {"state": "running"}, 999.)[0])

    def test_legacy_time_only_ignores_training_counter(self):
        state = {"cycles": 4, "next_eligible_wall": 800.}
        status = {"state": "running"}
        self.assertTrue(watch.cadence(self.args, state, status, 800.)[0])
        watch.complete_cycle_cadence(self.args, state, 900.)
        self.assertEqual(state["next_eligible_wall"], 2000.)
        self.assertFalse(watch.cadence(self.args, state, status, 1999.)[0])

    def test_game_target_persists_across_resume_without_eager_new_cycle(self):
        args = copy.copy(self.args)
        args.every_training_games = 100000
        watch.atomic_json(self.run_dir/"status.json", {"state": "running", "trainer_pid": os.getpid(), "games": 456000})
        def completed(args, job, snapshot, **kwargs):
            return {**job, "state": "completed", "seconds": 1.}
        with patch.object(watch, "run_job", side_effect=completed), \
             patch.object(watch.signal, "signal"), patch.object(watch.os, "getpriority", return_value=10):
            self.assertEqual(watch.run(copy.copy(args)), 0)
        first = watch.read_json(self.run_dir/"shadow-watch.json")
        self.assertEqual(first["snapshot_training_games"], 456000)
        self.assertEqual(first["next_training_games_target"], 556000)
        watch.atomic_json(self.run_dir/"status.json", {"state": "running", "trainer_pid": os.getpid(), "games": 500000})
        with patch.object(watch, "run_job") as runner, patch.object(watch.time, "sleep"), \
             patch.object(watch, "terminal_reason", side_effect=[None, "fixture_stop"]), \
             patch.object(watch.signal, "signal"), patch.object(watch.os, "getpriority", return_value=10):
            self.assertEqual(watch.run(copy.copy(args)), 0)
        runner.assert_not_called()
        second = watch.read_json(self.run_dir/"shadow-watch.json")
        for key in ("snapshot_training_games", "next_training_games_target", "next_eligible_wall", "initial_due_wall"):
            self.assertEqual(second[key], first[key])
        self.assertEqual(second["cadence"]["games_since_snapshot"], 44000)

    def test_balance_cli_forwarding_and_bounded_separate_statistics_command(self):
        self.args.balance_telemetry = True
        job = {"kind": "champion", "seed": 100, "sequence": 0, "output": "fixture.json"}
        command = watch.job_command(self.args, job, Path("snapshot"))
        self.assertIn("--balance-telemetry", command)
        aggregate = watch.statistics_command(self.args, {"snapshot_training_games": 98765})
        self.assertIn("55s", aggregate)
        self.assertIn("--kill-after=5s", aggregate)
        self.assertEqual(aggregate[aggregate.index("--snapshot-training-games")+1], "98765")
        self.assertEqual(aggregate[aggregate.index("--every-games")+1], "0")
        self.assertEqual(aggregate[aggregate.index("--output")+1], str(self.root/"balance-statistics-frozen.json"))

    def test_statistics_failure_is_separate_and_not_retried_after_resume(self):
        self.args.balance_telemetry = True
        def completed(args, job, snapshot, **kwargs):
            return {**job, "state": "completed", "seconds": 1.}
        before = self.ledger.read_bytes()
        with patch.object(watch, "run_job", side_effect=completed) as runner, \
             patch.object(watch, "run_statistics", return_value={"state": "failed", "reason": "fixture_stats_failure", "automatic_retry": False}) as stats, \
             patch.object(watch.signal, "signal"), patch.object(watch.os, "getpriority", return_value=10):
            self.assertEqual(watch.run(copy.copy(self.args)), 0)
            self.assertEqual(stats.call_count, 1)
            self.assertEqual(runner.call_count, 2)
            state = watch.read_json(self.run_dir/"shadow-watch.json")
            self.assertTrue(state["statistics_disabled"])
            self.assertEqual(state["recent_statistics"][0]["reason"], "fixture_stats_failure")
            # Explicit operator resume permits future evaluations, not a stats retry.
            state["next_eligible_wall"] = 0
            watch.atomic_json(self.run_dir/"shadow-watch.json", state)
            self.assertEqual(watch.run(copy.copy(self.args)), 0)
            self.assertEqual(stats.call_count, 1)
            self.assertEqual(runner.call_count, 4)
        self.assertEqual(before, self.ledger.read_bytes())

    def test_statistics_hard_timeout_cleans_own_group_and_reports_failure(self):
        process = FakeProcess()
        def terminate(child):
            child.returncode = -15
        with patch.object(watch.subprocess, "Popen", return_value=process) as spawn, \
             patch.object(watch, "terminate_group", side_effect=terminate) as cleanup, \
             patch.object(watch.time, "monotonic", side_effect=[0., 61., 62.]):
            result = watch.run_statistics(self.args, {"cycles": 1, "snapshot_training_games": 10})
        self.assertEqual(result["reason"], "statistics_timeout")
        self.assertFalse(result["automatic_retry"])
        cleanup.assert_called_once_with(process)
        self.assertTrue(spawn.call_args.kwargs["start_new_session"])
        self.assertEqual(spawn.call_args.kwargs["env"]["CUDA_VISIBLE_DEVICES"], "")

    def test_statistics_success_requires_new_matching_snapshot_sidecar(self):
        output = self.root/"balance-statistics-frozen.json"
        report = {"schema": "shards-balance-statistics-v1", "refresh": {"snapshot_training_games": 25}}
        watch.atomic_json(output, report)
        process = FakeProcess(returncode=0)
        with patch.object(watch.subprocess, "Popen", return_value=process), patch.object(watch, "terminate_group"):
            stale = watch.run_statistics(self.args, {"cycles": 1, "snapshot_training_games": 25})
        self.assertEqual(stale["state"], "failed")
        self.assertIn("did not refresh", stale["error"]["message"])
        def refresh(*args, **kwargs):
            watch.atomic_json(output, report)
            return process
        with patch.object(watch.subprocess, "Popen", side_effect=refresh), patch.object(watch, "terminate_group"):
            good = watch.run_statistics(self.args, {"cycles": 1, "snapshot_training_games": 25})
            wrong = watch.run_statistics(self.args, {"cycles": 1, "snapshot_training_games": 26})
        self.assertEqual(good["state"], "completed")
        self.assertEqual(wrong["state"], "failed")
        self.assertIn("different snapshot", wrong["error"]["message"])

    def test_cli_defaults_and_time_only_opt_out(self):
        base = ["shadow_watch.py", "--run-dir", str(self.run_dir), "--ledger", str(self.ledger),
                "--anchor", str(self.anchor), "--output-dir", str(self.output_dir)]
        for additions, count in (([], 100000), (["--every-training-games", "0", "--balance-telemetry"], 0),
                                 (["--variant", "v4"], 100000), (["--variant", "v5"], 100000)):
            with patch.object(sys, "argv", base+additions), patch.object(watch, "run", return_value=0) as run:
                with self.assertRaises(SystemExit) as ended:
                    watch.main()
                self.assertEqual(ended.exception.code, 0)
                args = run.call_args.args[0]
                self.assertEqual(args.every_training_games, count)
                self.assertEqual(args.game_min_interval, 60.)
                if "--variant" in additions:
                    self.assertEqual(args.variant, additions[1])
                    job = {"kind": "champion", "seed": 1, "sequence": 0, "output": "fixture.json"}
                    command = watch.job_command(args, job, Path("snapshot"))
                    self.assertEqual(command[command.index("--variant")+1], additions[1])


if __name__ == "__main__":
    unittest.main()
