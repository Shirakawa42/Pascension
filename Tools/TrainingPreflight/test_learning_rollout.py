"""CPU checks for terminal credit and immutable complete-episode storage."""
from types import SimpleNamespace
import unittest

import numpy as np
import torch

from learning_rollout import EpisodeStore, terminal_returns


class CreditTests(unittest.TestCase):
    def test_consecutive_and_defending_decisions_keep_their_actual_seat(self):
        # Seat 0 acts twice, seat 1 defends during seat 0's turn, then each
        # acts again. Episode 1 is a true draw; episode 2 is won by seat 1.
        result = terminal_returns([0, 0, 0, 0, 0, 1, 2], [0, 0, 1, 0, 1, 1, 0],
                                  [[1, -1], [0, 0], [-1, 1]], [True, True, True])
        np.testing.assert_array_equal(result, [1, 1, -1, 1, -1, 0, -1])

    def test_unfinished_or_administratively_truncated_is_not_a_draw(self):
        with self.assertRaisesRegex(ValueError, "Unresolved"):
            terminal_returns([0], [0], [[0, 0]], [False])
        with self.assertRaisesRegex(ValueError, "zero-sum"):
            terminal_returns([0], [0], [[1, 1]], [True])

    def test_owned_data_survives_reused_input_and_reward_wait(self):
        store = EpisodeStore(64, "cpu")
        store.reset(7)
        actor = SimpleNamespace(obs=torch.ones(2, 2048), candidates=torch.ones(2, 64, 32),
                                mask=torch.zeros(2, 64, dtype=torch.bool))
        actor.mask[:, 5] = True
        packet = torch.tensor([[5., -.2, .4], [5., -.3, -.2]])
        store.append(actor, packet, [0, 1], [1, 0])
        actor.obs.fill_(999)
        actor.candidates.zero_()
        actor.mask.zero_()
        packet.fill_(float("nan"))
        self.assertTrue(torch.equal(store.obs[:2], torch.ones(2, 2048)))
        self.assertTrue(bool(store.mask[:2, 5].all()))
        self.assertTrue(torch.equal(store.old_logp[:2], torch.tensor([-.2, -.3])))
        store.seal([[1, -1], [-1, 1]], [True, True])
        self.assertTrue(torch.equal(store.returns, torch.tensor([-1., -1.])))
        batch = store.minibatch(torch.tensor([1, 0]))
        self.assertTrue(torch.equal(batch["old_values"], torch.tensor([-.2, .4])))
        self.assertEqual(store.version, 7)
        with self.assertRaisesRegex(RuntimeError, "Cannot append"):
            store.append(actor, packet, [], [])

    def test_capacity_never_wraps_unresolved_rows(self):
        store = EpisodeStore(64, "cpu")
        store.reset(0)
        store.rows = 63
        with self.assertRaisesRegex(RuntimeError, "full"):
            store.append(None, None, [0, 1], [0, 1])
        self.assertEqual(store.rows, 63)

    def test_packed_actor_rows_retain_original_episode_identity(self):
        store = EpisodeStore(64, "cpu")
        store.reset(2)
        actor = SimpleNamespace(obs=torch.stack((torch.full((2048,), 30.), torch.full((2048,), 10.))),
            candidates=torch.zeros(2, 64, 32), mask=torch.ones(2, 64, dtype=torch.bool))
        store.append(actor, torch.tensor([[5., -.2, .4], [7., -.3, -.2]]),
                     [3, 1], [1, 0], source_rows=[0, 1])
        store.seal([[0, 0], [1, -1], [0, 0], [1, -1]], [True]*4)
        np.testing.assert_array_equal(store.episode_ids[:2], [3, 1])
        self.assertTrue(torch.equal(store.returns, torch.tensor([-1., 1.])))
        self.assertTrue(torch.equal(store.obs[:2, 0], torch.tensor([30., 10.])))


if __name__ == "__main__":
    unittest.main()
