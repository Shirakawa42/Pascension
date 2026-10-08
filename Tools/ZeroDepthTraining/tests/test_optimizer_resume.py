"""Actual Adam restores must honor the pinned rate without losing moments."""
import copy
import unittest

import torch

from train import Learner, TrainConfig


class OptimizerResumeTests(unittest.TestCase):
    def test_restores_moments_steps_and_approved_new_rate(self):
        model = torch.nn.Linear(3, 2)
        old = Learner(model, TrainConfig(learning_rate=.0003))
        model(torch.ones(2, 3)).square().mean().backward()
        old.optimizer.step()
        saved = copy.deepcopy(old.optimizer.state_dict())
        saved['param_groups'][0]['lr'] = .0001
        new = Learner(copy.deepcopy(model), TrainConfig(learning_rate=.0001))
        new.restore_optimizer(saved)
        actual = new.optimizer.state_dict()
        self.assertEqual(actual['param_groups'], saved['param_groups'])
        for key in saved['state']:
            for field, expected in saved['state'][key].items():
                torch.testing.assert_close(actual['state'][key][field], expected, rtol=0, atol=0)
        self.assertEqual(actual['param_groups'][0]['lr'], .0001)

    def test_rejects_silent_old_rate_restore_before_any_mutation(self):
        model = torch.nn.Linear(3, 2)
        old = Learner(model, TrainConfig(learning_rate=.0003))
        saved = copy.deepcopy(old.optimizer.state_dict())
        new = Learner(copy.deepcopy(model), TrainConfig(learning_rate=.0001))
        before = copy.deepcopy(new.optimizer.state_dict())
        with self.assertRaisesRegex(RuntimeError, 'learning rate'):
            new.restore_optimizer(saved)
        self.assertEqual(new.optimizer.state_dict(), before)

    def test_same_rate_remains_compatible(self):
        model = torch.nn.Linear(3, 2)
        old = Learner(model, TrainConfig())
        new = Learner(copy.deepcopy(model), TrainConfig())
        new.restore_optimizer(old.optimizer.state_dict())
        self.assertEqual(new.optimizer.param_groups[0]['lr'], .0003)


if __name__ == '__main__':
    unittest.main()
