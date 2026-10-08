"""Counterexample tests: the read-only audit must detect corrupted statistics."""
import unittest

from audit_training_counters import validate_generation


class CounterAuditTests(unittest.TestCase):
    def fixture(self):
        return dict(attempted_games=4, completed_games=3, censored_games=1,
                    seat0_wins=1, draws=1, action_kind_counts=[2, 3], learning_rows=5,
                    attempted_learning_rows=7, censored_learning_rows=2,
                    engine_submissions=7, wrapper_decisions=9, lane_occupancy=.5,
                    behavior_version=8, generation=9, archive_attempted_games=2,
                    archive_completed_games=1, archive_censored_games=1, archive_score=.5,
                    finite={'parameters_finite': True, 'optimizer_state_finite': True},
                    metrics={'clip_fraction': .1, 'entropy': .2}, seconds=1.,
                    collection_seconds=.7, learning_seconds=.2, verification_seconds=.1,
                    actor_seconds=.2, host_seconds=.3, storage_seconds=.1,
                    accepted_optimizer_steps=3, rejected_minibatches=0)

    def test_consistent_censored_and_terminal_outcomes(self):
        self.assertEqual(validate_generation(self.fixture()), [])

    def test_independent_counter_corruptions_are_detected(self):
        cases = [('completed_games', 4, 'terminal/censor partition'),
                 ('draws', 3, 'outcome bounds'),
                 ('action_kind_counts', [2, 4], 'action histogram/retained rows'),
                 ('attempted_learning_rows', 8, 'retained/censored rows'),
                 ('generation', 10, 'behavior version boundary'),
                 ('archive_attempted_games', 3, 'archive partition'),
                 ('archive_score', 1.1, 'archive score range'),
                 ('lane_occupancy', 0, 'lane occupancy range'),
                 ('engine_submissions', 10, 'engine/wrapper counts')]
        for key, value, error in cases:
            with self.subTest(key=key):
                row = self.fixture()
                row[key] = value
                self.assertIn(error, validate_generation(row))

    def test_nonfinite_metric_detected(self):
        row = self.fixture()
        row['metrics']['loss'] = float('nan')
        self.assertIn('finite metric: loss', validate_generation(row))

    def test_deadline_before_update_allows_empty_metrics_only_without_steps(self):
        row = self.fixture()
        row['metrics'] = {}
        self.assertIn('empty metrics require no attempted minibatches', validate_generation(row))
        row['accepted_optimizer_steps'] = 0
        self.assertEqual(validate_generation(row), [])


if __name__ == '__main__':
    unittest.main()
