"""CPU-only shadow-evaluator contract checks; no real engine or CUDA workload."""
import copy
import json
from pathlib import Path
import tempfile
from types import SimpleNamespace
import unittest
from unittest.mock import patch
import sys

import numpy as np
import torch

import cpu_shadow_eval as shadow
import learning_eval
import learning_model
from learning_model import LearningActor, LearningPolicy, PolicyConfig
from test_learning_collection import Decision, ScriptedHost


class HostFixture(ScriptedHost):
    def __init__(self, *, truncated=()):
        super().__init__([[Decision(0, 0)], [Decision(1, 1), Decision(0, 0), Decision(1, 1)]],
                         [(1, -1), (-1, 1)], initial_counters=False, truncated=truncated)
        self.close_calls = 0

    def _show(self, lane):
        super()._show(lane)
        seat = self.seats[lane]
        # Seat0 Decima, seat1 Tetra; relative indices change with decision owner.
        self.obs[lane, 22] = (seat+1)/5
        self.obs[lane, 70] = (2-seat)/5

    @property
    def upload(self):
        return torch.from_numpy(np.concatenate((self.obs.ravel(), self.candidates.ravel(), self.mask.ravel())))

    def close(self):
        super().close()
        self.close_calls += 1


class ShadowTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        shadow.configure_cpu()
        cls.a = LearningPolicy(PolicyConfig()).eval().requires_grad_(False)
        cls.b = copy.deepcopy(cls.a)

    def factory(self, hosts, truncated=()):
        def build(batch, workers, seed, **kwargs):
            self.assertEqual((batch, workers), (2, 1))
            self.assertFalse(kwargs["pinned"])
            host = HostFixture(truncated=truncated)
            hosts.append((seed, host))
            return host
        return build

    def test_cpu_preserves_exact_seeded_actions_outcomes_and_holds(self):
        baseline, observed = [], []
        torch.manual_seed(135)
        def actor_factory(policy, batch, **kwargs):
            return LearningActor(policy, batch, graph=False, device="cpu")
        with patch.object(learning_model, "LearningActor", actor_factory), \
             patch.object(learning_eval, "LearningHost", self.factory(baseline)):
            # Production asks pinned=True; baseline factory independently forces it.
            original = self.factory(baseline)
            def host_factory(batch, workers, seed, **kwargs):
                kwargs["pinned"] = False
                return original(batch, 1, seed, **kwargs)
            with patch.object(learning_eval, "LearningHost", host_factory):
                expected = learning_eval.evaluate_match(self.a, self.b, games=8, batch=2,
                    seed=100, workers=1, telemetry=True, censor_truncated=True)
        actual = shadow.run_cpu_match(self.a, self.b, games=8, batch=2, seed=100,
            sampling_seed=135, host_factory=self.factory(observed))
        for key in ("score_a", "score_bound_95", "wins_a", "draws", "losses_a",
                    "selected_actions_by_kind_card", "legal_candidate_opportunities_by_kind_card",
                    "wrapper_decisions", "engine_submissions"):
            self.assertEqual(actual[key], expected[key], key)
        self.assertEqual([seed for seed, _ in observed], [100, 100, 102, 102])
        self.assertEqual([host.executed for _, host in baseline], [host.executed for _, host in observed])
        self.assertTrue(all(host.close_calls == 1 for _, host in observed))
        self.assertTrue(all(np.array_equal(host.calls[-1][1], [False, True]) for _, host in observed))
        episodes = actual["shadow_diagnostics"]["episodes"]
        self.assertEqual(len(episodes), 8)
        self.assertEqual({(e["seed"], e["policy_a_seat"]) for e in episodes},
                         {(seed, seat) for seed in range(100, 104) for seat in (0, 1)})
        for e in episodes:
            self.assertEqual(e["policy_a_hero"], ("decima", "tetra")[e["policy_a_seat"]])
            self.assertEqual(e["policy_b_hero"], ("tetra", "decima")[e["policy_a_seat"]])
        groups = actual["shadow_diagnostics"]["hero_seat_summary"]
        self.assertEqual(len(groups), 2)
        self.assertEqual(sum(group["attempts"] for group in groups), 8)
        self.assertEqual({group["policy_a_seat"] for group in groups}, {0, 1})
        diagnostics = actual["shadow_diagnostics"]["policies"]
        self.assertEqual(sum(d["decisions"] for d in diagnostics.values()), actual["wrapper_decisions"])
        self.assertEqual(sum(d["resolved_value_rows"] for d in diagnostics.values()), actual["wrapper_decisions"])
        for data in diagnostics.values():
            self.assertEqual(sum(data["legal_menu_counts"]), data["decisions"])
            self.assertAlmostEqual(data["value_mse"], 1., places=6)  # initial V=0, terminal +/-1
            self.assertTrue(0 <= data["mean_normalized_choice_entropy"] <= 1.000001)
        self.assertTrue(actual["frozen_weights_unchanged"])
        self.assertFalse(torch.cuda.is_initialized())

    def test_censor_remains_unknown_and_excluded_from_value_calibration(self):
        hosts = []
        result = shadow.run_cpu_match(self.a, self.b, games=4, batch=2, seed=50,
            host_factory=self.factory(hosts, truncated=(0,)))
        self.assertEqual((result["resolved_games"], result["censored_games"]), (2, 2))
        self.assertIsNone(result["score_a"])
        self.assertEqual(result["score_identification_interval"], [.25, .75])
        self.assertEqual(result["draws"], 0)
        data = result["shadow_diagnostics"]
        self.assertEqual(sum(e["score_a"] is None for e in data["episodes"]), 2)
        self.assertEqual(sum(d["decisions"] for d in data["policies"].values()), 8)
        self.assertEqual(sum(d["resolved_value_rows"] for d in data["policies"].values()), 6)
        self.assertTrue(all(group["score_a"] is None for group in data["hero_seat_summary"]))

    def test_exception_restores_factories_and_closes_live_hosts(self):
        old_actor, old_host = learning_model.LearningActor, learning_eval.LearningHost
        hosts = []
        def broken(batch, workers, seed, **kwargs):
            host = self.factory(hosts)(batch, workers, seed, **kwargs)
            def fail(*args):
                raise RuntimeError("fixture host failure")
            host.advance_active = fail
            return host
        with self.assertRaisesRegex(RuntimeError, "fixture host failure"):
            shadow.run_cpu_match(self.a, self.b, games=4, batch=2, seed=100, host_factory=broken)
        self.assertIs(learning_model.LearningActor, old_actor)
        self.assertIs(learning_eval.LearningHost, old_host)
        self.assertEqual(hosts[0][1].close_calls, 1)

    def test_deadline_is_incomplete_not_fabricated_score(self):
        hosts = []
        with patch.object(shadow.time, "monotonic", side_effect=[0., 0., 2., 2.]):
            result = shadow.run_cpu_match(self.a, self.b, games=4, batch=2, seed=100,
                max_seconds=1, host_factory=self.factory(hosts))
        self.assertFalse(result["complete"])
        self.assertEqual(result["reason"], "cpu_shadow_cooperative_deadline")
        self.assertNotIn("score_a", result)
        self.assertEqual(hosts[0][1].close_calls, 1)

    def test_same_checkpoint_roles_share_one_immutable_snapshot(self):
        with tempfile.TemporaryDirectory() as folder:
            source = Path(folder)/"latest.soicp"
            source.write_bytes(b"first payload")
            (source.parent/"identity.json").write_text(json.dumps({"schema": "fixture"}))
            contents, copies = [], []
            def load(path, role):
                contents.append(path.read_bytes())
                copies.append(path)
                source.write_bytes(b"replaced payload")
                return SimpleNamespace(metadata={"role": role}, catalog={"schema": "fixture"})
            with patch.object(shadow.checkpoints, "load_selection", side_effect=load):
                a, b = shadow.frozen_selections(source, "learner", source, "champion")
            self.assertEqual(contents, [b"first payload"]*2)
            self.assertEqual(copies[0], copies[1])
            self.assertFalse(copies[0].exists())
            self.assertEqual(a.metadata["checkpoint"], str(source))

    def test_no_cuda_calls_or_pinned_tensors(self):
        with patch.object(torch.cuda, "_lazy_init", side_effect=AssertionError("CUDA initialization")):
            actor = shadow.CPUActor(self.a, 2, role=0, graph=True, device="cuda")
            self.assertEqual(actor.device.type, "cpu")
            self.assertIsNone(actor.graph)
            self.assertFalse(actor.upload.is_pinned())
            self.assertFalse(actor.output_host.is_pinned())
            actor.act(HostFixture())

    def test_variant_rebinds_captured_loader_identity_and_policy(self):
        def variant_identity(config):
            return {"schema": "fixture-v3"}, {}
        class VariantPolicy:
            pass
        def install():
            learning_model.LearningPolicy = VariantPolicy
        runtime = SimpleNamespace(install=install, variant_identity=variant_identity)
        with patch.dict(sys.modules, {"variant_runtime": runtime}), \
             patch.object(learning_model, "LearningPolicy", learning_model.LearningPolicy), \
             patch.object(shadow.checkpoints, "identity", shadow.checkpoints.identity), \
             patch.object(shadow.checkpoints, "LearningPolicy", shadow.checkpoints.LearningPolicy):
            shadow.configure_variant("v3")
            self.assertIs(shadow.checkpoints.identity, variant_identity)
            self.assertIs(shadow.checkpoints.LearningPolicy, VariantPolicy)
        self.assertFalse(torch.cuda.is_initialized())

    def test_v4_uses_its_own_installer_and_explicit_loader_references(self):
        def v4_identity(config):
            return {"schema": "fixture-v4"}, {}
        class V4Policy:
            pass
        def install_v4():
            learning_model.LearningPolicy = V4Policy
        runtime = SimpleNamespace(install=install_v4, variant_identity=v4_identity)
        with patch.dict(sys.modules, {"variant_v4_runtime": runtime}), \
             patch.object(learning_model, "LearningPolicy", learning_model.LearningPolicy), \
             patch.object(shadow.checkpoints, "identity", shadow.checkpoints.identity), \
             patch.object(shadow.checkpoints, "LearningPolicy", shadow.checkpoints.LearningPolicy), \
             patch.object(torch.cuda, "_lazy_init", side_effect=AssertionError("No CUDA")):
            shadow.configure_variant("v4")
            self.assertIs(shadow.checkpoints.identity, v4_identity)
            self.assertIs(shadow.checkpoints.LearningPolicy, V4Policy)
            with self.assertRaisesRegex(ValueError, "Expected runtime variant"):
                shadow.configure_variant("v99")
        self.assertFalse(torch.cuda.is_initialized())

    def test_v5_installation_keeps_the_natural_evaluation_host(self):
        def v5_identity(config):
            return {"schema": "fixture-v5"}, {}
        original_host = shadow.learning_eval.LearningHost
        runtime = SimpleNamespace(install=lambda: None, variant_identity=v5_identity)
        with patch.dict(sys.modules, {"variant_v5_runtime": runtime}), \
             patch.object(shadow.checkpoints, "identity", shadow.checkpoints.identity), \
             patch.object(shadow.checkpoints, "LearningPolicy", shadow.checkpoints.LearningPolicy), \
             patch.object(torch.cuda, "_lazy_init", side_effect=AssertionError("No CUDA")):
            shadow.configure_variant("v5")
            self.assertIs(shadow.checkpoints.identity, v5_identity)
            self.assertIs(shadow.learning_eval.LearningHost, original_host)
        self.assertFalse(torch.cuda.is_initialized())

    def test_throttle_preserves_seeded_actions_rng_and_counts_actual_replies(self):
        plain_hosts, delayed_hosts = [], []
        plain = shadow.run_cpu_match(self.a, self.b, games=4, batch=2, seed=100,
            sampling_seed=456, host_factory=self.factory(plain_hosts))
        plain_rng = torch.get_rng_state().clone()
        with patch.object(shadow.time, "sleep") as sleep:
            delayed = shadow.run_cpu_match(self.a, self.b, games=4, batch=2, seed=100,
                sampling_seed=456, host_factory=self.factory(delayed_hosts), step_delay_ms=3.)
        self.assertTrue(torch.equal(plain_rng, torch.get_rng_state()))
        self.assertEqual([h.executed for _, h in plain_hosts], [h.executed for _, h in delayed_hosts])
        self.assertEqual(plain["score_a"], delayed["score_a"])
        diagnostics = delayed["shadow_diagnostics"]
        self.assertEqual(diagnostics["host_advance_calls"], 6)
        self.assertEqual(sleep.call_count, 6)
        self.assertEqual(diagnostics["sleep_calls"], 6)
        self.assertAlmostEqual(diagnostics["sleep_requested_seconds"], .018)
        self.assertEqual(delayed["execution"]["step_delay_ms"], 3.)
        self.assertGreaterEqual(delayed["cpu_load"]["python_cpu_seconds"], 0)


if __name__ == "__main__":
    unittest.main()
