"""CPU-only differential checks; no game learning or CUDA initialization.

Run from the repository root:
  PYTHONPATH=Tools/TrainingPreflight:Tools/TrainingPreflight/experiments \
    /home/lva/.venvs/shards-preflight/bin/python -m unittest \
    Tools/TrainingPreflight/experiments/test_rollout_fastpath.py -v
"""
from collections import Counter
from types import SimpleNamespace
import unittest

import numpy as np
import torch
from torch.utils._python_dispatch import TorchDispatchMode

from learning_model import (ACTION_DIM, MAX_ACTIONS, OBS_DIM, UPLOAD_FLOATS_PER_ROW,
                            InvalidLearningBatch, LearningActor)
from learning_rollout import EpisodeStore
from rollout_fastpath import ContiguousEpisodeStore, validate_host_fast


class Operations(TorchDispatchMode):
    def __init__(self):
        super().__init__()
        self.counts = Counter()

    def __torch_dispatch__(self, function, types, args=(), kwargs=None):
        self.counts[str(function)] += 1
        return function(*args, **(kwargs or {}))


def actor_fixture(rows=16, float_mask=True):
    generator = torch.Generator(device="cpu").manual_seed(260926)
    obs = torch.randn(rows, OBS_DIM, generator=generator)
    candidates = torch.randn(rows, MAX_ACTIONS, ACTION_DIM, generator=generator)
    candidates[:, :, 16] = torch.arange(MAX_ACTIONS).float()[None, :] / 192
    mask = torch.zeros(rows, MAX_ACTIONS, dtype=torch.bool)
    mask[:, [0, 7, 63]] = True
    actions = torch.tensor([0, 7, 63])[torch.arange(rows) % 3]
    packet = torch.stack((actions.float(), -torch.arange(rows).float()/16,
                          torch.linspace(-1, 1, rows)), dim=1)
    return SimpleNamespace(obs=obs, candidates=candidates,
                           mask=mask.float() if float_mask else mask), packet


def host_fixture(batch=4, validate_inputs=True):
    actor, _ = actor_fixture(batch)
    source = torch.cat((actor.obs.flatten(), actor.candidates.flatten(), actor.mask.flatten()))
    owner = SimpleNamespace(batch=batch, upload=torch.empty_like(source), validate_inputs=validate_inputs)
    return owner, source


class FastAppendTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        torch.set_num_threads(1)
        if torch.cuda.is_initialized():
            raise RuntimeError("Run these tests in an independent CPU-only process")

    def stores(self, capacity=64):
        baseline, candidate = EpisodeStore(capacity, "cpu"), ContiguousEpisodeStore(capacity, "cpu")
        baseline.reset(17)
        candidate.reset(17)
        return baseline, candidate

    def assert_stores_equal(self, left, right):
        self.assertEqual((left.rows, left.sealed, left.version), (right.rows, right.sealed, right.version))
        for name in ("obs", "candidates", "mask", "actions", "old_logp", "old_values"):
            torch.testing.assert_close(getattr(left, name)[:left.rows], getattr(right, name)[:right.rows],
                                       rtol=0, atol=0, equal_nan=True)
        np.testing.assert_array_equal(left.episode_ids[:left.rows], right.episode_ids[:right.rows])
        np.testing.assert_array_equal(left.seats[:left.rows], right.seats[:right.rows])
        for name in ("returns", "advantages"):
            if getattr(left, name) is not None:
                torch.testing.assert_close(getattr(left, name), getattr(right, name), rtol=0, atol=0)

    def test_prefix_removes_four_gathers_without_changing_rows(self):
        baseline, candidate = self.stores()
        actor, packet = actor_fixture()
        args = (actor, packet, [13, 2, 8, 5, 4], [1, 0, 1, 1, 0])
        with Operations() as before:
            baseline.append(*args, source_rows=np.arange(5))
        with Operations() as after:
            candidate.append(*args, source_rows=np.arange(5))
        self.assert_stores_equal(baseline, candidate)
        self.assertEqual(before.counts["aten.index_select.default"], 4)
        self.assertEqual(after.counts["aten.index_select.default"], 0)
        self.assertEqual(after.counts["aten._to_copy.default"], 0)

    def test_prefix_sizes_masks_and_padding_remain_exact(self):
        for count in (1, 3, 7, 32, 64):
            for float_mask in (False, True):
                with self.subTest(count=count, float_mask=float_mask):
                    baseline, candidate = self.stores()
                    actor, packet = actor_fixture(64, float_mask)
                    actor.obs[count:] = float("nan")
                    actor.candidates[count:] = float("nan")
                    lanes = np.arange(count)[::-1].copy()
                    for store in (baseline, candidate):
                        store.append(actor, packet, lanes, lanes % 2, source_rows=np.arange(count))
                    self.assert_stores_equal(baseline, candidate)
                    self.assertTrue(bool(torch.isfinite(candidate.obs[:count]).all()))

    def test_indexed_fallback_keeps_original_slots_and_duplicates(self):
        for indices in ([7, 0, 4], [1, 2, 3], [4, 4, 0]):
            with self.subTest(indices=indices):
                baseline, candidate = self.stores()
                actor, packet = actor_fixture()
                for store in (baseline, candidate):
                    store.append(actor, packet, [9, 2, 7], [1, 0, 1], source_rows=indices)
                self.assert_stores_equal(baseline, candidate)
                self.assertEqual(candidate.actions[:3].tolist(), packet[indices, 0].long().tolist())

    def test_implicit_source_rows_match_baseline(self):
        baseline, candidate = self.stores()
        actor, packet = actor_fixture()
        for store in (baseline, candidate):
            store.append(actor, packet, [0, 1, 2], [1, 0, 1])
            store.append(actor, packet, [8, 1, 3], [0, 1, 0])
        self.assert_stores_equal(baseline, candidate)

    def test_owned_copies_credit_censoring_and_minibatches(self):
        baseline, candidate = self.stores()
        actor, packet = actor_fixture()
        for store in (baseline, candidate):
            store.append(actor, packet, [2, 0, 1], [1, 0, 1], source_rows=[0, 1, 2])
            store.append(actor, packet, [0, 2], [1, 0], source_rows=[0, 1])
        actor.obs.fill_(999)
        actor.candidates.fill_(999)
        actor.mask.zero_()
        packet.fill_(float("nan"))
        for store in (baseline, candidate):
            self.assertEqual(store.retain_completed([True, False, True]), 1)
            store.seal([[1, -1], [0, 0], [-1, 1]], [True, False, True])
        self.assert_stores_equal(baseline, candidate)
        self.assertEqual(candidate.returns.tolist(), [1., 1., -1., -1.])
        for key, value in baseline.minibatch(torch.tensor([3, 0, 3, 1])).items():
            torch.testing.assert_close(value, candidate.minibatch(torch.tensor([3, 0, 3, 1]))[key], rtol=0, atol=0)

    def test_capacity_sealing_and_empty_append_guards(self):
        for store in self.stores():
            actor, packet = actor_fixture(64)
            store.append(None, None, [], [])
            store.append(actor, packet, np.arange(64), np.arange(64) % 2, source_rows=np.arange(64))
            with self.assertRaisesRegex(RuntimeError, "full"):
                store.append(actor, packet, [0], [0], source_rows=[0])
            self.assertEqual(store.rows, 64)
            store.sealed = True
            with self.assertRaisesRegex(RuntimeError, "Cannot append"):
                store.append(None, None, [], [])

    def test_invalid_ownership_or_source_shape_does_not_append(self):
        actor, packet = actor_fixture()
        for store in self.stores():
            for lanes, seats, source in (([0, 1], [1], [0, 1]), ([0], [2], [0]), ([0, 1], [0, 1], [0])):
                with self.subTest(store=type(store).__name__, lanes=lanes, seats=seats):
                    with self.assertRaises(ValueError):
                        store.append(actor, packet, lanes, seats, source_rows=source)
                    self.assertEqual(store.rows, 0)

    def test_repeated_random_batches_remain_bit_exact(self):
        rng = np.random.default_rng(40926)
        baseline, candidate = self.stores(128)
        actor, packet = actor_fixture(16)
        for step in range(24):
            count = int(rng.integers(1, 6))
            lanes = rng.integers(0, 11, count)
            seats = rng.integers(0, 2, count)
            indices = np.arange(count) if step % 2 else rng.integers(0, 16, count)
            for store in (baseline, candidate):
                store.append(actor, packet, lanes, seats, source_rows=indices)
            self.assert_stores_equal(baseline, candidate)

    def test_overlapping_owned_source_uses_original_gather_fallback(self):
        baseline, candidate = self.stores()
        counts = []
        for store in (baseline, candidate):
            store.obs.copy_(torch.arange(64).float()[:, None].expand(64, OBS_DIM))
            store.candidates.zero_()
            store.mask.fill_(True)
            store.actions.zero_()
            store.old_logp.zero_()
            store.old_values.zero_()
            store.episode_ids.fill(0)
            store.seats.fill(0)
            store.rows = 1
            actor, packet = actor_fixture(5)
            actor.obs = store.obs[:5]
            with Operations() as operations:
                store.append(actor, packet, [0, 1, 2, 3, 4], [0, 1, 0, 1, 0], source_rows=np.arange(5))
            counts.append(operations.counts["aten.index_select.default"])
        self.assertEqual(counts, [4, 4])
        self.assert_stores_equal(baseline, candidate)
        self.assertEqual(candidate.obs[:6, 0].tolist(), [0., 0., 1., 2., 3., 4.])

    def test_short_prefix_or_wrong_shape_cannot_broadcast_into_experience(self):
        for name in ("obs", "candidates", "mask", "packet", "trailing_shape"):
            with self.subTest(field=name):
                store = ContiguousEpisodeStore(64, "cpu")
                store.reset(9)
                store.obs.fill_(123)
                actor, packet = actor_fixture(3)
                if name == "packet":
                    packet = packet[:1]
                elif name == "trailing_shape":
                    actor.obs = actor.obs[:, :1]
                else:
                    setattr(actor, name, getattr(actor, name)[:1])
                with self.assertRaisesRegex(ValueError, "Malformed contiguous"):
                    store.append(actor, packet, [5, 4, 3], [0, 1, 0], source_rows=[0, 1, 2])
                self.assertEqual(store.rows, 0)
                self.assertTrue(bool((store.obs == 123).all()))


class HostValidationTests(unittest.TestCase):
    def compare(self, actor, source):
        outcomes = []
        for function in (LearningActor._validate_host, validate_host_fast):
            try:
                outcomes.append(("ok", function(actor, source)))
            except (InvalidLearningBatch, AttributeError, TypeError) as error:
                outcomes.append((type(error), str(error)))
        self.assertEqual(outcomes[0][0], outcomes[1][0])
        if outcomes[0][0] == "ok":
            np.testing.assert_array_equal(outcomes[0][1], outcomes[1][1])
            self.assertTrue(np.shares_memory(outcomes[1][1], source.numpy()))
        else:
            self.assertEqual(outcomes[0][1], outcomes[1][1])

    def test_valid_exact_ids_extreme_values_and_no_mutation(self):
        actor, source = host_fixture()
        candidates = source[4*OBS_DIM:4*(OBS_DIM+MAX_ACTIONS*ACTION_DIM)].view(4, 64, 32)
        candidates[:, :, 16] = (torch.arange(256).reshape(4, 64) % 193).float()/192
        source[0], source[1], source[2] = 9999, -9999, torch.finfo(torch.float32).max
        before = source.clone()
        self.compare(actor, source)
        self.assertTrue(torch.equal(source, before))

    def test_outer_schema_guards_and_meta_device(self):
        actor, source = host_fixture()
        for bad in (source.double(), source[:-1], source.view(4, -1),
                    torch.zeros(len(source)*2)[::2], torch.empty(source.shape, device="meta")):
            with self.subTest(dtype=bad.dtype, shape=bad.shape, device=bad.device, stride=bad.stride()):
                self.compare(actor, bad)

    def test_invalid_masks_and_all_illegal_rows(self):
        for value in (float("nan"), float("inf"), -1., .5, 2.):
            actor, source = host_fixture()
            source[-1] = value
            with self.subTest(value=value):
                self.compare(actor, source)
        actor, source = host_fixture()
        source[-64:] = 0
        self.compare(actor, source)

    def test_nonfinite_features_and_card_id_boundaries(self):
        actor, original = host_fixture()
        card_offset = 4*OBS_DIM+16
        bad_values = [float("nan"), float("inf"), -float("inf")]
        for position in (0, 4*OBS_DIM+17, card_offset):
            for value in bad_values:
                source = original.clone()
                source[position] = value
                self.compare(actor, source)
        codes = [0., 1., 63., 192.]
        for code in codes:
            for deviation in (-.001, -.0002001, -.0002, -.0001999, 0., .0001999, .0002, .0002001, .001):
                value = np.float32((code+deviation)/192)
                for neighbor in (np.nextafter(value, np.float32(-np.inf)), value,
                                 np.nextafter(value, np.float32(np.inf))):
                    source = original.clone()
                    source[card_offset] = float(neighbor)
                    self.compare(actor, source)

    def test_disabled_numeric_checks_still_validate_masks(self):
        actor, source = host_fixture(validate_inputs=False)
        source[0] = float("nan")
        source[4*OBS_DIM+16] = 99
        self.compare(actor, source)
        source[-1] = .5
        self.compare(actor, source)

    def test_no_cuda_context_was_initialized(self):
        self.assertFalse(torch.cuda.is_initialized())


if __name__ == "__main__":
    unittest.main()
