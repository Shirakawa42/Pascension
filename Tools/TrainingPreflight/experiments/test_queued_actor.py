"""CPU parity first; CUDA tests require an exclusively allocated test window."""
import os
from pathlib import Path
import sys
import unittest

import numpy as np
import torch

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from adaptive_actor import AdaptiveLearningActor
from learning_model import LearningActor, LearningPolicy, LearningNumericalError
from test_learning_model import fixture, host_fixture
from queued_actor import enqueue_actor, finish_actor, enqueue_subset, finish_subset, paired_actions, collect_queued
from learning_rollout import collect_episodes, EpisodeStore
from test_learning_collection import ScriptedHost, Decision


class QueuedActorTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        torch.set_num_threads(1)

    def test_cpu_packet_equality_and_lifecycle_guards(self):
        actor = LearningActor(LearningPolicy(), 4, graph=False)
        host = host_fixture(*fixture(4))
        torch.manual_seed(31)
        expected = actor.act(host)
        torch.manual_seed(31)
        pending = enqueue_actor(actor, host)
        with self.assertRaises(RuntimeError):
            enqueue_actor(actor, host)
        actual = finish_actor(pending)
        for left, right in zip(expected, actual):
            np.testing.assert_array_equal(left, right)
        with self.assertRaises(RuntimeError):
            finish_actor(pending)
        preserved = actual[1].copy()
        finish_actor(enqueue_actor(actor, host))
        np.testing.assert_array_equal(actual[1], preserved)

    def test_cpu_subset_and_dual_policy_parity(self):
        torch.manual_seed(82)
        a, b = (AdaptiveLearningActor(LearningPolicy(), 64, graph=False) for _ in range(2))
        host = host_fixture(*fixture(64))
        host.batch = 64
        for left, right in ((np.arange(64), np.array([], dtype=np.int64)),
                            (np.arange(0, 64, 2), np.arange(1, 64, 2)),
                            (np.array([63, 5, 18]), np.array([1])),
                            (np.array([], dtype=np.int64), np.arange(64))):
            torch.manual_seed(987)
            expected = np.zeros(64, dtype=np.int64)
            if len(left):
                expected[left], _ = a.act_subset(host, left)
                raw = [getattr(a, field).clone() for field in ("obs", "candidates", "mask", "packet")]
            if len(right):
                expected[right], _ = b.act_subset(host, right)
            original = host.upload.clone()
            torch.manual_seed(987)
            actual = paired_actions(host, a, b, left, right)
            np.testing.assert_array_equal(expected, actual)
            self.assertTrue(torch.equal(original, host.upload))
            if len(left):
                for field, saved in zip(("obs", "candidates", "mask", "packet"), raw):
                    self.assertTrue(torch.equal(getattr(a, field), saved), field)
                np.testing.assert_array_equal(a.original_lanes, left)

    def test_invalid_packet_cannot_be_consumed(self):
        actor = LearningActor(LearningPolicy(), 2, graph=False)
        host = host_fixture(*fixture(2))
        pending = enqueue_actor(actor, host)
        actor.output_host[0, 0] = 64
        with self.assertRaises(LearningNumericalError):
            finish_actor(pending)
        with self.assertRaises(RuntimeError):
            enqueue_actor(actor, host)

    def test_subset_pending_and_overlap_guards(self):
        a, b = (AdaptiveLearningActor(LearningPolicy(), 32, graph=False) for _ in range(2))
        host = host_fixture(*fixture(32))
        host.batch = 32
        with self.assertRaises(ValueError):
            paired_actions(host, a, b, [1], [1])
        pending = enqueue_subset(a, host, [1, 3])
        with self.assertRaises(RuntimeError):
            enqueue_subset(a, host, [5])
        finish_subset(pending)

    def test_pending_bucket_is_checked_before_pinned_input_repacking(self):
        adaptive = AdaptiveLearningActor(LearningPolicy(), 32, graph=False)
        host = host_fixture(*fixture(32))
        bucket = adaptive.actors[32]
        # A direct queued bucket job also prevents its adaptive owner repacking.
        pending = enqueue_actor(bucket, host)
        original = bucket.upload.clone()
        with self.assertRaises(RuntimeError):
            enqueue_subset(adaptive, host, [3, 1])
        self.assertTrue(torch.equal(original, bucket.upload))
        finish_actor(pending)

    def test_complete_and_censored_collection_exactly_matches_baseline(self):
        scripts = [[Decision(0, 0), Decision(1, 0), Decision(0, 0)],
                   [Decision(1, 1)], [Decision(0, 0), Decision(1, 1)],
                   [Decision(1, 1), Decision(1, 1), Decision(0, 1), Decision(1, 1)]]

        def make_host():
            host = ScriptedHost(scripts, [(1, -1), (-1, 1), (0, 0), (1, -1)], truncated=(2,))
            upload_host = host_fixture(torch.from_numpy(host.obs), torch.from_numpy(host.candidates), torch.from_numpy(host.mask))
            host.upload = upload_host.upload
            values = host.upload.numpy()
            host.obs = values[:4*2048].reshape(4, 2048)
            host.candidates = values[4*2048:4*(2048+64*32)].reshape(4, 64, 32)
            host.mask = values[4*(2048+64*32):].reshape(4, 64)
            return host

        policy = LearningPolicy()
        actors = [AdaptiveLearningActor(policy, 4, graph=False) for _ in range(2)]
        outputs = []
        for collector in (collect_episodes, collect_queued):
            host, store = make_host(), EpisodeStore(64, device="cpu")
            torch.manual_seed(784)
            result = collector(host, actors[0], store, version=3, opponent=actors[1],
                               opponent_lanes=np.array([True, False, True, True]),
                               learner_seats=np.array([0, 0, 0, 0]), censor_truncated=True)
            outputs.append((host, store, result))
        baseline, queued = outputs
        self.assertEqual(baseline[0].executed, queued[0].executed)
        self.assertEqual(baseline[1].rows, queued[1].rows)
        for field in ("obs", "candidates", "mask", "actions", "old_logp", "old_values", "returns", "advantages"):
            torch.testing.assert_close(getattr(baseline[1], field)[:baseline[1].rows],
                                       getattr(queued[1], field)[:queued[1].rows], rtol=0, atol=0)
        for field in ("episode_ids", "seats"):
            np.testing.assert_array_equal(getattr(baseline[1], field)[:baseline[1].rows],
                                          getattr(queued[1], field)[:queued[1].rows])
        for key in ("completed_games", "censored_games", "learning_rows", "censored_learning_rows", "action_kind_counts"):
            self.assertEqual(baseline[2].metrics[key], queued[2].metrics[key], key)

    @unittest.skipUnless(os.environ.get("RUN_CUDA_LEARNING_TESTS") == "1", "Needs exclusive CUDA window")
    def test_cuda_graph_order_probability_and_owned_packets(self):
        torch.manual_seed(771)
        a, b = (AdaptiveLearningActor(LearningPolicy().cuda(), 64, graph=True) for _ in range(2))
        host = host_fixture(*fixture(64))
        host.batch = 64
        left, right = np.arange(0, 64, 2), np.arange(1, 64, 2)
        state = torch.cuda.get_rng_state()
        expected = np.empty(64, dtype=np.int64)
        expected[left], p = a.act_subset(host, left)
        expected[right], _ = b.act_subset(host, right)
        torch.cuda.set_rng_state(state)
        actual = paired_actions(host, a, b, left, right)
        np.testing.assert_array_equal(expected, actual)
        np.testing.assert_array_equal(p, a.packet.cpu().numpy())


if __name__ == "__main__":
    unittest.main()
