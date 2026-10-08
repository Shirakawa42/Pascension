"""Exercise real PPO updates while preserving every critic input and output."""
import unittest
from types import SimpleNamespace

import numpy as np
import torch

from train_against_search import configure_learning_scope, verify_frozen_state
from train import Learner, TrainConfig
from model import Policy, PolicyConfig
from scry_policy import ScryPolicy


class ActorOnlyTrainingTests(unittest.TestCase):
    def fixture(self, scope):
        torch.manual_seed(71)
        catalog = dict(obs_dim=24576, max_actions=64, action_dim=48,
            card_ids=['a', 'b', 'c'], contexts=['priority', 'soi.scry'],
            card_features=[[1., 0.], [0., 1.], [1., 1.]],
            observation_schema='shards-zero-depth-observation-v2',
            histograms=[dict(offset=256, length=3, scale=1)])
        base = Policy(catalog, PolicyConfig(width=64, key_width=8))
        with torch.no_grad():
            base.value.weight.normal_(std=.1)
            base.known_top_enabled.fill_(1)
            base.trunk[0].weight[:, 178:202].add_(.01)
        metadata, frozen = configure_learning_scope(base, scope)
        policy = ScryPolicy(base, temperature=64)
        obs = torch.zeros(16, 24576)
        obs[:, :128] = torch.rand(16, 128)
        obs[:, 256:259] = torch.rand(16, 3)
        obs[::2, 144] = 1
        obs[::2, 157] = .5
        obs[:, 17152:17160] = torch.tensor([1/192, 1/3, 0, 0, 1, 0, 1, 0])
        candidates = torch.rand(16, 64, 48)
        candidates[:, :, :16] = 0
        candidates[:, :, 16] = 0
        candidates[:, :3, 16] = torch.tensor([1, 2, 3])/192
        candidates[:, 0, 13] = 1
        candidates[:, 1:3, 12] = 1
        mask = torch.zeros(16, 64, dtype=torch.bool)
        mask[:, :3] = True
        with torch.no_grad():
            logits, value = policy(obs, candidates, mask)
        actions = torch.arange(16) % 3
        logp = logits.log_softmax(-1).gather(1, actions[:, None])[:, 0]
        packet = torch.stack((actions, logp, value), dim=1)
        store = SimpleNamespace(gpu=False, obs=obs.numpy(), candidates=candidates.numpy(),
            mask=mask.numpy(), packet=packet.numpy())
        return policy, metadata, frozen, (obs, candidates, mask), store, logits, value

    def test_real_ppo_changes_actions_but_preserves_critic_for_both_scopes(self):
        for scope in ('actor-head', 'actor'):
            with self.subTest(scope=scope):
                policy, metadata, frozen, inputs, store, before_z, before_v = self.fixture(scope)
                config = TrainConfig(width=64, minibatch=16, epochs=2,
                    learning_rate=1e-3, entropy=0., graph=False)
                learner = Learner(policy, config)
                returns = np.where(np.arange(16)%3 == 0, 1., -1.).astype(np.float32)
                stats = learner.update(store, np.arange(16), returns, returns - before_v.numpy())
                self.assertGreater(stats['optimizer_steps'], 0)
                verify_frozen_state(policy, frozen)
                with torch.no_grad():
                    after_z, after_v = policy(*inputs)
                self.assertTrue(torch.equal(before_v, after_v))
                self.assertFalse(torch.equal(before_z, after_z))
                self.assertTrue(metadata['critic_and_representation_frozen'])
                # Adam must have no momentum or step state for frozen weights.
                self.assertTrue(all(p.requires_grad for p in learner.optimizer.state))

    def test_guard_rejects_shared_representation_mutation(self):
        policy, _, frozen, *_ = self.fixture('actor')
        with torch.no_grad():
            policy.base.menu_projection.weight[0, 0].add_(.1)
        with self.assertRaisesRegex(RuntimeError, 'menu_projection.weight'):
            verify_frozen_state(policy, frozen)

    def test_full_scope_retains_existing_training_behavior(self):
        policy, metadata, frozen, *_ = self.fixture('full')
        self.assertFalse(metadata['critic_and_representation_frozen'])
        self.assertFalse(frozen)
        self.assertTrue(all(p.requires_grad for p in policy.parameters()))


if __name__ == '__main__':
    torch.set_num_threads(1)
    unittest.main()
