"""CPU-only real Adam preservation tests for an explicitly reviewed LR reduction."""
import copy
import io
import json
from pathlib import Path
import sys
import unittest
from unittest import mock

import torch

sys.path.insert(0, str(Path(__file__).resolve().parent))
import test_performance_upgrade as fixtures

upgrade = fixtures.upgrade
persistence = fixtures.persistence


class LearningRateUpgradeTests(unittest.TestCase):
    write = fixtures.UpgradeTests.write
    commit = fixtures.UpgradeTests.commit
    tearDown = fixtures.UpgradeTests.tearDown

    def setUp(self):
        fixtures.UpgradeTests.setUp(self)
        self.configuration.update(hero_mode='balanced_random', archive_strategy='historical')
        self.write('config.json', self.configuration)
        self.write('identity.json', self.old)
        self.parameters = [torch.nn.Parameter(torch.tensor([-0., 1.5])),
                           torch.nn.Parameter(torch.tensor([-.5, 2.]))]
        adam = torch.optim.Adam([{'params': [self.parameters[0]], 'betas': (.9, .999)},
                                 {'params': [self.parameters[1]], 'eps': 1e-7}],
                                lr=.0003, amsgrad=True)
        for tick in range(3):
            for i, parameter in enumerate(self.parameters):
                parameter.grad = torch.tensor([.25 + i, -.125 - tick])
            adam.step()
            adam.zero_grad(set_to_none=True)
        self.state['policy'] = {str(i): p.detach().clone() for i, p in enumerate(self.parameters)}
        self.state['optimizer'] = copy.deepcopy(adam.state_dict())

    def budget(self):
        return persistence.CampaignBudget(self.campaign / 'budget.json', 39012, clock=self.clock)

    def original_checkpoint(self, budget):
        with budget.start_session('zero-depth', 39012, 20) as session:
            self.anchor = {'campaign_id': budget.campaign_id, 'limit_seconds': 39012,
                           'boot_id': self.clock.boot,
                           'hard_deadline_monotonic': session.hard_deadline_monotonic,
                           'hard_deadline_wall': session.hard_deadline_wall}
            self.clock.advance(100)
            persistence.save_checkpoint_atomic(self.campaign / 'latest.soicp', self.state,
                identity=self.old, budget=budget, include_cuda_rng=False)

    def lr_identity(self, rate=.00015):
        self.new = copy.deepcopy(self.new)
        self.new['configuration']['learning_rate'] = rate
        return {'learning_rate': {'before': .0003, 'after': rate}}

    def assert_success(self, rate):
        with self.budget() as budget:
            self.original_checkpoint(budget)
            original = persistence.load_checkpoint(self.campaign / 'latest.soicp', expected_identity=self.old, budget=budget)
            previous = {'schema': upgrade.SCHEMA, 'state': 'committed', 'transaction': 'prior-reviewed',
                        'original_deadline': self.anchor}
            self.write('performance-upgrade.json', previous)
            approved = self.lr_identity(rate)
            ledger_bytes = budget.path.read_bytes()
            lineage = self.commit(budget, approved_configuration_changes=approved)
            migrated = persistence.load_checkpoint(self.campaign / 'latest.soicp', expected_identity=self.new, budget=budget)
            self.assertEqual(budget.path.read_bytes(), ledger_bytes)
            self.assertTrue(upgrade._same_state(original['budget'], migrated['budget']))
            self.assertTrue(upgrade._same_state(original['rng'], migrated['rng']))
            for key in self.state:
                if key not in ('configuration', 'optimizer'):
                    self.assertTrue(upgrade._same_state(original['state'][key], migrated['state'][key]), key)
            before, after = original['state']['optimizer'], migrated['state']['optimizer']
            self.assertTrue(upgrade._same_state(before['state'], after['state']))
            self.assertEqual(len(after['param_groups']), 2)
            for old_group, new_group in zip(before['param_groups'], after['param_groups']):
                self.assertEqual(new_group['lr'], rate)
                self.assertEqual(old_group['lr'], .0003)
                self.assertEqual(old_group.keys(), new_group.keys())
                self.assertTrue(upgrade._same_state({k: v for k, v in old_group.items() if k != 'lr'},
                                                   {k: v for k, v in new_group.items() if k != 'lr'}))
            # Two groups and their exact param IDs remain loadable.
            resumed = torch.optim.Adam([{'params': [p]} for p in self.parameters], lr=rate, amsgrad=True)
            resumed.load_state_dict(after)
            self.assertEqual([g['lr'] for g in resumed.param_groups], [rate, rate])
            self.assertEqual(lineage['configuration_changes'], approved)
            self.assertEqual(lineage['optimizer_learning_rate_change'], {'before': .0003, 'after': rate, 'groups': 2})
            self.assertIn('optimizer moments, steps and groups except explicitly approved learning rate', lineage['preserved'])
            self.assertEqual(lineage['original_deadline'], self.anchor)
            self.assertEqual(lineage['campaign_id'], budget.campaign_id)
            self.assertEqual(lineage['previous_upgrade_sha256'], upgrade.hashlib.sha256(upgrade.canonical(previous)).hexdigest())
            backup = Path(lineage['original_checkpoint']).parent
            self.assertEqual(json.loads((backup / 'previous-upgrade.json').read_text()), previous)
            self.assertEqual(json.loads((self.campaign / 'config.json').read_text()), self.new['configuration'])
            grant, deadline = upgrade.session_seconds(self.campaign, budget, 39012, with_deadline=True)
            self.assertEqual(grant, 38912)
            self.assertEqual(deadline, self.anchor['hard_deadline_monotonic'])
            for invalid_seconds in (43200, 39013):
                with self.assertRaises(upgrade.UpgradeError):
                    upgrade.session_seconds(self.campaign, budget, invalid_seconds)

    def assert_plateau_transition(self, before, after):
        self.configuration['learning_rate'] = before
        for group in self.state['optimizer']['param_groups']:
            group['lr'] = before
        self.write('config.json', self.configuration)
        self.write('identity.json', self.old)
        with self.budget() as budget:
            self.original_checkpoint(budget)
            original = persistence.load_checkpoint(self.campaign / 'latest.soicp', expected_identity=self.old, budget=budget)
            self.new = copy.deepcopy(self.new)
            self.new['configuration']['learning_rate'] = after
            approved = {'learning_rate': {'before': before, 'after': after}}
            ledger = budget.path.read_bytes()
            lineage = self.commit(budget, approved_configuration_changes=approved)
            migrated = persistence.load_checkpoint(self.campaign / 'latest.soicp', expected_identity=self.new, budget=budget)
            self.assertEqual(ledger, budget.path.read_bytes())
            self.assertTrue(upgrade._same_state(original['rng'], migrated['rng']))
            self.assertTrue(upgrade._same_state(original['state']['optimizer']['state'], migrated['state']['optimizer']['state']))
            for key in self.state:
                if key not in ('configuration', 'optimizer'):
                    self.assertTrue(upgrade._same_state(original['state'][key], migrated['state'][key]), key)
            self.assertEqual([g['lr'] for g in migrated['state']['optimizer']['param_groups']], [after, after])
            self.assertEqual(lineage['original_deadline'], self.anchor)
            self.assertEqual(lineage['configuration_changes'], approved)

    def test_plateau_half_rate_preserves_real_adam_rng_archive_and_allocation(self):
        self.assert_plateau_transition(.0001, .00005)

    def test_exact_plateau_rollback_preserves_latest_learned_state_and_allocation(self):
        self.assert_plateau_transition(.00005, .0001)

    def test_plateau_rate_cannot_change_unreviewed_rate_or_another_setting(self):
        old = {**self.configuration, 'learning_rate': .0001}
        for changes in ({'learning_rate': .00004}, {'learning_rate': 0.},
                        {'learning_rate': .00005, 'workers': 2}):
            new = {**old, **changes}
            approved = {key: {'before': old.get(key), 'after': new[key]} for key in changes}
            with self.subTest(changes=changes), self.assertRaises(upgrade.UpgradeError):
                upgrade.validate_configuration(old, new, approved)

    def test_half_rate_preserves_all_real_adam_state_and_39012_allocation(self):
        self.assert_success(.00015)

    def test_one_third_rate_preserves_all_real_adam_state_and_lineage(self):
        self.assert_success(.0001)

    def test_rate_change_requires_exact_explicit_approval(self):
        approved = self.lr_identity()
        with self.assertRaises(upgrade.UpgradeError):
            upgrade.validate_transition(self.old, self.new, self.before, self.after, self.approved)
        self.assertEqual(upgrade.validate_transition(self.old, self.new, self.before, self.after,
                                                    self.approved, approved), self.approved)

    def test_verify_cli_binds_exact_configuration_approval_without_writing_campaign(self):
        approved = self.lr_identity()
        paths = {'old-identity': self.old, 'new-identity': self.new,
                 'old-inventory': self.before, 'new-inventory': self.after,
                 'approved-changes': self.approved, 'approved-configuration-changes': approved}
        for name, value in paths.items():
            (self.root / (name + '.json')).write_bytes(upgrade.canonical(value))
        argv = ['performance_upgrade.py', 'verify']
        for name in paths:
            if name != 'approved-configuration-changes':
                argv += ['--' + name, str(self.root / (name + '.json'))]
        before = {p.name: p.read_bytes() for p in self.campaign.iterdir() if p.is_file()}
        with mock.patch.object(sys, 'argv', argv), self.assertRaises(upgrade.UpgradeError):
            upgrade.main()
        argv += ['--approved-configuration-changes', str(self.root / 'approved-configuration-changes.json')]
        output = io.StringIO()
        with mock.patch.object(sys, 'argv', argv), mock.patch.object(sys, 'stdout', output):
            upgrade.main()
        self.assertTrue(json.loads(output.getvalue())['valid'])
        self.assertEqual(before, {p.name: p.read_bytes() for p in self.campaign.iterdir() if p.is_file()})

    def test_only_reviewed_positive_reductions_from_original_rate_are_allowed(self):
        for before, after in ((.0003, .0003), (.0003, .0004), (.0003, .0002), (.0003, 0.),
                              (.0003, -.1), (.0003, float('inf')), (.0003, float('nan')),
                              (.0003, True), (.0003, '.00015'), (.0002, .0001), (None, .0001)):
            old = {**self.configuration, 'learning_rate': before}
            new = {**self.configuration, 'learning_rate': after}
            changes = {'learning_rate': {'before': before, 'after': after}}
            with self.subTest(before=before, after=after), self.assertRaises(upgrade.UpgradeError):
                upgrade.validate_configuration(old, new, changes)

    def test_any_second_changed_setting_is_rejected_even_when_exactly_approved(self):
        approved = self.lr_identity()
        for key, value in (('entropy', .01), ('archive_strategy', 'recent'), ('hero_mode', 'policy'),
                           ('width', 256), ('epochs', 4), ('engine_seed', 100)):
            new = copy.deepcopy(self.new)
            new['configuration'][key] = value
            changes = {**approved, key: {'before': self.configuration.get(key), 'after': value}}
            with self.subTest(field=key), self.assertRaises(upgrade.UpgradeError):
                upgrade.validate_transition(self.old, new, self.before, self.after, self.approved, changes)

    def assert_invalid_adam_is_unpublished(self):
        with self.budget() as budget:
            self.original_checkpoint(budget)
            approved = self.lr_identity()
            prior = {name: (self.campaign / name).read_bytes() for name in ('latest.soicp', 'config.json', 'identity.json', 'budget.json')}
            with self.assertRaisesRegex(upgrade.UpgradeError, 'optimizer|Adam|learning rate'):
                self.commit(budget, approved_configuration_changes=approved)
            for name, content in prior.items():
                self.assertEqual((self.campaign / name).read_bytes(), content, name)
            self.assertFalse((self.campaign / 'performance-upgrade.json').exists())

    def test_stale_rate_in_one_of_multiple_adam_groups_rejects_before_publication(self):
        self.state['optimizer']['param_groups'][1]['lr'] = .0001
        self.assert_invalid_adam_is_unpublished()

    def test_missing_adam_group_rate_rejects_before_publication(self):
        del self.state['optimizer']['param_groups'][0]['lr']
        self.assert_invalid_adam_is_unpublished()

    def test_empty_adam_groups_reject_before_publication(self):
        self.state['optimizer']['param_groups'] = []
        self.assert_invalid_adam_is_unpublished()

    def test_malformed_or_duplicate_adam_parameter_ids_reject_before_publication(self):
        self.state['optimizer']['param_groups'][1]['params'] = self.state['optimizer']['param_groups'][0]['params'][:]
        self.assert_invalid_adam_is_unpublished()

    def test_original_deadlines_cannot_extend_during_rate_migration(self):
        with self.budget() as budget:
            self.original_checkpoint(budget)
            approved = self.lr_identity()
            for key in ('limit_seconds', 'hard_deadline_monotonic', 'hard_deadline_wall'):
                original = self.anchor
                self.anchor = dict(original, **{key: original[key] + 1})
                try:
                    with self.subTest(field=key), self.assertRaises(upgrade.UpgradeError):
                        self.commit(budget, approved_configuration_changes=approved)
                finally:
                    self.anchor = original
            self.assertFalse((self.campaign / 'performance-upgrade.json').exists())

    def assert_candidate_drift_is_rejected(self, mutate):
        with self.budget() as budget:
            self.original_checkpoint(budget)
            approved = self.lr_identity()
            original_bytes = (self.campaign / 'latest.soicp').read_bytes()
            writer = upgrade.write_payload_atomic
            def drifting_writer(path, payload):
                owned = copy.deepcopy(payload)
                mutate(owned['state'])
                return writer(path, owned)
            with mock.patch.object(upgrade, 'write_payload_atomic', drifting_writer):
                with self.assertRaisesRegex(upgrade.UpgradeError, 'learned state|optimizer'):
                    self.commit(budget, approved_configuration_changes=approved)
            self.assertEqual((self.campaign / 'latest.soicp').read_bytes(), original_bytes)
            self.assertEqual(json.loads((self.campaign / 'identity.json').read_text()), self.old)
            self.assertEqual(json.loads((self.campaign / 'config.json').read_text()), self.configuration)
            self.assertEqual(json.loads((self.campaign / 'performance-upgrade.json').read_text())['state'], 'prepared')

    def test_policy_tensor_drift_is_rejected_before_latest_replacement(self):
        self.assert_candidate_drift_is_rejected(lambda state: state['policy']['0'].add_(1.))

    def test_adam_moment_drift_is_rejected_before_latest_replacement(self):
        self.assert_candidate_drift_is_rejected(lambda state: state['optimizer']['state'][0]['exp_avg'].add_(.5))

    def test_adam_step_drift_is_rejected_before_latest_replacement(self):
        self.assert_candidate_drift_is_rejected(lambda state: state['optimizer']['state'][0]['step'].add_(1.))

    def test_adam_non_rate_group_drift_is_rejected_before_latest_replacement(self):
        self.assert_candidate_drift_is_rejected(lambda state: state['optimizer']['param_groups'][0].update(eps=.1))


if __name__ == '__main__':
    unittest.main()
