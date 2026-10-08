"""Packed inference ownership and graph refresh; no optimizer or real games."""
from pathlib import Path
import sys
from types import SimpleNamespace
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

import numpy as np
import torch
from adaptive import AdaptiveActor
from model import Policy, PolicyConfig
from train import Rollout


CATALOG = {"obs_dim": 32, "max_actions": 64, "action_dim": 48,
           "card_ids": ["crystal", "shard_reactor"], "candidate_card_scale": 192,
           "histogram_descriptors": [{"offset": 20, "length": 2, "scale": 10}],
           "card_features": [[1, 0, 0], [0, 1, 1]]}


def host(batch=128, lanes=None):
    result = SimpleNamespace(obs=np.zeros((batch, 32), np.float32),
                             candidates=np.zeros((batch, 64, 48), np.float32),
                             mask=np.zeros((batch, 64), np.float32),
                             actors=np.arange(batch, dtype=np.int32) % 2,
                             done=np.ones(batch, np.int32))
    lanes = np.arange(batch) if lanes is None else np.asarray(lanes, dtype=np.int64)
    result.done[lanes] = 0
    result.mask[lanes, :2] = 1
    result.obs[:, 0] = np.arange(batch) + 1
    result.obs[:, 20] = .1
    result.candidates[:, 0, 0] = 1
    result.candidates[:, 1, 10] = 1
    result.candidates[:, 0, 16] = 1 / 192
    result.candidates[:, 1, 16] = 2 / 192
    return result


class AdaptivePackingTests(unittest.TestCase):
    def setUp(self):
        torch.set_num_threads(1)
        torch.manual_seed(281017)
        self.policy = Policy(CATALOG, PolicyConfig(width=64))

    def test_maps_global_lanes_into_smallest_bucket_and_padding_is_inert(self):
        actor = AdaptiveActor(self.policy, 128, graph=False, device="cpu")
        state = host(lanes=[7, 40, 100])
        actions, packet = actor.act(state)
        self.assertEqual(actor.active_actor.batch, 32)
        np.testing.assert_array_equal(actor.source_rows([100, 7]), [2, 0])
        np.testing.assert_array_equal(actor.obs[:3].numpy(), state.obs[[7, 40, 100]])
        np.testing.assert_array_equal(actor.obs[3:].numpy(), 0)
        np.testing.assert_array_equal(actor.mask[3:, 0].numpy(), 1)
        np.testing.assert_array_equal(actor.mask[3:, 1:].numpy(), 0)
        self.assertTrue(np.all(actions[state.done != 0] == -1))
        np.testing.assert_array_equal(packet[state.done != 0], 0)
        with self.assertRaises(ValueError):
            actor.source_rows([40, 41])

    def test_optional_archive_mask_packs_only_requested_live_seats(self):
        actor = AdaptiveActor(self.policy, 128, graph=False, device="cpu")
        state = host()
        requested = np.zeros(128, bool)
        requested[[2, 93]] = True
        actions, _ = actor.act(state, active=requested)
        np.testing.assert_array_equal(actor.source_rows([93, 2]), [1, 0])
        self.assertTrue(np.all(actions[~requested] == -1))
        with self.assertRaises(ValueError):
            actor.source_rows([0])

    def test_bucket_changes_never_reuse_previous_lane_mapping_and_packets_are_owned(self):
        actor = AdaptiveActor(self.policy, 128, graph=False, device="cpu")
        first = host(lanes=[7, 40, 100])
        _, packet = actor.act(first)
        owned = packet.copy()
        actor.act(host())
        self.assertEqual(actor.active_actor.batch, 128)
        np.testing.assert_array_equal(actor.source_rows([7, 40, 100]), [7, 40, 100])
        np.testing.assert_array_equal(packet, owned)
        actor.act(host(lanes=[0]))
        with self.assertRaises(ValueError):
            actor.source_rows([7])

    def test_refresh_updates_all_eager_buckets_in_place(self):
        actor = AdaptiveActor(self.policy, 128, graph=False, device="cpu")
        self.assertEqual(set(actor.children), {32, 64, 128})
        addresses = {size: child.policy.cached_table.data_ptr() for size, child in actor.children.items()}
        with torch.no_grad():
            self.policy.card_embedding.weight[1].add_(.25)
            self.policy.value.bias.fill_(.125)
        actor.refresh(self.policy)
        for size, child in actor.children.items():
            self.assertEqual(child.policy.cached_table.data_ptr(), addresses[size])
            torch.testing.assert_close(child.policy.cached_table, self.policy.embedding_table())
        _, packet = actor.act(host(lanes=[7]))
        self.assertAlmostEqual(float(packet[7, 2]), float(torch.tanh(torch.tensor(.125))), places=6)

    def test_empty_and_invalid_selections_never_produce_experience(self):
        actor = AdaptiveActor(self.policy, 8, graph=False, device="cpu")
        actions, packet = actor.act(host(batch=8, lanes=[]))
        np.testing.assert_array_equal(actions, -1)
        np.testing.assert_array_equal(packet, 0)
        self.assertIsNone(actor.active_actor)
        self.assertEqual(actor.source_rows([]).size, 0)
        with self.assertRaises(ValueError):
            actor.source_rows([0])
        with self.assertRaises(RuntimeError):
            _ = actor.obs
        state = host(batch=8, lanes=[2])
        with self.assertRaises(ValueError):
            actor.act(state, active=np.ones(8, np.int32))
        with self.assertRaises(ValueError):
            actor.act(state, active=np.ones(8, bool))
        with self.assertRaises(ValueError):
            actor.source_rows([-.5])

    def test_non_power_of_two_batch_stays_bounded(self):
        actor = AdaptiveActor(self.policy, 70, graph=False, device="cpu")
        self.assertEqual(actor.buckets, (32, 64, 70))
        actor.act(host(batch=70, lanes=np.arange(40)))
        self.assertEqual(actor.active_actor.batch, 64)
        actor.act(host(batch=70))
        self.assertEqual(actor.active_actor.batch, 70)

    @unittest.skipUnless(torch.cuda.is_available(), "CUDA graph parity requires a GPU")
    def test_cuda_graph_matches_current_inputs_and_gpu_store_owns_mapped_rows(self):
        policy = self.policy.cuda()
        actor = AdaptiveActor(policy, 128, graph=True, device="cuda")
        state = host(lanes=[7, 40, 100])
        _, packet = actor.act(state)
        child = actor.active_actor
        with torch.inference_mode():
            logits, values = policy(actor.obs, actor.candidates, actor.mask)
            selected = actor.packet[:, :1].long()
            logp = logits.log_softmax(-1).gather(1, selected).squeeze(1)
        torch.testing.assert_close(logp, actor.packet[:, 1], rtol=0, atol=.0002)
        torch.testing.assert_close(values, actor.packet[:, 2], rtol=0, atol=.0001)
        store = Rollout(64, CATALOG, device="cuda")
        store.append(state, packet, [100, 7], actor=actor)
        torch.testing.assert_close(store.obs[:2].cpu(), torch.from_numpy(state.obs[[100, 7]]))
        torch.testing.assert_close(store.packet[:2].cpu(), torch.from_numpy(packet[[100, 7]]))
        saved = store.obs[:2].clone()
        table_addresses = {size: item.policy.cached_table.data_ptr() for size, item in actor.children.items()}
        with torch.no_grad():
            policy.card_embedding.weight[1].add_(.125)
            policy.value.bias.fill_(.25)
        actor.refresh(policy)
        for size, item in actor.children.items():
            self.assertEqual(item.policy.cached_table.data_ptr(), table_addresses[size])
        _, refreshed = actor.act(state)
        self.assertAlmostEqual(float(refreshed[7, 2]), float(torch.tanh(torch.tensor(.25))), places=5)
        actor.act(host())
        torch.testing.assert_close(store.obs[:2], saved)


if __name__ == "__main__":
    unittest.main()
