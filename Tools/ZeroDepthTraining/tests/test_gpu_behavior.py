"""CPU regression tests for graph bindings and verification orchestration."""
import copy
from types import SimpleNamespace
import unittest
from unittest import mock
import numpy as np
import torch
from torch import nn
import gpu_behavior as module
from train import Learner, TrainConfig


class ToyPolicy(nn.Module):
    def __init__(self):
        super().__init__()
        self.weight = nn.Parameter(torch.tensor(.2))
        self.register_buffer("effect_table", torch.tensor([.03]))
    def forward(self, obs, candidates, mask):
        logits = candidates[..., 0] + self.weight * obs[:, :1]
        return logits.masked_fill(~mask.bool(), -1.e9), self.weight * obs[:, 0] + self.effect_table[0]


class CpuCapturedGraph(module.BehaviorGraph):
    """Retain the production guard/compute/run code, replacing CUDA replay only."""
    def __init__(self, policy, store, rows, **kwargs):
        self.policy, self.store, self.device = policy, store, torch.device("cpu")
        self.rows = rows.detach().clone()
        self.maximum = torch.zeros(2)
        self.source_tensors = [getattr(store, name) for name in ("obs", "candidates", "mask", "packet")]
        self.source_signatures = [module.tensor_signature(value) for value in self.source_tensors]
        self.policy_tensors = dict(policy.named_parameters()) | dict(policy.named_buffers())
        self.policy_signatures = {name: module.tensor_signature(value) for name, value in self.policy_tensors.items()}
        self.calls = 0
        self.graph = SimpleNamespace(replay=self.replay)
    def replay(self):
        self.calls += 1
        self.compute()


def fixture(count=5):
    policy = ToyPolicy()
    obs = torch.arange(count * 2, dtype=torch.float32).reshape(count, 2) / 10
    candidates = torch.tensor([0., .1, -.1]).repeat(count, 1).unsqueeze(-1)
    mask = torch.ones((count, 3), dtype=torch.bool)
    with torch.no_grad():
        logits, values = policy(obs, candidates, mask)
        packet = torch.stack((torch.zeros(count), logits.log_softmax(-1)[:, 0], values), 1)
    store = SimpleNamespace(gpu=True, obs=obs, candidates=candidates, mask=mask, packet=packet)
    config = TrainConfig(width=64, batch=2, capacity=64, minibatch=16, epochs=1, graph=False, fused_optimizer=False)
    learner = Learner(policy, config)
    learner.config.minibatch = 2
    return learner, store


class BindingTests(unittest.TestCase):
    def test_full_partial_reuse_and_all_rows_match_original_eager_verification(self):
        learner, store = fixture()
        for indices in (np.array([4, 0, 3, 1, 2]), np.array([3]), np.array([2, 1, 0, 4])):
            expected = learner.verify_behavior(store, indices)
            with mock.patch.object(module, "BehaviorGraph", CpuCapturedGraph):
                actual = module.verify_behavior(learner, store, indices)
            self.assertEqual(actual, expected)
        self.assertEqual(learner.behavior_graph.calls, 4)

    def test_all_replaced_owned_input_tensors_are_rejected_before_replay(self):
        for name in ("obs", "candidates", "mask", "packet"):
            with self.subTest(name=name):
                learner, store = fixture()
                with mock.patch.object(module, "BehaviorGraph", CpuCapturedGraph):
                    module.verify_behavior(learner, store, np.arange(5))
                graph = learner.behavior_graph
                count = graph.calls
                setattr(store, name, getattr(store, name).clone())
                with self.assertRaisesRegex(ValueError, "input tensor"):
                    module.verify_behavior(learner, store, np.arange(5))
                self.assertEqual(graph.calls, count)

    def test_replaced_policy_parameter_buffer_or_policy_object_is_rejected(self):
        for change in ("parameter", "buffer", "policy"):
            with self.subTest(change=change):
                learner, store = fixture()
                with mock.patch.object(module, "BehaviorGraph", CpuCapturedGraph):
                    module.verify_behavior(learner, store, np.arange(5))
                if change == "parameter":
                    learner.policy.weight = nn.Parameter(learner.policy.weight.detach().clone())
                elif change == "buffer":
                    learner.policy.effect_table = learner.policy.effect_table.clone()
                else:
                    learner.policy = copy.deepcopy(learner.policy)
                with self.assertRaisesRegex(ValueError, "policy"):
                    module.verify_behavior(learner, store, np.arange(5))

    def test_inplace_parameter_buffer_and_packet_updates_use_current_values(self):
        learner, store = fixture()
        with mock.patch.object(module, "BehaviorGraph", CpuCapturedGraph):
            module.verify_behavior(learner, store, np.arange(5))
        graph = learner.behavior_graph
        with torch.no_grad():
            learner.policy.weight.fill_(.7)
            learner.policy.effect_table.fill_(.2)
            logits, values = learner.policy(store.obs, store.candidates, store.mask)
            store.packet[:, 1] = logits.log_softmax(-1)[:, 0]
            store.packet[:, 2] = values
        actual = module.verify_behavior(learner, store, np.arange(5))
        self.assertTrue(actual["behavior_verification_complete"])
        self.assertEqual(actual["behavior_value_error"], 0.)
        self.assertIs(graph, learner.behavior_graph)

    def test_partial_and_full_nonfinite_or_bad_behavior_is_never_hidden(self):
        for row, field, value in ((4, 1, float("nan")), (0, 2, float("inf")), (4, 2, 1.)):
            with self.subTest(row=row, field=field, value=value):
                learner, store = fixture()
                store.packet[row, field] = value
                with mock.patch.object(module, "BehaviorGraph", CpuCapturedGraph), self.assertRaisesRegex(RuntimeError, "mismatch"):
                    module.verify_behavior(learner, store, np.arange(5))

    def test_stop_and_constructor_stop_never_mutate_policy_adam_or_rng(self):
        learner, store = fixture()
        before = {name: value.clone() for name, value in learner.policy.state_dict().items()}
        optimizer = copy.deepcopy(learner.optimizer.state_dict())
        rng = torch.get_rng_state().clone()
        stopped = module.verify_behavior(learner, store, np.arange(5), stop_check=lambda: True)
        self.assertFalse(stopped["behavior_verification_complete"])
        with mock.patch.object(module, "BehaviorGraph", side_effect=module.VerificationSetupStopped):
            stopped = module.verify_behavior(learner, store, np.arange(5))
        self.assertFalse(stopped["behavior_verification_complete"])
        for name, value in learner.policy.state_dict().items():
            torch.testing.assert_close(value, before[name], rtol=0, atol=0)
        self.assertEqual(learner.optimizer.state_dict(), optimizer)
        self.assertTrue(torch.equal(torch.get_rng_state(), rng))


if __name__ == "__main__":
    unittest.main(verbosity=2)
