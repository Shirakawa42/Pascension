"""Subset/padding/probability checks; no game training or strength evaluation."""
import os
import unittest

import numpy as np
import torch

from adaptive_actor import AdaptiveLearningActor
from learning_model import InvalidLearningBatch, LearningPolicy
from test_learning_model import fixture, host_fixture


class AdaptiveActorTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        torch.set_num_threads(1)

    def _check(self, device, graph):
        torch.manual_seed(471)
        policy = LearningPolicy().to(device)
        actor = AdaptiveLearningActor(policy, max_batch=64, graph=graph)
        data = fixture(64, device)
        host = host_fixture(*data)
        host_before = host.upload.clone()
        packets = []
        for count, expected_bucket in ((1, 32), (17, 32), (32, 32), (33, 64), (64, 64)):
            lanes = np.random.default_rng(count).permutation(64)[:count]
            actions, packet = actor.act_subset(host, lanes)
            self.assertEqual(actor.bucket_batch, expected_bucket)
            self.assertEqual(actor.valid_rows, count)
            self.assertEqual(actor.packet.shape, (count, 3))
            self.assertEqual(actor.obs.shape, (count, 2048))
            np.testing.assert_array_equal(actor.original_lanes, lanes)
            for actual, source in zip((actor.obs, actor.candidates, actor.mask), data):
                torch.testing.assert_close(actual, source[torch.from_numpy(lanes).to(device)].to(actual.dtype), rtol=0, atol=0)
            selected = torch.from_numpy(lanes).to(device)
            with torch.no_grad():
                logits, values = policy(data[0][selected], data[1][selected], data[2][selected])
                logp = logits.log_softmax(-1).gather(1, torch.from_numpy(actions).to(device)[:, None]).squeeze(1)
            np.testing.assert_allclose(packet[:, 1], logp.cpu().numpy(), rtol=2e-5, atol=2e-5)
            np.testing.assert_allclose(packet[:, 2], values.cpu().numpy(), rtol=2e-5, atol=2e-5)
            self.assertTrue(data[2][selected].gather(1, torch.from_numpy(actions).to(device)[:, None]).all())
            self.assertTrue(torch.equal(host.upload, host_before))
            packets.append((packet, packet.copy()))
        for original, preserved in packets:
            np.testing.assert_array_equal(original, preserved)
        pointers = {size: [p.data_ptr() for p in item.policy.parameters()] for size, item in actor.actors.items()}
        with torch.no_grad():
            policy.core.query.bias.add_(.2)
            policy.core.value.bias.fill_(.3)
        actor.refresh(policy, version=9)
        self.assertEqual(actor.version, 9)
        for size, item in actor.actors.items():
            self.assertEqual(item.version, 9)
            self.assertEqual(pointers[size], [p.data_ptr() for p in item.policy.parameters()])
        lanes = np.array([63, 5, 21])
        actions, packet = actor.act_subset(host, lanes)
        with torch.no_grad():
            logits, values = policy(data[0][lanes], data[1][lanes], data[2][lanes])
            logp = logits.log_softmax(-1).gather(1, torch.from_numpy(actions).to(device)[:, None]).squeeze(1)
        np.testing.assert_allclose(packet[:, 1], logp.cpu().numpy(), rtol=2e-5, atol=2e-5)
        np.testing.assert_allclose(packet[:, 2], values.cpu().numpy(), rtol=2e-5, atol=2e-5)

    def test_cpu_subsets_padding_refresh_and_behavior_parity(self):
        self._check("cpu", False)

    def test_invalid_lanes_rejected_before_packing(self):
        actor = AdaptiveLearningActor(LearningPolicy(), max_batch=64, graph=False)
        host = host_fixture(*fixture(64))
        for lanes in ([], [1, 1], [-1], [64], [1.5], [True], list(range(65))):
            with self.assertRaises(InvalidLearningBatch):
                actor.act_subset(host, lanes)

    def test_unselected_rows_are_not_consumed(self):
        actor = AdaptiveLearningActor(LearningPolicy(), max_batch=64, graph=False)
        host = host_fixture(*fixture(64))
        host.upload[2048:4096] = float("nan")
        host.upload[-64*64+64:-64*64+128] = 0  # Unselected lane1 has no legal action.
        actions, packet = actor.act_subset(host, np.array([0, 2, 63]))
        self.assertEqual(len(actions), 3)
        self.assertTrue(np.isfinite(packet).all())

    @unittest.skipUnless(os.environ.get("RUN_CUDA_LEARNING_TESTS") == "1", "Requires allocated CUDA window")
    def test_cuda_graph_subsets_padding_refresh_and_behavior_parity(self):
        self._check("cuda", True)


if __name__ == "__main__":
    unittest.main()
