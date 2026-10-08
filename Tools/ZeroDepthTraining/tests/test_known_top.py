import copy
import unittest

import torch

from known_top import center_top_ids
from model import Policy, PolicyConfig


def fixture():
    catalog = dict(obs_dim=24576, max_actions=4, action_dim=48,
                   card_ids=['a', 'b', 'c'], observation_schema='shards-zero-depth-observation-v2')
    policy = Policy(catalog, PolicyConfig(width=16, key_width=8))
    obs = torch.zeros(2, 24576)
    facts = obs[:, 17152:20224].reshape(2, 384, 8)
    for i in range(3):
        facts[:, i, :] = torch.tensor([(i + 1)/192, 1/3, i/384, i/384, 1, 0, 1, (i+1)/65536])
    candidates = torch.zeros(2, 4, 48)
    candidates[:, :, 16] = torch.tensor([1, 2, 3, 0])/192
    return policy, obs, candidates, torch.ones(2, 4, dtype=torch.bool)


class KnownTopTests(unittest.TestCase):
    def test_card_order_is_independent_of_fact_row_order(self):
        _, obs, _, _ = fixture()
        facts = obs[:, 17152:20224].reshape(2, 384, 8)
        facts[:, :3] = facts[:, [2, 0, 1]].clone()
        self.assertEqual(center_top_ids(obs, 3).tolist(), [[1, 2, 3], [1, 2, 3]])

    def test_uncertain_personal_absent_conflicting_and_invalid_facts_are_not_visible(self):
        for column, value in [(1, 2/3), (1, 4/3), (4, 0), (5, 1), (6, 0), (0, 4/192), (3, 1/384)]:
            with self.subTest(column=column, value=value):
                _, obs, _, _ = fixture()
                obs[:, 17152 + column] = value
                self.assertEqual(center_top_ids(obs, 3)[:, 0].tolist(), [0, 0])
        _, obs, _, _ = fixture()
        obs[:, 17152 + 24:17152 + 32] = obs[:, 17152:17152 + 8]
        self.assertEqual(center_top_ids(obs, 3)[:, 0].tolist(), [0, 0])

    def test_removal_moves_known_second_and_third_to_first_and_second(self):
        _, obs, _, _ = fixture()
        facts = obs[:, 17152:20224].reshape(2, 384, 8)
        facts[:, 0] = 0
        facts[:, 1:3, 2:4] -= 1/384
        self.assertEqual(center_top_ids(obs, 3).tolist(), [[2, 3, 0], [2, 3, 0]])

    def test_initial_enable_preserves_logits_values_and_parameter_inventory(self):
        p, *args = fixture()
        inventory = [(n, tuple(t.shape)) for n, t in p.named_parameters()]
        before = p(*args)
        p.known_top_enabled.fill_(1)
        for a, b in zip(before, p(*args)):
            self.assertTrue(torch.equal(a, b))
        self.assertEqual(inventory, [(n, tuple(t.shape)) for n, t in p.named_parameters()])

    def test_new_weights_receive_gradients_only_for_known_cards(self):
        for known in (True, False):
            p, obs, c, m = fixture()
            p.known_top_enabled.fill_(1)
            if not known:
                obs[:, 17152:20224] = 0
            logits, value = p(obs, c, m)
            logits[:, 0].sum().backward()
            gradient = p.trunk[0].weight.grad[:, 178:202].abs().sum().item()
            self.assertEqual(gradient > 0, known)

    def test_resume_and_legacy_archive_switch_preserve_behavior(self):
        p, *args = fixture()
        legacy = {k: v.clone() for k, v in p.state_dict().items() if not k.startswith('known_top_')}
        p.known_top_enabled.fill_(1)
        with torch.no_grad():
            p.trunk[0].weight[:, 178:202].add_(.1)
        q = copy.deepcopy(p)
        q.load_state_dict(p.state_dict())
        for a, b in zip(p(*args), q(*args)):
            self.assertTrue(torch.equal(a, b))
        q.load_state_dict(legacy)
        self.assertEqual(q.known_top_enabled.item(), 0)
        self.assertTrue(torch.equal(q.known_top_anchor, legacy['trunk.0.weight'][:, 178:202]))
        bad = p.state_dict()
        del bad['known_top_anchor']
        with self.assertRaises(RuntimeError):
            q.load_state_dict(bad)


if __name__ == '__main__':
    unittest.main()
