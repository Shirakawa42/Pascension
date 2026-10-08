"""Bounded launcher lifecycle probes; no GPU, outcome training or checkpoint load."""
import contextlib
from dataclasses import asdict
import io
import hashlib
import json
import os
from pathlib import Path
import signal
import subprocess
import sys
import tempfile
import time
import unittest
from unittest import mock

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import launch
from train import TrainConfig


class LaunchLifecycleTests(unittest.TestCase):
    def league_fixture(self, directory):
        baseline = directory / 'baseline'
        baseline.mkdir()
        manifest = baseline / 'manifest.json'
        manifest.write_text('{"immutable": true}')
        plan = {'schema': 'shards-zero-depth-league-plan-v1', 'baseline': str(baseline),
                'baseline_manifest_sha256': hashlib.sha256(manifest.read_bytes()).hexdigest(),
                'every_games': 100000, 'games': 400, 'workers': 2, 'max_seconds': 120,
                'seed_base': 0xA000000000000000}
        (directory / 'league-plan.json').write_text(json.dumps(plan))
        return plan

    def test_league_is_optional_and_does_not_spawn_for_legacy_campaigns(self):
        with tempfile.TemporaryDirectory() as folder, mock.patch.object(launch.subprocess, 'Popen') as spawn:
            service = launch.LeagueService(Path(folder))
            service.start()
            service.close()
            spawn.assert_not_called()

    def test_league_spawn_is_bounded_paired_and_owned_cleanup_before_final_evaluation(self):
        with tempfile.TemporaryDirectory() as folder:
            directory = Path(folder)
            plan = self.league_fixture(directory)
            plan['max_seconds'] = 600
            (directory / 'league-plan.json').write_text(json.dumps(plan))
            process = mock.Mock(pid=23456, returncode=0)
            process.poll.return_value = None
            with mock.patch.object(launch.subprocess, 'Popen', return_value=process) as spawn:
                service = launch.LeagueService(directory)
                service.start()
                command = spawn.call_args.args[0]
                self.assertEqual(command[command.index('--max-seconds') + 1], '600')
                self.assertEqual(command[command.index('--parent-pid') + 1], str(os.getpid()))
                self.assertEqual(command[command.index('--games') + 1], '400')
                self.assertEqual(command[command.index('--workers') + 1], '2')
                self.assertTrue(spawn.call_args.kwargs['start_new_session'])
                service.close()
            process.terminate.assert_called_once()
            process.wait.assert_called_once_with(timeout=5)
            self.assertEqual(json.loads((directory / 'league-service.json').read_text())['state'], 'stopped')

    def test_league_rejects_changed_baseline_unbounded_budgets_and_incomplete_hero_cycles(self):
        for field, value in (('max_seconds', 601), ('max_seconds', float('nan')), ('games', 410),
                             ('workers', 8), ('every_games', 1), ('seed_base', 2**63),
                             ('baseline_manifest_sha256', 'changed')):
            with self.subTest(field=field), tempfile.TemporaryDirectory() as folder:
                directory = Path(folder)
                plan = self.league_fixture(directory)
                plan[field] = value
                (directory / 'league-plan.json').write_text(json.dumps(plan))
                with mock.patch.object(launch.subprocess, 'Popen') as spawn:
                    with self.assertRaises(ValueError):
                        launch.LeagueService(directory).start()
                    spawn.assert_not_called()

    def test_private_league_descendant_cleanup_never_signals_unrelated_process(self):
        unrelated = subprocess.Popen([sys.executable, '-c', 'import time;time.sleep(30)'], start_new_session=True)
        process = None
        try:
            with tempfile.TemporaryDirectory() as folder:
                pid_file = Path(folder) / 'host.json'
                script = """import json,os,signal,subprocess,sys,time
signal.signal(signal.SIGTERM,signal.SIG_IGN)
child=subprocess.Popen([sys.executable,'-c','import signal,time;signal.signal(signal.SIGTERM,signal.SIG_IGN);time.sleep(30)'])
open(sys.argv[1],'w').write(json.dumps({'pid':child.pid}))
time.sleep(30)
"""
                process = subprocess.Popen([sys.executable, '-c', script, str(pid_file)], start_new_session=True)
                until = time.monotonic() + 3
                while not pid_file.exists() and time.monotonic() < until:
                    time.sleep(.01)
                self.assertTrue(pid_file.exists())
                child_pid = json.loads(pid_file.read_text())['pid']
                owner = launch._service_identity(process.pid)
                self.assertIsNotNone(owner)
                launch._join_service_descendants(owner)
                self.assertEqual(process.wait(timeout=3), -signal.SIGKILL)
                self.assertIsNone(launch._service_identity(child_pid))
                self.assertIsNone(unrelated.poll())
        finally:
            if process is not None and process.poll() is None:
                process.kill(); process.wait(timeout=3)
            unrelated.terminate(); unrelated.wait(timeout=3)

    def test_private_league_cleanup_rejects_foreign_session(self):
        identity = launch._service_identity(os.getpid())
        self.assertIsNotNone(identity)
        with self.assertRaises(RuntimeError):
            launch._join_service_descendants(identity)

    def test_recycled_numeric_league_session_without_exact_continuity_is_never_signaled(self):
        boot = Path('/proc/sys/kernel/random/boot_id').read_text().strip()
        owner = (10002, 101, 10002, 10002, b'original-watcher', boot)
        foreign = (10002, 900, 10002, 10002, b'unrelated-recycled-session', boot)
        with mock.patch.object(launch, '_service_identity', return_value=foreign), \
                mock.patch.object(launch, '_unreaped_service_owner', return_value=False), \
                mock.patch.object(launch.os, 'pidfd_open') as bind:
            launch._join_service_descendants(owner)
            bind.assert_not_called()

    def test_unreaped_owner_proves_descendants_before_wait_then_saved_identity_survives(self):
        with tempfile.TemporaryDirectory() as folder:
            ready = Path(folder) / 'ready.json'
            script = """import json,subprocess,sys,time
child=subprocess.Popen([sys.executable,'-c','import time;time.sleep(30)'])
open(sys.argv[1],'w').write(json.dumps({'pid':child.pid}))
time.sleep(.5)
"""
            process = subprocess.Popen([sys.executable, '-c', script, str(ready)], start_new_session=True)
            try:
                until = time.monotonic() + 3
                while not ready.exists() and time.monotonic() < until:
                    time.sleep(.01)
                owner = launch._service_identity(process.pid)
                self.assertIsNotNone(owner)
                child_pid = json.loads(ready.read_text())['pid']
                # Sleep without wait/poll, preserving this exact zombie owner.
                time.sleep(.6)
                known = launch._service_members(owner)
                self.assertTrue(any(x[0] == child_pid for x in known))
                self.assertEqual(process.wait(timeout=3), 0)
                launch._join_service_descendants(owner, known)
                self.assertIsNone(launch._service_identity(child_pid))
            finally:
                if process.poll() is None:
                    process.kill(); process.wait(timeout=3)

    def fixture(self, folder):
        directory = Path(folder)
        config = TrainConfig(width=512)
        for name, value in (("config.json", asdict(config)), ("identity.json", {"fixture": True}),
                            ("status.json", {"optimizer_steps": 1}), ("supervisor.json", {"stop_reason": None}),
                            ("prepared.json", {"post_training_pairs": 2048})):
            (directory / name).write_text(json.dumps(value))
        (directory / "latest.soicp").write_bytes(b"fabricated checkpoint identifier; never loaded")
        return directory

    def invoke(self, directory, *, supervise=None, evaluation=None, seconds="43200", resume=False):
        argv = ["launch.py", "--campaign", str(directory), "--seconds", seconds]
        if resume:
            argv.append("--resume")
        with mock.patch.object(sys, "argv", argv), \
                mock.patch.object(launch, "catalog", return_value={"fixture": True}), \
                mock.patch.object(launch, "identity", return_value={"fixture": True}), \
                mock.patch.object(launch, "verify_bundle") as bundle, \
                mock.patch.object(launch, "supervise", side_effect=supervise, return_value=0) as supervisor, \
                mock.patch.object(launch, "run_evaluation", side_effect=evaluation,
                                  return_value={"summary": {"stronger_than_current": False}}) as evaluate, \
                contextlib.redirect_stdout(io.StringIO()):
            launch.main()
        return bundle, supervisor, evaluate

    def test_training_signal_handlers_are_restored_before_automatic_evaluation(self):
        previous = {number: signal.getsignal(number) for number in (signal.SIGTERM, signal.SIGINT)}
        def supervisor(*args):
            for number in previous:
                signal.signal(number, lambda *_: None)
            return 0
        def evaluation(*args, **kwargs):
            for number, expected in previous.items():
                self.assertEqual(signal.getsignal(number), expected)
            return {"summary": {"stronger_than_current": False}}
        try:
            with tempfile.TemporaryDirectory() as folder:
                self.invoke(self.fixture(folder), supervise=supervisor, evaluation=evaluation)
        finally:
            for number, handler in previous.items():
                signal.signal(number, handler)

    def test_supervision_exception_also_restores_prior_signal_handlers(self):
        previous = {number: signal.getsignal(number) for number in (signal.SIGTERM, signal.SIGINT)}
        def supervisor(*args):
            for number in previous:
                signal.signal(number, lambda *_: None)
            raise RuntimeError("fabricated supervisor failure")
        try:
            with tempfile.TemporaryDirectory() as folder:
                with self.assertRaisesRegex(RuntimeError, "fabricated supervisor failure"):
                    self.invoke(self.fixture(folder), supervise=supervisor)
                for number, expected in previous.items():
                    self.assertEqual(signal.getsignal(number), expected)
        finally:
            for number, handler in previous.items():
                signal.signal(number, handler)

    def test_width512_resume_keeps_12_hour_limit_and_4096_game_frozen_evaluation(self):
        with tempfile.TemporaryDirectory() as folder:
            directory = self.fixture(folder)
            bundle, supervisor, evaluate = self.invoke(directory, resume=True)
            command = supervisor.call_args.args[0]
            self.assertEqual(command[command.index("--seconds") + 1], "43200.0")
            self.assertEqual(command[command.index("--resume") + 1], str(directory / "latest.soicp"))
            self.assertEqual(command[command.index("--config") + 1], str(directory / "config.json"))
            self.assertEqual(json.loads((directory / "config.json").read_text())["width"], 512)
            self.assertEqual(evaluate.call_args.kwargs["pairs"], 2048)
            self.assertEqual(evaluate.call_args.kwargs["bundle_directory"], directory / "incumbent")
            self.assertTrue(bundle.called)

    def test_no_launch_can_grant_more_than_twelve_hours_or_nonfinite_time(self):
        for value in ("43201", "nan", "inf", "0"):
            with self.subTest(seconds=value), tempfile.TemporaryDirectory() as folder:
                with contextlib.redirect_stderr(io.StringIO()), self.assertRaises(SystemExit) as result:
                    self.invoke(self.fixture(folder), seconds=value)
                self.assertEqual(result.exception.code, 2)

    def test_final_benchmark_respects_six_worker_resource_limit(self):
        with tempfile.TemporaryDirectory() as folder:
            directory = self.fixture(folder)
            config = json.loads((directory / 'config.json').read_text())
            config['workers'] = 6
            (directory / 'config.json').write_text(json.dumps(config))
            _, _, evaluate = self.invoke(directory, resume=True)
            self.assertEqual(evaluate.call_args.kwargs['workers'], 6)

    def test_supervisor_deadline_cleans_only_its_owned_process_group(self):
        previous = {number: signal.getsignal(number) for number in (signal.SIGTERM, signal.SIGINT)}
        unrelated = subprocess.Popen([sys.executable, "-c", "import time; time.sleep(30)"], start_new_session=True)
        try:
            with tempfile.TemporaryDirectory() as folder:
                directory = Path(folder)
                ledger, child_pid_file = directory / "budget.json", directory / "owned-child.json"
                script = """import json,os,signal,subprocess,sys,time
signal.signal(signal.SIGTERM,signal.SIG_IGN)
child=subprocess.Popen([sys.executable,'-c','import time; time.sleep(30)'])
open(sys.argv[2],'w').write(json.dumps({'pid':child.pid,'parent_pgid':os.getpgrp()}))
now=time.monotonic()
open(sys.argv[1],'w').write(json.dumps({'active':{'pid':os.getpid(),'hard_deadline_monotonic':now+1.5,'last_heartbeat_monotonic':now}}))
time.sleep(30)
"""
                code = launch.supervise([sys.executable, "-c", script, str(ledger), str(child_pid_file)], directory, ledger)
                self.assertEqual(code, -signal.SIGKILL)
                self.assertIsNone(unrelated.poll())
                owned = json.loads(child_pid_file.read_text())
                status = json.loads((directory / "supervisor.json").read_text())
                self.assertEqual(owned["parent_pgid"], status["trainer_pid"])
                self.assertEqual(status["stop_reason"], "hard_deadline")
                self.assertFalse(status["automatic_retry"])
                # An orphan may briefly remain as a zombie before init reaps it.
                child_stat = Path(f"/proc/{owned['pid']}/stat")
                if child_stat.exists():
                    self.assertEqual(child_stat.read_text().split(")", 1)[1].split()[0], "Z")
        finally:
            for number, handler in previous.items():
                signal.signal(number, handler)
            unrelated.terminate()
            unrelated.wait(timeout=5)


if __name__ == "__main__":
    unittest.main()
