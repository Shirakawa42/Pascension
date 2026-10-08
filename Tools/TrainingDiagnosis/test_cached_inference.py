import unittest
import numpy as np

from cached_inference import CachedInference, NativeCachedInference


class CachedInferenceTests(unittest.TestCase):
    def test_native_cache_matches_reference_through_collisions_eviction_and_aliases(self):
        def infer(rows, _):
            return np.repeat(rows[:, :1], 64, axis=1), rows.sum(axis=1)
        rng = np.random.default_rng(77)
        bank = rng.normal(size=(11, 100)).astype(np.float32)
        for mask in (0, (1<<64)-1):
            cache = NativeCachedInference(infer, capacity=3, audit_every=1, hash_mask=mask)
            for _ in range(100):
                rows = bank[rng.integers(0, len(bank), size=15)]
                actual = cache(rows, None)
                expected = infer(rows, None)
                for a, b in zip(actual, expected):np.testing.assert_array_equal(a, b)
                rows.fill(-999)  # The native cache must own retained observations.

    def test_hash_collisions_and_reused_input_storage_cannot_return_wrong_predictions(self):
        calls = []
        def infer(rows, _):
            calls.append(rows.copy())
            return np.repeat(rows[:, :1], 64, axis=1), rows.sum(axis=1)
        cache = CachedInference(infer, capacity=2, audit_every=1)
        cache.hash = lambda *_: 0  # Every distinct observation collides.
        original = np.array([[1, 2], [3, 4], [1, 2]], dtype=np.float32)
        for rows in (original, original[::-1].copy(), np.full_like(original, 7)):
            logits, values = cache(rows, None)
            np.testing.assert_array_equal(logits[:, 0], rows[:, 0])
            np.testing.assert_array_equal(values, rows.sum(axis=1))
        original.fill(-99)
        logits, values = cache(np.array([[1, 2]], dtype=np.float32), None)
        self.assertEqual(float(logits[0, 0]), 1)
        self.assertEqual(float(values[0]), 3)
        self.assertLessEqual(len(cache.cache), 2)

    def test_repeated_positions_are_inferred_once_and_full_observation_matters(self):
        counts = []
        def infer(rows, _):
            counts.append(len(rows))
            return np.zeros((len(rows), 64), np.float32), rows.sum(axis=1)
        cache = CachedInference(infer, capacity=2)
        packet = np.zeros((4, 100), np.float32)
        cache(packet, None)
        cache(packet, None)
        packet[0, -1] = 123
        _, values = cache(packet, None)
        self.assertEqual(counts, [1, 1])
        np.testing.assert_array_equal(values, [123, 0, 0, 0])


if __name__ == '__main__':unittest.main()
