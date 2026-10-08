"""Analytic diagnostics and optimizer equivalence; CPU synthetic data only."""
import copy
import math
from pathlib import Path
import sys
from types import SimpleNamespace
import unittest
from unittest.mock import patch

import numpy as np
import torch

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from train import Learner
import gpu_ppo_step as diagnostics

torch.set_num_threads(1)


class Policy(torch.nn.Module):
    def __init__(self):
        super().__init__()
        self.w = torch.nn.Parameter(torch.zeros(4))
        self.v = torch.nn.Parameter(torch.tensor(0.))

    def forward(self, obs, candidates, mask):
        return (obs[:, :1] * self.w).masked_fill(~mask, -1.e9), obs[:, 0] * self.v


def fixture(*, size=5, minibatch=3, lr=0., epochs=1):
    policy = Policy()
    obs = torch.arange(1, size + 1, dtype=torch.float32).reshape(-1, 1)
    counts = [2, 4, 1, 3, 2][:size]
    mask = torch.arange(4)[None, :] < torch.tensor(counts)[:, None]
    candidates = torch.zeros(size, 4, 1)
    with torch.no_grad():
        logits, values = policy(obs, candidates, mask)
        packet = torch.stack((torch.zeros(size), logits.log_softmax(-1)[:, 0], values), -1)
    store = SimpleNamespace(gpu=True, obs=obs, candidates=candidates, mask=mask, packet=packet)
    config = SimpleNamespace(learning_rate=lr, fused_optimizer=False, graph=False,
                             epochs=epochs, minibatch=minibatch, target_kl=.03, entropy=0.)
    targets = np.array([1., 1., 1., -1., -1.], dtype=np.float32)[:size]
    advantages = np.array([.2, -.4, .3, .1, -.2], dtype=np.float32)[:size]
    return Learner(policy, config), store, np.arange(size), targets, advantages


def baseline_update(policy, optimizer, store, rows, targets, advantages, config):
    """Original PPO math independent of the diagnostic accumulator."""
    steps = 0
    for _ in range(config.epochs):
        order = np.random.permutation(len(rows))
        for start in range(0, len(rows), config.minibatch):
            selected = rows[order[start:start + config.minibatch]]
            logits, values = policy(store.obs[selected], store.candidates[selected], store.mask[selected])
            logp_all = logits.log_softmax(-1)
            logp = logp_all.gather(1, store.packet[selected, :1].long()).squeeze(1)
            log_ratio = logp - store.packet[selected, 1]
            ratio = log_ratio.exp()
            if float(((ratio - 1) - log_ratio).mean().detach()) > config.target_kl:
                return steps
            advantage = torch.as_tensor(advantages[selected])
            actor = -torch.minimum(ratio * advantage, ratio.clamp(.8, 1.2) * advantage).mean()
            value = .5 * (values - torch.as_tensor(targets[selected])).square().mean()
            entropy = -(logp_all.exp() * logp_all).sum(-1).mean()
            loss = actor + .5 * value - config.entropy * entropy
            optimizer.zero_grad(set_to_none=True)
            loss.backward()
            torch.nn.utils.clip_grad_norm_(policy.parameters(), .5, error_if_nonfinite=True)
            optimizer.step()
            steps += 1
    return steps


class Tests(unittest.TestCase):
    def test_accepted_row_weighting_partial_batch_and_global_explained_variance(self):
        learner, store, rows, targets, advantages = fixture()
        with patch('numpy.random.permutation', return_value=np.arange(5)):
            result = learner.update(store, rows, targets, np.zeros_like(advantages))
        self.assertEqual(result['optimizer_steps'], 2)
        self.assertEqual(result['diagnostic_rows'], 5)
        self.assertAlmostEqual(result['loss'], .25)
        self.assertAlmostEqual(result['value_mse'], 1.)
        self.assertAlmostEqual(result['value_explained_variance'], 0., places=6)
        self.assertAlmostEqual(result['entropy'], sum(math.log(n) for n in (2, 4, 1, 3, 2)) / 5, places=6)
        self.assertAlmostEqual(result['normalized_entropy'], .8, places=6)
        self.assertAlmostEqual(result['mean_legal_actions'], 2.4, places=6)
        self.assertAlmostEqual(result['clip_fraction'], 0.)
        self.assertEqual(result['effective_epochs'], 1.)
        self.assertEqual(result['update_coverage'], 1.)
        self.assertIsNone(result['rejected_kl'])
        self.assertIn('row_weighted', result['diagnostic_scope'])

    def test_constant_targets_explained_variance_is_missing_not_invented(self):
        learner, store, rows, targets, advantages = fixture()
        result = learner.update(store, rows, np.ones_like(targets), advantages)
        self.assertIsNone(result['value_explained_variance'])

    def test_loss_mse_and_raw_gradient_norm_weight_short_batch_by_rows(self):
        learner, store, rows, targets, advantages = fixture()
        targets = np.array([1., 1., 1., 0., 0.], dtype=np.float32)
        norms = []
        original = torch.nn.utils.clip_grad_norm_
        def collect(*args, **kwargs):
            value = original(*args, **kwargs)
            norms.append(float(value))
            return value
        with patch('numpy.random.permutation', return_value=np.arange(5)),\
             patch('torch.nn.utils.clip_grad_norm_', side_effect=collect):
            result = learner.update(store, rows, targets, np.zeros_like(advantages))
        self.assertAlmostEqual(result['loss'], .15, places=6)
        self.assertAlmostEqual(result['value_mse'], .6, places=6)
        self.assertAlmostEqual(result['gradient_norm'], (3*norms[0]+2*norms[1])/5, places=6)

    def test_actual_graph_compute_math_matches_eager_aggregation_with_partial_batch(self):
        eager, store, rows, targets, advantages = fixture(lr=.0003)
        graph = Learner(copy.deepcopy(eager.policy), copy.copy(eager.config))
        graph.graph_learning = True
        compute_class = diagnostics.GpuPpoStep
        class CpuGraphMath:
            def __init__(self, policy, config, inputs, optimizer, **kwargs):
                self.math = object.__new__(compute_class)
                self.math.policy, self.math.config = policy, config
                self.math.parameters = list(policy.parameters())
                self.optimizer = optimizer
            def run(self, inputs):
                self.optimizer.zero_grad(set_to_none=True)
                self.math.inputs = inputs
                return self.math.compute().tolist()
        initial = np.random.RandomState(145).get_state()
        np.random.set_state(initial);expected=eager.update(store, rows, targets, advantages)
        np.random.set_state(initial)
        with patch.object(diagnostics, 'GpuPpoStep', CpuGraphMath):
            actual=graph.update(store, rows, targets, advantages)
        for key in (*diagnostics.DIAGNOSTIC_FIELDS[:8], 'value_explained_variance', 'effective_epochs', 'update_coverage'):
            self.assertAlmostEqual(expected[key], actual[key], places=7, msg=key)
        for first, second in zip(eager.policy.parameters(), graph.policy.parameters()):
            self.assertTrue(torch.equal(first, second))
        self.assertEqual(actual['accepted_minibatches'], 2)

    def test_first_rejected_graph_batch_reports_no_accepted_loss_or_adam_step(self):
        learner, store, rows, targets, advantages = fixture(size=3, minibatch=3)
        learner.graph_learning = True
        class Rejected:
            def __init__(self, *args, **kwargs):pass
            def run(self, inputs):return [.2, 999., 999., 999.]
        before = copy.deepcopy(learner.policy.state_dict())
        with patch.object(diagnostics, 'GpuPpoStep', Rejected):
            result=learner.update(store, rows, targets, advantages)
        self.assertEqual(result['accepted_minibatches'], 0)
        self.assertEqual(result['update_coverage'], 0.)
        self.assertEqual(result['rejected_kl'], .2)
        self.assertEqual(result['rejected_minibatch_rows'], 3)
        self.assertIsNone(result['loss'])
        self.assertEqual(len(learner.optimizer.state), 0)
        for key,value in before.items():self.assertTrue(torch.equal(value, learner.policy.state_dict()[key]))

    def test_kl_rejected_batch_is_reported_without_counting_or_stepping_it(self):
        learner, store, rows, targets, advantages = fixture(lr=1.)
        # Strong positive policy gradient on first3 rows makes the next selected
        # action much likelier than its recorded behavior, crossing KL .03.
        with patch('numpy.random.permutation', return_value=np.arange(5)):
            result = learner.update(store, rows, targets, np.ones_like(advantages))
        self.assertTrue(result['kl_early_stop'])
        self.assertEqual(result['optimizer_steps'], 1)
        self.assertEqual(result['example_passes'], 3)
        self.assertEqual(result['diagnostic_rows'], 3)
        self.assertEqual(result['rejected_minibatch_rows'], 2)
        self.assertGreater(result['rejected_kl'], learner.config.target_kl)
        self.assertAlmostEqual(result['approx_kl'], 0., places=7)
        self.assertAlmostEqual(result['entropy'], (math.log(2) + math.log(4)) / 3, places=6)
        self.assertAlmostEqual(result['update_coverage'], .6)
        for state in learner.optimizer.state.values():
            self.assertEqual(int(state['step']), 1)

    def test_new_diagnostics_preserve_original_weights_adam_and_numpy_rng(self):
        learner, store, rows, targets, advantages = fixture(lr=.0003, epochs=3)
        reference = copy.deepcopy(learner.policy)
        optimizer = torch.optim.Adam(reference.parameters(), lr=learner.config.learning_rate)
        initial = np.random.RandomState(14321).get_state()
        np.random.set_state(initial)
        count = baseline_update(reference, optimizer, store, rows, targets, advantages, learner.config)
        expected_rng = np.random.get_state()
        np.random.set_state(initial)
        torch_rng = torch.get_rng_state().clone()
        result = learner.update(store, rows, targets, advantages)
        self.assertTrue(torch.equal(torch_rng, torch.get_rng_state()))
        self.assertEqual(result['optimizer_steps'], count)
        actual_rng = np.random.get_state()
        self.assertEqual(expected_rng[0], actual_rng[0])
        np.testing.assert_array_equal(expected_rng[1], actual_rng[1])
        self.assertEqual(expected_rng[2:], actual_rng[2:])
        for expected, actual in zip(reference.parameters(), learner.policy.parameters()):
            self.assertTrue(torch.equal(expected, actual))
            self.assertTrue(torch.equal(expected.grad, actual.grad))
        for key, state in optimizer.state_dict()['state'].items():
            for name, value in state.items():
                self.assertTrue(torch.equal(value, learner.optimizer.state_dict()['state'][key][name]))

    def test_diagnostic_vector_is_detached_and_per_row_normalized(self):
        probability = torch.tensor([[.75, .25, 0., 0.], [.25, .25, .25, .25]])
        logp = probability.clamp_min(1.e-30).log().requires_grad_()
        per_row = -(probability * logp).sum(-1)
        ratio = torch.tensor([.7, 1.3], requires_grad=True)
        values = torch.tensor([.1, .4], requires_grad=True)
        target = torch.tensor([-1., 1.])
        mask = probability > 0
        result = diagnostics.ppo_diagnostics(torch.tensor(0.), torch.tensor(0.), per_row.mean(),
            torch.tensor(1.), per_row, ratio, values, target, mask, (values - target).square().mean())
        self.assertFalse(result.requires_grad)
        fields = dict(zip(diagnostics.DIAGNOSTIC_FIELDS, result.tolist()))
        expected_normalized = (-(.75*math.log(.75)+.25*math.log(.25))/math.log(2)+1) / 2
        self.assertAlmostEqual(fields['normalized_entropy'], expected_normalized, places=6)
        self.assertEqual(fields['clip_fraction'], 1.)
        self.assertAlmostEqual(fields['value_mse'], (1.1**2 + .6**2) / 2, places=6)

    def test_preexisting_cancellation_reports_zero_coverage_and_no_accepted_metrics(self):
        learner, store, rows, targets, advantages = fixture()
        before = copy.deepcopy(learner.optimizer.state_dict())
        result = learner.update(store, rows, targets, advantages, stop_check=lambda: True)
        self.assertEqual(result['update_coverage'], 0.)
        self.assertEqual(result['accepted_minibatches'], 0)
        self.assertIsNone(result['loss'])
        self.assertEqual(learner.optimizer.state_dict(), before)


if __name__ == '__main__':
    unittest.main()
