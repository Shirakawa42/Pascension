"""Fabricated learner data: finite guards, retained row mapping and ownership."""
from pathlib import Path
import sys
import unittest
from types import SimpleNamespace

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

import numpy as np
import torch
from model import Actor, Policy, PolicyConfig
from train import Learner, Rollout, TrainConfig
from test_learning import fake_host, small_catalog


def retained_fixture(device="cpu", rows=37):
    """Distinct observations and fabricated deciding-seat rewards, no games."""
    catalog = small_catalog()
    torch.manual_seed(281490)
    policy = Policy(catalog, PolicyConfig(width=64)).to(device)
    example = fake_host()
    host = SimpleNamespace(obs=np.repeat(example.obs[:1], rows, axis=0),
                           candidates=np.repeat(example.candidates[:1], rows, axis=0),
                           mask=np.repeat(example.mask[:1], rows, axis=0),
                           actors=np.arange(rows, dtype=np.int32) % 2)
    host.obs[:, 0] = np.arange(rows, dtype=np.float32)
    with torch.no_grad():
        logits, values = policy(torch.from_numpy(host.obs).to(device),
                                torch.from_numpy(host.candidates).to(device),
                                torch.from_numpy(host.mask).to(device))
        actions = logits.argmax(-1)
        packet = torch.stack((actions.float(), logits.log_softmax(-1).gather(1, actions[:, None]).squeeze(1), values), -1).cpu().numpy()
    # CPU append validates legal actions; the CUDA version owns identical bytes.
    store = Rollout(64, catalog)
    store.append(host, packet, np.arange(rows))
    if device == "cuda":
        gpu = Rollout(64, catalog, device=device)
        for name in ("obs", "candidates", "mask", "packet"):
            getattr(gpu, name)[:rows].copy_(torch.from_numpy(getattr(store, name)[:rows]))
        gpu.lanes[:rows] = store.lanes[:rows]
        gpu.seats[:rows] = store.seats[:rows]
        gpu.rows = store.rows
        store = gpu
    done = np.ones(rows, np.int8)
    done[::5] = 2
    rewards = np.tile(np.array([[1, -1]], np.float32), (rows, 1))
    indices, returns, advantages, excluded = store.seal(done, rewards)
    config = TrainConfig(batch=rows, width=64, capacity=64, minibatch=16, epochs=2, learning_rate=1.e-7)
    return policy, store, indices, returns, advantages, excluded, config


class RecordingLearner(Learner):
    def __init__(self, *args):
        super().__init__(*args)
        self.observed = []

    def batch(self, store, rows, returns=None, advantages=None):
        output = super().batch(store, rows, returns, advantages)
        if returns is not None:
            self.observed.append(tuple(value.detach().cpu().numpy().copy()
                                       for value in (output[0][:, 0], output[4], output[5])))
        return output


class LearnerBehaviorTests(unittest.TestCase):
    def setUp(self):
        torch.set_num_threads(1)
        torch.manual_seed(281489)
        self.policy = Policy(small_catalog(), PolicyConfig(width=64))
        self.state = fake_host()
        self.actor = Actor(self.policy, 3, graph=False, device="cpu")
        _, self.packet = self.actor.act(self.state)
        self.store = Rollout(64, small_catalog())
        self.store.append(self.state, self.packet, [0, 1, 2])
        self.learner = Learner(self.policy, TrainConfig(batch=3, width=64, capacity=64, minibatch=16, epochs=1))

    def test_nan_behavior_log_probability_and_value_are_never_reported_as_zero_error(self):
        for column in (1, 2):
            with self.subTest(packet_column=column):
                self.store.packet[:self.store.rows] = self.packet
                self.store.packet[1, column] = float("nan")
                with self.assertRaisesRegex(RuntimeError, "behavior mismatch"):
                    self.learner.verify_behavior(self.store, np.arange(3))
                self.assertEqual(len(self.learner.optimizer.state), 0)

    def test_every_retained_row_including_last_partial_batch_is_verified(self):
        policy, store, indices, _, _, _, config = retained_fixture()
        learner = Learner(policy, config)
        saved = store.packet[indices[-1]].copy()
        for column, delta in ((1, .003), (2, .002), (1, float("nan")), (2, float("inf"))):
            with self.subTest(column=column, delta=delta):
                store.packet[indices[-1]] = saved
                store.packet[indices[-1], column] += delta
                beats = []
                with self.assertRaisesRegex(RuntimeError, "behavior mismatch"):
                    learner.verify_behavior(store, indices, heartbeat=lambda: beats.append(1))
                self.assertEqual(len(beats), 2)
                self.assertEqual(len(learner.optimizer.state), 0)

    def test_cpu_and_cuda_batch_select_identical_rows_targets_and_owned_source(self):
        devices = ["cpu"] + (["cuda"] if torch.cuda.is_available() else [])
        for device in devices:
            with self.subTest(device=device):
                policy, store, indices, returns, advantages, _, config = retained_fixture(device)
                learner = Learner(policy, config)
                self.assertIsNone(learner.staging)
                selected = np.array([19, 0, 19, 4, 7, 2, len(indices) - 1])
                rows = indices[selected]
                output = learner.batch(store, rows, returns[selected], advantages[selected])
                for value, name in zip(output[:4], ("obs", "candidates", "mask", "packet")):
                    original = getattr(store, name)
                    expected = original[torch.from_numpy(rows).to(device)] if store.gpu else original[rows]
                    np.testing.assert_array_equal(value.cpu().numpy(), expected.cpu().numpy() if store.gpu else expected)
                np.testing.assert_array_equal(output[4].cpu().numpy(), returns[selected])
                np.testing.assert_array_equal(output[5].cpu().numpy(), advantages[selected])
                before = store.obs[rows[0]].clone() if store.gpu else store.obs[rows[0]].copy()
                output[0][0].fill_(-999)
                np.testing.assert_array_equal(store.obs[rows[0]].cpu().numpy() if store.gpu else store.obs[rows[0]],
                                              before.cpu().numpy() if store.gpu else before)
                if store.gpu:
                    self.assertIsNone(learner.staging)
                    retained = output[0].clone()
                    learner.batch(store, indices[:16])
                    self.assertTrue(torch.equal(output[0], retained))
                else:
                    self.assertIsNotNone(learner.staging)

    def test_gpu_generation_shuffle_preserves_row_target_order_and_censors(self):
        devices = ["cpu"] + (["cuda"] if torch.cuda.is_available() else [])
        for device in devices:
            with self.subTest(device=device):
                policy, store, indices, returns, advantages, excluded, config = retained_fixture(device)
                learner = RecordingLearner(policy, config)
                self.assertEqual(excluded, 8)
                rng = np.random.RandomState(816283)
                orders = [rng.permutation(len(indices)) for _ in range(config.epochs)]
                np.random.seed(816283)
                snapshot = {name: getattr(store, name)[:store.rows].clone() if store.gpu else getattr(store, name)[:store.rows].copy()
                            for name in ("obs", "candidates", "mask", "packet")}
                result = learner.update(store, indices, returns, advantages)
                self.assertEqual(result["optimizer_steps"], 4)
                self.assertEqual(result["example_passes"], config.epochs * len(indices))
                self.assertFalse(result["kl_early_stop"])
                for number, order in enumerate(orders):
                    pair = learner.observed[number * 2:(number + 1) * 2]
                    actual = [np.concatenate([batch[column] for batch in pair]) for column in range(3)]
                    for value, expected in zip(actual, (indices[order], returns[order], advantages[order])):
                        np.testing.assert_array_equal(value, expected)
                expected_rng, actual_rng = rng.get_state(), np.random.get_state()
                np.testing.assert_array_equal(actual_rng[1], expected_rng[1])
                self.assertEqual(actual_rng[2:], expected_rng[2:])
                for name, expected in snapshot.items():
                    actual = getattr(store, name)[:store.rows]
                    self.assertTrue(torch.equal(actual, expected) if store.gpu else np.array_equal(actual, expected))
                learner.verify_finite_state()
                self.assertTrue(all(isinstance(value, float) for key, value in result.items()
                                    if key in ("loss", "entropy", "approx_kl", "gradient_norm")))

    def test_deadline_during_verification_makes_no_update_and_partial_update_reports_metrics(self):
        for stop_after_verification in (True, False):
            with self.subTest(stop_after_verification=stop_after_verification):
                policy, store, indices, returns, advantages, _, config = retained_fixture()
                learner = Learner(policy, config)
                state = {"heartbeats": 0}
                threshold = 1 if stop_after_verification else 3
                def heartbeat():
                    state["heartbeats"] += 1
                result = learner.update(store, indices, returns, advantages,
                    stop_check=lambda: state["heartbeats"] >= threshold, heartbeat=heartbeat)
                self.assertTrue(result["deadline_stop"])
                self.assertEqual(result["optimizer_steps"], 0 if stop_after_verification else 1)
                self.assertEqual(result["example_passes"], 0 if stop_after_verification else 16)
                if stop_after_verification:
                    self.assertFalse(result["behavior_verification_complete"])
                    self.assertEqual(len(learner.optimizer.state), 0)
                else:
                    self.assertTrue(result["behavior_verification_complete"])
                    self.assertTrue(all(np.isfinite(result[key]) for key in ("loss", "entropy", "approx_kl", "gradient_norm")))

    def test_nonfinite_targets_rejected_before_any_optimizer_step(self):
        policy, store, indices, returns, advantages, _, config = retained_fixture()
        learner = Learner(policy, config)
        returns[:] = float("nan")
        before = [value.detach().clone() for value in policy.parameters()]
        with self.assertRaisesRegex(RuntimeError, "Nonfinite PPO loss"):
            learner.update(store, indices, returns, advantages)
        self.assertEqual(len(learner.optimizer.state), 0)
        self.assertTrue(all(torch.equal(a, b) for a, b in zip(before, policy.parameters())))

    @unittest.skipUnless(torch.cuda.is_available(), "CUDA required for pinned-memory lifetime regression")
    def test_pinned_cpu_fallback_cannot_be_overwritten_before_prior_transfer(self):
        policy, store, _, _, _, _, config = retained_fixture()
        learner = Learner(policy.to("cuda"), config)
        learner.batch(store, np.arange(16))
        torch.cuda.synchronize()
        # Hold queued transfers briefly while the CPU tries to refill the same
        # pinned staging. This makes the old asynchronous ownership race visible.
        torch.cuda._sleep(20_000_000)
        first = learner.batch(store, np.arange(16))
        second = learner.batch(store, np.arange(16, 32))
        for outputs, rows in ((first, np.arange(16)), (second, np.arange(16, 32))):
            for actual, name in zip(outputs, ("obs", "candidates", "mask", "packet")):
                np.testing.assert_array_equal(actual.cpu().numpy(), getattr(store, name)[rows])

    @unittest.skipUnless(torch.cuda.is_available(), "CUDA required for fallback/resident PPO parity")
    def test_cuda_cpu_fallback_and_owned_gpu_rollout_normal_lr_updates_match(self):
        policy, gpu, indices, returns, advantages, _, config = retained_fixture("cuda")
        config.learning_rate, config.epochs = 3.e-4, 1
        fallback = Rollout(64, small_catalog())
        for name in ("obs", "candidates", "mask", "packet"):
            getattr(fallback, name)[:gpu.rows] = getattr(gpu, name)[:gpu.rows].cpu().numpy()
        fallback.rows = gpu.rows
        fallback_policy = Policy(small_catalog(), PolicyConfig(width=64)).to("cuda")
        fallback_policy.load_state_dict(policy.state_dict())
        learners = [Learner(policy, config), Learner(fallback_policy, config)]
        for learner, store in zip(learners, (gpu, fallback)):
            verified = learner.verify_behavior(store, indices)
            self.assertLessEqual(verified["behavior_logp_error"], .002)
            self.assertLessEqual(verified["behavior_value_error"], .001)
            np.random.seed(192847)
            result = learner.update(store, indices[:16], returns[:16], advantages[:16])
            self.assertEqual(result["optimizer_steps"], 1)
            self.assertEqual(result["example_passes"], 16)
            learner.verify_finite_state()
        for first, second in zip(policy.parameters(), fallback_policy.parameters()):
            torch.testing.assert_close(first, second, rtol=0, atol=2.e-6)
            for key in ("step", "exp_avg", "exp_avg_sq"):
                torch.testing.assert_close(learners[0].optimizer.state[first][key], learners[1].optimizer.state[second][key],
                                           rtol=0, atol=1.e-8)


if __name__ == "__main__":
    unittest.main()
