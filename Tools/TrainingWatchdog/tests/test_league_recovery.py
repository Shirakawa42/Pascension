"""A watchdog boot transfer must keep the original fixed neural reference."""
import hashlib
import json
from pathlib import Path
import sys
import tempfile
import unittest
from unittest import mock

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import recover
import league


class LeagueRecoveryTests(unittest.TestCase):
    def test_champion_trial_and_evaluation_history_survive_transfer(self):
        with tempfile.TemporaryDirectory() as folder:
            parent, target = Path(folder) / 'parent', Path(folder) / 'new'
            (parent / 'champion').mkdir(parents=True)
            (parent / 'league').mkdir()
            (parent / 'champion/state.json').write_text('{"trials":7,"promotions":2}')
            (parent / 'champion/latest.json').write_text('{"score":0.5}')
            (parent / 'league/latest.json').write_text('{"games":1234}')
            (parent / 'league/baseline').mkdir()
            (parent / 'league/baseline/policy.pt').write_bytes(b'not copied: immutable external reference')
            state = {'trials': 7, 'promotions': 2}
            with mock.patch.object(league, 'read_champion', return_value=state) as verify:
                hashes = recover.preserve_evaluation_history(parent, target)
            self.assertEqual(verify.call_args_list, [mock.call(parent), mock.call(target)])
            self.assertEqual(len(hashes), 3)
            for name, sha in hashes.items():
                self.assertEqual((parent / name).read_bytes(), (target / name).read_bytes())
                self.assertEqual(recover.digest(target / name), sha)
            self.assertFalse((target / 'league/baseline').exists())

    def test_invalid_champion_and_symlink_history_are_rejected(self):
        with tempfile.TemporaryDirectory() as folder:
            parent, target = Path(folder) / 'parent', Path(folder) / 'new'
            (parent / 'champion').mkdir(parents=True)
            with mock.patch.object(league, 'read_champion', side_effect=ValueError('pin mismatch')):
                with self.assertRaises(ValueError): recover.preserve_evaluation_history(parent, target)
            (parent / 'champion').rmdir()
            (parent / 'league').mkdir()
            (parent / 'outside').write_text('{}')
            (parent / 'league/latest.json').symlink_to(parent / 'outside')
            with self.assertRaises(ValueError): recover.preserve_evaluation_history(parent, target)

    def test_legacy_campaign_without_league_remains_compatible(self):
        with tempfile.TemporaryDirectory() as folder:
            self.assertFalse(recover.preserve_league_plan(Path(folder), Path(folder)))

    def test_recovery_copies_exact_pinned_plan_without_resetting_baseline_or_interval(self):
        with tempfile.TemporaryDirectory() as folder:
            parent = Path(folder) / 'parent'; parent.mkdir()
            target = Path(folder) / 'recovered'; target.mkdir()
            plan = {'schema': league.PLAN_SCHEMA, 'baseline': str(parent / 'original-baseline'),
                    'baseline_manifest_sha256': 'a' * 64, 'every_games': 100000,
                    'games': 400, 'workers': 2, 'max_seconds': 120, 'seed_base': league.SEED_BASE}
            source = parent / 'league-plan.json'; source.write_text(json.dumps(plan, indent=2))
            with mock.patch.object(league, 'verify_frozen_policy') as verify:
                self.assertTrue(recover.preserve_league_plan(parent, target))
                verify.assert_called_once_with(plan['baseline'], manifest_sha256='a' * 64)
            self.assertEqual((target / source.name).read_bytes(), source.read_bytes())
            with self.assertRaises(ValueError):
                recover.preserve_league_plan(parent, target)

    def test_changed_reference_or_missing_pin_cannot_create_unverified_recovery_plan(self):
        with tempfile.TemporaryDirectory() as folder:
            parent = Path(folder) / 'parent'; parent.mkdir()
            target = Path(folder) / 'recovered'; target.mkdir()
            plan = {'schema': league.PLAN_SCHEMA, 'baseline': str(parent / 'original-baseline'),
                    'baseline_manifest_sha256': 'a' * 64}
            source = parent / 'league-plan.json'; source.write_text(json.dumps(plan))
            with mock.patch.object(league, 'verify_frozen_policy', side_effect=ValueError('changed reference')):
                with self.assertRaises(ValueError):
                    recover.preserve_league_plan(parent, target)
            self.assertFalse((target / source.name).exists())
            del plan['baseline_manifest_sha256']; source.write_text(json.dumps(plan))
            with self.assertRaises(ValueError):
                recover.preserve_league_plan(parent, target)


if __name__ == '__main__':
    unittest.main()
