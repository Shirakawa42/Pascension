"""Adversarial orchestration with fabricated games and isolated persistence."""
import contextlib
import builtins
from dataclasses import asdict
import io
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

import numpy as np
import torch
import train
from campaign_state import CampaignBudget, CheckpointError, _snapshot, load_checkpoint


CATALOG = {"obs_dim": 32, "max_actions": 64, "action_dim": 48,
           "card_ids": ["crystal"], "candidate_card_scale": 192, "histograms": []}


class FailedAdvanceHost:
    """Only fabricated metadata, with an exception on its first submission."""
    def __init__(self, *_args, **_kwargs):
        self.obs = np.zeros((2, 32), np.float32)
        self.candidates = np.zeros((2, 64, 48), np.float32)
        self.mask = np.zeros((2, 64), np.float32)
        self.mask[:, 0] = 1
        self.actors = np.array([0, 1], np.int32)
        self.done = np.zeros(2, np.int32)
        self.rewards = np.zeros((2, 2), np.float32)

    def __enter__(self):
        return self

    def __exit__(self, *_):
        pass

    def advance(self, _actions):
        raise RuntimeError("fabricated host failure")


def fixture_config(**changes):
    values = dict(batch=2, workers=1, width=64, capacity=64, minibatch=16, epochs=1,
                  archive_fraction=0, graph=False, adaptive_actors=False,
                  packed_inputs=False, fused_optimizer=False)
    return train.TrainConfig(**(values | changes))


def fixture_identity(config):
    return {"schema": "isolated-recovery-fixture", "configuration": asdict(config)}


def run_fixture(directory, host_class, *, config=None, update=None, max_games=2, resume=None):
    config = config or fixture_config()
    previous = {sig: signal.getsignal(sig) for sig in (signal.SIGTERM, signal.SIGINT)}
    try:
        with contextlib.ExitStack() as stack:
            stack.enter_context(mock.patch.object(train, "Host", host_class))
            stack.enter_context(mock.patch.object(train, "catalog", return_value=CATALOG))
            stack.enter_context(mock.patch.object(train, "identity", return_value=fixture_identity(config)))
            stack.enter_context(contextlib.redirect_stdout(io.StringIO()))
            if update is not None:
                stack.enter_context(mock.patch.object(train.Learner, "update", new=update))
            return train.train(config, directory, 120, max_games=max_games, resume=resume, device="cpu")
    finally:
        for sig, handler in previous.items():
            signal.signal(sig, handler)


def crash_fixture(directory):
    """Disposable child exits abruptly after publishing a partial heartbeat."""
    elapsed = [0.]
    real_monotonic = time.monotonic

    class CrashHost(FailedAdvanceHost):
        def __init__(self, *_args, **_kwargs):
            super().__init__()
            self.steps = 0
        def advance(self, _actions):
            self.steps += 1
            if self.steps == 1:
                self.done[0] = 1
                self.rewards[0] = [1, -1]
                self.mask[0] = 0
                elapsed[0] += 11.
            else:
                os._exit(17)

    with mock.patch.object(train.time, "monotonic", side_effect=lambda: real_monotonic() + elapsed[0]):
        run_fixture(Path(directory), CrashHost)


class TrainingRecoveryTests(unittest.TestCase):
    def test_checkpoint_parallel_snapshot_preserves_values_rng_and_restores_threads_on_failure(self):
        previous = torch.get_num_threads()
        torch.set_num_threads(1)
        try:
            # Snapshot data includes optimizer-shaped moments and signed zero.
            state = {"weights": torch.arange(65536, dtype=torch.float32).reshape(256, 256),
                     "moment": torch.full((256, 256), -0.0)}
            rng = torch.get_rng_state().clone()
            for workers in (1, 8, 16):
                with train.checkpoint_cpu_threads(workers):
                    self.assertEqual(torch.get_num_threads(), min(8, workers))
                    saved = _snapshot(state)
                    for name in state:
                        self.assertTrue(torch.equal(state[name].view(torch.uint8), saved[name].view(torch.uint8)))
                self.assertEqual(torch.get_num_threads(), 1)
                self.assertTrue(torch.equal(rng, torch.get_rng_state()))
            with self.assertRaisesRegex(CheckpointError, "Nonfinite checkpoint tensor"):
                with train.checkpoint_cpu_threads(8):
                    _snapshot({"weights": torch.tensor([float("nan")])})
            self.assertEqual(torch.get_num_threads(), 1)
            self.assertTrue(torch.equal(rng, torch.get_rng_state()))
        finally:
            torch.set_num_threads(previous)

    def test_game_statistics_resume_without_duplicate_credit_and_log_wall_time(self):
        class FinishedHost(FailedAdvanceHost):
            def advance(self, _actions):
                self.done[:] = 1
                self.rewards[:] = [[1, -1], [-1, 1]]
                self.mask[:] = 0

        def completed_update(*_args, **_kwargs):
            return {"optimizer_steps": 0, "example_passes": 0}

        config = fixture_config()
        with tempfile.TemporaryDirectory() as temporary:
            directory = Path(temporary)
            checkpoint = run_fixture(directory, FinishedHost, update=completed_update)
            first = load_checkpoint(checkpoint, expected_identity=fixture_identity(config))["state"]
            self.assertEqual(first["rolling_game_stats"]["count"], 2)
            self.assertIsInstance(first["rolling_game_stats"]["records"], bytes)
            first_status = json.loads((directory / "status.json").read_text())
            self.assertEqual(first_status["rolling_game_stats"]["games_in_window"], 2)
            run_fixture(directory, FinishedHost, update=completed_update, max_games=4, resume=checkpoint)
            final = load_checkpoint(checkpoint, expected_identity=fixture_identity(config))["state"]
            restored = train.RollingGameStats.from_state(CATALOG, final["rolling_game_stats"])
            status = json.loads((directory / "status.json").read_text())
            self.assertEqual(restored.snapshot(), status["rolling_game_stats"])
            self.assertEqual((final["games"], restored.count, restored.total_natural_games), (4, 4, 4))
            events = [json.loads(line) for line in (directory / "metrics.jsonl").read_text().splitlines()]
            self.assertTrue(all(isinstance(event["wall"], (float, int)) for event in events))
            starts = [event for event in events if event["event"] == "start"]
            self.assertEqual([event["games"] for event in starts], [0, 2])
            self.assertTrue(all(event["model_parameters"] > 0 for event in starts))

    def test_host_exception_accounts_current_partial_collection_without_waiting_for_heartbeat(self):
        config = train.TrainConfig(batch=2, workers=1, width=64, capacity=64,
                                   minibatch=16, epochs=1, archive_fraction=0,
                                   graph=False, adaptive_actors=False, packed_inputs=False,
                                   fused_optimizer=False)
        pinned = {"schema": "isolated-recovery-fixture", "configuration": asdict(config)}
        previous = {sig: signal.getsignal(sig) for sig in (signal.SIGTERM, signal.SIGINT)}
        with tempfile.TemporaryDirectory() as temporary:
            directory = Path(temporary)
            try:
                with mock.patch.object(train, "Host", FailedAdvanceHost), \
                        mock.patch.object(train, "catalog", return_value=CATALOG), \
                        mock.patch.object(train, "identity", return_value=pinned), \
                        contextlib.redirect_stdout(io.StringIO()):
                    with self.assertRaisesRegex(RuntimeError, "fabricated host failure"):
                        train.train(config, directory, 30, device="cpu")
            finally:
                for sig, handler in previous.items():
                    signal.signal(sig, handler)
            payload = load_checkpoint(directory / "latest.soicp", expected_identity=pinned)
            self.assertEqual(payload["state"]["optimizer_steps"], 0)
            ledger = json.loads((directory / "budget.json").read_text())
            self.assertIsNone(ledger["active"])
            self.assertEqual((ledger["discarded_episodes"], ledger["discarded_decisions"]), (2, 2))
            self.assertEqual(ledger["collection_accounting"]["attempted_games"], 0)
            traces = list((directory / "diagnostics").glob("*.npz"))
            self.assertEqual(len(traces), 1)
            with np.load(traces[0]) as trace:
                np.testing.assert_array_equal(trace["actions"], [[0, 0]])

    def test_abrupt_recovery_accounts_held_terminal_lanes_in_a_discarded_cohort(self):
        with tempfile.TemporaryDirectory() as temporary:
            directory = Path(temporary)
            code = ("import sys; sys.path.insert(0,sys.argv[1]); "
                    "from test_training_recovery import crash_fixture; crash_fixture(sys.argv[2])")
            result = subprocess.run([sys.executable, "-c", code, str(Path(__file__).parent), str(directory)],
                                    capture_output=True, text=True, timeout=20)
            self.assertEqual(result.returncode, 17, result.stderr)
            ledger = json.loads((directory / "budget.json").read_text())
            self.assertIsNotNone(ledger["active"])
            with CampaignBudget(directory / "budget.json", limit_seconds=120):
                pass
            recovered = json.loads((directory / "budget.json").read_text())
            self.assertEqual(recovered["discarded_episodes"], 2)
            self.assertEqual(recovered["discarded_decisions"], 2)
            self.assertTrue(recovered["discard_events"][-1]["unknown_unreported_tail"])
            payload = load_checkpoint(directory / "latest.soicp", expected_identity=fixture_identity(fixture_config()))
            self.assertEqual(payload["state"]["optimizer_steps"], 0)

    def test_finalized_collection_is_not_counted_as_unresolved_on_update_failure(self):
        elapsed = [0.]
        real_monotonic = time.monotonic

        class FinishedHost(FailedAdvanceHost):
            def advance(self, _actions):
                self.done[:] = 1
                self.rewards[:] = [[1, -1], [-1, 1]]
                self.mask[:] = 0
                elapsed[0] += 11.

        def fail_update(*_args, **_kwargs):
            raise RuntimeError("fabricated update failure")

        with tempfile.TemporaryDirectory() as temporary:
            directory = Path(temporary)
            with mock.patch.object(train.time, "monotonic", side_effect=lambda: real_monotonic() + elapsed[0]):
                with self.assertRaisesRegex(RuntimeError, "fabricated update failure"):
                    run_fixture(directory, FinishedHost, update=fail_update)
            ledger = json.loads((directory / "budget.json").read_text())
            self.assertEqual((ledger["discarded_episodes"], ledger["discarded_decisions"]), (0, 0))
            self.assertEqual(ledger["collection_accounting"]["completed_games"], 2)
            self.assertEqual(ledger["collection_accounting"]["learning_rows"], 2)

    def test_archive_subset_keeps_only_learner_actions_and_deciding_seat_credit(self):
        observed = []

        class SeatHost(FailedAdvanceHost):
            def __init__(self, *_args, **_kwargs):
                super().__init__()
                self.actors[:] = [1, 0]
                self.steps = 0
            def advance(self, _actions):
                self.steps += 1
                self.actors[:] = [0, 1]
                if self.steps == 2:
                    self.done[:] = 1
                    self.rewards[:] = [[1, -1], [-1, 1]]
                    self.mask[:] = 0

        def observe_update(_learner, store, indices, returns, _advantages, **_kwargs):
            observed.append((store.lanes[indices].copy(), store.seats[indices].copy(), returns.copy()))
            return {"optimizer_steps": 0, "example_passes": 0}

        config = fixture_config(archive_fraction=.5)
        with tempfile.TemporaryDirectory() as temporary:
            with mock.patch.object(train.np.random, "random", return_value=np.array([.1, .9])):
                run_fixture(Path(temporary), SeatHost, config=config, update=observe_update)
        self.assertEqual(len(observed), 1)
        np.testing.assert_array_equal(observed[0][0], [1, 0, 1])
        np.testing.assert_array_equal(observed[0][1], [0, 0, 1])
        np.testing.assert_array_equal(observed[0][2], [-1, 1, 1])

    def test_signal_after_one_fabricated_target_update_saves_only_completed_optimizer_work(self):
        class FinishedHost(FailedAdvanceHost):
            def advance(self, _actions):
                self.done[:] = 1
                # Fabricated utilities only; no actual engine game runs here.
                self.rewards[:] = [[1, -1], [-1, 1]]
                self.mask[:] = 0
            def reset(self, *_):
                raise AssertionError("A stop during update must not begin another cohort")

        original_step = train.torch.optim.Adam.step

        def stop_after_step(optimizer, *args, **kwargs):
            result = original_step(optimizer, *args, **kwargs)
            signal.getsignal(signal.SIGTERM)(signal.SIGTERM, None)
            return result

        config = fixture_config(epochs=3, archive_every=1)
        with tempfile.TemporaryDirectory() as temporary:
            directory = Path(temporary)
            with mock.patch.object(train.torch.optim.Adam, "step", new=stop_after_step):
                run_fixture(directory, FinishedHost, config=config, max_games=100)
            payload = load_checkpoint(directory / "latest.soicp", expected_identity=fixture_identity(config))
            state = payload["state"]
            self.assertEqual((state["games"], state["optimizer_steps"], state["example_passes"]), (2, 1, 2))
            self.assertEqual(state["next_engine_seed"], config.engine_seed + 2)
            self.assertGreater(abs(float(state["policy"]["value.bias"].item())), 0)
            self.assertTrue(all(float(moment["step"].item()) == 1 for moment in state["optimizer"]["state"].values()))
            self.assertEqual(len(state["archive"]), 2)
            for name, value in state["policy"].items():
                train.torch.testing.assert_close(value, state["archive"][-1][name], rtol=0, atol=0)
            ledger = json.loads((directory / "budget.json").read_text())
            self.assertIsNone(ledger["active"])
            self.assertEqual(ledger["discarded_episodes"], 0)

    def test_fourth_censor_across_resumes_stops_before_update_and_keeps_exact_diagnostics(self):
        observed = []

        class CappedHost(FailedAdvanceHost):
            def advance(self, _actions):
                self.done[:] = [1, 2]
                self.rewards[0] = [1, -1]
                self.mask[:] = 0

        def observe_update(_learner, _store, indices, returns, _advantages, **_kwargs):
            observed.append((indices.copy(), returns.copy()))
            return {"optimizer_steps": 0, "example_passes": 0}

        config = fixture_config()
        with tempfile.TemporaryDirectory() as temporary:
            directory = Path(temporary)
            checkpoint = directory / "latest.soicp"
            for completed in range(1, 4):
                run_fixture(directory, CappedHost, config=config, update=observe_update,
                            max_games=completed, resume=checkpoint if completed > 1 else None)
            with self.assertRaisesRegex(RuntimeError, "Four censored games"):
                run_fixture(directory, CappedHost, config=config, update=observe_update,
                            max_games=4, resume=checkpoint)
            self.assertEqual(len(observed), 3)
            for indices, returns in observed:
                np.testing.assert_array_equal(indices, [0])
                np.testing.assert_array_equal(returns, [1])
            ledger = json.loads((directory / "budget.json").read_text())
            counts = ledger["collection_accounting"]
            self.assertEqual((counts["attempted_games"], counts["completed_games"], counts["censored_games"]), (8, 4, 4))
            self.assertEqual((counts["learning_rows"], counts["censored_rows"]), (4, 4))
            self.assertEqual((ledger["discarded_episodes"], ledger["discarded_decisions"]), (0, 0))
            traces = sorted((directory / "diagnostics").glob("*.npz"))
            self.assertEqual(len(traces), 4)
            saved = load_checkpoint(checkpoint, expected_identity=fixture_identity(config))["state"]
            self.assertEqual(saved["rolling_game_stats"]["count"], 3)
            self.assertEqual(saved["rolling_game_stats"]["total_censored_games"], 3)
            for trace in traces:
                with np.load(trace) as archive:
                    np.testing.assert_array_equal(archive["censored_lanes"], [1])
                    np.testing.assert_array_equal(archive["actions"], [[0, 0]])

    def test_error_after_explicit_partial_discard_does_not_count_the_cohort_twice(self):
        class StoppedHost(FailedAdvanceHost):
            def advance(self, _actions):
                signal.getsignal(signal.SIGTERM)(signal.SIGTERM, None)

        class FailedDiscardLog:
            def __init__(self, destination):
                self.destination = destination
            def __enter__(self):
                return self
            def __exit__(self, *_):
                self.destination.close()
            def write(self, line):
                if json.loads(line)["event"] == "discarded_partial_generation":
                    raise OSError("cannot persist discard metrics")
                return self.destination.write(line)

        def failing_metrics(*args, **kwargs):
            return FailedDiscardLog(builtins.open(*args, **kwargs))

        with tempfile.TemporaryDirectory() as temporary:
            directory = Path(temporary)
            with mock.patch.object(train, "open", side_effect=failing_metrics, create=True):
                with self.assertRaisesRegex(OSError, "cannot persist discard metrics"):
                    run_fixture(directory, StoppedHost)
            ledger = json.loads((directory / "budget.json").read_text())
            self.assertEqual((ledger["discarded_episodes"], ledger["discarded_decisions"]), (2, 2))


if __name__ == "__main__":
    unittest.main()
