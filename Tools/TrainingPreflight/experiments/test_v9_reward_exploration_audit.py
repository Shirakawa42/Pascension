"""CPU-only synthetic contracts; these do not measure playing strength.

Run with CUDA_VISIBLE_DEVICES='' and PYTHONPATH containing parent/experiments.
No checkpoints, live processes, executable training sources, or GPU are modified.
"""
import copy
from types import SimpleNamespace
import unittest

import numpy as np
import torch

import variant_v9_runtime as runtime
import learning_model as lm
from model import sample_actions


class RewardExplorationContracts(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        torch.set_num_threads(1)
        runtime.install()

    def setUp(self):
        torch.manual_seed(927)
        self.policy = lm.LearningPolicy(lm.PolicyConfig(width=128))
        self.obs = torch.zeros(8, 2816)
        self.candidates = torch.zeros(8, 64, 32)
        self.mask = torch.zeros(8, 64, dtype=torch.bool)
        self.mask[:, :6] = True
        for i, kind in enumerate([0, 4, 7, 7, 6, 6]):
            self.candidates[:, i, kind] = 1
            self.candidates[:, i, 16] = (i + 1) / 192

    def batch(self):
        with torch.no_grad():
            logits, values = self.policy(self.obs, self.candidates, self.mask)
            packet = sample_actions(logits, values)
        return [self.obs, self.candidates, self.mask, packet[:, 0].long(),
                packet[:, 1], values, torch.ones(8), torch.ones(8)]

    def test_installed_actor_records_exact_mixed_behavior_likelihood(self):
        self.assertEqual(self.policy.core.trunk[0].in_features, 2048)
        actor = lm.LearningActor(self.policy, 8, graph=False)
        host = SimpleNamespace(upload=torch.cat((self.obs.flatten(),
            self.candidates.flatten(), self.mask.float().flatten())))
        actions, packet = actor.act(host)
        logits, values = self.policy(self.obs, self.candidates, self.mask)
        expected = logits.log_softmax(-1).gather(1, torch.from_numpy(actions)[:, None])[:, 0]
        np.testing.assert_allclose(packet[:, 1], expected.detach().numpy(), rtol=1e-6, atol=1e-6)
        np.testing.assert_allclose(packet[:, 2], values.detach().numpy(), rtol=1e-6, atol=1e-6)

    def test_positive_and_negative_advantage_move_selected_action_oppositely(self):
        original = copy.deepcopy(self.policy.state_dict())
        batch = self.batch()
        for sign in (1, -1):
            self.policy.load_state_dict(original)
            learner = lm.PPOLearner(self.policy, lm.PPOConfig(entropy_coefficient=0, value_coefficient=0))
            batch[-1] = torch.full((8,), float(sign))
            result = learner.step(*batch)
            self.assertTrue(result['accepted'])
            logits, _ = self.policy(self.obs, self.candidates, self.mask)
            selected = logits.log_softmax(-1).gather(1, batch[3][:, None])[:, 0]
            self.assertGreater(float((selected.detach() - batch[4]).mean()) * sign, 0)

    def test_mixed_behavior_has_exact_legal_category_mass(self):
        baseline = copy.deepcopy(self.policy)
        baseline.choice_exploration.zero_()
        raw, _ = baseline(self.obs, self.candidates, self.mask)
        mixed, _ = self.policy(self.obs, self.candidates, self.mask)
        expected = .92 * raw.softmax(-1)
        expected[:, 2:4] += .03
        expected[:, 4:6] += .01
        torch.testing.assert_close(mixed.softmax(-1), expected, rtol=1e-5, atol=1e-7)
        self.assertTrue((mixed.softmax(-1)[:, 6:] == 0).all())

    def test_gumbel_sampling_is_categorical_and_returns_actual_log_probability(self):
        probabilities = torch.tensor([.1, .3, .6])
        logits = probabilities.log().expand(80000, -1)
        packet = sample_actions(logits, torch.zeros(80000))
        frequencies = torch.bincount(packet[:, 0].long(), minlength=3) / 80000
        torch.testing.assert_close(frequencies, probabilities, rtol=0, atol=.006)
        torch.testing.assert_close(packet[:, 1], probabilities.log()[packet[:, 0].long()])

    def test_potential_shaping_telescopes_and_shifted_baseline_cancels(self):
        # Algebraic example, not an implementation or claim of better credit.
        rewards = torch.tensor([0., 0., 0., 1.], dtype=torch.float64)
        potential = torch.tensor([.1, .4, .2, .7, 0.], dtype=torch.float64)
        values = torch.tensor([.2, .3, .25, .6], dtype=torch.float64)
        returns = rewards.flip(0).cumsum(0).flip(0)
        shaped = rewards + potential[1:] - potential[:-1]
        shaped_returns = shaped.flip(0).cumsum(0).flip(0)
        torch.testing.assert_close(shaped_returns, returns - potential[:-1])
        torch.testing.assert_close(shaped_returns - (values - potential[:-1]), returns - values)


if __name__ == '__main__':
    unittest.main()
