"""Disposable fabricated-target PPO, optimizer parity and resume ownership."""
import copy
import io
from pathlib import Path
import sys
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

import numpy as np
import torch
from model import Actor, Policy, PolicyConfig, cpu_state
from train import Learner, Rollout, TrainConfig
from test_learning import fake_host, small_catalog


def fabricated_rollout(policy, device):
    state = fake_host()
    state.obs[:, 20] = [.1, .3, .2]
    state.candidates[:, 1, 16] = 2 / 192
    actor = Actor(policy, 3, graph=device == "cuda", device=device)
    _, packet = actor.act(state)
    store = Rollout(64, small_catalog(), device=device)
    store.append(state, packet, [0, 1, 2], actor=actor)
    # Explicitly fabricated utilities; these rows are not completed real games.
    indices, returns, advantages, excluded = store.seal([1, 1, 1],
        np.array([[1, -1], [-1, 1], [0, 0]], np.float32))
    assert excluded == 0
    return store, indices, returns, advantages


def config(fused=True):
    return TrainConfig(batch=3, width=64, capacity=64, minibatch=16,
                       epochs=1, learning_rate=3e-4, fused_optimizer=fused)


class FusedOptimizerCpuTests(unittest.TestCase):
    def setUp(self):
        torch.set_num_threads(1)
        torch.manual_seed(279341)

    def test_requested_fused_optimizer_falls_back_on_cpu_and_fabricated_update_is_finite(self):
        policy = Policy(small_catalog(), PolicyConfig(width=64))
        learner = Learner(policy, config())
        self.assertIsNot(learner.optimizer.defaults["fused"], True)
        result = learner.update(*fabricated_rollout(policy, "cpu"))
        self.assertEqual(result["optimizer_steps"], 1)
        self.assertTrue(result["behavior_verification_complete"])
        learner.verify_finite_state()

    def test_fused_flag_requires_a_boolean(self):
        with self.assertRaisesRegex(ValueError, "booleans"):
            TrainConfig(fused_optimizer=1).validate()


@unittest.skipUnless(torch.cuda.is_available(), "Fused optimizer parity requires CUDA")
class FusedOptimizerCudaTests(unittest.TestCase):
    def setUp(self):
        torch.set_num_threads(1)
        torch.manual_seed(279341)
        torch.cuda.manual_seed_all(279341)
        torch.backends.fp32_precision = "ieee"
        torch.backends.cuda.matmul.fp32_precision = "ieee"

    def test_normal_rate_fused_and_ordinary_ppo_have_matching_parameters_and_moments(self):
        original = Policy(small_catalog(), PolicyConfig(width=64)).cuda()
        before = cpu_state(original)
        rollout = fabricated_rollout(original, "cuda")
        policies = [copy.deepcopy(original) for _ in range(2)]
        learners = [Learner(policy, config(fused)) for policy, fused in zip(policies, (False, True))]
        self.assertIsNone(learners[0].optimizer.defaults["fused"])
        self.assertTrue(learners[1].optimizer.defaults["fused"])
        for learner in learners:
            np.random.seed(89324)
            result = learner.update(*rollout)
            self.assertEqual(result["optimizer_steps"], 1)
            self.assertLess(result["behavior_logp_error"], .002)
            self.assertLess(result["behavior_value_error"], .001)
            learner.verify_finite_state()
        for first, second in zip(policies[0].parameters(), policies[1].parameters()):
            torch.testing.assert_close(first, second, rtol=2e-5, atol=2e-6)
            ordinary = learners[0].optimizer.state[first]
            fused = learners[1].optimizer.state[second]
            for key in ("exp_avg", "exp_avg_sq", "step"):
                if key in ordinary:
                    torch.testing.assert_close(ordinary[key].cpu(), fused[key].cpu(), rtol=2e-4, atol=2e-7)
        for name, value in original.state_dict().items():
            torch.testing.assert_close(value.cpu(), before[name], rtol=0, atol=0)

    def test_optimizer_state_roundtrip_retains_moments_and_identical_next_update(self):
        policy = Policy(small_catalog(), PolicyConfig(width=64)).cuda()
        learner = Learner(policy, config())
        learner.update(*fabricated_rollout(policy, "cuda"))
        learner.verify_finite_state()
        memory = io.BytesIO()
        torch.save({"policy": cpu_state(policy), "optimizer": learner.optimizer.state_dict()}, memory)
        memory.seek(0)
        restored_payload = torch.load(memory, map_location="cpu", weights_only=True)
        restored_policy = Policy(small_catalog(), PolicyConfig(width=64)).cuda()
        restored_policy.load_state_dict(restored_payload["policy"])
        restored = Learner(restored_policy, config())
        restored.optimizer.load_state_dict(restored_payload["optimizer"])
        restored.verify_finite_state()
        for first, second in zip(policy.parameters(), restored_policy.parameters()):
            for key, value in learner.optimizer.state[first].items():
                if torch.is_tensor(value):
                    torch.testing.assert_close(value.cpu(), restored.optimizer.state[second][key].cpu(), rtol=0, atol=0)
        rollout = fabricated_rollout(policy, "cuda")
        for candidate in (learner, restored):
            np.random.seed(89324)
            self.assertEqual(candidate.update(*rollout)["optimizer_steps"], 1)
            candidate.verify_finite_state()
        for first, second in zip(policy.parameters(), restored_policy.parameters()):
            torch.testing.assert_close(first, second, rtol=0, atol=0)
            for key, value in learner.optimizer.state[first].items():
                if torch.is_tensor(value):
                    torch.testing.assert_close(value.cpu(), restored.optimizer.state[second][key].cpu(), rtol=0, atol=0)

    def test_fused_backend_preserves_behavior_verification_and_nonfinite_moment_guards(self):
        policy = Policy(small_catalog(), PolicyConfig(width=64)).cuda()
        learner = Learner(policy, config())
        store, indices, returns, advantages = fabricated_rollout(policy, "cuda")
        store.packet[0, 1] += .1
        with self.assertRaisesRegex(RuntimeError, "behavior mismatch"):
            learner.update(store, indices, returns, advantages)
        self.assertEqual(len(learner.optimizer.state), 0)
        store, indices, returns, advantages = fabricated_rollout(policy, "cuda")
        learner.update(store, indices, returns, advantages)
        parameter = next(parameter for parameter in policy.parameters()
                         if "exp_avg" in learner.optimizer.state[parameter])
        learner.optimizer.state[parameter]["exp_avg"].fill_(float("nan"))
        with self.assertRaisesRegex(RuntimeError, "Nonfinite learned parameter or Adam state"):
            learner.verify_finite_state()


if __name__ == "__main__":
    unittest.main()
