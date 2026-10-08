import math
import unittest
from types import SimpleNamespace
from pathlib import Path
import progress_watch as p


class ProgressContracts(unittest.TestCase):
    def report(self, score=.6):
        return dict(complete=True, all_terminal=True, games=4096, censored_games=0,
                    paired_seed_count=2048, score_a=score, seat_swapped=True,
                    frozen_weights_unchanged=True, cuda_initialized=False,
                    policy_a={'version': 9000}, policy_b={'version': 8800})

    def test_classification_requires_evidence_not_point_estimate(self):
        self.assertEqual(p.classify(self.report(.51), 1)['verdict'], 'inconclusive')
        self.assertEqual(p.classify(self.report(.60), 1)['verdict'], 'improved')
        self.assertEqual(p.classify(self.report(.40), 1)['verdict'], 'regressed')

    def test_censored_or_incomplete_never_promotes(self):
        for changes in ({'complete': False}, {'censored_games': 1},
                        {'frozen_weights_unchanged': False}, {'cuda_initialized': True},
                        {'score_a': float('nan')}, {'paired_seed_count': 4000}):
            r=self.report();r.update(changes)
            self.assertEqual(p.classify(r, 1)['verdict'], 'invalid')

    def test_repeated_comparisons_share_five_percent_error_budget(self):
        self.assertLessEqual(sum(p.test_alpha(i) for i in range(1, 10001)), .05)
        self.assertGreater(p.classify(self.report(), 100)['margin'],
                           p.classify(self.report(), 1)['margin'])

    def test_roles_explicitly_compare_learners(self):
        a=SimpleNamespace(variant='v9', games=4096, batch=64, timeout=900)
        cmd=p.command(a, Path('/a/latest.soicp'), Path('/b/latest.soicp'),
                      Path('/out.json'), 1234, 4567)
        self.assertEqual(cmd[cmd.index('--a-role')+1], 'learner')
        self.assertEqual(cmd[cmd.index('--b-role')+1], 'learner')
        self.assertEqual(cmd[cmd.index('--games')+1], '4096')


if __name__ == '__main__': unittest.main()
