import json
from pathlib import Path
import sys
import tempfile
import time
import unittest
from unittest import mock

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import hourly_review as review


class HourlyReviewTests(unittest.TestCase):
    def test_repeated_evaluations_require_actual_learning_pilot(self):
        with tempfile.TemporaryDirectory() as folder:
            p = Path(folder)
            for slot in (1, 2):
                (p / f'review-{slot:02d}.receipt.json').write_text(json.dumps({'intervention_required': True}))
                (p / f'action-evidence-{slot:02d}.json').write_text(json.dumps({'action': 'experiment'}))
            self.assertFalse(review.learning_pilot_required(p, 2, 2))
            self.assertTrue(review.learning_pilot_required(p, 3, 2))
            self.assertFalse(review.learning_pilot_required(p, 3, 0))
            (p / 'action-evidence-02.json').write_text(json.dumps({'action': 'experiment', 'experiment_kind': 'learning_pilot'}))
            self.assertFalse(review.learning_pilot_required(p, 3, 2))

    def test_strength_only_evidence_cannot_satisfy_learning_requirement(self):
        with tempfile.TemporaryDirectory() as folder:
            p = Path(folder); artifact = p / 'results.json'; artifact.write_text('{"games": 1000}')
            evidence = {'slot': 3, 'action': 'experiment', 'conclusion': 'Inconclusive', 'artifacts': [str(artifact)]}
            path = p / 'evidence.json'; path.write_text(json.dumps(evidence))
            self.assertTrue(review.verify_intervention(path, 3))
            self.assertFalse(review.verify_intervention(path, 3, True))
            evidence['experiment_kind'] = 'learning_pilot'; path.write_text(json.dumps(evidence))
            self.assertTrue(review.verify_intervention(path, 3, True))

    def test_plateau_requires_experiment_even_when_training_has_no_errors(self):
        with tempfile.TemporaryDirectory() as folder:
            p = Path(folder); (p / 'champion').mkdir()
            (p / 'champion/state.json').write_text(json.dumps({'training': {'games': 2800000}, 'trials': 26}))
            (p / 'status.json').write_text(json.dumps({'games': 5400000, 'state': 'running'}))
            self.assertTrue(review.intervention_required(p))
            self.assertFalse(review.verify_intervention(p / 'absent.json', 10))
            (p / 'learning-intervention.json').write_text(json.dumps({'at_games': 5350000}))
            self.assertFalse(review.intervention_required(p))

    def test_depth_plateau_uses_search_game_scale_and_promotion_anchor(self):
        with tempfile.TemporaryDirectory() as folder:
            p=Path(folder)
            (p/'status.json').write_text(json.dumps({'mode':'depth expert iteration','games':1500}))
            (p/'champion.json').write_text(json.dumps({'games':0,'trials':2}))
            self.assertTrue(review.intervention_required(p))
            (p/'champion.json').write_text(json.dumps({'games':1000,'trials':3,'trial_at_promotion':2}))
            self.assertFalse(review.intervention_required(p))

    def test_exit_zero_does_not_complete_an_unacted_plateau_review(self):
        with tempfile.TemporaryDirectory() as folder:
            config = self.configuration(folder)
            p = Path(folder)
            (p / 'state.json').write_text(json.dumps({'current_campaign': str(p)}))
            with mock.patch.object(review, 'intervention_required', return_value=True), \
                 mock.patch.object(review, 'command', return_value=[sys.executable, '-c',
                    'import sys,pathlib; sys.stdin.read(); pathlib.Path("review-01.txt").write_text("No changes.")']):
                result = review.run_review(config, 1)
            self.assertEqual(result['returncode'], 0)
            self.assertEqual(result['state'], 'intervention_incomplete')

    def test_hourly_cadence_coalesces_reboot_gap_without_duplicate(self):
        self.assertIsNone(review.next_slot(100, 99, -1))
        self.assertEqual(review.next_slot(100, 100, -1), 0)
        self.assertIsNone(review.next_slot(100, 3699, 0))
        self.assertEqual(review.next_slot(100, 3700, 0), 1)
        self.assertEqual(review.next_slot(100, 10900, 0), 3)
        self.assertEqual(review.next_slot(100, 100000, 5), 12)
        self.assertIsNone(review.next_slot(100, 100000, 12))

    def configuration(self, folder):
        directory = Path(folder)
        (directory / 'prompt.txt').write_text('Audit fixture')
        return dict(directory=str(directory), watchdog_config=str(directory / 'config.json'),
            project=str(directory), prompt=str(directory / 'prompt.txt'),
            deadline_wall=time.time() + 43200, codex='/fake/codex')

    def test_half_hour_cadence_and_new_trial_threshold(self):
        self.assertEqual(review.next_slot(100,1900,0,1800,16),1)
        self.assertIsNone(review.next_slot(100,1899,0,1800,16))
        self.assertEqual(review.next_slot(100,28900,15,1800,16),16)
        self.assertIsNone(review.next_slot(100,40000,16,1800,16))
        with tempfile.TemporaryDirectory() as folder:
            p=Path(folder);(p/'champion').mkdir()
            (p/'champion/state.json').write_text(json.dumps({'training':{'games':5000000},'trials':30}))
            (p/'status.json').write_text(json.dumps({'games':5600000}))
            (p/'learning-intervention.json').write_text(json.dumps({'at_games':5500000,'at_champion_trial':29}))
            self.assertFalse(review.intervention_required(p,50000,2,28))
            (p/'champion/state.json').write_text(json.dumps({'training':{'games':5000000},'trials':31}))
            self.assertTrue(review.intervention_required(p,50000,2,28))

    def test_real_child_receipt_and_output_success(self):
        with tempfile.TemporaryDirectory() as folder:
            config = self.configuration(folder)
            script = Path(folder) / 'child.py'
            script.write_text('import sys,pathlib\nprompt=sys.stdin.read()\n'
                              'pathlib.Path(sys.argv[1]).write_text(prompt)\n')
            with mock.patch.object(review, 'command', side_effect=lambda c, p, r: [sys.executable, str(script), str(p)]):
                result = review.run_review(config, 0)
            self.assertEqual(result['state'], 'complete')
            self.assertTrue(result['read_only'])
            self.assertIn('Audit fixture', Path(result['report']).read_text())
            self.assertEqual(json.loads((Path(folder) / 'review-00.receipt.json').read_text()), result)

    def test_failure_is_not_reported_as_success(self):
        with tempfile.TemporaryDirectory() as folder:
            config = self.configuration(folder)
            with mock.patch.object(review, 'command', return_value=[sys.executable, '-c', 'import sys; sys.stdin.read(); sys.exit(7)']):
                result = review.run_review(config, 1)
            self.assertEqual(result['state'], 'failed')
            self.assertEqual(result['returncode'], 7)

    def test_stop_and_deadline_never_spawn_mutating_review(self):
        with tempfile.TemporaryDirectory() as folder:
            config = self.configuration(folder)
            (Path(folder) / 'STOP').touch()
            with mock.patch.object(review.subprocess, 'Popen') as launch:
                self.assertEqual(review.run_review(config, 1)['state'], 'cancelled')
                launch.assert_not_called()
            self.assertIn('read-only', review.command(config, 'report', True))


if __name__ == '__main__': unittest.main()
