"""Restart health uses the authoritative censor window, never lifetime growth."""
import json
from pathlib import Path
import signal
import sys
import tempfile
from types import SimpleNamespace
import unittest
from unittest import mock

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "TrainingPreflight"))
from campaign_state import CampaignBudget
import test_audit as audit
w = audit.w
Clock, FakeSystem = audit.Clock, audit.FakeSystem


class CensorRestartTests(unittest.TestCase):
    fixture = audit.AuditTests.fixture
    owner = audit.AuditTests.owner
    bind = audit.AuditTests.bind

    def ledger(self, path, censors, *, clear_window=False):
        path.unlink(missing_ok=True)
        with CampaignBudget(path, limit_seconds=39012.) as budget:
            budget.record_collection(128, 128-censors, censors, 100, 3 if censors else 0,
                                     {"reason": "round_limit"} if censors else None)
            if clear_window:
                for _ in range(32):
                    budget.record_collection(128, 128, 0, 100, 0)
            summary = budget.collection_summary()
        return json.loads(path.read_text()), summary

    def run_case(self, mutate=None, *, censors=1, clear_window=True, expected_retry):
        clock = Clock()
        system = FakeSystem(clock)
        with tempfile.TemporaryDirectory() as temporary, system.install():
            controller, checked = self.fixture(temporary, clock)
            path = controller.campaign / "budget.json"
            ledger, summary = self.ledger(path, censors, clear_window=clear_window)
            if mutate:
                mutate(ledger)
                path.write_text(json.dumps(ledger))
            launcher = self.owner(10_001, "launch", controller.campaign)
            self.bind(controller, launcher)
            controller.state.update(status="training", campaign_id=ledger["campaign_id"],
                                    attempt_censored_baseline=0)
            # This is the actual signal-loss status; there was no completed evaluation.
            (controller.campaign / "supervisor.json").write_text(json.dumps(dict(
                supervisor_pid=launcher.pid, returncode=-signal.SIGKILL,
                state="failed", stop_reason=None)))
            Path(controller.state["attempt_log"]).write_text(
                '{"event":"administrative_censor","reason":"round_limit","excluded_rows":3221}\n')
            checked.return_value = dict(campaign_id=ledger["campaign_id"], limit_seconds=39012.,
                                        remaining_seconds=39012.)
            spawned = []

            def popen(command, **_kwargs):
                # A harmless simulated launcher, never an OS process or NN.
                owner = w.Process(10_020, 202, 10_020, 10_020, tuple(command), clock.boot)
                system.processes[owner.pid] = owner
                spawned.append(tuple(command))
                return SimpleNamespace(pid=owner.pid, poll=lambda: None)

            with mock.patch.object(w.subprocess, "Popen", side_effect=popen):
                result = controller.after_exit(-signal.SIGKILL)
            if expected_retry:
                self.assertTrue(result)
                self.assertEqual(len(spawned), 1)
                self.assertEqual(controller.state["restarts"], 1)
                self.assertIn("--resume", spawned[0])
                self.assertEqual(controller.config["hard_deadline_wall"], 40012.)
            else:
                self.assertFalse(result)
                self.assertEqual(spawned, [])
                self.assertEqual(controller.state["status"], "blocked")
                checked.assert_not_called()
            return summary

    def test_one_earlier_excluded_censor_outside_the_window_does_not_block_process_loss(self):
        summary = self.run_case(expected_retry=True)
        self.assertEqual((summary["censored_games"], summary["recent_censored"]), (1, 0))

    def test_one_recent_excluded_censor_stays_below_the_existing_four_game_threshold(self):
        summary = self.run_case(clear_window=False, expected_retry=True)
        self.assertEqual(summary["recent_censored"], 1)

    def test_four_recent_censors_refuse_a_process_loss_restart(self):
        summary = self.run_case(censors=4, clear_window=False, expected_retry=False)
        self.assertEqual(summary["recent_censored"], 4)

    def test_missing_or_empty_nonzero_censor_history_is_not_assumed_healthy(self):
        def missing(data):
            del data["collection_accounting"]["recent_batches"]

        for mutation in (missing, lambda data: data["collection_accounting"].update(recent_batches=[])):
            with self.subTest(mutation=mutation):
                self.run_case(mutation, expected_retry=False)

    def test_malformed_order_counts_coverage_and_nonfinite_history_are_refused(self):
        mutations = (
            lambda a: a["recent_batches"][-1].update(censored_games=True),
            lambda a: a["recent_batches"][-1].update(censored_games=0.0),
            lambda a: a["recent_batches"][-1].update(wall=float("nan")),
            lambda a: a["recent_batches"][-1].update(collection=999),
            lambda a: a["recent_batches"][-1].update(collection=33.0),
            lambda a: a["recent_batches"].pop(0),
            lambda a: a.update(censored_games=0),
            lambda a: a.update(window_games=2048),
        )
        for mutate_accounting in mutations:
            with self.subTest(mutation=mutate_accounting):
                self.run_case(lambda data: mutate_accounting(data["collection_accounting"]),
                              expected_retry=False)

    def test_minimal_known_zero_history_fixture_is_supported_without_accepting_corruption(self):
        for accounting in ({"censored_games": 0}, {"censored_games": 0, "recent_batches": []}):
            self.assertFalse(w.unsafe_recent_censors({"collection_accounting": accounting}))
        for accounting in ({}, {"censored_games": False}, {"censored_games": 0, "recent_batches": None},
                           {"censored_games": 1}, {"censored_games": 1, "recent_batches": []}):
            self.assertTrue(w.unsafe_recent_censors({"collection_accounting": accounting}))

    def test_actual_four_censor_guard_and_unrelated_model_faults_remain_fatal(self):
        for message in ("Four censored games in the last4096 attempts",
                        "Traceback (most recent call last)", "Nonfinite model", "CUDA error"):
            with self.subTest(message=message):
                self.assertEqual(w.failure_reason(-signal.SIGKILL, {}, message), "unsafe_error")


if __name__ == "__main__":
    unittest.main()
