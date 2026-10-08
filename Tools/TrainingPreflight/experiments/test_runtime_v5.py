"""CPU-only identity, isolated migration, routing and real-host smoke tests.

No campaign checkpoint/ledger or live runtime is changed. Test publications are
synthetic temporary files. CUDA initialization is an explicit test failure.
"""
import copy
from dataclasses import asdict
import hashlib
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import patch

import numpy as np
import torch

import campaign_state as persistence
from learning_model import LearningPolicy, PolicyConfig, PPOConfig, PPOLearner
from test_migrate_checkpoint import Clock, encode_payload
from train_campaign import TrainConfig
import migrate_runtime_v5 as migration
import variant_v5_runtime as runtime


class RuntimeMigrationTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        torch.set_num_threads(1)
        cls.guard = patch.object(torch.cuda, "_lazy_init", side_effect=AssertionError("CUDA forbidden"))
        cls.guard.start()
        cls.source_identity, cls.catalog = runtime.v4.variant_identity(TrainConfig(adaptive_actors=True))
        cls.target_identity, new_catalog = runtime.variant_identity(TrainConfig(adaptive_actors=True))
        assert cls.catalog == new_catalog
        with torch.random.fork_rng(devices=[]):
            torch.manual_seed(9126)
            policy = LearningPolicy(PolicyConfig())
            initial = copy.deepcopy(policy.state_dict())
            states = {}
            with torch.no_grad():
                for index, parameter in enumerate(policy.parameters()):
                    parameter.add_(.002)
                    states[index] = {"step": torch.tensor(7.), "exp_avg": torch.full_like(parameter, .02),
                                     "exp_avg_sq": torch.full_like(parameter, .003)}
            learner = {"format": PPOLearner.FORMAT, "policy_config": policy.config.to_dict(),
                "ppo_config": asdict(PPOConfig()), "policy": copy.deepcopy(policy.state_dict()),
                "optimizer": {"state": states, "param_groups": [{"params": list(states), "lr": .0003}]},
                "updates": 7, "rejected": 2, "torch_rng_cpu": torch.get_rng_state().clone(),
                "torch_rng_device": torch.arange(32, dtype=torch.uint8)}
            cls.state = {"learner": learner, "generations": 9, "decisions": 13245, "games": 256,
                "optimizer_passes": 28, "next_engine_seed": 1152921504606847400, "eval_index": 3,
                "policy_config": policy.config.to_dict(), "configuration": asdict(TrainConfig(adaptive_actors=True)),
                "reason": "isolated-fixture", "champion": {"version": 8, "policy": copy.deepcopy(initial)},
                "initial_policy": initial, "archive": [{"version": 0, "policy": copy.deepcopy(initial)},
                    {"version": 9, "policy": copy.deepcopy(policy.state_dict())}],
                "opaque_preserved_metadata": {"bytes": b"fixture", "items": [3, "keep", None]}}

    @classmethod
    def tearDownClass(cls):
        cls.guard.stop()

    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.root = Path(self.tmp.name)
        self.source = self.root / "source.soicp"
        self.target = self.root / "target.soicp"
        self.ledger = self.root / "budget.json"
        clock = Clock()
        with persistence.CampaignBudget(self.ledger, clock=clock) as budget:
            with budget.start_session("fixture", 30, 0) as session:
                clock.advance(7)
                session.heartbeat()
                budget.record_collection(4, 3, 1, 50, 10)
                persistence.save_checkpoint_atomic(self.source, copy.deepcopy(self.state),
                    identity=self.source_identity, budget=budget, include_cuda_rng=False)
                clock.advance(2)
        self.original = persistence.load_checkpoint(self.source, expected_identity=self.source_identity)
        # Existing provenance must survive unchanged, including old migrations.
        self.original["migration"] = {"schema": "old-observation-migration", "opaque": b"unchanged"}
        self.original["runtime_migration"] = {"schema": "shards-v3-v4-runtime-migration-v1", "opaque": b"preserve"}
        self.original.pop("file_sha256")
        self.source.write_bytes(encode_payload(self.original))
        self.source_bytes, self.ledger_bytes = self.source.read_bytes(), self.ledger.read_bytes()

    def tearDown(self):
        self.tmp.cleanup()

    def preview(self):
        return migration.prepare_runtime_migration(self.source, source_identity=self.source_identity)

    def publish(self):
        return migration.migrate_runtime_checkpoint(self.source, self.target,
            ledger_path=self.ledger, source_identity=self.source_identity)

    def test_exact_identity_catalog_and_all_payload_preservation(self):
        before_rng = torch.get_rng_state().clone()
        result, report = self.preview()
        self.assertEqual(result["identity"], self.target_identity)
        self.assertEqual(migration.protected_payload_digest(result, self.source_identity),
                         migration.protected_payload_digest(self.original, self.source_identity))
        for key in ("state", "rng", "budget", "migration", "runtime_migration", "created_wall", "boundary", "recovery"):
            self.assertEqual(migration._tree_digest(result[key]), migration._tree_digest(self.original[key]))
        # These v3 input columns and their nonzero learned Adam moments must not
        # be zeroed by a same-observation runtime-only migration.
        moments = result["state"]["learner"]["optimizer"]["state"]
        self.assertTrue(all(torch.count_nonzero(item["exp_avg"]) > 0 for item in moments.values()))
        self.assertTrue(torch.equal(before_rng, torch.get_rng_state()))
        self.assertEqual(self.source.read_bytes(), self.source_bytes)
        self.assertEqual(self.ledger.read_bytes(), self.ledger_bytes)
        self.assertFalse(self.target.exists())
        self.assertFalse(torch.cuda.is_initialized())
        self.assertEqual(report["state_mutation"], "none")

    def test_temporary_publication_strict_reload_no_overwrite(self):
        report = self.publish()
        self.assertTrue(report["strict_target_reload_passed"])
        restored = persistence.load_checkpoint(self.target, expected_identity=self.target_identity)
        self.assertEqual(migration.protected_payload_digest(restored, self.source_identity),
                         migration.protected_payload_digest(self.original, self.source_identity))
        self.assertEqual(self.source.read_bytes(), self.source_bytes)
        self.assertEqual(self.ledger.read_bytes(), self.ledger_bytes)
        existing = self.target.read_bytes()
        with self.assertRaises(persistence.CheckpointError):
            self.publish()
        self.assertEqual(self.target.read_bytes(), existing)

    def test_active_or_locked_ledger_rejected(self):
        with persistence.CampaignBudget(self.ledger) as budget:
            with self.assertRaises(persistence.CampaignStateError):
                self.publish()
        ledger = json.loads(self.ledger.read_text())
        ledger["active"] = {"fixture": True}
        self.ledger.write_text(json.dumps(ledger))
        with self.assertRaises(persistence.CheckpointError):
            self.publish()
        self.assertFalse(self.target.exists())

    def test_cross_campaign_and_charge_ahead_rejected(self):
        for field, value in (("campaign_id", "other"), ("charged_seconds", 0)):
            ledger = json.loads(self.ledger_bytes)
            ledger[field] = value
            self.ledger.write_text(json.dumps(ledger))
            with self.assertRaises(persistence.CampaignStateError):
                self.publish()
            self.assertFalse(self.target.exists())

    def test_source_configuration_and_partial_boundary_rejected(self):
        for mutate in (lambda p: p.update(boundary="partial"),
                       lambda p: p["state"]["configuration"].update(width=256),
                       lambda p: p["state"]["learner"]["optimizer"]["state"].pop(0)):
            payload = copy.deepcopy(self.original)
            mutate(payload)
            self.source.write_bytes(encode_payload(payload))
            with self.assertRaises(persistence.CheckpointError):
                self.preview()

    def test_identity_drift_or_installed_runtime_rejected(self):
        invalid = copy.deepcopy(self.source_identity)
        invalid["rules_sha256"] = "0" * 64
        with self.assertRaises(persistence.CheckpointError):
            migration.prepare_runtime_migration(self.source, source_identity=invalid)
        with patch.object(runtime, "_installed", True):
            with self.assertRaises(persistence.CheckpointError):
                self.preview()
        with patch.object(runtime, "PINNED_V5_HOST", "0" * 64):
            with self.assertRaises(RuntimeError):
                runtime.variant_identity(TrainConfig(adaptive_actors=True))

    def test_real_cross_variant_loader_preserves_roles_and_exact_catalog(self):
        import evaluate_variants
        target, _ = self.preview()
        target_folder = self.root / "v5"
        target_folder.mkdir()
        target_path = target_folder / "preview.soicp"
        target_path.write_bytes(encode_payload(target))
        (target_folder / "identity.json").write_text(json.dumps(self.target_identity))
        (self.root / "identity.json").write_text(json.dumps(self.source_identity))
        for role in ("learner", "champion", "initial"):
            before = evaluate_variants.load_selection(self.source, role)
            after = evaluate_variants.load_selection(target_path, role)
            self.assertEqual(before.catalog, after.catalog)
            self.assertEqual(migration._tree_digest(before.weights), migration._tree_digest(after.weights))
            self.assertEqual(before.metadata["role"], role)
            self.assertEqual(after.metadata["training_variant"], runtime.SCHEMA)


class RoutingTests(unittest.TestCase):
    def test_constructor_context_resets_after_failure(self):
        with patch.object(runtime.v4.TrainingStatisticsHost, "__init__", side_effect=RuntimeError("fixture")):
            with self.assertRaises(RuntimeError):
                runtime.TrainingCurriculumHost(2, 1, seed=91)
        self.assertFalse(runtime._training_setup.get())

    def test_factory_defaults_natural_and_explicit_training_has_curriculum_and_statistics(self):
        factory = runtime._HostProcessFactory()
        args = [runtime.pipeline_bench.DOTNET,
            str(runtime.pipeline_bench.ROOT / "Tools/TrainingPreflight/Host/bin/Release/net8.0/TrainingHost.dll"), "serve"]
        env = {"PATH": "fixture", "SHARDS_STATS_DIRECTORY": "/wrong", "SHARDS_STATS_PURPOSE": "wrong"}
        with patch.object(runtime.v4.v3._original_subprocess, "Popen") as popen:
            factory.Popen(args, env=env)
            self.assertEqual(popen.call_args.kwargs["env"], {"PATH": "fixture"})
            self.assertEqual(popen.call_args.args[0][-2:], ["--hero-setup", "natural"])
            stats = runtime.v4._statistics_launch.set({"directory": "/explicit", "seed": 91, "batch": 2})
            try:
                with self.assertRaises(RuntimeError):
                    factory.Popen(args, env=env)
                token = runtime._training_setup.set(True)
                try:
                    factory.Popen(args, env=env)
                finally:
                    runtime._training_setup.reset(token)
            finally:
                runtime.v4._statistics_launch.reset(stats)
            self.assertEqual(popen.call_args.args[0][-2:], ["--hero-setup", "curriculum-75-25"])
            self.assertEqual(popen.call_args.kwargs["env"]["SHARDS_STATS_EXPECTED_SEED"], "91")
            self.assertEqual(popen.call_args.kwargs["env"]["SHARDS_STATS_PURPOSE"], "training_pool")
            factory.Popen(args, env=env)
            self.assertEqual(popen.call_args.args[0][-1], "natural")
            self.assertFalse(any(k.startswith("SHARDS_STATS_") for k in popen.call_args.kwargs["env"]))

    def test_real_prefix_replay_and_five_cpu_policy_steps_no_setup_rows(self):
        program = r'''
import json, sys
from pathlib import Path
from unittest.mock import patch
import numpy as np
import torch
torch.set_num_threads(1)
with patch.object(torch.cuda, "_lazy_init", side_effect=AssertionError("CUDA forbidden")):
    import variant_v5_runtime as runtime
    import train_campaign, learning_eval
    from learning_model import LearningPolicy, PolicyConfig
    from evaluate_variants import force_initial_draft
    root = Path(sys.argv[1])
    runtime.install(statistics_directory=root)
    assert train_campaign.LearningHost is runtime.TrainingCurriculumHost
    assert learning_eval.LearningHost is not runtime.TrainingCurriculumHost
    policy = LearningPolicy(PolicyConfig())
    for seed in (0, 1):
        hosts = [train_campaign.LearningHost(1, 1, seed=seed, pinned=False, transport="shared", split_branches=8),
                 learning_eval.LearningHost(1, 1, seed=seed, pinned=False, transport="shared", split_branches=8)]
        try:
            assert hosts[0].metrics[2] == 0 and hosts[0].metrics[3] == 0
            if seed == 0:
                force_initial_draft(hosts[1], ("decima", "tetra"))
            with torch.inference_mode():
                for _ in range(5):
                    np.testing.assert_array_equal(hosts[0].raw, hosts[1].raw)
                    host = hosts[0]
                    logits, values = policy(torch.from_numpy(host.obs), torch.from_numpy(host.candidates), torch.from_numpy(host.mask))
                    assert torch.isfinite(logits).all() and torch.isfinite(values).all()
                    actions = logits.argmax(-1).numpy()
                    for host in hosts:
                        host.advance_active(actions, np.ones(1, dtype=bool))
            np.testing.assert_array_equal(hosts[0].raw, hosts[1].raw)
            assert hosts[0].metrics[2] == 5
        finally:
            for host in hosts: host.close()
    snapshots = list(root.glob("*/session-*.json"))
    snapshots = [p for p in snapshots if not p.name.endswith("error.json")]
    assert snapshots
    assert {p.parent.name for p in snapshots} == {"natural-draft", "forced-random"}
    for path in snapshots:
        snapshot = json.loads(path.read_text())
        assert snapshot["schema"] == "shards-training-pool-stats-v1" and snapshot["final"]
        assert snapshot["purpose"] == "training_pool"
        assert snapshot["hero_setup"]["cohort"] == path.parent.name
    assert not torch.cuda.is_initialized()
    print(json.dumps({"passed": True, "real_cpu_policy_steps": 10, "forced_prefix_replayed": True,
        "natural_seed_unchanged": True, "setup_policy_rows": 0, "cuda_initialized": False}))
'''
        with tempfile.TemporaryDirectory() as directory:
            env = os.environ.copy()
            env.update(DOTNET_PROCESSOR_COUNT="1", OMP_NUM_THREADS="1", OPENBLAS_NUM_THREADS="1")
            result = subprocess.run([sys.executable, "-c", program, directory], env=env,
                text=True, capture_output=True, timeout=60)
            self.assertEqual(result.returncode, 0, result.stderr)
            self.assertTrue(json.loads(result.stdout)["passed"])


if __name__ == "__main__":
    unittest.main()
