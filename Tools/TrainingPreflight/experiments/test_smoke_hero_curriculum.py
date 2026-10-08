"""Small CPU-only smoke-report invariants; never invokes its worker or CUDA."""
from collections import Counter
import json
from pathlib import Path
import tempfile
import unittest

import numpy as np
import smoke_hero_curriculum as smoke


class SmokeTests(unittest.TestCase):
    def fixture(self):
        obs = np.zeros((256, 2048), np.float32)
        seats = np.ones(256, np.int32)
        obs[:, 4] = 1
        obs[:, 112:116] = smoke.context_bytes("soi.herodraft")
        obs[:192, 4] = 0
        obs[:192, 112:116] = smoke.context_bytes("")
        obs[:192, 22] = np.float32(.2)
        obs[:192, 70] = np.float32(.4)
        seats[:192] = 0
        return obs, seats, np.zeros(8)

    def write_stats(self, directory, forced, censored):
        for cohort, chosen in (("forced-random", forced), ("natural-draft", ~forced)):
            completed = int((chosen & ~censored).sum())
            cap = int((chosen & censored).sum())
            path = Path(directory)/cohort/"session-fixture.json"
            path.parent.mkdir()
            path.write_text(json.dumps({"schema": "shards-training-pool-stats-v1", "final": True,
                "purpose": "training_pool", "hero_setup": {"cohort": cohort, "requested_mode": "curriculum-75-25"},
                "totals": {"completed_games": completed, "censored_games": cap, "unfinished_discarded_games": 0},
                "matchup_rows": [{"seat0_hero_id": "decima", "seat1_hero_id": "tetra", "games": completed, "censored_games": cap}]}))

    def test_initial_natural_forced_and_zero_policy_counters(self):
        forced, result = smoke.initial_setup(*self.fixture())
        self.assertEqual((forced.sum(), result["natural_games"]), (192, 64))
        self.assertEqual(result["forced_pair_counts"], [{"seat0_hero": "decima", "seat1_hero": "tetra", "games": 192}])
        obs, seats, metrics = self.fixture()
        metrics[2] = 384
        with self.assertRaises(RuntimeError):
            smoke.initial_setup(obs, seats, metrics)

    def test_wrong_context_partial_draft_and_illegal_hero_rejected(self):
        for mutate in (lambda x: x.__setitem__((0, 70), 0),
                       lambda x: x.__setitem__((0, 22), .21),
                       lambda x: x.__setitem__((193, 112), 0)):
            obs, seats, metrics = self.fixture()
            mutate(obs)
            with self.assertRaises(RuntimeError):
                smoke.initial_setup(obs, seats, metrics)

    def test_two_exact_cohorts_and_censors_are_not_duplicated(self):
        forced, _ = smoke.initial_setup(*self.fixture())
        capped = np.zeros(256, bool)
        capped[[0, 195]] = True
        with tempfile.TemporaryDirectory() as directory:
            self.write_stats(directory, forced, capped)
            results, pairs = smoke.validate_statistics(directory, forced, {"censored_lanes": [0, 195]})
            self.assertEqual(pairs, Counter({("decima", "tetra"): 256}))
            self.assertEqual(results["forced-random"]["totals"]["completed_games"], 191)
            self.assertEqual(results["natural-draft"]["totals"]["completed_games"], 63)
            path = Path(results["natural-draft"]["path"])
            report = json.loads(path.read_text())
            report["totals"]["censored_games"] = 2
            path.write_text(json.dumps(report))
            with self.assertRaises(RuntimeError):
                smoke.validate_statistics(directory, forced, {"censored_lanes": [0, 195]})

    def test_statistics_errors_and_wrong_cohort_fail(self):
        forced, _ = smoke.initial_setup(*self.fixture())
        with tempfile.TemporaryDirectory() as directory:
            self.write_stats(directory, forced, np.zeros(256, bool))
            path = Path(directory)/"forced-random/session-fixture.json"
            report = json.loads(path.read_text())
            report["hero_setup"]["cohort"] = "natural-draft"
            path.write_text(json.dumps(report))
            with self.assertRaises(RuntimeError):
                smoke.validate_statistics(directory, forced, {"censored_lanes": []})


if __name__ == "__main__":
    unittest.main()
