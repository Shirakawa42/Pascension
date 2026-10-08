"""Compiled and packed actor integration: actual CUDA graphs, no learning."""
import copy
from pathlib import Path
import sys
from types import SimpleNamespace
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

import numpy as np
import torch
from adaptive import AdaptiveActor
from model import Policy, PolicyConfig
from packed_transport import triton
from train import Rollout


CATALOG = {
    "observation_schema": "shards-zero-depth-observation-v2",
    "obs_dim": 24576, "max_actions": 64, "action_dim": 48,
    "candidate_card_column": 16, "candidate_card_scale": 192,
    "card_ids": [f"card_{index:03d}" for index in range(189)],
    "histogram_descriptors": [
        {"offset": 256 + index * 192, "length": 189, "scale": 10}
        for index in range(24)],
    "card_features": [
        [float(column == index % 16) for column in range(512)]
        for index in range(189)],
    "tables": {
        "public_entities": {"offset": 4864, "capacity": 384, "stride": 12, "count_column": 166, "count_scale": 384},
        "all_pending_options": {"offset": 9472, "capacity": 384, "stride": 20, "count_column": 149, "count_scale": 384},
        "known_positions": {"offset": 17152, "capacity": 384, "stride": 8, "count_column": 167, "count_scale": 384},
        "staged_selection": {"offset": 20224, "capacity": 384, "stride": 4, "count_column": 177, "count_scale": 384},
        "played_this_turn": {"offset": 21760, "capacity": 384, "stride": 4, "count_column": 168, "count_scale": 384},
        "public_continuations": {"offset": 23296, "capacity": 64, "stride": 8, "count_column": 174, "count_scale": 64},
        "public_copy_frames": {"offset": 23808, "capacity": 96, "stride": 8, "count_column": 176, "count_scale": 96}}}


def state(batch=128, lanes=None, mode=0):
    """Real schema/normalization; empty unused tails and valid card identities."""
    lanes = np.arange(batch) if lanes is None else np.asarray(lanes, np.int64)
    host = SimpleNamespace(obs=np.zeros((batch, 24576), np.float32),
        candidates=np.zeros((batch, 64, 48), np.float32),
        mask=np.zeros((batch, 64), np.float32),
        actors=np.arange(batch, dtype=np.int32) % 2,
        done=np.ones(batch, np.int32))
    host.done[lanes] = 0
    host.mask[:, 0] = 1  # Actual held terminal lanes have a dummy legal prefix.
    extents = ({"public_entities": 15, "all_pending_options": 3, "known_positions": 6,
                "staged_selection": 2, "played_this_turn": 9, "public_continuations": 2, "public_copy_frames": 3},
               {"public_entities": 70, "all_pending_options": 330, "known_positions": 85,
                "staged_selection": 40, "played_this_turn": 82, "public_continuations": 22, "public_copy_frames": 96},
               {name: 0 for name in CATALOG["tables"]},
               {"public_entities": 7, "all_pending_options": 8, "known_positions": 4,
                "staged_selection": 0, "played_this_turn": 3, "public_continuations": 0, "public_copy_frames": 1})[mode]
    random = np.random.default_rng(81331 + mode)
    for lane in lanes:
        obs = host.obs[lane]
        obs[0] = host.actors[lane]
        obs[1] = lane % 2
        obs[2] = (lane % 30 + 1) / 100
        obs[3] = 1
        obs[4:6] = (100 / 256, 24 / 64)
        obs[7] = -1
        obs[8:14] = (1, 1, .5, 1, 1, 0)
        obs[16:22] = (.8, .5, .3, .2, .08, .12)
        obs[80:86] = (.9, .4, .2, .1, .08, .12)
        obs[31] = -0.0
        for zone in range(24):
            index = 256 + zone * 192 + (lane + zone) % 189
            obs[index] = ((lane + zone) % 4 + 1) / 10
        for name, table in CATALOG["tables"].items():
            count = extents[name]
            obs[table["count_column"]] = count / table["count_scale"]
            if not count:
                continue
            start, stride = table["offset"], table["stride"]
            rows = obs[start:start + count * stride].reshape(count, stride)
            rows[:] = random.uniform(0, .5, rows.shape).astype(np.float32)
            rows[:, 0] = ((lane + np.arange(count)) % 189 + 1) / 192
            rows[0, 2] = -0.0
            if name == "known_positions":
                rows[:, 1] = np.arange(count) % 3 / 3 + 1 / 3
                # Public known enemy-hand instances share this prefix and its
                # extent, even beyond the first old 64 remembered identities.
                rows[-min(3, count):, 1] = 4 / 3
                rows[:, 6] = 1
                rows[:, 7] = (lane * 100 + np.arange(count) + 1) / 65536
            elif name == "public_copy_frames":
                rows[:, 1] = 1
                rows[:, 2] = -(np.arange(count) % max(extents["known_positions"], 1) + 1) / 384
                rows[:, 6] = np.arange(count) % 3 - 1
                rows[:, 7] = .2
            elif name == "all_pending_options":
                rows[:, 6] = (np.arange(count) % 7 == 0)
            elif name == "public_entities":
                rows[:, 9] = (np.arange(count) + 1) / 384
        legal = 64 if mode == 1 else (int(lane) % 12 + 2)
        host.mask[lane, :legal] = 1
        candidates = host.candidates[lane, :legal]
        candidates[:, 16] = ((lane + np.arange(legal)) % 189 + 1) / 192
        candidates[:, 17:22] = (.5, .3, .25, .1, .05)
        candidates[:, 25] = (np.arange(legal) + 1) / 384
        candidates[:, 32] = (np.arange(legal) % max(extents["public_entities"], 1) + 1) / 384
        if extents["all_pending_options"]:
            candidates[:, 12] = 1
            candidates[-1, 12] = 0
            candidates[-1, 14 if legal == 64 else 13] = 1
        else:
            candidates[:, 0] = 1
            candidates[-1, 0] = 0
            candidates[-1, 10] = 1
        candidates[0, 23] = -0.0
    return host


@unittest.skipUnless(torch.cuda.is_available() and triton is not None,
                     "Compiled packed integration requires CUDA/Triton")
class OptimizedActorTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        torch.set_num_threads(1)
        torch.manual_seed(919017)
        cls.previous_precision = torch.get_float32_matmul_precision()
        torch.set_float32_matmul_precision("highest")
        cls.policy = Policy(copy.deepcopy(CATALOG), PolicyConfig(width=64, key_width=32)).cuda()
        cls.actor = AdaptiveActor(cls.policy, 128, graph=True, device="cuda", compiled=True, packed=True)
        cls.initial_state = {key: value.detach().clone() for key, value in cls.policy.state_dict().items()}

    def setUp(self):
        self.policy.load_state_dict(self.initial_state)
        self.actor.refresh(self.policy)

    @classmethod
    def tearDownClass(cls):
        del cls.actor
        del cls.policy
        del cls.initial_state
        torch.set_float32_matmul_precision(cls.previous_precision)

    def assert_bits(self, expected, actual):
        np.testing.assert_array_equal(np.asarray(expected).view(np.uint32), actual.cpu().numpy().view(np.uint32))

    def verify_inputs_and_behavior(self, host, packet, lanes):
        lanes = np.asarray(lanes, np.int64)
        actor = self.actor
        child = actor.active_actor
        self.assertIsNotNone(child.graph)
        self.assertIsNotNone(child.packed_transport.graph)
        self.assertTrue(child.compiled_enabled and child.packed_enabled)
        for original, actual in zip((host.obs, host.candidates, host.mask), (actor.obs, actor.candidates, actor.mask)):
            self.assert_bits(original[lanes], actual[:len(lanes)])
        padding = child.batch - len(lanes)
        self.assert_bits(np.zeros((padding, 24576), np.float32), actor.obs[len(lanes):])
        self.assert_bits(np.zeros((padding, 64, 48), np.float32), actor.candidates[len(lanes):])
        padding_mask = np.zeros((padding, 64), np.float32)
        padding_mask[:, 0] = 1
        self.assert_bits(padding_mask, actor.mask[len(lanes):])
        with torch.inference_mode():
            obs = torch.as_tensor(host.obs[lanes], device="cuda")
            candidates = torch.as_tensor(host.candidates[lanes], device="cuda")
            mask = torch.as_tensor(host.mask[lanes], device="cuda")
            logits, values = self.policy(obs, candidates, mask)
            selected = torch.as_tensor(packet[lanes, 0], device="cuda", dtype=torch.int64)
            logp = logits.log_softmax(-1).gather(1, selected[:, None]).squeeze(1)
            expected = torch.stack((logp, values), -1)
        torch.testing.assert_close(torch.as_tensor(packet[lanes, 1:], device="cuda"), expected, rtol=0, atol=.0002)
        self.assertTrue(np.all(host.mask[lanes, packet[lanes, 0].astype(np.int64)] == 1))

    def test_01_graph_buckets_dynamic_tables_raw_bits_and_initial_eager_behavior(self):
        actor = self.actor
        pointers = {size: (child.obs.data_ptr(), child.candidates.data_ptr(), child.mask.data_ptr(), child.policy.cached_table.data_ptr())
                    for size, child in actor.children.items()}
        captured = {}
        schedules = ((3, 0), (41, 1), (103, 0), (1, 2), (60, 2), (128, 1), (7, 3), (41, 0), (103, 3))
        for count, mode in schedules:
            lanes = np.sort(np.random.default_rng(8121 + count).choice(128, count, replace=False))
            host = state(lanes=lanes, mode=mode)
            actions, packet = actor.act(host)
            self.verify_inputs_and_behavior(host, packet, lanes)
            self.assertEqual(actor.active_actor.batch, 32 if count <= 32 else 64 if count <= 64 else 128)
            np.testing.assert_array_equal(actor.source_rows(lanes[::-1]), np.arange(count - 1, -1, -1))
            self.assertTrue(np.all(actions[host.done != 0] == -1))
            np.testing.assert_array_equal(packet[host.done != 0], 0)
            np.testing.assert_array_equal(packet[lanes, 2], 0)
            child = actor.active_actor
            graphs = (id(child.graph), id(child.packed_transport.graph))
            if child.batch in captured:
                self.assertEqual(captured[child.batch], graphs)
            captured[child.batch] = graphs
        self.assertEqual(set(captured), {32, 64, 128})
        for size, child in actor.children.items():
            self.assertEqual(pointers[size], (child.obs.data_ptr(), child.candidates.data_ptr(), child.mask.data_ptr(), child.policy.cached_table.data_ptr()))

    def test_02_nonzero_weight_value_and_semantic_cache_refresh_every_captured_bucket(self):
        pointers = {size: (child.policy.cached_table.data_ptr(), id(child.graph))
                    for size, child in self.actor.children.items()}
        with torch.no_grad():
            self.policy.card_embedding.weight[1:4].add_(.25)
            self.policy.effect_projection.weight[0, 3].add_(.125)
            self.policy.query.weight.add_(.001)
            self.policy.query.bias.add_(.02)
            self.policy.candidate_bias.weight[0, 16].fill_(.03)
            self.policy.value.weight.normal_(0, .025)
            self.policy.value.bias.fill_(.125)
        self.actor.refresh(self.policy)
        for size, child in self.actor.children.items():
            self.assertEqual(pointers[size], (child.policy.cached_table.data_ptr(), id(child.graph)))
            torch.testing.assert_close(child.policy.cached_table, self.policy.embedding_table(), rtol=0, atol=1.e-6)
        for count, mode in ((9, 1), (43, 0), (111, 3)):
            lanes = np.sort(np.random.default_rng(7433 + count).choice(128, count, replace=False))
            host = state(lanes=lanes, mode=mode)
            _, packet = self.actor.act(host)
            self.verify_inputs_and_behavior(host, packet, lanes)
            self.assertGreater(float(np.abs(packet[lanes, 2]).mean()), .01)

    def test_03_archive_subset_gpu_rollout_ownership_and_zero_active_not_credited(self):
        actor = self.actor
        host = state(mode=1)
        requested = np.zeros(128, bool)
        requested[[2, 9, 21, 46, 77, 101, 119]] = True
        lanes = np.flatnonzero(requested)
        actions, packet = actor.act(host, active=requested)
        self.verify_inputs_and_behavior(host, packet, lanes)
        self.assertTrue(np.all(actions[~requested] == -1))
        np.testing.assert_array_equal(packet[~requested], 0)
        with self.assertRaises(ValueError):
            actor.source_rows([0])
        retained = np.array([119, 2, 46])
        saved_obs, saved_candidates, saved_mask = (value[retained].copy() for value in (host.obs, host.candidates, host.mask))
        saved_packet = packet.copy()
        saved_seats = host.actors[retained].copy()
        store = Rollout(8, CATALOG, device="cuda")
        store.append(host, packet, retained, actor=actor)
        self.assertEqual(store.rows, 3)
        self.assert_bits(saved_obs, store.obs[:3])
        self.assert_bits(saved_candidates, store.candidates[:3])
        self.assert_bits(saved_packet[retained], store.packet[:3])
        np.testing.assert_array_equal(store.mask[:3].cpu().numpy(), saved_mask.astype(bool))
        borrowed_obs = actor.obs
        borrowed_snapshot = borrowed_obs.clone()
        host.obs.fill(17);host.candidates.fill(23);host.mask.fill(0);host.actors[:] = 1 - host.actors
        torch.testing.assert_close(borrowed_obs, borrowed_snapshot, rtol=0, atol=0)
        actor.act(state(lanes=np.arange(15), mode=2))  # Reuses the same 32-row child.
        self.assert_bits(saved_obs, store.obs[:3])
        self.assert_bits(saved_candidates, store.candidates[:3])
        self.assert_bits(saved_packet[retained], store.packet[:3])
        np.testing.assert_array_equal(packet, saved_packet)
        np.testing.assert_array_equal(store.seats[:3], saved_seats)
        done = np.ones(128, np.int32)
        done[2] = 2
        rewards = np.tile(np.array([1, -1], np.float32), (128, 1))
        indices, returns, _, censored_rows = store.seal(done, rewards)
        np.testing.assert_array_equal(indices, [0, 2])
        np.testing.assert_array_equal(returns, rewards[retained[[0, 2]], saved_seats[[0, 2]]])
        self.assertEqual(censored_rows, 1)
        empty = state(lanes=[])
        actions, packet = actor.act(empty)
        np.testing.assert_array_equal(actions, -1)
        np.testing.assert_array_equal(packet, 0)
        self.assertIsNone(actor.active_actor)
        before = store.rows
        store.append(empty, packet, [], actor=actor)
        self.assertEqual(store.rows, before)
        with self.assertRaises(ValueError):
            actor.source_rows([0])
        with self.assertRaises(RuntimeError):
            _ = actor.obs

    def test_04_fixed_actor_direct_and_pinned_staged_packed_paths(self):
        child = self.actor.children[32]
        for mode in (0, 1, 2, 3):
            host = state(batch=32, mode=mode)
            actions, packet = child.act(host)
            for original, actual in zip((host.obs, host.candidates, host.mask), (child.obs, child.candidates, child.mask)):
                self.assert_bits(original, actual)
            with torch.inference_mode():
                logits, values = self.policy(child.obs, child.candidates, child.mask)
                selected = torch.as_tensor(actions, device="cuda")
                logp = logits.log_softmax(-1).gather(1, selected[:, None]).squeeze(1)
                expected = torch.stack((logp, values), -1)
            torch.testing.assert_close(torch.as_tensor(packet[:, 1:], device="cuda"), expected, rtol=0, atol=.0002)
        host = state(batch=32, lanes=np.arange(27), mode=0)
        host.obs[27:] = 0
        host.candidates[27:] = 0
        host.mask[27:] = 0  # Dense adaptive staging starts with zero padding masks.
        for staging, original in zip((child.obs_host, child.candidates_host, child.mask_host), (host.obs, host.candidates, host.mask)):
            staging.numpy()[:] = original
        child.act_staged()
        expected_mask = host.mask.copy()
        expected_mask[27:, 0] = 1
        self.assert_bits(host.obs, child.obs)
        self.assert_bits(host.candidates, child.candidates)
        self.assert_bits(expected_mask, child.mask)


if __name__ == "__main__":
    unittest.main()
