"""CPU-only fake-host checks for the cross-runtime frozen evaluator."""
import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

import numpy as np
import torch

import evaluate_variants as evaluation


class PrefixHost:
    def __init__(self, batch, workers, seed, **kwargs):
        self.batch, self.seed = batch, seed
        self.obs = np.zeros((batch, 2048), np.float32)
        self.candidates = np.zeros((batch, 64, 32), np.float32)
        self.mask = np.zeros((batch, 64), np.float32)
        self.done = np.zeros(batch, np.int32)
        self.seats = np.ones(batch, np.int32)
        self.heroes = np.zeros((batch, 2), np.int32)
        self.phase, self.closed, self.trace = 0, False, []
        self.show()

    def show(self):
        self.obs.fill(0); self.mask.fill(0); self.candidates.fill(0)
        seat = 1-self.phase if self.phase < 2 else 0
        self.seats.fill(seat)
        self.obs[:, 22] = self.heroes[:, seat]/5
        self.obs[:, 70] = self.heroes[:, 1-seat]/5
        self.obs[:, 4] = self.phase < 2
        self.obs[:, 112:116] = evaluation._context_bytes("soi.herodraft" if self.phase < 2 else "")
        for lane in range(self.batch):
            for hero in range(1, 6):
                if hero in self.heroes[lane]:
                    continue
                slot = hero*3
                self.mask[lane, slot] = 1
                self.candidates[lane, slot, 12] = 1
                self.candidates[lane, slot, 31] = hero/5

    def advance_active(self, actions, active):
        assert self.phase < 2 and active.all()
        for lane, slot in enumerate(actions):
            assert self.mask[lane, slot]
            self.heroes[lane, self.seats[lane]] = round(float(self.candidates[lane, slot, 31])*5)
        self.trace.append(actions.copy())
        self.phase += 1
        self.show()

    def close(self):
        self.closed = True


class EvaluationTests(unittest.TestCase):
    def test_pinned_plan_matches_shared_cpu_plan_exactly(self):
        from hero_curriculum.balanced_evaluation import evaluation_plan as cpu_plan
        for count, seed in ((1, 0), (100, evaluation.PRIMARY_SEED), (3, 7321)):
            self.assertEqual(evaluation.evaluation_plan(pairs_per_cell=count, seed=seed),
                             cpu_plan(pairs_per_cell=count, seed=seed))
        hashes = evaluation.helper_hashes("cpu", "balanced")
        self.assertTrue(all(len(value) == 64 for value in hashes.values()))
        self.assertIn("experiments/hero_curriculum/balanced_evaluation.py", hashes)

    def test_import_does_not_load_cpu_shadow_or_initialize_cuda(self):
        # CPU-only modules set CUDA_VISIBLE_DEVICES as an import side effect;
        # the CUDA path must never import them.
        import sys
        self.assertNotIn("cpu_shadow_eval", sys.modules)
        self.assertFalse(torch.cuda.is_initialized())

    def test_all_balanced_prefixes_use_original_legal_slots(self):
        for a in evaluation.HEROES:
            for b in evaluation.HEROES:
                if a == b:
                    continue
                host = PrefixHost(3, 1, 91)
                evaluation.force_initial_draft(host, (a, b))
                self.assertEqual(host.phase, 2)
                np.testing.assert_array_equal(host.heroes, np.tile([evaluation.HEROES.index(a)+1, evaluation.HEROES.index(b)+1], (3, 1)))
                with self.assertRaises(RuntimeError):
                    evaluation.force_initial_draft(host, (a, b))

    def test_balanced_cuda_adapter_allocation_and_censors_with_fake_evaluator(self):
        hosts = []
        def factory(*args, **kwargs):
            host = PrefixHost(*args, **kwargs)
            hosts.append(host)
            return host
        def fake_match(a, b, *, games, seed, batch, **kwargs):
            offset = 0
            while offset < games//2:
                n = min(batch, games//2-offset)
                for _ in (0, 1):
                    host = evaluation.learning_eval.LearningHost(n, 1, seed+offset)
                    self.assertEqual(host.phase, 2)
                    host.close()
                offset += n
            return {"complete": True, "games": games, **evaluation.learning_eval.score_summary([1., 0.]*(games//2-1), games, 2)}
        with patch.object(torch.cuda, "_lazy_init", side_effect=AssertionError("CUDA forbidden")):
            result = evaluation.run_balanced_cuda(None, None, pairs_per_cell=3, seed=100, batch=2,
                host_factory=factory, evaluate_fn=fake_match)
        self.assertTrue(result["complete"])
        self.assertEqual((result["games"], result["resolved_games"], result["censored_games"], result["draws"]), (120, 80, 40, 0))
        self.assertIsNone(result["balanced_macro_score"])
        self.assertEqual(len(hosts), 80)
        self.assertTrue(all(host.closed for host in hosts))
        for index, specification in enumerate(result["plan"]["cells"]):
            a = evaluation.HEROES.index(specification["policy_a_hero"])+1
            b = evaluation.HEROES.index(specification["policy_b_hero"])+1
            group = hosts[index*4:index*4+4]
            for sequence, host in enumerate(group):
                expected = [a, b] if sequence % 2 == 0 else [b, a]
                np.testing.assert_array_equal(host.heroes, np.tile(expected, (host.batch, 1)))
            self.assertEqual([host.seed for host in group], [100+index*3, 100+index*3, 102+index*3, 102+index*3])

    def test_prefix_failure_closes_host_and_restores_evaluation_factory(self):
        host = PrefixHost(1, 1, 100)
        host.obs[:, 4] = 0
        original = evaluation.learning_eval.LearningHost
        def fake_match(*args, **kwargs):
            evaluation.learning_eval.LearningHost(1, 1, 100)
        with self.assertRaises(RuntimeError):
            evaluation.run_balanced_cuda(None, None, pairs_per_cell=1,
                host_factory=lambda *args, **kwargs: host, evaluate_fn=fake_match)
        self.assertTrue(host.closed)
        self.assertIs(evaluation.learning_eval.LearningHost, original)

    def test_incomplete_cell_is_not_reported_as_complete(self):
        result = evaluation.run_balanced_cuda(None, None, pairs_per_cell=1,
            evaluate_fn=lambda *args, **kwargs: {"complete": False, "games": 0})
        self.assertFalse(result["complete"])
        self.assertNotIn("score_a", result)
        self.assertEqual(len(result["cells"]), 1)

    def test_censored_replay_directory_records_forced_prefix_and_is_forwarded(self):
        with tempfile.TemporaryDirectory() as directory:
            def fake_match(*args, **kwargs):
                target = kwargs["debug_dir"]
                metadata = json.loads((target / "setup.json").read_text())
                self.assertEqual(metadata["host_setup"], "natural")
                self.assertEqual(metadata["planned_configuration"]["seed_start"], 333)
                self.assertIn("seat1 then seat0", metadata["prefix"])
                return {"complete": False, "games": 0}
            result = evaluation.run_balanced_cuda(None, None, pairs_per_cell=1, seed=333,
                evaluate_fn=fake_match, debug_dir=directory)
            self.assertFalse(result["complete"])

    def test_loader_selects_only_exact_v4_or_v5_resolver_and_restores_global(self):
        original = evaluation.checkpoints.identity
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory)/"fixture.soicp"
            for runtime in (evaluation.v4, evaluation.v5):
                (path.parent/"identity.json").write_text(json.dumps({"schema": runtime.SCHEMA}))
                expected = object()
                def load(checkpoint, role):
                    self.assertIs(evaluation.checkpoints.identity, expected)
                    return evaluation.checkpoints.FrozenSelection(None, {}, {}, {"cards": ["a"], "observationSchema": "shards-observation-v3"})
                with patch.object(runtime, "variant_identity", expected), patch.object(evaluation, "_base_load_selection", side_effect=load):
                    selection = evaluation.load_selection(path)
                    self.assertEqual(selection.metadata["training_variant"], runtime.SCHEMA)
                self.assertIs(evaluation.checkpoints.identity, original)
            (path.parent/"identity.json").write_text(json.dumps({"schema": "unknown"}))
            with self.assertRaises(ValueError):
                evaluation.load_selection(path)


if __name__ == "__main__":
    unittest.main()
