"""CPU-only strict runtime migration and original-deadline recovery tests."""
import copy
import hashlib
import json
from pathlib import Path
import sys
import tempfile
import unittest
from unittest import mock

import torch

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "TrainingPreflight"))
import campaign_state as persistence
import performance_upgrade as upgrade
from evaluate import ARTIFACT_NAMES, freeze_incumbent


class Clock:
    def __init__(self):
        self.mono, self.wall, self.boot = 1000., 100000., "upgrade-test-boot"

    def monotonic(self):
        return self.mono

    def time(self):
        return self.wall

    def boot_id(self):
        return self.boot

    def advance(self, seconds):
        self.mono += seconds
        self.wall += seconds


class UpgradeTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.root = Path(self.temporary.name)
        self.repo = self.root / "repo"
        self.campaign = self.root / "campaign"
        self.campaign.mkdir()
        self.clock = Clock()
        self.boot = self.root / "boot"
        self.boot.write_text(self.clock.boot)
        self.patches = [mock.patch.object(upgrade, "BOOT_PATH", self.boot),
                        mock.patch.object(upgrade.time, "monotonic", self.clock.monotonic)]
        for patch in self.patches:
            patch.start()
        sources = ("Tools/ZeroDepthTraining/packed_transport.py", "Tools/ZeroDepthTraining/train.py",
                   "Tools/ZeroDepthTraining/model.py", "Tools/TrainingPreflight/campaign_state.py",
                   "Tools/TrainingPreflight/supervise_training.py", "Tools/TrainingPreflight/bench_common.py",
                   "Tools/BalancePatchHost/EffectDescriptors.cs", "Assets/Scripts/Shards/Engine/Game.cs",
                   "Assets/Scripts/Game/Soi/SoiSoloMatch.cs")
        for relative in sources:
            path = self.repo / relative
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_text("original " + relative)
        resources = self.repo / "Assets/Resources/AI"
        resources.mkdir(parents=True)
        for name in ARTIFACT_NAMES:
            (resources / name).write_bytes(b"frozen" if name.endswith(".bytes") else b"{}")
        freeze_incumbent(self.campaign / "incumbent", repo_root=self.repo)
        self.catalog = {"schema": "synthetic-frozen-catalog"}
        self.configuration = {"width": 512, "workers": 8, "batch": 128, "epochs": 3,
                              "learning_rate": .0003, "engine_seed": 2**61}
        self.binary = self.root / "host.dll"
        self.binary.write_bytes(b"new-host-orchestration")
        self.before = upgrade.source_inventory(self.repo)
        self.old = {"schema": "shards-zero-depth-training-v1", "configuration": self.configuration,
                    "source_fingerprint": self.before["source_fingerprint"], "host_sha256": "old-host",
                    "catalog_sha256": hashlib.sha256(json.dumps(self.catalog, sort_keys=True).encode()).hexdigest(),
                    "lookahead_depth": 0, "outcomes": "actual terminal zero-sum deciding-seat utilities"}
        (self.repo / "Tools/ZeroDepthTraining/packed_transport.py").write_text("reviewed execution optimization")
        self.after = upgrade.source_inventory(self.repo)
        self.new = {**self.old, "source_fingerprint": self.after["source_fingerprint"],
                    "host_sha256": upgrade.sha256_file(self.binary)}
        self.approved = upgrade.source_delta(self.before, self.after)
        for name, value in (("identity.json", self.old), ("config.json", self.configuration),
                            ("catalog.json", self.catalog), ("prepared.json", {"post_training_pairs": 2048})):
            self.write(name, value)
        self.anchor = None
        self.state = {"configuration": self.configuration, "policy": {"weight": torch.tensor([-0., 1.5])},
                      "optimizer": {"state": {0: {"step": torch.tensor(3.), "exp_avg": torch.tensor([.25, -.125]),
                                                 "exp_avg_sq": torch.tensor([.5, .5])}}, "param_groups": [{"lr": .0003}]},
                      "archive": [{"weight": torch.tensor([-.5, 2.])}, {"weight": torch.tensor([1., 3.])}],
                      "games": 256, "generations": 2, "optimizer_steps": 3, "decisions": 200,
                      "attempts": 256, "censored": 0, "example_passes": 600,
                      "next_engine_seed": 2**61 + 256, "rolling_game_stats": {"records": b"owned-ring", "count": 256}}

    def tearDown(self):
        for patch in reversed(self.patches):
            patch.stop()
        self.temporary.cleanup()

    def write(self, name, value):
        (self.campaign / name).write_bytes(upgrade.canonical(value))

    def budget(self):
        return persistence.CampaignBudget(self.campaign / "budget.json", 43200, clock=self.clock)

    def original_checkpoint(self, budget):
        with budget.start_session("zero-depth", 43200, 20) as session:
            self.anchor = {"campaign_id": budget.campaign_id, "limit_seconds": 43200, "boot_id": self.clock.boot,
                           "hard_deadline_monotonic": session.hard_deadline_monotonic,
                           "hard_deadline_wall": session.hard_deadline_wall}
            self.clock.advance(100)
            persistence.save_checkpoint_atomic(self.campaign / "latest.soicp", self.state,
                                               identity=self.old, budget=budget, include_cuda_rng=False)

    def commit(self, budget, **kwargs):
        return upgrade.commit_upgrade(self.campaign, repo_root=self.repo, binary_path=self.binary,
                    current_catalog=self.catalog, budget=budget, old_identity=self.old, new_identity=self.new,
                    old_inventory=self.before, new_inventory=self.after, approved_changes=self.approved,
                    original_deadline=self.anchor, **kwargs)

    def test_rollout_memory_commit_preserves_learned_state_rng_and_budget(self):
        self.configuration['capacity'] = 131072
        self.write('config.json', self.configuration)
        self.write('identity.json', self.old)
        with self.budget() as budget:
            self.original_checkpoint(budget)
            original = persistence.load_checkpoint(self.campaign / 'latest.soicp', expected_identity=self.old, budget=budget)
            self.new = copy.deepcopy(self.new)
            self.new['configuration'].update(batch=32, capacity=32768)
            approved = {'batch': {'before': 128, 'after': 32},
                        'capacity': {'before': 131072, 'after': 32768}}
            ledger_bytes = budget.path.read_bytes()
            lineage = self.commit(budget, approved_configuration_changes=approved)
            migrated = persistence.load_checkpoint(self.campaign / 'latest.soicp', expected_identity=self.new, budget=budget)
            self.assertEqual(budget.path.read_bytes(), ledger_bytes)
            self.assertTrue(upgrade._same_state(original['rng'], migrated['rng']))
            for key in self.state:
                if key != 'configuration':
                    self.assertTrue(upgrade._same_state(original['state'][key], migrated['state'][key]), key)
            self.assertEqual(migrated['state']['configuration'], self.new['configuration'])
            self.assertEqual(lineage['configuration_changes'], approved)
            self.assertEqual(lineage['original_deadline'], self.anchor)

    def test_batch96_commit_preserves_learned_state_rng_and_budget(self):
        self.configuration.update(batch=32, capacity=32768)
        self.write('config.json', self.configuration)
        self.write('identity.json', self.old)
        with self.budget() as budget:
            self.original_checkpoint(budget)
            original = persistence.load_checkpoint(self.campaign / 'latest.soicp', expected_identity=self.old, budget=budget)
            self.new = copy.deepcopy(self.new)
            self.new['configuration'].update(batch=96, capacity=98304)
            approved = {'batch': {'before': 32, 'after': 96},
                        'capacity': {'before': 32768, 'after': 98304}}
            ledger_bytes = budget.path.read_bytes()
            lineage = self.commit(budget, approved_configuration_changes=approved)
            migrated = persistence.load_checkpoint(self.campaign / 'latest.soicp', expected_identity=self.new, budget=budget)
            self.assertEqual(budget.path.read_bytes(), ledger_bytes)
            self.assertTrue(upgrade._same_state(original['rng'], migrated['rng']))
            for key in self.state:
                if key != 'configuration':
                    self.assertTrue(upgrade._same_state(original['state'][key], migrated['state'][key]), key)
            self.assertEqual(migrated['state']['configuration'], self.new['configuration'])
            self.assertEqual(lineage['configuration_changes'], approved)
            self.assertEqual(lineage['original_deadline'], self.anchor)

    def test_batch64_six_workers_commit_preserves_learned_state_rng_and_budget(self):
        self.configuration.update(batch=96, capacity=98304, workers=8)
        self.write('config.json', self.configuration)
        self.write('identity.json', self.old)
        with self.budget() as budget:
            self.original_checkpoint(budget)
            original = persistence.load_checkpoint(self.campaign / 'latest.soicp', expected_identity=self.old, budget=budget)
            self.new = copy.deepcopy(self.new)
            self.new['configuration'].update(batch=64, capacity=65536, workers=6)
            approved = {'batch': {'before': 96, 'after': 64},
                        'capacity': {'before': 98304, 'after': 65536},
                        'workers': {'before': 8, 'after': 6}}
            ledger_bytes = budget.path.read_bytes()
            lineage = self.commit(budget, approved_configuration_changes=approved)
            migrated = persistence.load_checkpoint(self.campaign / 'latest.soicp', expected_identity=self.new, budget=budget)
            self.assertEqual(budget.path.read_bytes(), ledger_bytes)
            self.assertTrue(upgrade._same_state(original['rng'], migrated['rng']))
            for key in self.state:
                if key != 'configuration':
                    self.assertTrue(upgrade._same_state(original['state'][key], migrated['state'][key]), key)
            self.assertEqual(migrated['state']['configuration'], self.new['configuration'])
            self.assertEqual(lineage['configuration_changes'], approved)
            self.assertEqual(lineage['original_deadline'], self.anchor)

    def test_shared_computer_resize_rejects_unreviewed_profiles(self):
        old = dict(batch=96, capacity=98304, workers=8, width=512)
        wanted = dict(batch=64, capacity=65536, workers=6, width=512)
        for field, value in [('workers', 7), ('batch', 65), ('capacity', 98304), ('width', 256)]:
            new = {**wanted, field: value}
            approved = {k: dict(before=old[k], after=v) for k, v in new.items() if old[k] != v}
            with self.assertRaises(upgrade.UpgradeError):
                upgrade.validate_configuration(old, new, approved)

    def test_hero_curriculum_requires_exact_explicit_approval(self):
        original = copy.deepcopy(self.new)
        self.new = copy.deepcopy(self.new)
        self.new['configuration']['hero_mode'] = 'balanced_random'
        approved = {'hero_mode': {'before': None, 'after': 'balanced_random'}}
        with self.assertRaises(upgrade.UpgradeError):
            upgrade.validate_transition(self.old, self.new, self.before, self.after, self.approved)
        self.assertEqual(upgrade.validate_transition(self.old, self.new, self.before, self.after,
                                                    self.approved, approved), self.approved)
        for field in ('width', 'workers', 'batch', 'epochs', 'learning_rate', 'engine_seed'):
            changed = copy.deepcopy(self.new)
            changed['configuration'][field] += 1
            with self.assertRaises(upgrade.UpgradeError):
                upgrade.validate_transition(self.old, changed, self.before, self.after, self.approved, approved)
        with self.assertRaises(upgrade.UpgradeError):
            upgrade.validate_transition(self.old, original, self.before, self.after, self.approved, approved)
        for before, after in (('balanced_random', 'policy'), (None, 'balanced_random'),
                              ('unknown', 'balanced_random'), ('policy', 'unknown'), ('policy', None)):
            old = {**self.configuration, 'hero_mode': before}
            new = {**self.configuration, 'hero_mode': after}
            changes = {'hero_mode': {'before': before, 'after': after}}
            with self.assertRaises(upgrade.UpgradeError):
                upgrade.validate_configuration(old, new, changes)
        upgrade.validate_configuration({**self.configuration, 'hero_mode': 'policy'},
                                       {**self.configuration, 'hero_mode': 'balanced_random'},
                                       {'hero_mode': {'before': 'policy', 'after': 'balanced_random'}})

    def test_hero_curriculum_commit_changes_only_reviewed_configuration(self):
        with self.budget() as budget:
            self.original_checkpoint(budget)
            original = persistence.load_checkpoint(self.campaign / 'latest.soicp', expected_identity=self.old, budget=budget)
            self.new = copy.deepcopy(self.new)
            self.new['configuration']['hero_mode'] = 'balanced_random'
            approved = {'hero_mode': {'before': None, 'after': 'balanced_random'}}
            lineage = self.commit(budget, approved_configuration_changes=approved)
            migrated = persistence.load_checkpoint(self.campaign / 'latest.soicp', expected_identity=self.new, budget=budget)
            self.assertEqual(lineage['configuration_changes'], approved)
            self.assertFalse(lineage['configuration_unchanged'])
            self.assertTrue(upgrade._same_state(original['rng'], migrated['rng']))
            for key in self.state:
                if key != 'configuration':
                    self.assertTrue(upgrade._same_state(original['state'][key], migrated['state'][key]), key)
            self.assertEqual(migrated['state']['configuration'], self.new['configuration'])
            self.assertEqual(json.loads((self.campaign / 'config.json').read_text()), self.new['configuration'])
            self.assertEqual(json.loads((self.campaign / 'identity.json').read_text()), self.new)
            self.assertEqual(original['budget'], migrated['budget'])
            self.assertEqual(upgrade.session_seconds(self.campaign, budget, 43200, with_deadline=True)[1],
                             self.anchor['hard_deadline_monotonic'])

    def test_exact_reviewed_source_delta_and_configuration_are_required(self):
        self.assertEqual(upgrade.validate_transition(self.old, self.new, self.before, self.after, self.approved), self.approved)
        for key in ("width", "workers", "batch", "epochs", "learning_rate", "engine_seed"):
            changed = copy.deepcopy(self.new)
            changed["configuration"][key] += 1
            with self.assertRaises(upgrade.UpgradeError):
                upgrade.validate_transition(self.old, changed, self.before, self.after, self.approved)
        with self.assertRaises(upgrade.UpgradeError):
            upgrade.validate_transition(self.old, self.new, self.before, self.after, {})
        changed = copy.deepcopy(self.new)
        changed["catalog_sha256"] = "different schema"
        with self.assertRaises(upgrade.UpgradeError):
            upgrade.validate_transition(self.old, changed, self.before, self.after, self.approved)

    def execution_bundle(self):
        """Create only the nine reviewed execution and regression source paths."""
        paths = ('train.py', 'native_packing.py', 'performance_upgrade.py',
                 'combined_actor.py', 'gpu_behavior.py', 'gpu_owned_copy.py',
                 'tests/test_performance_upgrade.py', 'tests/test_gpu_behavior.py',
                 'tests/test_gpu_owned_copy.py')
        for relative in paths:
            path = self.repo / 'Tools/ZeroDepthTraining' / relative
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_text('reviewed bundle ' + relative)
        # This bundle does not migrate packed_transport or any prior scope.
        relative = 'Tools/ZeroDepthTraining/packed_transport.py'
        (self.repo / relative).write_text('original ' + relative)
        inventory = upgrade.source_inventory(self.repo)
        identity = {**self.new, 'source_fingerprint': inventory['source_fingerprint']}
        identity['configuration'] = {**self.configuration, 'hero_mode': 'balanced_random'}
        original = {**self.old, 'configuration': dict(identity['configuration'])}
        return original, identity, inventory, upgrade.source_delta(self.before, inventory)

    def test_exact_reviewed_execution_bundle_accepts_only_its_bound_hash_delta(self):
        old, new, inventory, approved = self.execution_bundle()
        self.assertEqual(len(approved), 9)
        self.assertEqual(upgrade.validate_transition(old, new, self.before, inventory,
                                                    approved, {}), approved)
        omitted = dict(approved)
        omitted.pop('Tools/ZeroDepthTraining/gpu_owned_copy.py')
        with self.assertRaisesRegex(upgrade.UpgradeError, 'hash delta'):
            upgrade.validate_transition(old, new, self.before, inventory, omitted, {})
        altered = copy.deepcopy(approved)
        altered['Tools/ZeroDepthTraining/combined_actor.py']['after'] = 'unreviewed bytes'
        with self.assertRaisesRegex(upgrade.UpgradeError, 'hash delta'):
            upgrade.validate_transition(old, new, self.before, inventory, altered, {})

    def test_execution_bundle_rejects_arbitrary_new_sources_and_tests(self):
        old, new, inventory, approved = self.execution_bundle()
        for relative in ('Tools/ZeroDepthTraining/extra_helper.py',
                         'Tools/ZeroDepthTraining/tests/test_extra_helper.py',
                         'Tools/ZeroDepthTraining/combined_actor_extra.py',
                         'Tools/ZeroDepthTraining/Host/Extra.cs'):
            path = self.repo / relative
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_text('unapproved')
            changed_inventory = upgrade.source_inventory(self.repo)
            changed_identity = {**new, 'source_fingerprint': changed_inventory['source_fingerprint']}
            with self.subTest(relative=relative):
                with self.assertRaisesRegex(upgrade.UpgradeError, 'hash delta'):
                    upgrade.validate_transition(old, changed_identity, self.before,
                                                changed_inventory, approved, {})
                with self.assertRaisesRegex(upgrade.UpgradeError, 'source path'):
                    upgrade.validate_transition(old, changed_identity, self.before,
                                                changed_inventory,
                                                upgrade.source_delta(self.before, changed_inventory), {})
            path.unlink()

    def test_execution_bundle_rejects_config_identity_and_source_deletions(self):
        old, new, inventory, approved = self.execution_bundle()
        for key, value in (('width', 256), ('workers', 4), ('batch', 256),
                           ('epochs', 4), ('learning_rate', .003),
                           ('engine_seed', 123), ('hero_mode', 'policy'),
                           ('capacity', 65536), ('new_flag', True)):
            changed = copy.deepcopy(new)
            changed['configuration'][key] = value
            with self.subTest(configuration=key), self.assertRaises(upgrade.UpgradeError):
                upgrade.validate_transition(old, changed, self.before, inventory, approved, {})
        for key, value in (('lookahead_depth', 1), ('catalog_sha256', 'different'),
                           ('outcomes', 'invented reward'), ('new_identity_flag', True)):
            changed = {**new, key: value}
            with self.subTest(identity=key), self.assertRaises(upgrade.UpgradeError):
                upgrade.validate_transition(old, changed, self.before, inventory, approved, {})
        (self.repo / 'Tools/ZeroDepthTraining/train.py').unlink()
        changed_inventory = upgrade.source_inventory(self.repo)
        changed_identity = {**new, 'source_fingerprint': changed_inventory['source_fingerprint']}
        with self.assertRaisesRegex(upgrade.UpgradeError, 'deletion'):
            upgrade.validate_transition(old, changed_identity, self.before, changed_inventory,
                                        upgrade.source_delta(self.before, changed_inventory), {})

    def test_execution_scope_adds_exact_paths_to_historical_allowlists(self):
        helpers = {'Tools/ZeroDepthTraining/combined_actor.py',
                   'Tools/ZeroDepthTraining/gpu_behavior.py',
                   'Tools/ZeroDepthTraining/gpu_owned_copy.py'}
        tests = {'Tools/ZeroDepthTraining/tests/test_gpu_behavior.py',
                 'Tools/ZeroDepthTraining/tests/test_gpu_owned_copy.py'}
        self.assertTrue(helpers <= upgrade.PRODUCTION_PATHS)
        self.assertTrue(tests <= upgrade.TEST_PATHS)
        self.assertTrue(helpers | tests <= upgrade.NEW_SOURCE_PATHS)
        self.assertNotIn('Tools/ZeroDepthTraining/model.py', upgrade.PRODUCTION_PATHS)
        self.assertNotIn('Tools/ZeroDepthTraining/tests/test_extra_helper.py', upgrade.TEST_PATHS)

    def test_model_rules_and_unapproved_inventory_changes_are_refused(self):
        for relative in ("Tools/ZeroDepthTraining/model.py", "Assets/Scripts/Shards/Engine/Game.cs",
                         "Tools/ZeroDepthTraining/unreviewed.py"):
            path = self.repo / relative
            original = path.read_bytes() if path.exists() else None
            path.write_text("changed")
            inventory = upgrade.source_inventory(self.repo)
            identity = {**self.new, "source_fingerprint": inventory["source_fingerprint"]}
            with self.assertRaises(upgrade.UpgradeError):
                upgrade.validate_transition(self.old, identity, self.before, inventory, upgrade.source_delta(self.before, inventory))
            if original is None:
                path.unlink()
            else:
                path.write_bytes(original)

    def test_commit_preserves_weights_adam_archive_rng_ring_seed_and_original_backup(self):
        with self.budget() as budget:
            self.original_checkpoint(budget)
            original_bytes = (self.campaign / "latest.soicp").read_bytes()
            original = persistence.load_checkpoint(self.campaign / "latest.soicp", expected_identity=self.old, budget=budget)
            rng = persistence.capture_rng(include_cuda=False)
            charged = budget.charged_seconds
            lineage = self.commit(budget)
            migrated = persistence.load_checkpoint(self.campaign / "latest.soicp", expected_identity=self.new, budget=budget)
            self.assertEqual(lineage["state"], "committed")
            self.assertEqual(Path(lineage["original_checkpoint"]).read_bytes(), original_bytes)
            self.assertTrue(upgrade._same_state(original["rng"], migrated["rng"]))
            self.assertTrue(upgrade._same_state(rng, persistence.capture_rng(include_cuda=False)))
            for key in self.state:
                self.assertTrue(upgrade._same_state(original["state"][key], migrated["state"][key]), key)
            self.assertEqual(original["created_wall"], migrated["created_wall"])
            self.assertEqual(original["budget"], migrated["budget"])
            self.assertEqual(budget.charged_seconds, charged)
            with self.assertRaises(persistence.CheckpointError):
                persistence.load_checkpoint(self.campaign / "latest.soicp", expected_identity=self.old, budget=budget)

    def test_original_deadline_is_preserved_even_when_session_starts_later(self):
        with self.budget() as budget:
            self.original_checkpoint(budget)
            self.commit(budget)
            self.clock.advance(50)
            grant, deadline = upgrade.session_seconds(self.campaign, budget, 43200, with_deadline=True)
            self.clock.advance(7)
            with budget.start_session("resume", grant, 20, absolute_deadline_monotonic=deadline) as session:
                self.assertEqual(session.hard_deadline_monotonic, self.anchor["hard_deadline_monotonic"])
                self.assertEqual(session.remaining_seconds, grant - 7)
            self.clock.mono = deadline
            with self.assertRaises(upgrade.UpgradeError):
                upgrade.session_seconds(self.campaign, budget, 43200)
            with self.assertRaises(persistence.BudgetExceeded):
                budget.start_session("expired", 100, 0, absolute_deadline_monotonic=deadline)

    def test_boot_deadline_identity_and_allocation_drift_fail_closed(self):
        with self.budget() as budget:
            self.original_checkpoint(budget)
            self.commit(budget)
            self.boot.write_text("different-boot")
            with self.assertRaises(upgrade.UpgradeError):
                upgrade.session_seconds(self.campaign, budget, 43200)
            self.boot.write_text(self.clock.boot)
            with self.assertRaises(upgrade.UpgradeError):
                upgrade.session_seconds(self.campaign, budget, 40000)
            manifest = json.loads((self.campaign / "performance-upgrade.json").read_text())
            manifest["original_deadline"]["hard_deadline_monotonic"] += 10
            self.write("performance-upgrade.json", manifest)
            with self.assertRaises(upgrade.UpgradeError):
                upgrade.session_seconds(self.campaign, budget, 43200)

    def test_incomplete_commit_cannot_resume_and_retains_original_checkpoint(self):
        with self.budget() as budget:
            self.original_checkpoint(budget)
            original = (self.campaign / "latest.soicp").read_bytes()
            with mock.patch.object(upgrade, "write_payload_atomic", side_effect=OSError("injected disk failure")):
                with self.assertRaises(OSError):
                    self.commit(budget)
            self.assertEqual((self.campaign / "latest.soicp").read_bytes(), original)
            self.assertEqual(json.loads((self.campaign / "identity.json").read_text()), self.old)
            with self.assertRaises(upgrade.UpgradeError):
                upgrade.session_seconds(self.campaign, budget, 43200)

    def test_active_session_and_binary_or_catalog_drift_are_rejected_before_publish(self):
        with self.budget() as budget:
            self.original_checkpoint(budget)
            with budget.start_session("still active", 10, 0):
                with self.assertRaises(upgrade.UpgradeError):
                    self.commit(budget)
            self.binary.write_bytes(b"unexpected")
            with self.assertRaises(upgrade.UpgradeError):
                self.commit(budget)
            self.binary.write_bytes(b"new-host-orchestration")
            self.catalog["schema"] = "changed"
            with self.assertRaises(upgrade.UpgradeError):
                self.commit(budget)
            self.assertFalse((self.campaign / "performance-upgrade.json").exists())

    def test_fresh_campaign_returns_existing_budget_grant_without_deadline(self):
        with self.budget() as budget:
            self.assertEqual(upgrade.session_seconds(self.campaign, budget, 43200, with_deadline=True), (43200, None))


class RolloutMemoryConfigurationTests(unittest.TestCase):
    def setUp(self):
        self.old = {'batch': 128, 'capacity': 131072, 'width': 512,
                    'workers': 8, 'learning_rate': .0001, 'hero_mode': 'balanced_random'}
        self.new = {**self.old, 'batch': 32, 'capacity': 32768}
        self.approved = {'batch': {'before': 128, 'after': 32},
                         'capacity': {'before': 131072, 'after': 32768}}

    def test_exact_memory_reduction_is_allowed(self):
        self.assertEqual(upgrade.validate_configuration(self.old, self.new, self.approved), self.approved)

    def test_memory_reduction_requires_explicit_exact_review(self):
        for approval in (None, {}, {'batch': self.approved['batch']}):
            with self.subTest(approval=approval), self.assertRaises(upgrade.UpgradeError):
                upgrade.validate_configuration(self.old, self.new, approval)

    def test_mismatched_buffer_or_extra_training_change_is_rejected(self):
        for changes in ({'capacity': 131072}, {'batch': 64}, {'capacity': 16384},
                        {'width': 256}, {'workers': 4}, {'learning_rate': .00015},
                        {'hero_mode': 'policy'}, {'batch': 32.0}, {'capacity': True}):
            candidate = {**self.new, **changes}
            approval = {key: {'before': self.old.get(key), 'after': candidate.get(key)}
                        for key in self.old.keys() | candidate.keys()
                        if self.old.get(key) != candidate.get(key)}
            with self.subTest(changes=changes), self.assertRaises(upgrade.UpgradeError):
                upgrade.validate_configuration(self.old, candidate, approval)

    def test_batch96_requires_exact_profile_and_approval(self):
        old = {**self.old, 'batch': 32, 'capacity': 32768}
        new = {**old, 'batch': 96, 'capacity': 98304}
        approval = {'batch': {'before': 32, 'after': 96},
                    'capacity': {'before': 32768, 'after': 98304}}
        self.assertEqual(upgrade.validate_configuration(old, new, approval), approval)
        with self.assertRaises(upgrade.UpgradeError):
            upgrade.validate_configuration(old, new)
        for changes in ({'batch': 128}, {'capacity': 32768}, {'capacity': 131072},
                        {'width': 256}, {'workers': 4}, {'batch': 96.0}):
            candidate = {**new, **changes}
            reviewed = {key: {'before': old.get(key), 'after': candidate.get(key)}
                        for key in old.keys() | candidate.keys() if old.get(key) != candidate.get(key)}
            with self.subTest(changes=changes), self.assertRaises(upgrade.UpgradeError):
                upgrade.validate_configuration(old, candidate, reviewed)

    def test_reverse_transition_cannot_increase_memory(self):
        approval = {key: {'before': change['after'], 'after': change['before']}
                    for key, change in self.approved.items()}
        with self.assertRaises(upgrade.UpgradeError):
            upgrade.validate_configuration(self.new, self.old, approval)


if __name__ == "__main__":
    unittest.main()
