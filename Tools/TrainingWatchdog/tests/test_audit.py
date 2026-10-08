"""Independent watchdog corner cases; every process and signal is simulated.

The filesystem fixtures are disposable. These tests never create a learner,
load Torch/CUDA, or send a signal to an operating-system process.
"""
from __future__ import annotations

import contextlib
from dataclasses import asdict, replace
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import signal
import sys
import tempfile
import unittest
from unittest import mock


SOURCE = Path(__file__).resolve().parents[1] / "runner.py"
SPEC = importlib.util.spec_from_file_location("shards_watchdog_independent_audit", SOURCE)
w = importlib.util.module_from_spec(SPEC)
sys.modules[SPEC.name] = w
SPEC.loader.exec_module(w)


class Clock:
    def __init__(self, wall=1000., mono=1000., boot="audit-boot-a"):
        self.wall, self.mono, self.boot = wall, mono, boot

    def advance(self, seconds):
        self.wall += seconds
        self.mono += seconds


class FakeSystem:
    """An OS boundary that pins identity records rather than numeric PIDs."""
    def __init__(self, clock, processes=None, on_signal=None, on_sleep=None):
        self.clock = clock
        self.processes = dict(processes or {})
        self.on_signal, self.on_sleep = on_signal, on_sleep
        self.signals, self.closed, self.pinned = [], [], {}
        self.next_fd = 10_000_000

    def inspect(self, pid):
        return self.processes.get(pid)

    def pidfd(self, pid):
        if pid not in self.processes:
            raise ProcessLookupError(pid)
        self.next_fd += 1
        self.pinned[self.next_fd] = self.processes[pid]
        return self.next_fd

    def send(self, fd, number, *_args):
        owner = self.pinned[fd]
        # A pidfd remains attached to its original process after PID reuse.
        if self.processes.get(owner.pid) != owner:
            raise ProcessLookupError(owner.pid)
        self.signals.append((owner.pid, number))
        if self.on_signal:
            self.on_signal(owner, number)

    def sleep(self, seconds):
        self.clock.advance(seconds)
        if self.on_sleep:
            self.on_sleep()

    @contextlib.contextmanager
    def install(self):
        actual_close = os.close

        def close(fd):
            if fd in self.pinned:
                self.closed.append(fd)
                del self.pinned[fd]
            else:
                actual_close(fd)

        with contextlib.ExitStack() as stack:
            stack.enter_context(mock.patch.object(w, "boot", side_effect=lambda: self.clock.boot))
            stack.enter_context(mock.patch.object(w, "inspect", side_effect=self.inspect))
            stack.enter_context(mock.patch.object(w, "session_members", side_effect=lambda owner:
                [p for p in self.processes.values()
                 if p.boot_id == owner.boot_id and p.pgid == owner.pgid and p.session == owner.session]))
            stack.enter_context(mock.patch.object(w.time, "time", side_effect=lambda: self.clock.wall))
            stack.enter_context(mock.patch.object(w.time, "monotonic", side_effect=lambda: self.clock.mono))
            stack.enter_context(mock.patch.object(w.time, "sleep", side_effect=self.sleep))
            stack.enter_context(mock.patch.object(w.os, "pidfd_open", side_effect=self.pidfd, create=True))
            stack.enter_context(mock.patch.object(w.signal, "pidfd_send_signal", side_effect=self.send, create=True))
            stack.enter_context(mock.patch.object(w.os, "close", side_effect=close))
            # Fail loudly if any implementation ever falls back to numeric PID signaling.
            stack.enter_context(mock.patch.object(w.os, "kill", side_effect=AssertionError("numeric PID signal")))
            stack.enter_context(mock.patch.object(w.os, "killpg", side_effect=AssertionError("numeric PGID signal")))
            yield self


class AuditTests(unittest.TestCase):
    def fixture(self, temporary, clock=None):
        clock = clock or Clock()
        campaign = Path(temporary) / "campaign"
        campaign.mkdir()
        directory = Path(temporary) / "control"
        config = dict(schema=w.SCHEMA, initial_campaign=str(campaign), python=sys.executable,
            seconds=39012., hard_deadline_wall=40012., port=0, bind="127.0.0.1",
            max_restarts=3, poll_seconds=.1, grace_seconds=1.,
            recovery_root=str(directory / "recoveries"), project=str(w.ROOT))
        (campaign / "budget.json").write_text(json.dumps(dict(campaign_id="audit-allocation",
            limit_seconds=39012., charged_seconds=0., active=None,
            collection_accounting={"censored_games": 0})))
        (campaign / "prepared.json").write_text(json.dumps({"post_training_pairs": 2048}))
        (campaign / "latest.soicp").write_bytes(b"not a checkpoint; never loaded by these OS fixtures")
        (campaign / "incumbent").mkdir()
        (campaign / "incumbent/manifest.json").write_text("{}")
        checked = mock.Mock(return_value=dict(campaign_id="audit-allocation", limit_seconds=39012.,
                                              remaining_seconds=39012.))
        controller = w.Runner(config, directory, checker=checked,
                              recovery=mock.Mock(side_effect=AssertionError("unexpected recovery")))
        return controller, checked

    def owner(self, pid, role, campaign, *, leader=None, boot="audit-boot-a", ticks=101):
        flag = "--run-dir" if role == "train" else "--campaign"
        command = (sys.executable, str(w.ROOT / "Tools/ZeroDepthTraining" / (role + ".py")),
                   flag, str(campaign), "--resume")
        return w.Process(pid, ticks, leader or pid, leader or pid, command, boot)

    def bind(self, controller, launcher, trainer=None, descendants=()):
        controller.state.update(launcher=asdict(launcher), trainer=asdict(trainer) if trainer else None,
            campaign_id="audit-allocation", attempt_has_active=bool(trainer),
            attempt_censored_baseline=0, owned_descendants=[asdict(p) for p in descendants],
            attempt_log=str(controller.directory / "attempt.log"))
        (controller.campaign / "supervisor.json").write_text(json.dumps(dict(
            supervisor_pid=launcher.pid, trainer_pid=trainer.pid if trainer else None,
            state="starting" if trainer else "complete", returncode=None if trainer else 0,
            stop_reason=None)))

    def test_reused_pid_is_never_signaled(self):
        owner = w.Process(10_001, 100, 10_001, 10_001, ("python", "trainer"), "audit-boot-a")
        system = FakeSystem(Clock(), {owner.pid: replace(owner, start_ticks=200)})
        with system.install(), self.assertRaises(RuntimeError):
            w.signal_owned(owner, signal.SIGTERM)
        self.assertEqual(system.signals, [])

    def test_identity_change_during_pidfd_open_is_rejected_without_a_signal(self):
        owner = w.Process(10_001, 100, 10_001, 10_001, ("python", "trainer"), "audit-boot-a")
        system = FakeSystem(Clock(), {owner.pid: owner})
        original_open = system.pidfd

        def reuse(pid):
            descriptor = original_open(pid)
            system.processes[pid] = replace(owner, start_ticks=200)
            return descriptor

        with system.install(), mock.patch.object(w.os, "pidfd_open", side_effect=reuse), self.assertRaises(RuntimeError):
            w.signal_owned(owner, signal.SIGTERM)
        self.assertEqual(system.signals, [])
        self.assertEqual(system.pinned, {})

    def test_unavailable_pidfd_never_falls_back_to_a_numeric_signal(self):
        owner = w.Process(10_001, 100, 10_001, 10_001, ("python", "trainer"), "audit-boot-a")
        system = FakeSystem(Clock(), {owner.pid: owner})
        with system.install(), mock.patch.object(w.os, "pidfd_open", side_effect=AttributeError("pidfd unavailable")):
            with self.assertRaises((AttributeError, RuntimeError)):
                w.signal_owned(owner, signal.SIGTERM)
        self.assertEqual(system.signals, [])

    def test_dead_launcher_live_trainer_gets_a_graceful_stop(self):
        clock = Clock()
        system = FakeSystem(clock)
        with tempfile.TemporaryDirectory() as temporary, system.install():
            controller, _ = self.fixture(temporary, clock)
            launcher = self.owner(10_001, "launch", controller.campaign)
            trainer = self.owner(10_002, "train", controller.campaign)
            host = w.Process(10_003, 103, trainer.pid, trainer.pid, ("dotnet", "ZeroDepthHost.dll"), clock.boot)
            system.processes.update({trainer.pid: trainer, host.pid: host})
            self.bind(controller, launcher, trainer, [host])

            def finish(owner, number):
                if owner == trainer and number == signal.SIGTERM:
                    system.processes.pop(trainer.pid)
                    system.processes.pop(host.pid)

            system.on_signal = finish
            controller.stop_owned()
            self.assertEqual(system.signals, [(trainer.pid, signal.SIGTERM)])
            self.assertNotIn(trainer.pid, system.processes)
            self.assertNotIn(host.pid, system.processes)

    def test_dead_trainer_still_cleans_captured_host_without_touching_unrelated_processes(self):
        clock = Clock()
        system = FakeSystem(clock)
        with tempfile.TemporaryDirectory() as temporary, system.install():
            controller, _ = self.fixture(temporary, clock)
            launcher = self.owner(10_001, "launch", controller.campaign)
            trainer = self.owner(10_002, "train", controller.campaign)
            host = w.Process(10_003, 103, trainer.pid, trainer.pid, ("dotnet", "ZeroDepthHost.dll"), clock.boot)
            unrelated = self.owner(10_004, "train", controller.campaign, ticks=204)
            system.processes.update({host.pid: host, unrelated.pid: unrelated})
            system.on_signal = lambda owner, number: system.processes.pop(owner.pid, None)
            self.bind(controller, launcher, trainer, [host])
            controller.stop_owned()
            self.assertNotIn(host.pid, system.processes)
            self.assertEqual(system.processes[unrelated.pid], unrelated)
            self.assertTrue(system.signals)
            self.assertTrue(all(pid == host.pid for pid, _ in system.signals))

    def test_recycled_dead_trainer_session_does_not_authorize_unrelated_members(self):
        clock = Clock()
        system = FakeSystem(clock)
        with tempfile.TemporaryDirectory() as temporary, system.install():
            controller, _ = self.fixture(temporary, clock)
            launcher = self.owner(10_001, "launch", controller.campaign)
            trainer = self.owner(10_002, "train", controller.campaign)
            replacement = w.Process(trainer.pid, 900, trainer.pid, trainer.pid,
                                    ("unrelated-job", "--private-session"), clock.boot)
            system.processes[replacement.pid] = replacement
            system.on_signal = lambda owner, number: system.processes.pop(owner.pid, None)
            self.bind(controller, launcher, trainer)
            controller.stop_owned()
            self.assertEqual(system.processes.get(replacement.pid), replacement)
            self.assertEqual(system.signals, [])

    def test_unknown_adopted_evaluation_exit_never_restarts_learning_or_claims_completion(self):
        system = FakeSystem(Clock())
        with tempfile.TemporaryDirectory() as temporary, system.install():
            controller, checked = self.fixture(temporary)
            launcher = self.owner(10_001, "launch", controller.campaign)
            self.bind(controller, launcher)
            controller.state.update(status="evaluating", attempt_has_active=True)
            with mock.patch.object(w.subprocess, "Popen", side_effect=AssertionError("learning restarted")):
                self.assertFalse(controller.after_exit(None))
            self.assertNotEqual(controller.state["status"], "completed")
            checked.assert_not_called()

    def test_only_a_finished_comparison_for_the_exact_checkpoint_and_incumbent_completes(self):
        system = FakeSystem(Clock())
        with tempfile.TemporaryDirectory() as temporary, system.install():
            controller, _ = self.fixture(temporary)
            report = dict(purpose="final_strength_evaluation", pairs=2048,
                checkpoint_sha256=hashlib.sha256((controller.campaign / "latest.soicp").read_bytes()).hexdigest(),
                incumbent_manifest_sha256=hashlib.sha256((controller.campaign / "incumbent/manifest.json").read_bytes()).hexdigest(),
                summary=dict(evaluation_finished=False, planned_pairs=2048,
                             complete_pairs=0, recorded_games=0))
            path = controller.campaign / "post-training-evaluation-audit.json"
            path.write_text(json.dumps(report))
            self.assertFalse(w.completed_evaluation(controller.campaign))
            report["summary"].update(evaluation_finished=True, complete_pairs=2048, recorded_games=4096)
            path.write_text(json.dumps(report))
            self.assertTrue(w.completed_evaluation(controller.campaign))
            (controller.campaign / "latest.soicp").write_bytes(b"a different committed checkpoint")
            self.assertFalse(w.completed_evaluation(controller.campaign))

    def test_reboot_during_healthy_deadline_evaluation_never_funds_more_learning(self):
        clock = Clock(wall=1100., mono=100., boot="audit-boot-b")
        system = FakeSystem(clock)
        with tempfile.TemporaryDirectory() as temporary, system.install():
            controller, checked = self.fixture(temporary, clock)
            launcher = self.owner(10_001, "launch", controller.campaign, boot="audit-boot-a")
            self.bind(controller, launcher)
            supervision = json.loads((controller.campaign / "supervisor.json").read_text())
            supervision["stop_reason"] = "hard_deadline"
            (controller.campaign / "supervisor.json").write_text(json.dumps(supervision))
            controller.state.update(boot_id="audit-boot-a", status="deadline_stopping",
                observed_boot_id="audit-boot-a", observed_wall=1000., observed_monotonic=1000.,
                authorization_remaining_seconds=39012.)
            w.atomic(controller.path, controller.state)
            controller.recovery = mock.Mock(return_value={})
            with mock.patch.object(w.subprocess, "Popen", side_effect=AssertionError("learning restarted")):
                controller.run()
            controller.recovery.assert_not_called()
            checked.assert_not_called()
            self.assertNotEqual(controller.state["status"], "completed")

    def test_dead_launcher_at_the_cap_stops_the_surviving_trainer(self):
        clock = Clock(wall=40012., mono=40012.)
        system = FakeSystem(clock)
        with tempfile.TemporaryDirectory() as temporary, system.install():
            controller, checked = self.fixture(temporary, clock)
            launcher = self.owner(10_001, "launch", controller.campaign)
            trainer = self.owner(10_002, "train", controller.campaign)
            system.processes[trainer.pid] = trainer
            system.on_signal = lambda owner, number: system.processes.pop(owner.pid, None)
            self.bind(controller, launcher, trainer)
            self.assertFalse(controller.after_exit(None))
            self.assertNotIn(trainer.pid, system.processes)
            checked.assert_not_called()

    def test_reboot_grant_debits_exact_remaining_time_and_only_one_retry(self):
        clock = Clock(wall=2100., mono=100., boot="audit-boot-b")
        system = FakeSystem(clock)
        with tempfile.TemporaryDirectory() as temporary, system.install():
            controller, _ = self.fixture(temporary, clock)
            controller.state.update(boot_id="audit-boot-a", observed_boot_id="audit-boot-a",
                observed_wall=2000., observed_monotonic=2000., authorization_remaining_seconds=38012.,
                authorization_deadline_monotonic=40012.)
            granted = []

            def recover(parent, destination, seconds):
                granted.append(seconds)
                return {"campaign": str(destination), "new_allocation_seconds": seconds,
                        "complete_state_byte_exact": True, "all_rng_byte_exact": True}

            controller.recovery = recover
            self.assertTrue(controller.recover_boot())
            self.assertEqual(granted, [37912.])
            self.assertEqual(controller.state["restarts"], 1)
            self.assertLessEqual(controller.state["authorization_remaining_seconds"], 37912.)

    def test_backwards_wall_after_reboot_cannot_mint_new_training_time(self):
        clock = Clock(wall=1990., mono=100., boot="audit-boot-b")
        system = FakeSystem(clock)
        with tempfile.TemporaryDirectory() as temporary, system.install():
            controller, _ = self.fixture(temporary, clock)
            controller.state.update(boot_id="audit-boot-a", observed_boot_id="audit-boot-a",
                observed_wall=2000., observed_monotonic=2000., authorization_remaining_seconds=38012.)
            with self.assertRaises(RuntimeError):
                controller.recover_boot()
            controller.recovery.assert_not_called()

    def test_respawned_watchdog_adopts_live_components_without_a_second_learner(self):
        clock = Clock()
        system = FakeSystem(clock)
        with tempfile.TemporaryDirectory() as temporary, system.install():
            controller, checked = self.fixture(temporary, clock)
            launcher = self.owner(10_001, "launch", controller.campaign)
            trainer = self.owner(10_002, "train", controller.campaign)
            monitor = self.owner(10_003, "monitor", controller.campaign)
            system.processes.update({p.pid: p for p in (launcher, trainer, monitor)})
            self.bind(controller, launcher, trainer)
            controller.state["monitor"] = asdict(monitor)
            ledger = json.loads((controller.campaign / "budget.json").read_text())
            ledger["active"] = {"pid": trainer.pid, "boot_id": clock.boot,
                "last_heartbeat_monotonic": clock.mono, "last_heartbeat_wall": clock.wall}
            (controller.campaign / "budget.json").write_text(json.dumps(ledger))
            controller.publish("training")
            system.on_sleep = lambda: setattr(controller, "stop_requested", True)
            system.on_signal = lambda owner, number: system.processes.pop(owner.pid, None)
            with mock.patch.object(w.subprocess, "Popen", side_effect=AssertionError("duplicate learner or monitor")):
                controller.run()
            checked.assert_not_called()

    def test_durable_stop_while_watchdog_is_down_stops_the_existing_learner_on_respawn(self):
        clock = Clock()
        system = FakeSystem(clock)
        with tempfile.TemporaryDirectory() as temporary, system.install():
            controller, checked = self.fixture(temporary, clock)
            launcher = self.owner(10_001, "launch", controller.campaign)
            trainer = self.owner(10_002, "train", controller.campaign)
            system.processes.update({p.pid: p for p in (launcher, trainer)})
            self.bind(controller, launcher, trainer)
            controller.publish("training")
            (controller.directory / "STOP").write_text("user canceled while watchdog was down")
            system.on_signal = lambda owner, number: system.processes.pop(owner.pid, None)
            with mock.patch.object(w.subprocess, "Popen", side_effect=AssertionError("new learner")):
                controller.run()
            self.assertNotIn(trainer.pid, system.processes)
            self.assertNotIn(launcher.pid, system.processes)
            self.assertEqual(controller.state["status"], "stopped")
            checked.assert_not_called()


if __name__ == "__main__":
    unittest.main()
