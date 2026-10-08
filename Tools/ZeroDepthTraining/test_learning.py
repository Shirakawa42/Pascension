"""Credit ownership, censoring, actor parity and owned storage regressions."""
import sys
from pathlib import Path
sys.path.insert(0, str(Path(__file__).resolve().parent))

import unittest
from types import SimpleNamespace

import numpy as np
import torch

from model import Actor, Policy, PolicyConfig
from train import Learner, Rollout, TrainConfig


def small_catalog():
    return {"obs_dim": 32, "max_actions": 64, "action_dim": 48,
            "card_ids": ["crystal", "shard_reactor"], "candidate_card_scale": 192,
            "histograms": [{"offset": 20, "length": 2}]}


def fake_host():
    host = SimpleNamespace(obs=np.zeros((3, 32), np.float32),
        candidates=np.zeros((3, 64, 48), np.float32), mask=np.zeros((3, 64), np.float32),
        actors=np.array([0, 1, 0], np.int32))
    host.mask[:, :2] = 1
    host.candidates[:, 0, 0] = 1
    host.candidates[:, 1, 10] = 1
    host.candidates[:, 0, 16] = 1 / 192
    return host


class CreditTests(unittest.TestCase):
    def test_credit_uses_deciding_seat_with_same_seat_successive_actions(self):
        host = fake_host()
        store = Rollout(32, small_catalog())
        packet = np.zeros((3, 3), np.float32)
        store.append(host, packet, [0, 1, 2])
        host.actors[:] = [0, 0, 1]
        store.append(host, packet, [0, 1, 2])
        rows, returns, _, excluded = store.seal(np.ones(3, np.int32),
            np.array([[1, -1], [-1, 1], [0, 0]], np.float32))
        np.testing.assert_array_equal(rows, np.arange(6))
        np.testing.assert_array_equal(returns, [1, 1, 0, 1, -1, 0])
        self.assertEqual(excluded, 0)

    def test_all_censored_lane_rows_excluded_before_normalization(self):
        host = fake_host()
        store = Rollout(32, small_catalog())
        packet = np.zeros((3, 3), np.float32)
        packet[1, 2] = -10000
        store.append(host, packet, [0, 1, 2])
        store.append(host, packet, [1])
        indices, returns, advantages, excluded = store.seal([1, 2, 1], np.array([[1, -1], [0, 0], [-1, 1]]))
        np.testing.assert_array_equal(indices, [0, 2])
        np.testing.assert_array_equal(returns, [1, -1])
        np.testing.assert_array_equal(advantages, [1, -1])
        self.assertEqual(excluded, 2)

    def test_borrowed_host_and_packet_do_not_overwrite_experience(self):
        host = fake_host()
        host.obs[0, 0] = 42
        packet = np.zeros((3, 3), np.float32)
        packet[0, 1] = -2
        store = Rollout(32, small_catalog())
        store.append(host, packet, [0])
        host.obs.fill(-999)
        packet.fill(999)
        self.assertEqual(store.obs[0, 0], 42)
        self.assertEqual(store.packet[0, 1], -2)

    def test_unresolved_episodes_and_invalid_outcomes_rejected(self):
        store = Rollout(32, small_catalog())
        with self.assertRaises(RuntimeError):
            store.seal([0], [[0, 0]])
        with self.assertRaises(RuntimeError):
            store.seal([1], [[1, 1]])

    def test_capacity_never_wraps_over_unresolved_rows(self):
        host = fake_host()
        store = Rollout(1, small_catalog())
        store.append(host, np.zeros((3, 3), np.float32), [0])
        with self.assertRaises(RuntimeError):
            store.append(host, np.zeros((3, 3), np.float32), [1])
        self.assertEqual(store.rows, 1)
        self.assertEqual(store.lanes[0], 0)


class PolicyTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        torch.set_num_threads(1)

    def test_mask_and_owned_actor_packet(self):
        model = Policy(small_catalog(), PolicyConfig(width=64))
        host = fake_host()
        host.mask[:, 1] = 0
        actor = Actor(model, 3, graph=False, device="cpu")
        actions, packet = actor.act(host)
        np.testing.assert_array_equal(actions, [0, 0, 0])
        saved = packet.copy()
        actor.act(host)
        np.testing.assert_array_equal(packet, saved)
        np.testing.assert_array_equal(packet[:, 1], 0)

    def test_numeric_transform_has_no_count_clip(self):
        x = torch.tensor([-100000., -10000., 10000., 100000.])
        value = Policy.numeric(x)
        self.assertTrue(bool((value[1:] > value[:-1]).all()))

    def test_behavior_verification_detects_wrong_sampling_probabilities(self):
        policy = Policy(small_catalog(), PolicyConfig(width=64))
        actor = Actor(policy, 3, graph=False, device="cpu")
        host = fake_host()
        _, packet = actor.act(host)
        store = Rollout(32, small_catalog())
        store.append(host, packet, [0, 1, 2])
        learner = Learner(policy, TrainConfig(minibatch=16))
        learner.verify_behavior(store, np.arange(3))
        store.packet[1, 1] += .1
        with self.assertRaises(RuntimeError):
            learner.verify_behavior(store, np.arange(3))

    def test_semantic_actor_cache_refresh_matches_learner_after_weight_change(self):
        catalog = small_catalog()
        catalog["card_features"] = [[1, 0], [0, 1]]
        policy = Policy(catalog, PolicyConfig(width=64))
        actor = Actor(policy, 3, graph=False, device="cpu")
        host = fake_host()
        host.obs[:, 20] = .2
        with torch.no_grad():
            policy.effect_projection.weight.add_(.25)
            policy.query.weight.add_(.1)
        actor.refresh(policy)
        _, packet = actor.act(host)
        store = Rollout(32, catalog)
        store.append(host, packet, [0, 1, 2])
        Learner(policy, TrainConfig(minibatch=16)).verify_behavior(store, np.arange(3))

    def test_corrupt_optimizer_moment_is_rejected(self):
        policy = Policy(small_catalog(), PolicyConfig(width=64))
        learner = Learner(policy, TrainConfig(minibatch=16))
        parameter = next(policy.parameters())
        learner.optimizer.state[parameter]["exp_avg"] = torch.full_like(parameter, float("nan"))
        with self.assertRaises(RuntimeError):
            learner.verify_finite_state()

    def test_disposable_ppo_uses_finite_actual_stored_action_distribution(self):
        policy = Policy(small_catalog(), PolicyConfig(width=64))
        actor = Actor(policy, 3, graph=False, device="cpu")
        host = fake_host()
        _, packet = actor.act(host)
        store = Rollout(32, small_catalog())
        store.append(host, packet, [0, 1, 2])
        indices, returns, advantages, _ = store.seal([1, 1, 1], np.array([[1, -1], [-1, 1], [0, 0]], np.float32))
        before = {name: p.clone() for name, p in policy.named_parameters()}
        learner = Learner(policy, TrainConfig(minibatch=16, epochs=1))
        result = learner.update(store, indices, returns, advantages)
        self.assertEqual(result["example_passes"], 3)
        self.assertTrue(all(bool(torch.isfinite(p).all()) for p in policy.parameters()))
        self.assertTrue(any(not torch.equal(p, before[name]) for name, p in policy.named_parameters()))


if __name__ == "__main__":
    unittest.main()
