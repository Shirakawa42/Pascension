"""Bounded mathematical/ownership tests; no game outcome learning campaign.

CPU: python -m unittest discover -s Tools/TrainingPreflight -p test_learning_model.py -v
CUDA correctness (only in allocated window): RUN_CUDA_LEARNING_TESTS=1 <same command>
Synthetic targets exercise optimizer mechanics only and do not claim strength.
"""
import copy
import os
from types import SimpleNamespace
import unittest

import numpy as np
import torch

from learning_model import (ACTION_DIM, MAX_ACTIONS, OBS_DIM, UPLOAD_FLOATS_PER_ROW,
                            InvalidLearningBatch, LearningActor, LearningNumericalError,
                            LearningPolicy, PolicyConfig, PPOConfig, PPOLearner)


def fixture(batch=16, device="cpu"):
    generator = torch.Generator().manual_seed(318)
    obs = torch.randn(batch, OBS_DIM, generator=generator) * .15
    candidates = torch.zeros(batch, MAX_ACTIONS, ACTION_DIM)
    for slot in range(6):
        candidates[:, slot, slot % 4] = 1
        candidates[:, slot, 16] = (slot + 1) / 192
        candidates[:, slot, 17:] = torch.rand(batch, ACTION_DIM-17, generator=generator)
    mask = torch.zeros(batch, MAX_ACTIONS, dtype=torch.bool)
    mask[:, :6] = True
    # Nonprefix original slots ensure no accidental compaction/renumbering.
    candidates[:, 63] = candidates[:, 5]
    candidates[:, 5] = 0
    mask[:, 63], mask[:, 5] = True, False
    return obs.to(device), candidates.to(device), mask.to(device)


def host_fixture(obs, candidates, mask):
    return SimpleNamespace(upload=torch.cat((obs.cpu().flatten(), candidates.cpu().flatten(), mask.float().cpu().flatten())))


def behavior_batch(policy, data):
    obs, candidates, mask = data
    with torch.no_grad():
        logits, old_values = policy(obs, candidates, mask)
        actions = logits.argmax(-1)
        old_logp = logits.log_softmax(-1).gather(1, actions[:, None]).squeeze(1)
    returns = torch.ones_like(old_values)
    advantages = torch.zeros_like(old_values)
    return (obs, candidates, mask, actions, old_logp, old_values, returns, advantages)


def assert_state_equal(case, actual, expected):
    if torch.is_tensor(actual):
        case.assertTrue(torch.equal(actual.cpu(), expected.cpu()))
    elif isinstance(actual, dict):
        case.assertEqual(actual.keys(), expected.keys())
        for key in actual:
            assert_state_equal(case, actual[key], expected[key])
    elif isinstance(actual, (list, tuple)):
        case.assertEqual(len(actual), len(expected))
        for left, right in zip(actual, expected):
            assert_state_equal(case, left, right)
    else:
        case.assertEqual(actual, expected)


class LearningMathTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        torch.set_num_threads(1)

    def setUp(self):
        torch.manual_seed(1984)
        self.policy = LearningPolicy(PolicyConfig(width=128))
        self.data = fixture()

    def test_transform_extremes_preserves_identity_and_kind(self):
        obs, candidates, mask = self.data
        obs[:, ::2] = 9999
        obs[:, 1::2] = -torch.finfo(torch.float32).max
        candidates[:, :, 17::2] = 9999
        candidates[:, :, 18::2] = -torch.finfo(torch.float32).max
        transformed_obs, transformed_candidates = self.policy.transform_inputs(obs, candidates)
        self.assertTrue(torch.equal(candidates[:, :, :17], transformed_candidates[:, :, :17]))
        self.assertLessEqual(transformed_obs.abs().max(), np.log1p(1000) + 1e-6)
        logits, values = self.policy(obs, candidates, mask)
        self.assertTrue(torch.isfinite(logits).all())
        self.assertTrue(torch.isfinite(values).all())
        self.assertTrue((values.abs() <= 1).all())
        scalar = torch.tensor([1e-5])
        torch.testing.assert_close(self.policy._numeric(scalar), scalar, rtol=1e-5, atol=1e-10)

    def test_actor_uses_exact_policy_probability_and_owned_packet(self):
        actor = LearningActor(self.policy, 16, graph=False)
        host = host_fixture(*self.data)
        actions, packet = actor.act(host)
        logits, values = self.policy(*self.data)
        expected = logits.log_softmax(-1).gather(1, torch.from_numpy(actions)[:, None]).squeeze(1)
        np.testing.assert_allclose(packet[:, 1], expected.detach().numpy(), atol=1e-6, rtol=1e-6)
        np.testing.assert_allclose(packet[:, 2], values.detach().numpy(), atol=1e-6, rtol=1e-6)
        self.assertTrue(torch.equal(actor.obs, self.data[0]))
        previous = packet.copy()
        actor.act(host)
        np.testing.assert_array_equal(packet, previous)
        actions.fill(0)
        np.testing.assert_array_equal(packet, previous)

    def test_prior_keeps_concede_legal_and_nonzero(self):
        with torch.no_grad():
            self.policy.core.query.weight.zero_()
            self.policy.core.query.bias.zero_()
        obs, candidates, mask = fixture(1)
        candidates.zero_(); mask.zero_()
        candidates[0, 0, 0] = 1
        candidates[0, 63, 11] = 1
        mask[0, 0] = mask[0, 63] = True
        logits, _ = self.policy(obs, candidates, mask)
        self.assertEqual(float(logits[0, 0].detach()), 1.)
        self.assertEqual(float(logits[0, 63].detach()), -12.)
        self.assertGreater(float(logits.softmax(-1)[0, 63].detach()), 0.)

    def test_actor_rejects_all_invalid_and_nonfinite_inputs(self):
        actor = LearningActor(self.policy, 16, graph=False)
        host = host_fixture(*self.data)
        host.upload[-16*MAX_ACTIONS:] = 0
        with self.assertRaises(InvalidLearningBatch):
            actor.act(host)
        host = host_fixture(*self.data)
        host.upload[0] = float("inf")
        with self.assertRaises(InvalidLearningBatch):
            actor.act(host)

    def test_real_value_update_and_frozen_actor_refresh(self):
        actor = LearningActor(self.policy, 16, graph=False)
        actor_before = copy.deepcopy(actor.policy.state_dict())
        learner = PPOLearner(self.policy, PPOConfig(entropy_coefficient=0, value_coefficient=1))
        batch = behavior_batch(self.policy, self.data)
        before_value = self.policy(*self.data)[1].detach().clone()
        result = learner.step(*batch)
        self.assertTrue(result["accepted"])
        self.assertLess(result["approx_kl"], 1e-7)
        self.assertGreater(result["grad_norm"], 0)
        self.assertGreater(float(self.policy(*self.data)[1].mean()), float(before_value.mean()))
        assert_state_equal(self, actor.policy.state_dict(), actor_before)
        addresses = [p.data_ptr() for p in actor.policy.parameters()]
        actor.refresh(self.policy, version=7)
        self.assertEqual(actor.version, 7)
        self.assertEqual(addresses, [p.data_ptr() for p in actor.policy.parameters()])
        assert_state_equal(self, actor.policy.state_dict(), self.policy.state_dict())

    def test_policy_gradient_moves_positive_advantage_action(self):
        learner = PPOLearner(self.policy, PPOConfig(entropy_coefficient=0, value_coefficient=0))
        batch = list(behavior_batch(self.policy, self.data))
        batch[-1] = torch.ones_like(batch[-1])
        result = learner.step(*batch)
        self.assertTrue(result["accepted"])
        with torch.no_grad():
            logits, _ = self.policy(*self.data)
            new_logp = logits.log_softmax(-1).gather(1, batch[3][:, None]).squeeze(1)
        self.assertGreater(float(new_logp.mean()), float(batch[4].mean()))

    def test_stale_ratio_and_kl_rejections_do_not_mutate_optimizer(self):
        learner = PPOLearner(self.policy)
        self.assertTrue(learner.step(*behavior_batch(self.policy, self.data))["accepted"])
        policy_before = copy.deepcopy(self.policy.state_dict())
        optimizer_before = copy.deepcopy(learner.optimizer.state_dict())
        batch = list(behavior_batch(self.policy, self.data))
        batch[4] = batch[4] - 1000
        result = learner.step(*batch)
        self.assertFalse(result["accepted"])
        self.assertEqual(result["reason"], "log_ratio_guard")
        assert_state_equal(self, self.policy.state_dict(), policy_before)
        assert_state_equal(self, learner.optimizer.state_dict(), optimizer_before)
        batch = list(behavior_batch(self.policy, self.data))
        batch[4] = batch[4] - 1
        result = learner.step(*batch)
        self.assertFalse(result["accepted"])
        self.assertEqual(result["reason"], "target_kl")
        assert_state_equal(self, self.policy.state_dict(), policy_before)
        assert_state_equal(self, learner.optimizer.state_dict(), optimizer_before)

    def test_invalid_stored_actions_stop_before_gather_or_update(self):
        learner = PPOLearner(self.policy)
        batch = list(behavior_batch(self.policy, self.data))
        batch[3] = torch.full_like(batch[3], 64)
        before = copy.deepcopy(self.policy.state_dict())
        with self.assertRaises(InvalidLearningBatch):
            learner.step(*batch)
        assert_state_equal(self, self.policy.state_dict(), before)
        batch[3].fill_(5)  # Nonprefix fixture's explicitly illegal slot.
        with self.assertRaises(InvalidLearningBatch):
            learner.step(*batch)

    def test_nonfinite_gradient_rejects_before_optimizer_and_recovers(self):
        learner = PPOLearner(self.policy)
        saved = learner.state_dict()
        before = copy.deepcopy(self.policy.state_dict())
        hook = self.policy.core.value.bias.register_hook(lambda grad: torch.full_like(grad, float("nan")))
        with self.assertRaises(LearningNumericalError):
            learner.step(*behavior_batch(self.policy, self.data))
        hook.remove()
        self.assertTrue(learner.failed)
        self.assertFalse(learner.optimizer.state)
        assert_state_equal(self, self.policy.state_dict(), before)
        with self.assertRaises(LearningNumericalError):
            learner.state_dict()
        learner.load_state_dict(saved)
        self.assertTrue(learner.step(*behavior_batch(self.policy, self.data))["accepted"])

    def test_checkpoint_snapshot_is_owned_and_restores_adam(self):
        learner = PPOLearner(self.policy)
        learner.step(*behavior_batch(self.policy, self.data))
        saved = learner.state_dict()
        saved_copy = copy.deepcopy(saved)
        learner.step(*behavior_batch(self.policy, self.data))
        assert_state_equal(self, saved, saved_copy)
        learner.load_state_dict(saved)
        self.assertEqual(learner.updates, 1)
        assert_state_equal(self, self.policy.state_dict(), saved["policy"])
        assert_state_equal(self, learner.optimizer.state_dict(), saved["optimizer"])
        corrupted = copy.deepcopy(saved)
        next(iter(corrupted["optimizer"]["state"].values()))["exp_avg"].flatten()[0] = float("nan")
        with self.assertRaises(LearningNumericalError):
            learner.load_state_dict(corrupted)


@unittest.skipUnless(os.environ.get("RUN_CUDA_LEARNING_TESTS") == "1", "CUDA correctness requires an allocated window")
class LearningCudaTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        torch.set_num_threads(1)
        torch.backends.fp32_precision = "ieee"
        torch.backends.cuda.matmul.fp32_precision = "tf32"

    def test_captured_behavior_parity_rng_refresh_and_genuine_step(self):
        torch.manual_seed(9001)
        config = PolicyConfig(action_kind_bias=(0.,) * 16)
        policy = LearningPolicy(config).cuda()
        data = fixture(32, "cuda")
        host = host_fixture(*data)
        actor = LearningActor(policy, 32, graph=True)
        actor_before = copy.deepcopy(actor.policy.state_dict())
        addresses = [actor.input.data_ptr(), actor.packet.data_ptr()] + [p.data_ptr() for p in actor.policy.parameters()]
        draws = []
        packet = None
        for _ in range(4):
            actions, packet = actor.act(host)
            draws.append(actions.copy())
            with torch.no_grad():
                logits, values = policy(*data)
                expected = logits.log_softmax(-1).gather(1, torch.from_numpy(actions).cuda()[:, None]).squeeze(1)
            np.testing.assert_allclose(packet[:, 1], expected.cpu().numpy(), rtol=2e-5, atol=2e-5)
            np.testing.assert_allclose(packet[:, 2], values.cpu().numpy(), rtol=2e-5, atol=2e-5)
        self.assertTrue(any(not np.array_equal(draws[0], draw) for draw in draws[1:]))
        learner = PPOLearner(policy)
        packet_gpu = torch.from_numpy(packet).cuda()
        actual_behavior = (*data, packet_gpu[:, 0].long(), packet_gpu[:, 1], packet_gpu[:, 2],
                           torch.ones(32, device="cuda"), torch.linspace(-1, 1, 32, device="cuda"))
        result = learner.step(*actual_behavior)
        self.assertTrue(result["accepted"])
        self.assertLess(result["approx_kl"], 1e-6)
        assert_state_equal(self, actor.policy.state_dict(), actor_before)
        actor.refresh(policy, version=1)
        self.assertEqual(addresses, [actor.input.data_ptr(), actor.packet.data_ptr()] + [p.data_ptr() for p in actor.policy.parameters()])
        changed_obs = data[0].clone()
        changed_obs[:, 19] = 9999
        host = host_fixture(changed_obs, data[1], data[2])
        actions, packet = actor.act(host)
        with torch.no_grad():
            logits, values = policy(changed_obs, data[1], data[2])
            logp = logits.log_softmax(-1).gather(1, torch.from_numpy(actions).cuda()[:, None]).squeeze(1)
        np.testing.assert_allclose(packet[:, 1], logp.cpu().numpy(), rtol=2e-5, atol=2e-5)
        np.testing.assert_allclose(packet[:, 2], values.cpu().numpy(), rtol=2e-5, atol=2e-5)
        learner.validate_state()
        policy_before, optimizer_before = copy.deepcopy(policy.state_dict()), copy.deepcopy(learner.optimizer.state_dict())
        stale = list(behavior_batch(policy, (changed_obs, data[1], data[2])))
        stale[4] -= 1
        self.assertEqual(learner.step(*stale)["reason"], "target_kl")
        assert_state_equal(self, policy.state_dict(), policy_before)
        assert_state_equal(self, learner.optimizer.state_dict(), optimizer_before)

    def test_captured_backward_accepted_update_and_gradient_parity(self):
        torch.manual_seed(9221)
        eager_policy = LearningPolicy().cuda()
        captured_policy = copy.deepcopy(eager_policy)
        eager = PPOLearner(eager_policy)
        captured = PPOLearner(captured_policy, mode="captured_backward", batch=128)
        data = fixture(128, "cuda")
        for iteration in range(3):
            batch = list(behavior_batch(eager_policy, data))
            batch[-1] = torch.linspace(-1, 1, 128, device="cuda")
            if iteration == 1:
                # Both clipping signs occur; the speculative ±20 clamp stays
                # exactly inactive for this accepted genuine PPO objective.
                batch[4] += torch.linspace(-.1, .1, 128, device="cuda")
            left = eager.step(*batch)
            right = captured.step(*batch)
            self.assertTrue(left["accepted"] and right["accepted"])
            for key in ("loss", "policy_loss", "value_loss", "entropy", "approx_kl", "clip_fraction", "grad_norm"):
                self.assertAlmostEqual(left[key], right[key], delta=2e-5)
            for lparam, rparam in zip(eager_policy.parameters(), captured_policy.parameters()):
                torch.testing.assert_close(lparam, rparam, rtol=2e-5, atol=2e-6)
                torch.testing.assert_close(lparam.grad, rparam.grad, rtol=2e-4, atol=2e-6)
        # Partial minibatch uses eager, then full batch safely rebuilds gradient
        # capture after eager zero_grad may have replaced gradient addresses.
        partial = tuple(value[:17] for value in behavior_batch(captured_policy, data))
        self.assertTrue(captured.step(*partial)["accepted"])
        self.assertTrue(captured.step(*behavior_batch(captured_policy, data))["accepted"])

    def test_captured_backward_rejects_without_parameter_or_adam_mutation(self):
        policy = LearningPolicy().cuda()
        learner = PPOLearner(policy, mode="captured_backward", batch=64)
        data = fixture(64, "cuda")
        self.assertTrue(learner.step(*behavior_batch(policy, data))["accepted"])
        before_policy, before_optimizer = copy.deepcopy(policy.state_dict()), copy.deepcopy(learner.optimizer.state_dict())
        for offset, expected_reason in ((1000., "log_ratio_guard"), (1., "target_kl")):
            batch = list(behavior_batch(policy, data))
            batch[4] -= offset
            result = learner.step(*batch)
            self.assertFalse(result["accepted"])
            self.assertEqual(result["reason"], expected_reason)
            assert_state_equal(self, policy.state_dict(), before_policy)
            assert_state_equal(self, learner.optimizer.state_dict(), before_optimizer)
        # Speculative safe gathers must never accept their clamped indices.
        for bad_action in (64, -1, 5):
            batch = list(behavior_batch(policy, data))
            batch[3].fill_(bad_action)
            with self.assertRaises(InvalidLearningBatch):
                learner.step(*batch)
            assert_state_equal(self, policy.state_dict(), before_policy)
            assert_state_equal(self, learner.optimizer.state_dict(), before_optimizer)
        batch = list(behavior_batch(policy, data))
        batch[-1][0] = float("nan")
        with self.assertRaises(InvalidLearningBatch):
            learner.step(*batch)
        assert_state_equal(self, policy.state_dict(), before_policy)
        assert_state_equal(self, learner.optimizer.state_dict(), before_optimizer)
        # Rejected speculative gradients are overwritten, including NaNs.
        self.assertTrue(learner.step(*behavior_batch(policy, data))["accepted"])
        saved = learner.state_dict()
        learner.load_state_dict(saved)
        self.assertTrue(learner.step(*behavior_batch(policy, data))["accepted"])


if __name__ == "__main__":
    unittest.main()
