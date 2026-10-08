"""CPU-only fork tests; all publications use temporary fixtures, never live runs.

PYTHONDONTWRITEBYTECODE=1 OMP_NUM_THREADS=1 MKL_NUM_THREADS=1 \
PYTHONPATH=Tools/TrainingPreflight:Tools/TrainingPreflight/experiments \
  /home/lva/.venvs/shards-preflight/bin/python -m unittest test_fork_reuse_checkpoint -v
"""
import copy
from dataclasses import asdict
import fcntl
import hashlib
import io
import json
import os
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

import torch

import campaign_state as persistence
from learning_model import LearningPolicy, PolicyConfig, PPOConfig, PPOLearner
from model import sample_actions
from train_campaign import TrainConfig
from test_learning_model import fixture
import fork_reuse_checkpoint as fork


class ReuseForkTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        torch.set_num_threads(1)
        if torch.cuda.is_initialized():
            raise RuntimeError("Run fork tests in a fresh CPU-only process")
        cls.cuda_guard = patch.object(torch.cuda, "_lazy_init", side_effect=AssertionError("CUDA initialization forbidden"))
        cls.cuda_guard.start()
        with torch.random.fork_rng(devices=[]):
            torch.manual_seed(39126)
            model = LearningPolicy(PolicyConfig(width=128))
            with torch.no_grad():
                for parameter in model.parameters():
                    parameter.add_(.001)
            optimizer_states = {index: {"step": torch.tensor(29.),
                "exp_avg": torch.full_like(parameter, .01), "exp_avg_sq": torch.full_like(parameter, .001)}
                for index, parameter in enumerate(model.parameters())}
            optimizer = {"state": optimizer_states, "param_groups": [{"params": list(optimizer_states),
                "lr": .0003, "betas": (.9, .999), "eps": 1e-8, "weight_decay": .01,
                "amsgrad": False, "maximize": False, "foreach": None, "capturable": False,
                "differentiable": False, "fused": False}]}
            cls.state = {"configuration": asdict(TrainConfig()), "policy_config": model.config.to_dict(),
                "generations": 12, "decisions": 900, "games": 8, "optimizer_passes": 2700,
                "next_engine_seed": 1152921504606847999, "eval_index": 2, "reason": "saved_boundary",
                "learner": {"format": PPOLearner.FORMAT, "policy_config": model.config.to_dict(),
                    "ppo_config": asdict(PPOConfig()), "policy": copy.deepcopy(model.state_dict()),
                    "optimizer": optimizer, "updates": 29, "rejected": 3,
                    "torch_rng_cpu": torch.get_rng_state().clone(), "torch_rng_device": torch.arange(32, dtype=torch.uint8)},
                "champion": {"version": 10, "policy": copy.deepcopy(model.state_dict())},
                "initial_policy": copy.deepcopy(model.state_dict()),
                "archive": [{"version": 0, "policy": copy.deepcopy(model.state_dict())},
                            {"version": 8, "policy": copy.deepcopy(model.state_dict())}]}

    @classmethod
    def tearDownClass(cls):
        cls.cuda_guard.stop()

    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.root = Path(self.temp.name)
        self.source, self.destination, self.ledger = self.root/"old.soicp", self.root/"fork.soicp", self.root/"budget.json"
        self.identity = {"schema": "shards-real-selfplay-v3", "observation_schema": "shards-observation-v3",
            "training_source_sha256": fork.PINNED_RUNTIME_SHA256, "rules_sha256": "1"*64,
            "host_binary_sha256": "2"*64, "catalog_sha256": "3"*64,
            "base_training_source_sha256": "03454a86354ccd5c320604511df055e3043a9c56f298c2c2087fc3972070cd42",
            "configuration": copy.deepcopy(self.state["configuration"]),
            "execution": {"matmul_precision": "ieee", "contiguous_store": True,
                          "fast_validation": True, "queued_actors": False},
            "new_observation_slots": [317, 318, 319, 509, 510, 511, 701]}
        self.catalog = {"observationSchema": "shards-observation-v3", "cards": ["fixture"]}
        def derive(config):
            return {**copy.deepcopy(self.identity), "configuration": asdict(config)}, copy.deepcopy(self.catalog)
        self.derive = derive
        self.runtime = patch.object(fork.variant_runtime, "variant_identity", side_effect=derive)
        self.runtime.start()
        with persistence.CampaignBudget(self.ledger) as budget:
            budget.record_collection(8, 8, 0, 900, 0)
            persistence.save_checkpoint_atomic(self.source, copy.deepcopy(self.state),
                identity=self.identity, budget=budget, include_cuda_rng=False)
        payload = persistence.load_checkpoint(self.source, expected_identity=self.identity)
        payload.pop("file_sha256")
        payload["migration"] = {"schema": "prior-migration", "source_file_sha256": "7"*64, "slots": self.identity["new_observation_slots"]}
        self.write_payload(payload)
        self.source_bytes, self.ledger_bytes = self.source.read_bytes(), self.ledger.read_bytes()

    def tearDown(self):
        self.runtime.stop()
        self.temp.cleanup()

    def write_payload(self, payload):
        output = io.BytesIO()
        torch.save(payload, output)
        content = output.getvalue()
        self.source.write_bytes(persistence._HEADER.pack(persistence._MAGIC, len(content), hashlib.sha256(content).digest())+content)

    def prepare(self, identity=None):
        return fork.prepare_reuse_fork(self.source, source_identity=self.identity if identity is None else identity)

    def publish(self):
        return fork.fork_reuse_checkpoint(self.source, self.destination, ledger_path=self.ledger, source_identity=self.identity)

    def test_exact_trained_state_rng_budget_seed_and_prior_provenance_preserved(self):
        before = persistence.load_checkpoint(self.source, expected_identity=self.identity)
        after, report = self.prepare()
        self.assertEqual(after["identity"]["configuration"]["epochs"], 6)
        self.assertEqual(after["state"]["configuration"]["epochs"], 6)
        self.assertEqual(fork._protected_payload_digest(before), fork._protected_payload_digest(after))
        for key in set(before)-{"state", "identity", "identity_sha256", "file_sha256"}:
            self.assertEqual(fork._tree_digest(before[key]), fork._tree_digest(after[key]), key)
        for key in set(before["state"])-{"configuration"}:
            self.assertEqual(fork._tree_digest(before["state"][key]), fork._tree_digest(after["state"][key]), key)
        self.assertEqual(after["state"]["learner"]["optimizer"]["state"][0]["step"].item(), 29)
        self.assertTrue(bool(torch.count_nonzero(after["state"]["learner"]["optimizer"]["state"][0]["exp_avg"][:, [317, 318, 319, 509, 510, 511, 701]])))
        self.assertFalse(report["training_started"])
        self.assertEqual(self.source.read_bytes(), self.source_bytes)
        self.assertEqual(self.ledger.read_bytes(), self.ledger_bytes)
        self.assertFalse(self.destination.exists())

    def test_exact_cpu_logits_values_and_samples_on_nonzero_new_features(self):
        before = persistence.load_checkpoint(self.source, expected_identity=self.identity)
        after, _ = self.prepare()
        with torch.random.fork_rng(devices=[]):
            a, b = LearningPolicy(), LearningPolicy()
            a.load_state_dict(before["state"]["learner"]["policy"])
            b.load_state_dict(after["state"]["learner"]["policy"])
            obs, candidates, mask = fixture(4)
            obs[:, self.identity["new_observation_slots"]] = .7
            with torch.no_grad():
                x, y = a(obs, candidates, mask), b(obs, candidates, mask)
                for left, right in zip(x, y):
                    self.assertTrue(torch.equal(left, right))
                torch.manual_seed(981); first = sample_actions(*x)
                torch.manual_seed(981); second = sample_actions(*y)
                self.assertTrue(torch.equal(first, second))

    def test_cpu_rng_not_recaptured_and_cuda_forbidden(self):
        before = torch.get_rng_state().clone()
        with patch.object(persistence, "capture_rng", side_effect=AssertionError("Do not recapture saved RNG")):
            self.prepare()
        self.assertTrue(torch.equal(before, torch.get_rng_state()))
        self.assertFalse(torch.cuda.is_initialized())

    def test_bad_source_identity_width_lr_other_config_changes_rejected(self):
        for key, value in (("width", 256), ("learning_rate", .001), ("entropy", .1), ("workers", 4)):
            wrong = copy.deepcopy(self.identity)
            wrong["configuration"][key] = value
            with self.subTest(key=key), self.assertRaises(persistence.CheckpointError):
                self.prepare(wrong)
        for key, value in (("training_source_sha256", "8"*64), ("schema", "shards-real-selfplay-v2"),
                           ("observation_schema", "shards-observation-v2")):
            with self.subTest(key=key), self.assertRaises(fork.ForkError):
                self.prepare({**self.identity, key: value})

    def test_wrong_epoch_type_or_value_and_arbitrary_overrides_rejected(self):
        for epochs in (1, 4, 6, True, "3"):
            wrong = copy.deepcopy(self.identity)
            wrong["configuration"]["epochs"] = epochs
            with self.subTest(epochs=epochs), self.assertRaises(fork.ForkError):
                self.prepare(wrong)
        with self.assertRaises(TypeError):
            fork.prepare_reuse_fork(self.source, source_identity=self.identity, learning_rate=.001)

    def test_runtime_drift_or_unexpected_target_delta_rejected(self):
        for field, value in (("width", 256), ("learning_rate", .001), ("entropy", .1)):
            def drift(config):
                identity, catalog = self.derive(config)
                if config.epochs == 6:
                    identity["configuration"][field] = value
                return identity, catalog
            with patch.object(fork.variant_runtime, "variant_identity", side_effect=drift):
                with self.subTest(field=field), self.assertRaisesRegex(fork.ForkError, "more than"):
                    self.prepare()
        with patch.object(fork.variant_runtime, "variant_identity", return_value=({**self.identity, "host_binary_sha256": "9"*64}, self.catalog)):
            with self.assertRaisesRegex(fork.ForkError, "current frozen"):
                self.prepare()

    def test_state_configuration_mismatch_and_existing_fork_rejected(self):
        for change in ("state", "existing"):
            self.source.write_bytes(self.source_bytes)
            payload = persistence.load_checkpoint(self.source, expected_identity=self.identity)
            payload.pop("file_sha256")
            if change == "state":
                payload["state"]["configuration"]["learning_rate"] = .002
            else:
                payload["configuration_fork"] = {"old": True}
            self.write_payload(payload)
            with self.subTest(change=change), self.assertRaises(fork.ForkError):
                self.prepare()

    def test_publish_new_strict_target_reload_keeps_original_and_ledger(self):
        report = self.publish()
        restored = persistence.load_checkpoint(self.destination, expected_identity=report["target_identity"])
        self.assertEqual(restored["state"]["configuration"]["epochs"], 6)
        self.assertTrue(report["strict_target_reload_passed"])
        self.assertEqual(self.source.read_bytes(), self.source_bytes)
        self.assertEqual(self.ledger.read_bytes(), self.ledger_bytes)
        with self.assertRaises(persistence.CheckpointError):
            persistence.load_checkpoint(self.destination, expected_identity=self.identity)

    def test_active_or_locked_ledger_never_recovers_or_publishes(self):
        fd = os.open(str(self.ledger)+".lock", os.O_RDWR)
        try:
            fcntl.flock(fd, fcntl.LOCK_EX | fcntl.LOCK_NB)
            with self.assertRaises(persistence.CampaignLocked):
                self.publish()
        finally:
            os.close(fd)
        data = json.loads(self.ledger.read_text())
        data["active"] = {"session_id": "still-active"}
        self.ledger.write_text(json.dumps(data))
        before = self.ledger.read_bytes()
        with self.assertRaises(persistence.CheckpointError):
            self.publish()
        self.assertEqual(self.ledger.read_bytes(), before)
        self.assertFalse(self.destination.exists())

    def test_existing_destination_corruption_wrong_campaign_no_overwrite(self):
        self.destination.write_bytes(b"preserve")
        with self.assertRaises(persistence.CheckpointError):
            self.publish()
        self.assertEqual(self.destination.read_bytes(), b"preserve")
        self.destination.unlink()
        content = bytearray(self.source_bytes); content[-1] ^= 1
        self.source.write_bytes(content)
        with self.assertRaises(persistence.CheckpointError):
            self.publish()
        self.source.write_bytes(self.source_bytes)
        data = json.loads(self.ledger_bytes); data["campaign_id"] = "wrong-campaign"
        self.ledger.write_text(json.dumps(data))
        with self.assertRaises(fork.ForkError):
            self.publish()
        self.assertFalse(self.destination.exists())


if __name__ == "__main__":
    unittest.main()
