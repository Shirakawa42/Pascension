"""CPU-only frozen-checkpoint selection and identity checks; no match execution."""
import copy
from dataclasses import asdict
import json
from pathlib import Path
import tempfile
from unittest import mock
import unittest

import torch

import evaluate_checkpoints as evaluator
from learning_model import LearningPolicy, PolicyConfig
from train_campaign import TrainConfig


class FrozenSelectionTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        torch.set_num_threads(1)

    def setUp(self):
        self.directory = tempfile.TemporaryDirectory()
        self.addCleanup(self.directory.cleanup)
        self.identities, self.payloads = {}, {}
        self.catalog = {"observationSchema": "shards-observation-v2", "cards": ["fixture"]}
        self.identity_mock = mock.patch.object(evaluator, "identity", side_effect=self.current_identity).start()
        self.load_mock = mock.patch.object(evaluator, "load_checkpoint", side_effect=self.read_checkpoint).start()
        self.addCleanup(mock.patch.stopall)

    def current_identity(self, config):
        return copy.deepcopy(self.identities[config.width]), self.catalog.copy()

    def read_checkpoint(self, path, *, expected_identity, budget):
        self.assertIsNone(budget, "Frozen evaluation must not open or charge a budget")
        payload = self.payloads[str(path)]
        self.assertEqual(expected_identity, self.identities[payload["state"]["configuration"]["width"]])
        return copy.deepcopy(payload)

    def make_run(self, width=128):
        run = Path(self.directory.name)/str(width)
        run.mkdir()
        config = TrainConfig(width=width)
        pinned = {"schema": "shards-real-selfplay-v1", "configuration": asdict(config),
                  "training_source_sha256": "current-source", "host_binary_sha256": "current-host"}
        self.identities[width] = pinned
        (run/"identity.json").write_text(json.dumps(pinned))
        path = (run/"latest.soicp").resolve()
        path.write_bytes(b"mocked checksum-verified checkpoint")
        policy = LearningPolicy(PolicyConfig(width=width))
        states = []
        for marker in (1., 2., 3.):
            state = copy.deepcopy(policy.state_dict())
            state["core.value.bias"].fill_(marker)
            states.append(state)
        payload = {"file_sha256": "exact-loaded-payload-hash", "created_wall": 1234.,
                   "budget": {"campaign_id": "one-campaign", "charged_seconds": 123.},
                   "state": {"configuration": asdict(config), "policy_config": policy.config.to_dict(),
                             "generations": 8,
                             "learner": {"policy": states[0], "policy_config": policy.config.to_dict(), "updates": 42},
                             "champion": {"policy": states[1], "version": 4},
                             "initial_policy": states[2]}}
        self.payloads[str(path)] = payload
        return path

    def test_roles_select_exact_checkpoint_policy_and_version(self):
        path = self.make_run()
        for role, marker, version in (("learner", 1., 8), ("champion", 2., 4), ("initial", 3., 0)):
            with self.subTest(role=role):
                selected = evaluator.load_selection(path, role)
                self.assertEqual(float(selected.weights["core.value.bias"][0]), marker)
                self.assertEqual(selected.metadata["version"], version)
                self.assertEqual(selected.metadata["role"], role)
                self.assertEqual(selected.metadata["checkpoint_payload_sha256"], "exact-loaded-payload-hash")
                self.assertEqual(selected.metadata["checkpoint_training_seconds"], 123.)
        self.assertEqual(self.load_mock.call_count, 3)
        self.assertFalse(torch.cuda.is_initialized())

    def test_two_supported_widths_load_without_configuration_conflation(self):
        a, b = self.make_run(128), self.make_run(256)
        selection_a = evaluator.load_selection(a)
        selection_b = evaluator.load_selection(b, "champion")
        self.assertEqual((selection_a.config.width, selection_b.config.width), (128, 256))
        self.assertEqual(selection_a.catalog, selection_b.catalog)
        model_a = evaluator.materialize_policy(selection_a)
        model_b = evaluator.materialize_policy(selection_b)
        self.assertEqual(model_a.core.trunk[0].out_features, 128)
        self.assertEqual(model_b.core.trunk[0].out_features, 256)
        self.assertTrue(all(not parameter.requires_grad for parameter in model_a.parameters()))
        self.assertFalse(model_a.training)
        self.assertFalse(torch.cuda.is_initialized())

    def test_current_source_mismatch_rejects_before_checkpoint_load(self):
        path = self.make_run()
        self.identities[128]["training_source_sha256"] = "changed-source"
        with self.assertRaisesRegex(ValueError, "training_source_sha256"):
            evaluator.load_selection(path)
        self.load_mock.assert_not_called()

    def test_checkpoint_configuration_mismatch_is_rejected(self):
        path = self.make_run()
        self.payloads[str(path)]["state"]["configuration"]["epochs"] += 1
        with self.assertRaisesRegex(ValueError, "training configuration disagree"):
            evaluator.load_selection(path)

    def test_invalid_champion_version_is_rejected(self):
        path = self.make_run()
        self.payloads[str(path)]["state"]["champion"]["version"] = 9
        with self.assertRaisesRegex(ValueError, "policy version"):
            evaluator.load_selection(path, "champion")

    def test_materialized_policy_owns_weights_and_has_stable_hash(self):
        selected = evaluator.load_selection(self.make_run())
        policy = evaluator.materialize_policy(selected)
        digest = evaluator.policy_hash(policy)
        selected.weights["core.value.bias"].fill_(999)
        self.assertEqual(evaluator.policy_hash(policy), digest)
        with torch.no_grad():
            policy.core.value.bias.fill_(4)
        self.assertNotEqual(evaluator.policy_hash(policy), digest)


if __name__ == "__main__":
    unittest.main()
