"""CPU-only benchmark routing and final-counter validation; no host launch."""
from copy import deepcopy
import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

import benchmark_host_v4 as b


class FinalStatisticsTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.folder = Path(self.temp.name)
        self.raw = json.loads((b.HERE / "HostV4/statistics-fixture.json").read_text())
        self.identity = {key: self.raw[key] for key in ("host_binary_sha256", "observation_schema")}
        self.warm = {"completed_games": 1, "censored_games": 0, "draws": 0, "seat0_wins": 1}
        self.measured = {"completed_games": 1, "censored_games": 1, "draws": 1, "seat0_wins": 0}
        self.path = self.folder / "session-fixture.json"
        self.write(self.raw)

    def write(self, raw):
        self.path.write_text(json.dumps(raw))

    def validate(self, **kwargs):
        return b.validate_statistics(self.folder, self.warm, self.measured, self.identity, **kwargs)

    def test_actual_fixture_totals_and_immutable_provenance(self):
        result = self.validate(returncode=0)
        self.assertEqual(result["verified_totals"], {"completed_games": 2, "censored_games": 1,
            "draws": 1, "seat0_wins": 1, "seat1_wins": 0})
        self.assertEqual(len(result["file_sha256"]), 64)
        self.assertTrue(result["clean_exit"])

    def test_each_wrong_outcome_or_identity_field_rejected(self):
        for field in ("completed_games", "censored_games", "draws", "seat0_wins", "seat1_wins"):
            raw = deepcopy(self.raw)
            raw["totals"][field] += 1
            self.write(raw)
            with self.subTest(counter=field), self.assertRaises(AssertionError):
                self.validate(returncode=0)
        for field in ("schema", "purpose", "host_binary_sha256", "observation_schema", "final", "cumulative"):
            raw = deepcopy(self.raw)
            raw[field] = False if field in ("final", "cumulative") else "wrong"
            self.write(raw)
            with self.subTest(metadata=field), self.assertRaises(AssertionError):
                self.validate(returncode=0)

    def test_dirty_exit_error_sidecar_and_stderr_failure_rejected(self):
        with self.assertRaises(AssertionError):
            self.validate(returncode=-15)
        with self.assertRaises(AssertionError):
            self.validate(returncode=0, diagnostics='{"statistics_error":true}')
        for suffix in (".error.json", ".history-error.json"):
            error = self.folder / ("session-fixture" + suffix)
            error.write_text("{}")
            with self.subTest(suffix=suffix), self.assertRaises(AssertionError):
                self.validate(returncode=0)
            error.unlink()

    def test_factory_clears_inherited_routing_and_sets_only_requested_statistics(self):
        command = [b.pipeline_bench.DOTNET,
            str(b.pipeline_bench.ROOT / "Tools/TrainingPreflight/Host/bin/Release/net8.0/TrainingHost.dll"), "serve"]
        env = {"SHARDS_STATS_DIRECTORY": "inherited", "SHARDS_STATS_UNEXPECTED": "bad", "SHARDS_SHARED_COPY": "span"}
        for statistics in (None, self.folder):
            factory = b.Factory(Path("candidate.dll"), statistics=statistics, seed=123, batch=16)
            with patch.object(b.v3._original_subprocess, "Popen") as launch:
                factory.Popen(command, env=env)
                args, kwargs = launch.call_args
                self.assertEqual(args[0], [command[0], "candidate.dll", "serve"])
                self.assertEqual(kwargs["env"]["SHARDS_SHARED_COPY"], "span")
                self.assertNotIn("SHARDS_STATS_UNEXPECTED", kwargs["env"])
                if statistics is None:
                    self.assertFalse(any(key.startswith("SHARDS_STATS_") for key in kwargs["env"]))
                else:
                    self.assertEqual(kwargs["env"]["SHARDS_STATS_PURPOSE"], "training_pool")
                    self.assertEqual(kwargs["env"]["SHARDS_STATS_EXPECTED_SEED"], "123")
                    self.assertEqual(kwargs["env"]["SHARDS_STATS_EXPECTED_BATCH"], "16")
                with self.assertRaises(ValueError):
                    factory.Popen(command[:-1] + ["catalog"], env=env)
        self.assertEqual(env["SHARDS_STATS_DIRECTORY"], "inherited")
        self.assertFalse(b.torch.cuda.is_initialized())


if __name__ == "__main__":
    unittest.main()
