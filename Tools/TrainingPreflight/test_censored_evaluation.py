import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch
import numpy as np

from learning_eval import evaluate_match, score_summary
from test_learning_collection import ScriptedHost, ScriptedActor, Decision


class CensoredEvaluationTests(unittest.TestCase):
    def test_unknown_scores_are_bounds_and_never_draws(self):
        result = score_summary([1., .5, 0.], 4, 1)
        self.assertIsNone(result['score_a'])
        self.assertEqual(result['score_identification_interval'], [.375, .625])
        self.assertEqual((result['wins_a'], result['draws'], result['losses_a']), (1, 1, 1))
        self.assertFalse(result['all_terminal'])

    def test_no_resolved_games_stays_entirely_unknown(self):
        result = score_summary([], 2, 2)
        self.assertIsNone(result['score_a_resolved_only'])
        self.assertEqual(result['score_identification_interval'], [0., 1.])

    def test_cap_keeps_planned_pairs_and_records_replay(self):
        hosts = []
        def host_factory(batch, workers, seed, **kwargs):
            host = ScriptedHost([[Decision(0, 0)], [Decision(0, 0), Decision(1, 1)]],
                                [(1, -1), (-1, 1)], initial_counters=False, truncated=(0,))
            hosts.append(host)
            return host
        def actor_factory(policy, batch, **kwargs):
            return ScriptedActor(batch)
        with tempfile.TemporaryDirectory() as folder, \
             patch('learning_eval.LearningHost', side_effect=host_factory), \
             patch('learning_model.LearningActor', side_effect=actor_factory):
            result = evaluate_match(None, None, games=4, batch=2, seed=100,
                                    censor_truncated=True, debug_dir=folder)
            files = list(Path(folder).glob('*.json'))
            self.assertEqual(len(files), 2)
        self.assertEqual((result['games'], result['resolved_games'], result['censored_games']), (4, 2, 2))
        self.assertEqual(result['paired_seed_count'], 2)
        self.assertEqual(result['draws'], 0)
        self.assertEqual(result['score_identification_interval'], [.25, .75])
        self.assertTrue(all(host.closed for host in hosts))
        self.assertTrue(all(np.array_equal(host.calls[-1][1], [False, True]) for host in hosts))


if __name__ == '__main__':
    unittest.main()
