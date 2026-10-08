from __future__ import annotations

import importlib.util
import json
from pathlib import Path
from types import SimpleNamespace
import unittest
from unittest.mock import patch


path = Path(__file__).resolve().parents[1] / "benchmark.py"
spec = importlib.util.spec_from_file_location("zero_depth_benchmark", path)
benchmark = importlib.util.module_from_spec(spec)
spec.loader.exec_module(benchmark)


class AutomationParityTests(unittest.TestCase):
    def args(self):
        return SimpleNamespace(workers=[4], binary=Path("host.dll"), parity_games=8,
                               seed=17, host_timeout=10)

    def response(self, digest, seconds, automated):
        value = {"final_outcome_digest": digest, "seconds": seconds, "completed": 8,
                 "censored": 0, "engine_submissions": 100,
                 "automatically_applied": automated}
        return SimpleNamespace(returncode=0, stdout=json.dumps(value), stderr="")

    def test_forced_steps_may_differ_when_real_outcomes_and_submissions_match(self):
        with patch.object(benchmark.subprocess, "run", side_effect=[
                self.response("equal", 2., 0), self.response("equal", 1., 10)]):
            result = benchmark.cpu_automation_parity(self.args())
        self.assertTrue(result["reports"][0]["digest_matched"])
        self.assertEqual(result["reports"][0]["speedup"], 2.)

    def test_different_final_rule_state_rejects_apparent_automation_speedup(self):
        with patch.object(benchmark.subprocess, "run", side_effect=[
                self.response("before", 2., 0), self.response("after", 1., 10)]):
            with self.assertRaisesRegex(AssertionError, "changed deterministic outcomes"):
                benchmark.cpu_automation_parity(self.args())

    def test_same_digest_but_different_engine_submission_count_is_rejected(self):
        first = self.response("equal", 2., 0)
        second = self.response("equal", 1., 10)
        altered = json.loads(second.stdout)
        altered["engine_submissions"] = 99
        second.stdout = json.dumps(altered)
        with patch.object(benchmark.subprocess, "run", side_effect=[first, second]):
            with self.assertRaisesRegex(AssertionError, "engine_submissions"):
                benchmark.cpu_automation_parity(self.args())


if __name__ == "__main__":
    unittest.main()
