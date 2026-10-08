"""Independent CPU integration contracts for V7; synthetic data, no campaign."""
import copy
import unittest

import torch

from choice_policy_v7 import ChoicePolicy, context_bytes
from learning_model import LearningActor, PPOLearner
from test_learning_model import assert_state_equal, behavior_batch, fixture, host_fixture


class V7LearningIntegrityTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        torch.set_num_threads(1)

    def setUp(self):
        torch.manual_seed(2692026)
        self.policy = ChoicePolicy()
        self.data = fixture(16)
        obs, candidates, mask = self.data
        # Half Volos mode menus, half ordinary relic/destiny choice menus.
        candidates[:, :, :16] = 0
        candidates[:8, :, 12] = 1
        candidates[:8, :, 25] = torch.arange(64).clamp_max(3) / 128
        obs[:8, 112:116] = torch.tensor(context_bytes('soi.volos'))
        candidates[8:, 0, 7] = 1
        candidates[8:, 1, 6] = 1
        candidates[8:, 2:, 0] = 1

    def batch(self, policy):
        batch = list(behavior_batch(policy, self.data))
        batch[-1] = torch.linspace(-1, 1, len(batch[-1]))
        return batch

    def test_frozen_actor_saves_actual_mixture_likelihood(self):
        actor = LearningActor(self.policy, 16, graph=False, device='cpu')
        actions, packet = actor.act(host_fixture(*self.data))
        logits, values = self.policy(*self.data)
        expected = logits.log_softmax(-1).gather(1, torch.tensor(actions)[:, None]).squeeze(1)
        torch.testing.assert_close(torch.tensor(packet[:, 1]), expected, rtol=0, atol=0)
        torch.testing.assert_close(torch.tensor(packet[:, 2]), values, rtol=0, atol=0)
        actor_state = copy.deepcopy(actor.policy.state_dict())
        learner = PPOLearner(self.policy)
        self.assertTrue(learner.step(*self.batch(self.policy))['accepted'])
        assert_state_equal(self, actor.policy.state_dict(), actor_state)
        actor.refresh(self.policy, 11)
        assert_state_equal(self, actor.policy.state_dict(), self.policy.state_dict())

    def test_v7_resume_preserves_next_adam_update_and_sampling(self):
        learner = PPOLearner(self.policy)
        self.assertTrue(learner.step(*self.batch(self.policy))['accepted'])
        saved = learner.state_dict()
        draws = torch.rand(16)
        self.assertTrue(learner.step(*self.batch(self.policy))['accepted'])
        expected_policy = copy.deepcopy(self.policy.state_dict())
        expected_adam = copy.deepcopy(learner.optimizer.state_dict())
        restored = PPOLearner(ChoicePolicy())
        restored.load_state_dict(saved)
        torch.testing.assert_close(torch.rand(16), draws, rtol=0, atol=0)
        self.assertTrue(restored.step(*self.batch(restored.policy))['accepted'])
        assert_state_equal(self, restored.policy.state_dict(), expected_policy)
        assert_state_equal(self, restored.optimizer.state_dict(), expected_adam)

    def test_v7_rejected_ratio_does_not_mutate_new_head_or_adam(self):
        learner = PPOLearner(self.policy)
        self.assertTrue(learner.step(*self.batch(self.policy))['accepted'])
        previous = learner.state_dict()
        bad_batch = self.batch(self.policy)
        bad_batch[4] = bad_batch[4] - 11
        result = learner.step(*bad_batch)
        self.assertFalse(result['accepted'])
        self.assertEqual(result['reason'], 'log_ratio_guard')
        assert_state_equal(self, self.policy.state_dict(), previous['policy'])
        assert_state_equal(self, learner.optimizer.state_dict(), previous['optimizer'])

    def test_mixture_chain_rule_matches_independent_autograd(self):
        # The fused CUDA derivative is ds_j = g_j*r_j-p_j*sum_i(g_i*r_i).
        # Check that identity independently of ChoicePolicy's implementation.
        for scale in (1., 25., 200.):
            scores = (torch.randn(32, 64, dtype=torch.float64) * scale).requires_grad_()
            legal = torch.rand(32, 64) > .35
            legal[:, 0] = True
            probabilities = scores.masked_fill(~legal, -torch.inf).softmax(-1)
            chosen = legal & (torch.arange(64)[None] % 3 == 0)
            # Logaddexp retains very small legal base probabilities.
            base_log = scores.masked_fill(~legal, -torch.inf).log_softmax(-1)
            uniform_log = torch.where(chosen, (.06 / chosen.sum(-1, keepdim=True)).log(), -torch.inf)
            # Omit illegal entries before logaddexp: -inf/-inf has an undefined
            # derivative even when a subsequent mask sets its output to zero.
            logs = torch.logaddexp(base_log[legal] + torch.tensor(.94, dtype=torch.float64).log(), uniform_log[legal])
            output_gradient = torch.randn_like(scores).masked_fill(~legal, 0)
            gradient, = torch.autograd.grad((logs * output_gradient[legal]).sum(), scores)
            responsibility = torch.zeros_like(scores)
            responsibility[legal] = torch.exp(base_log[legal] + torch.tensor(.94, dtype=torch.float64).log() - logs)
            weighted = output_gradient * responsibility
            expected = weighted - probabilities * weighted.sum(-1, keepdim=True)
            torch.testing.assert_close(gradient, expected, rtol=1e-10, atol=1e-12)
            self.assertTrue(torch.isfinite(gradient).all())


if __name__ == '__main__':
    unittest.main()
