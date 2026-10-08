import hashlib
import json
import math
import sys
import tempfile
import types
import unittest
from unittest import mock
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from evaluate import ARTIFACT_NAMES, GameResult, freeze_incumbent, run_evaluation, summarize_pairs, verify_bundle

try:
    import numpy as np
except ImportError:
    np = None


class PairedStrengthTests(unittest.TestCase):
    @staticmethod
    def games(count, winner="learner"):
        return [GameResult(seed=1000 + i, learner_seat=seat,
                           winner=seat if winner == "learner" else -1)
                for i in range(count) for seat in (0, 1)]

    def test_uses_seed_pairs_as_statistical_units(self):
        result = summarize_pairs(self.games(100), planned_pairs=100)
        self.assertAlmostEqual(result["paired_hoeffding_score_interval"][0],
                               1 - math.sqrt(math.log(40) / 200))
        self.assertTrue(result["stronger_than_current"])

    def test_terminal_draws_are_half_a_win(self):
        result = summarize_pairs(self.games(100, "draw"), planned_pairs=100)
        self.assertEqual(result["draws"], 200)
        self.assertEqual(result["resolved_game_score"], 0.5)
        self.assertFalse(result["stronger_than_current"])

    def test_censored_games_are_unknown_not_draws(self):
        games = [GameResult(seed=1000 + i, learner_seat=seat, winner=None, censored=True)
                 for i in range(100) for seat in (0, 1)]
        result = summarize_pairs(games, planned_pairs=100)
        self.assertEqual(result["draws"], 0)
        self.assertEqual(result["censored"], 200)
        self.assertEqual(result["paired_hoeffding_score_interval"], [0.0, 1.0])
        self.assertFalse(result["stronger_than_current"])

    def test_censor_pessimism_limits_claim_despite_resolved_wins(self):
        games = self.games(40) + [GameResult(seed=2000 + i, learner_seat=seat, winner=None, censored=True)
                               for i in range(60) for seat in (0, 1)]
        result = summarize_pairs(games, planned_pairs=100)
        self.assertEqual(result["resolved_game_score"], 1)
        self.assertFalse(result["stronger_than_current"])

    def test_early_stop_never_promotes(self):
        result = summarize_pairs(self.games(99), planned_pairs=100)
        self.assertFalse(result["evaluation_finished"])
        self.assertFalse(result["stronger_than_current"])
        self.assertEqual(result["missing_games"], 2)

    def test_correlated_seats_average_to_one_pair(self):
        games = [GameResult(seed=1000 + i, learner_seat=seat, winner=0)
                 for i in range(100) for seat in (0, 1)]
        result = summarize_pairs(games, planned_pairs=100)
        self.assertEqual(result["score_bounds_including_censors"], [0.5, 0.5])
        self.assertEqual(result["by_learner_seat"][0]["wins"], 100)
        self.assertEqual(result["by_learner_seat"][1]["losses"], 100)

    def test_duplicate_or_ambiguous_results_rejected(self):
        with self.assertRaises(ValueError):
            summarize_pairs([GameResult(1, 0, 0), GameResult(1, 0, 0)], planned_pairs=1)
        with self.assertRaises(ValueError):
            summarize_pairs([GameResult(1, 0, 0, censored=True)], planned_pairs=1)


class FrozenIncumbentTests(unittest.TestCase):
    def make_repo(self, root):
        resources = root / "Assets/Resources/AI"
        resources.mkdir(parents=True)
        (resources / ARTIFACT_NAMES[0]).write_bytes(b"frozen policy")
        (resources / ARTIFACT_NAMES[1]).write_text('{"Hybrid": true, "Depth": 24}')
        (resources / ARTIFACT_NAMES[2]).write_text(json.dumps({
            "policy_sha256": hashlib.sha256(b"frozen policy").hexdigest()}))
        (resources / ARTIFACT_NAMES[3]).write_text(json.dumps({
            "settings_sha256": hashlib.sha256((resources / ARTIFACT_NAMES[1]).read_bytes()).hexdigest()}))
        solo = root / "Assets/Scripts/Game/Soi/SoiSoloMatch.cs"
        solo.parent.mkdir(parents=True)
        solo.write_text("configured production launch")

    def test_frozen_files_are_independent_and_tampering_fails(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary) / "repo"
            self.make_repo(root)
            bundle = Path(temporary) / "bundle"
            freeze_incumbent(bundle, repo_root=root)
            (root / "Assets/Resources/AI" / ARTIFACT_NAMES[0]).write_bytes(b"new release")
            self.assertEqual((bundle / ARTIFACT_NAMES[0]).read_bytes(), b"frozen policy")
            verify_bundle(bundle, repo_root=root)
            (bundle / ARTIFACT_NAMES[0]).write_bytes(b"corruption")
            with self.assertRaises(ValueError):
                verify_bundle(bundle)

    def test_runtime_changes_require_review(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary) / "repo"
            self.make_repo(root)
            bundle = Path(temporary) / "bundle"
            freeze_incumbent(bundle, repo_root=root)
            (root / "Assets/Scripts/Game/Soi/SoiSoloMatch.cs").write_text("new search behavior")
            with self.assertRaises(ValueError):
                verify_bundle(bundle, repo_root=root)


@unittest.skipIf(np is None, "Transport integration needs the training NumPy environment")
class EvaluationTransportTests(unittest.TestCase):
    def test_persistent_terminal_rows_are_counted_once_and_held(self):
        advances = []
        actor_configurations = []

        class FakeHost:
            def __init__(self, **kwargs):
                self.batch = kwargs["batch"]
                self.actors = np.array([0, 1, 0, 1], dtype=np.int32)
                self.done = np.zeros(4, dtype=np.int32)
                self.rewards = np.zeros((4, 2), dtype=np.float32)
                self.mask = np.ones((4, 1), dtype=np.float32)
            def __enter__(self):
                return self
            def __exit__(self, *_):
                pass
            def advance(self, actions):
                advances.append(actions.copy())
                if len(advances) == 1:
                    self.done[:] = [1, 0, 2, 0]
                    self.rewards[0] = [1, -1]
                    self.mask[[0, 2]] = 0
                else:
                    self.done[:] = [1, 1, 2, 1]
                    self.rewards[3] = [-1, 1]
                    self.mask[:] = 0

        class FakeActor:
            def __init__(self, *_args, **_kwargs):
                actor_configurations.append(_kwargs)
            def act(self, host):
                return np.zeros(host.batch, dtype=np.int32), None

        with tempfile.TemporaryDirectory() as temporary:
            directory = Path(temporary)
            checkpoint, binary = directory / "checkpoint.bin", directory / "host.dll"
            checkpoint.write_bytes(b"fixed weights")
            binary.write_bytes(b"fixed host")
            (directory / "manifest.json").write_text("{}")
            frozen = {"files": {name: {"sha256": "abc"} for name in ARTIFACT_NAMES}}
            identity = {"schema": "shards-zero-depth-training-v1", "source_fingerprint": "abc",
                        "host_sha256": hashlib.sha256(b"fixed host").hexdigest(),
                        "configuration": {"engine_seed": 100, "compiled_actor": True, "packed_inputs": True}}
            current_catalog = {"version": 1, "observation_schema": "coverage-fixture-v1",
                               "limitations": ["Fixture coverage boundary"]}
            modules = {
                "host": types.SimpleNamespace(BINARY=binary, Host=FakeHost, catalog=lambda _: current_catalog),
                "model": types.SimpleNamespace(Actor=FakeActor, load_checkpoint=lambda *_args, **_kwargs:
                                               (types.SimpleNamespace(catalog=current_catalog),
                                                {"identity": identity, "state": {"next_engine_seed": 200}})),
                "train": types.SimpleNamespace(source_fingerprint=lambda: "abc"),
                "torch": types.SimpleNamespace(manual_seed=lambda _: None, set_num_threads=lambda _: None,
                    backends=types.SimpleNamespace(cuda=types.SimpleNamespace(matmul=types.SimpleNamespace()))),
            }
            with mock.patch.dict(sys.modules, modules), mock.patch("evaluate.verify_bundle", return_value=frozen):
                result = run_evaluation(checkpoint, bundle_directory=directory, output=directory / "evaluation.json",
                                        pairs=2, batch=4, graph=False, device="cpu")
            self.assertEqual(result["summary"]["recorded_games"], 4)
            self.assertEqual(result["summary"]["wins"], 2)
            self.assertEqual(result["summary"]["draws"], 1)
            self.assertEqual(result["summary"]["censored"], 1)
            self.assertFalse(result["summary"]["stronger_than_current"])
            np.testing.assert_array_equal(advances[1], [-1, 0, -1, 0])
            self.assertEqual(len(result["results"]), 4)
            self.assertEqual(result["observation_schema"], "coverage-fixture-v1")
            self.assertEqual(result["observation_limitations"], ["Fixture coverage boundary"])
            self.assertTrue(result["compiled_actor"] and result["packed_inputs"])
            self.assertEqual(actor_configurations, [{"graph": False, "device": "cpu", "compiled": True, "packed": True}])


if __name__ == "__main__":
    unittest.main()
