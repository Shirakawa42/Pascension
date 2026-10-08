"""Real persistence, mocked games and mocked updates: never outcome learning."""
import contextlib
from dataclasses import asdict
import hashlib
import io
import json
from pathlib import Path
import signal
import sys
import tempfile
from types import SimpleNamespace
import unittest
from unittest import mock

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

import numpy as np
import torch
import launch
import prepare
import train
from campaign_state import CampaignBudget, load_checkpoint, save_checkpoint_atomic


CATALOG = {"obs_dim": 32, "max_actions": 64, "action_dim": 48,
           "card_ids": ["crystal"], "candidate_card_scale": 192, "histograms": []}


class PreparationTests(unittest.TestCase):
    def test_preparation_publishes_launch_inputs_without_training_state(self):
        with tempfile.TemporaryDirectory() as temporary:
            directory = Path(temporary) / "prepared"
            frozen = {"files": {"shards-policy.bytes": {"sha256": "abc"}}}
            with mock.patch.object(prepare.subprocess, "run"), \
                    mock.patch.object(prepare, "freeze_incumbent", return_value=frozen), \
                    mock.patch.object(prepare, "catalog", return_value=CATALOG), \
                    mock.patch.object(prepare, "identity", return_value={"fixture": True}), \
                    mock.patch.object(torch.cuda, "is_available", return_value=False):
                prepare.prepare(directory, train.TrainConfig(), build=False)
                prepare.prepare(directory, train.TrainConfig(), build=False)
                with self.assertRaises(RuntimeError):
                    prepare.prepare(directory, train.TrainConfig(width=128), build=False)
            plan = json.loads((directory / "prepared.json").read_text())
            self.assertFalse(plan["training_started"])
            self.assertTrue(plan["incumbent_tactical_search"])
            self.assertEqual(plan["lookahead_depth"], 0)
            self.assertFalse((directory / "budget.json").exists())
            self.assertFalse((directory / "latest.soicp").exists())

    def test_unlaunchable_configurations_rejected_before_work(self):
        invalid = ({"capacity": 64}, {"seed": -1}, {"seed": 1 << 32},
                   {"batch": 64.5}, {"workers": 1.5}, {"capacity": 131072.5},
                   {"minibatch": 1024.5}, {"engine_seed": (1 << 63) - 1})
        for changes in invalid:
            with self.subTest(changes=changes), self.assertRaises(ValueError):
                train.TrainConfig(**changes).validate()


class TrainingOrchestrationTests(unittest.TestCase):
    def fixture(self, directory, *, interrupted=False, resume=None, host_calls=None):
        updates, checkpoints = [], []
        original_save = train.save_checkpoint_atomic
        config = train.TrainConfig(batch=2, workers=1, width=64, capacity=64,
                                   minibatch=16, epochs=1, archive_fraction=0,
                                   graph=False, checkpoint_seconds=3600)
        pinned = {"schema": "isolated-orchestration-fixture", "configuration": asdict(config)}

        class FakeHost:
            def __init__(self, *_args, **_kwargs):
                if host_calls is not None:
                    host_calls.append(True)
                self.obs = np.zeros((2, 32), np.float32)
                self.candidates = np.zeros((2, 64, 48), np.float32)
                self.mask = np.zeros((2, 64), np.float32)
                self.mask[:, 0] = 1
                self.actors = np.array([0, 1], np.int32)
                self.done = np.zeros(2, np.int32)
                self.rewards = np.zeros((2, 2), np.float32)
                self.steps = 0
            def __enter__(self):
                return self
            def __exit__(self, *_):
                pass
            def advance(self, actions):
                np.testing.assert_array_equal(actions, [0, 0])
                self.steps += 1
                self.actors[:] = [1, 0]
                if interrupted:
                    # Invoke only this trainer's installed handler, never signal
                    # another process or call a real game/optimizer.
                    signal.getsignal(signal.SIGTERM)(signal.SIGTERM, None)
                elif self.steps == 2:
                    self.done[:] = [1, 2]
                    self.rewards[0] = [1, -1]
                    self.mask[:] = 0
            def reset(self, *_):
                raise AssertionError("One mock generation must stop at max_games")

        class FakeLearner:
            def __init__(self, policy, _config):
                self.policy = policy
                self.optimizer = SimpleNamespace(state_dict=lambda: {}, load_state_dict=lambda _: None)
            def restore_optimizer(self, state):
                # This orchestration fixture deliberately has no learned Adam
                # state. Real optimizer restoration is covered separately.
                self.optimizer.load_state_dict(state)
            def update(self, store, indices, returns, advantages, **_kwargs):
                updates.append({"rows": indices.copy(), "returns": returns.copy(),
                                "seats": store.seats[indices].copy()})
                # Synthetic metadata exercises counters; weights never change.
                return {"optimizer_steps": 1, "example_passes": len(indices)}
            def verify_finite_state(self):
                self.assert_finite = all(bool(torch.isfinite(p).all()) for p in self.policy.parameters())

        def save(path, state, **kwargs):
            checkpoints.append({"reason": state["reason"], "policy": train.cpu_state(SimpleNamespace(state_dict=lambda: state["policy"]))})
            return original_save(path, state, **kwargs)

        previous_handlers = {sig: signal.getsignal(sig) for sig in (signal.SIGTERM, signal.SIGINT)}
        try:
            with mock.patch.object(train, "Host", FakeHost), mock.patch.object(train, "Learner", FakeLearner), \
                    mock.patch.object(train, "catalog", return_value=CATALOG), \
                    mock.patch.object(train, "identity", return_value=pinned), \
                    mock.patch.object(train, "save_checkpoint_atomic", side_effect=save), \
                    contextlib.redirect_stdout(io.StringIO()):
                checkpoint = train.train(config, directory, 30, max_games=1, resume=resume, device="cpu")
        finally:
            for sig, handler in previous_handlers.items():
                signal.signal(sig, handler)
        payload = load_checkpoint(checkpoint, expected_identity=pinned)
        for key, value in checkpoints[0]["policy"].items():
            torch.testing.assert_close(value, checkpoints[-1]["policy"][key], rtol=0, atol=0)
        return config, updates, checkpoints, payload

    def test_initial_and_clean_stop_checkpoints_preserve_credit_and_exclude_caps(self):
        with tempfile.TemporaryDirectory() as temporary:
            directory = Path(temporary)
            config, updates, checkpoints, payload = self.fixture(directory)
            self.assertEqual([item["reason"] for item in checkpoints], ["initial", "complete"])
            self.assertEqual(len(updates), 1)
            np.testing.assert_array_equal(updates[0]["rows"], [0, 2])
            np.testing.assert_array_equal(updates[0]["seats"], [0, 1])
            np.testing.assert_array_equal(updates[0]["returns"], [1, -1])
            state = payload["state"]
            self.assertEqual((state["games"], state["attempts"], state["censored"], state["decisions"]), (1, 2, 1, 2))
            self.assertEqual(state["next_engine_seed"], config.engine_seed + 2)
            ledger = json.loads((directory / "budget.json").read_text())
            self.assertIsNone(ledger["active"])
            self.assertEqual(ledger["collection_accounting"]["learning_rows"], 2)
            self.assertEqual(ledger["collection_accounting"]["censored_rows"], 2)

    def test_signal_discards_partial_rows_saves_and_resumes_without_budget_refund(self):
        with tempfile.TemporaryDirectory() as temporary:
            directory = Path(temporary)
            config, updates, checkpoints, payload = self.fixture(directory, interrupted=True)
            self.assertFalse(updates)
            self.assertEqual(payload["state"]["games"], 0)
            self.assertEqual(payload["state"]["next_engine_seed"], config.engine_seed + 2)
            before = json.loads((directory / "budget.json").read_text())
            self.assertEqual((before["discarded_episodes"], before["discarded_decisions"]), (2, 2))
            _, resumed_updates, _, resumed = self.fixture(directory, resume=directory / "latest.soicp")
            after = json.loads((directory / "budget.json").read_text())
            self.assertEqual(len(resumed_updates), 1)
            self.assertEqual(resumed["state"]["next_engine_seed"], config.engine_seed + 4)
            self.assertGreaterEqual(after["charged_seconds"], before["charged_seconds"])
            self.assertEqual(after["campaign_id"], before["campaign_id"])

    def test_resume_at_reserved_seed_boundary_rejects_before_host_creation(self):
        with tempfile.TemporaryDirectory() as temporary:
            directory = Path(temporary)
            _, _, _, payload = self.fixture(directory)
            state = payload["state"]
            state["next_engine_seed"] = (1 << 63) - 1
            with CampaignBudget(directory / "budget.json", limit_seconds=30) as budget:
                save_checkpoint_atomic(directory / "latest.soicp", state, identity=payload["identity"],
                                       budget=budget, include_cuda_rng=False)
            host_calls = []
            with self.assertRaisesRegex(RuntimeError, "reserved held-out"):
                self.fixture(directory, resume=directory / "latest.soicp", host_calls=host_calls)
            self.assertEqual(host_calls, [])


class LaunchOrchestrationTests(unittest.TestCase):
    def invoke(self, directory, *, steps=1, stop_reason=None, resume=False):
        config = train.TrainConfig()
        pinned = {"fixture": True}
        for name, content in (("config.json", asdict(config)), ("identity.json", pinned),
                              ("status.json", {"generations": 1, "optimizer_steps": steps}),
                              ("supervisor.json", {"stop_reason": stop_reason}),
                              ("prepared.json", {"post_training_pairs": 2048})):
            (directory / name).write_text(json.dumps(content))
        checkpoint = directory / "latest.soicp"
        checkpoint.write_bytes(b"fixed fixture checkpoint")
        argv = ["launch.py", "--campaign", str(directory), "--seconds", "30", "--games", "100"]
        if resume:
            argv.append("--resume")
        with mock.patch.object(sys, "argv", argv), mock.patch.object(launch, "identity", return_value=pinned), \
                mock.patch.object(launch, "catalog", return_value=CATALOG), \
                mock.patch.object(launch, "verify_bundle") as verify, \
                mock.patch.object(launch, "supervise", return_value=0) as supervise, \
                mock.patch.object(launch, "run_evaluation", return_value={"summary": {"stronger_than_current": False}}) as evaluate, \
                contextlib.redirect_stdout(io.StringIO()):
            error = None
            try:
                launch.main()
            except RuntimeError as caught:
                error = caught
        return error, verify, supervise, evaluate

    def test_automatic_evaluation_uses_current_checkpoint_hash_and_resume_signature(self):
        with tempfile.TemporaryDirectory() as temporary:
            directory = Path(temporary)
            error, _, supervise, evaluate = self.invoke(directory, resume=True)
            self.assertIsNone(error)
            command = supervise.call_args.args[0]
            self.assertIn("--resume", command)
            self.assertIn(str(directory / "latest.soicp"), command)
            arguments = evaluate.call_args.kwargs
            digest = hashlib.sha256(b"fixed fixture checkpoint").hexdigest()[:16]
            self.assertEqual(arguments["output"].name, f"post-training-evaluation-{digest}.json")
            self.assertEqual(arguments["pairs"], 2048)
            self.assertEqual(arguments["bundle_directory"], directory / "incumbent")

    def test_no_update_or_user_stop_never_starts_evaluation(self):
        for steps, reason in ((0, None), (1, "requested_stop"), (1, "heartbeat_timeout")):
            with self.subTest(steps=steps, reason=reason), tempfile.TemporaryDirectory() as temporary:
                error, _, _, evaluate = self.invoke(Path(temporary), steps=steps, stop_reason=reason)
                self.assertFalse(evaluate.called)
                self.assertEqual(error is None, reason == "requested_stop")


if __name__ == "__main__":
    unittest.main()
