"""CPU-only migration tests with isolated synthetic approval/ledger fixtures.

PYTHONDONTWRITEBYTECODE=1 OMP_NUM_THREADS=1 MKL_NUM_THREADS=1 \
PYTHONPATH=Tools/TrainingPreflight:Tools/TrainingPreflight/experiments \
  /home/lva/.venvs/shards-preflight/bin/python -m unittest test_migrate_checkpoint -v

No real manifest is approved, live checkpoint written, campaign ledger touched,
CUDA context initialized, or outcome-training session started by these tests.
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

import numpy as np
import torch

import campaign_state as persistence
from learning_model import LearningPolicy, PolicyConfig, PPOConfig, PPOLearner
from model import sample_actions
import migrate_checkpoint as migration
from test_learning_model import fixture
from train_campaign import TrainConfig


class Clock:
    def __init__(self):
        self.mono, self.wall = 100., 1700000000.

    def monotonic(self):
        return self.mono

    def time(self):
        return self.wall

    def boot_id(self):
        return "isolated-migration-test"

    def advance(self, seconds):
        self.mono += seconds
        self.wall += seconds


def encode_payload(payload):
    stream = io.BytesIO()
    torch.save(payload, stream)
    content = stream.getvalue()
    return persistence._HEADER.pack(persistence._MAGIC, len(content), hashlib.sha256(content).digest())+content


class MigrationTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        torch.set_num_threads(1)
        if torch.cuda.is_initialized():
            raise RuntimeError("Migration tests require an independent CPU-only process")
        cls.cuda_guard = patch.object(torch.cuda, "_lazy_init", side_effect=AssertionError("CUDA initialization forbidden in migration tests"))
        cls.cuda_guard.start()
        with torch.random.fork_rng(devices=[]):
            torch.manual_seed(260926)
            policy = LearningPolicy(PolicyConfig(width=128))
            initial = copy.deepcopy(policy.state_dict())
            obs, candidates, mask = fixture(4)
            obs[:, list(migration.SLOTS)] = 0
            # Explicit saved optimizer fixture: CPU torch.optim.step can still
            # query CUDA capture state on a CUDA-equipped machine. No optimizer
            # instance or training update is needed to test migration.
            states = {}
            with torch.no_grad():
                for index, (name, parameter) in enumerate(policy.named_parameters()):
                    parameter.add_(.001)
                    first, second = torch.full_like(parameter, .01), torch.full_like(parameter, .001)
                    if name == migration.WEIGHT_KEY:
                        first[:, list(migration.SLOTS)] = 0
                        second[:, list(migration.SLOTS)] = 0
                    states[index] = {"step": torch.tensor(1.), "exp_avg": first, "exp_avg_sq": second}
            optimizer = {"state": states, "param_groups": [{"params": list(states), "lr": .0003,
                "betas": (.9, .999), "eps": 1e-8, "weight_decay": .01, "amsgrad": False,
                "maximize": False, "foreach": None, "capturable": False, "differentiable": False, "fused": False}]}
            learner_state = {"format": PPOLearner.FORMAT, "policy_config": policy.config.to_dict(),
                "ppo_config": asdict(PPOConfig()), "policy": copy.deepcopy(policy.state_dict()),
                "optimizer": optimizer, "updates": 1, "rejected": 2,
                "torch_rng_cpu": torch.get_rng_state().clone(), "torch_rng_device": None}
            cls.state = {"learner": learner_state, "generations": 3, "decisions": 100,
                "games": 3, "optimizer_passes": 4, "next_engine_seed": 1152921504606847000,
                "eval_index": 2, "policy_config": policy.config.to_dict(),
                "configuration": asdict(TrainConfig()), "reason": "fixture-boundary",
                "champion": {"version": 3, "policy": copy.deepcopy(policy.state_dict())},
                "initial_policy": initial,
                "archive": [{"version": 0, "policy": copy.deepcopy(initial)},
                            {"version": 3, "policy": copy.deepcopy(policy.state_dict())}],
                "extra_preserved_metadata": {"opaque": [17, "marker", b"bytes"], "counter": 37}}
            # Device RNG bytes are opaque to CPU migration and must survive.
            cls.state["learner"]["torch_rng_device"] = torch.arange(32, dtype=torch.uint8)
            cls.obs, cls.candidates, cls.mask = obs, candidates, mask

    @classmethod
    def tearDownClass(cls):
        cls.cuda_guard.stop()

    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.root = Path(self.temporary.name)
        self.source, self.destination = self.root/"old.soicp", self.root/"new.soicp"
        self.ledger = self.root/"budget.json"
        self.source_catalog = {"ObsDim": 2048, "MaxActions": 64, "ActionDim": 32,
            "observationSchema": migration.SOURCE_OBSERVATION,
            "cards": [f"fixture_card_{index:03d}" for index in range(189)]}
        self.target_catalog = copy.deepcopy(self.source_catalog)
        self.target_catalog.update(observationSchema=migration.TARGET_OBSERVATION,
            extraObservationFeatures=migration.expected_features(), maxCardDefinitions=189)
        self.source_identity = {"schema": migration.SOURCE_SCHEMA,
            "observation_schema": migration.SOURCE_OBSERVATION, "rules_sha256": "1"*64,
            "host_binary_sha256": "2"*64, "catalog_sha256": migration.catalog_sha256(self.source_catalog),
            "training_source_sha256": migration.SOURCE_TRAINING_SHA256,
            "configuration": copy.deepcopy(self.state["configuration"])}
        self.target_identity = {**copy.deepcopy(self.source_identity), "schema": migration.TARGET_SCHEMA,
            "observation_schema": migration.TARGET_OBSERVATION, "host_binary_sha256": "3"*64,
            "catalog_sha256": migration.catalog_sha256(self.target_catalog), "training_source_sha256": "4"*64,
            "base_training_source_sha256": migration.SOURCE_TRAINING_SHA256,
            "new_observation_slots": list(migration.SLOTS),
            "execution": {"matmul_precision": "ieee", "contiguous_store": True,
                          "fast_validation": True, "queued_actors": False}}
        self.manifest = self.make_manifest()
        self.approval = patch.object(migration, "APPROVED_MANIFEST_SHA256", frozenset({migration.canonical_sha256(self.manifest)}))
        self.approval.start()
        clock = Clock()
        with persistence.CampaignBudget(self.ledger, clock=clock) as budget:
            with budget.start_session("isolated-fixture", 30, 0) as session:
                clock.advance(7)
                session.heartbeat()
                budget.record_collection(4, 3, 1, 100, 11)
                persistence.save_checkpoint_atomic(self.source, copy.deepcopy(self.state),
                    identity=self.source_identity, budget=budget, include_cuda_rng=False)
                clock.advance(2)
        self.source_bytes, self.ledger_bytes = self.source.read_bytes(), self.ledger.read_bytes()

    def tearDown(self):
        self.approval.stop()
        self.temporary.cleanup()

    def make_manifest(self, source=None, target=None):
        source = self.source_identity if source is None else source
        target = self.target_identity if target is None else target
        return {"schema": migration.APPROVAL_SCHEMA,
            "source_identity_sha256": migration.canonical_sha256(source),
            "target_identity_sha256": migration.canonical_sha256(target),
            "slots": list(migration.SLOTS), "weight_key": migration.WEIGHT_KEY,
            "allowed_identity_changes": sorted(key for key in set(source)|set(target) if source.get(key) != target.get(key))}

    def arguments(self):
        return {"source_identity": self.source_identity, "target_identity": self.target_identity,
                "slots": list(migration.SLOTS), "source_catalog": self.source_catalog,
                "target_catalog": self.target_catalog, "reviewed_manifest": self.manifest}

    def preview(self, **changes):
        return migration.prepare_migration(self.source, **{**self.arguments(), **changes})

    def publish(self, **changes):
        return migration.migrate_checkpoint(self.source, self.destination, ledger_path=self.ledger,
                                            **{**self.arguments(), **changes})

    def assert_tree_equal(self, a, b):
        self.assertEqual(migration._tree_digest(a), migration._tree_digest(b))

    def mutate_source_state(self, callback):
        payload = persistence.load_checkpoint(self.source, expected_identity=self.source_identity)
        payload.pop("file_sha256")
        callback(payload["state"])
        self.source.write_bytes(encode_payload(payload))

    def test_only_approved_columns_change_in_all_five_policies(self):
        original = persistence.load_checkpoint(self.source, expected_identity=self.source_identity)
        migrated, report = self.preview()
        self.assertEqual(len(report["changed_policy_paths"]), 5)
        for path in report["changed_policy_paths"]:
            before, after = original["state"], migrated["state"]
            for key in path:
                before, after = before[key], after[key]
            self.assertTrue(bool(torch.count_nonzero(before[:, list(migration.SLOTS)])))
            self.assertFalse(bool(torch.count_nonzero(after[:, list(migration.SLOTS)])))
            expected = before.clone()
            expected[:, list(migration.SLOTS)] = 0
            self.assertTrue(torch.equal(expected, after))
        for key in ("optimizer", "updates", "rejected", "torch_rng_cpu", "torch_rng_device", "ppo_config"):
            self.assert_tree_equal(original["state"]["learner"][key], migrated["state"]["learner"][key])
        for key in ("rng", "budget", "created_wall", "recovery", "boundary"):
            self.assert_tree_equal(original[key], migrated[key])
        self.assertEqual(self.source.read_bytes(), self.source_bytes)
        self.assertEqual(self.ledger.read_bytes(), self.ledger_bytes)
        self.assertFalse(self.destination.exists())

    def test_cpu_fp32_logits_values_probabilities_and_sampled_actions_exact(self):
        migrated, report = self.preview()
        original = persistence.load_checkpoint(self.source, expected_identity=self.source_identity)
        for path in report["changed_policy_paths"]:
            old_weights, new_weights = original["state"], migrated["state"]
            for key in path[:-1]:
                old_weights, new_weights = old_weights[key], new_weights[key]
            with torch.random.fork_rng(devices=[]):
                old, new = LearningPolicy(PolicyConfig(width=128)), LearningPolicy(PolicyConfig(width=128))
                old.load_state_dict(old_weights)
                new.load_state_dict(new_weights)
                new_obs = self.obs.clone()
                new_obs[:, list(migration.SLOTS)] = torch.arange(28).view(4, 7).float()/20
                with torch.no_grad():
                    a = old(self.obs, self.candidates, self.mask)
                    b = new(new_obs, self.candidates, self.mask)
                    for left, right in zip(a, b):
                        torch.testing.assert_close(left, right, rtol=0, atol=0)
                    torch.testing.assert_close(a[0].softmax(-1), b[0].softmax(-1), rtol=0, atol=0)
                    torch.manual_seed(20926)
                    expected = sample_actions(*a)
                    torch.manual_seed(20926)
                    actual = sample_actions(*b)
                    self.assertTrue(torch.equal(expected, actual))

    def test_caller_rng_not_recaptured_or_consumed(self):
        before = torch.get_rng_state().clone()
        with patch.object(persistence, "capture_rng", side_effect=AssertionError("RNG must not be recaptured")):
            self.preview()
        self.assertTrue(torch.equal(before, torch.get_rng_state()))
        self.assertFalse(torch.cuda.is_initialized())

    def test_atomic_new_copy_strict_target_reload_and_ledger_unchanged(self):
        report = self.publish()
        self.assertTrue(report["strict_target_reload_passed"])
        restored = persistence.load_checkpoint(self.destination, expected_identity=self.target_identity)
        self.assertEqual(restored["budget"]["charged_seconds"], 7)
        self.assertEqual(json.loads(self.ledger.read_text())["charged_seconds"], 9)
        self.assertEqual(self.source.read_bytes(), self.source_bytes)
        self.assertEqual(self.ledger.read_bytes(), self.ledger_bytes)
        self.assertEqual(restored["state"]["learner"]["optimizer"]["state"][0]["step"].item(), 1)
        with self.assertRaises(persistence.CheckpointError):
            persistence.load_checkpoint(self.destination, expected_identity=self.source_identity)
        self.assertFalse(list(self.root.glob("*.tmp")))

    def test_unknown_manifest_and_modified_target_are_rejected(self):
        with patch.object(migration, "APPROVED_MANIFEST_SHA256", frozenset()):
            with self.assertRaisesRegex(migration.MigrationError, "not been explicitly approved"):
                self.preview()
        changed = copy.deepcopy(self.target_identity)
        changed["execution"]["matmul_precision"] = "tf32"
        with self.assertRaisesRegex(migration.MigrationError, "reviewed manifest"):
            self.preview(target_identity=changed)

    def test_even_reviewed_manifest_cannot_change_rules_configuration_or_slots(self):
        for field, value in (("rules_sha256", "5"*64), ("configuration", {"width": 256}),
                             ("base_training_source_sha256", "6"*64), ("new_observation_slots", [1]*7)):
            with self.subTest(field=field):
                target = {**self.target_identity, field: value}
                manifest = self.make_manifest(target=target)
                with patch.object(migration, "APPROVED_MANIFEST_SHA256", frozenset({migration.canonical_sha256(manifest)})):
                    with self.assertRaises(migration.MigrationError):
                        self.preview(target_identity=target, reviewed_manifest=manifest)
        for slots in ([317]*7, [True]*7, list(migration.SLOTS)[::-1], [1, 2, 3, 4, 5, 6, 7]):
            with self.assertRaises(migration.MigrationError):
                self.preview(slots=slots)

    def test_catalog_reordering_or_extra_semantic_change_is_rejected(self):
        for field, value in (("cards", list(reversed(self.target_catalog["cards"]))),
                             ("MaxActions", 63), ("maxCardDefinitions", 190)):
            with self.subTest(field=field):
                changed = {**self.target_catalog, field: value}
                with self.assertRaises(migration.MigrationError):
                    self.preview(target_catalog=changed)

    def test_nonzero_adam_column_is_rejected_instead_of_reset(self):
        def corrupt(state):
            state["learner"]["optimizer"]["state"][0]["exp_avg"][2, migration.SLOTS[3]] = 1e-30
        self.mutate_source_state(corrupt)
        before = self.source.read_bytes()
        with self.assertRaisesRegex(migration.MigrationError, "nonzero Adam"):
            self.preview()
        self.assertEqual(self.source.read_bytes(), before)

    def test_optimizer_parameter_mapping_and_missing_state_are_rejected(self):
        original = self.source.read_bytes()
        mutations = [lambda state: state["learner"]["optimizer"]["param_groups"][0]["params"].reverse(),
                     lambda state: state["learner"]["optimizer"]["state"].pop(0)]
        for mutate in mutations:
            self.source.write_bytes(original)
            self.mutate_source_state(mutate)
            with self.assertRaises(migration.MigrationError):
                self.preview()

    def test_locked_or_active_ledger_is_rejected_without_recovery(self):
        fd = os.open(str(self.ledger)+".lock", os.O_RDWR)
        try:
            fcntl.flock(fd, fcntl.LOCK_EX | fcntl.LOCK_NB)
            with self.assertRaises(persistence.CampaignLocked):
                self.publish()
        finally:
            os.close(fd)
        ledger = json.loads(self.ledger.read_text())
        ledger["active"] = {"session_id": "unclean-still-active"}
        self.ledger.write_text(json.dumps(ledger))
        before = self.ledger.read_bytes()
        with self.assertRaisesRegex(migration.MigrationError, "Active or unclean"):
            self.publish()
        self.assertEqual(self.ledger.read_bytes(), before)
        self.assertFalse(self.destination.exists())

    def test_wrong_campaign_and_time_ahead_of_ledger_are_rejected(self):
        for field, value in (("campaign_id", "another-campaign"), ("charged_seconds", 0.)):
            ledger = json.loads(self.ledger_bytes)
            ledger[field] = value
            if field == "charged_seconds":
                ledger["sessions"] = []
            self.ledger.write_text(json.dumps(ledger))
            with self.subTest(field=field):
                with self.assertRaises(persistence.CheckpointError):
                    self.publish()
                self.assertFalse(self.destination.exists())

    def test_existing_destination_and_publish_failure_never_overwrite(self):
        self.destination.write_bytes(b"keep existing file")
        with self.assertRaisesRegex(migration.MigrationError, "already exists"):
            self.publish()
        self.assertEqual(self.destination.read_bytes(), b"keep existing file")
        self.destination.unlink()
        with patch.object(migration.os, "link", side_effect=OSError("injected atomic publication failure")):
            with self.assertRaises(OSError):
                self.publish()
        self.assertFalse(self.destination.exists())
        self.assertEqual(self.source.read_bytes(), self.source_bytes)
        self.assertEqual(self.ledger.read_bytes(), self.ledger_bytes)
        self.assertFalse(list(self.root.glob("*.tmp")))

    def test_corrupt_source_checksum_or_missing_lock_never_publishes(self):
        content = bytearray(self.source_bytes)
        content[-1] ^= 1
        self.source.write_bytes(content)
        with self.assertRaises(persistence.CheckpointError):
            self.publish()
        self.source.write_bytes(self.source_bytes)
        Path(str(self.ledger)+".lock").unlink()
        with self.assertRaisesRegex(migration.MigrationError, "lock file"):
            self.publish()
        self.assertFalse(self.destination.exists())


if __name__ == "__main__":
    unittest.main()
