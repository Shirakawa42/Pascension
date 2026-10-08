"""A balance patch must publish one complete cohort with its own card rules."""
import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

from publish_strategy_statistics import publish


class PublicationTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.campaign = Path(self.temp.name)
        self.evaluation = self.campaign / 'new-evaluation'
        self.evaluation.mkdir()
        self.manifest = dict(state='completed', completed_games=50000,
                             frozen_weights_unchanged=True, policy_sha256='new-policy',
                             host_sha256='patched-host')
        self.snapshot = dict(snapshot_id='new-only', scope=dict(policy_sha256='new-policy', notes=[]),
                             totals=dict(resolved_games=50000),
                             card_catalog=dict(cards=[dict(id='doom_gate', defense=7)], heroes=[]))
        self.write()

    def write(self):
        (self.evaluation / 'manifest.json').write_text(json.dumps(self.manifest))
        (self.evaluation / 'statistics.json').write_text(json.dumps(self.snapshot))

    def test_new_cohort_never_needs_or_merges_old_evaluation(self):
        # No historic final-50000 directory exists. Its absence must not matter.
        with patch('publish_strategy_statistics.build', return_value=dict(rows=[], trace_sha256='new-trace')) as strategies, \
             patch('publish_strategy_statistics.build_balance', return_value=dict(victory={'normal_damage': 50000})):
            result = publish(self.campaign, self.evaluation, new_cohort=True)
        published = json.loads((self.campaign / 'balance-statistics-final.json').read_text())
        self.assertEqual(published['totals'], self.snapshot['totals'])
        self.assertEqual(published['card_catalog'], self.snapshot['card_catalog'])
        self.assertEqual(strategies.call_args.args[2], self.snapshot['card_catalog'])
        self.assertEqual(result['capture_kind'], 'new_cohort')
        self.assertIsNone(result['original_aggregate_replay_exact'])
        self.assertEqual(published['strategy_provenance']['policy_sha256'], 'new-policy')

    def test_incomplete_evaluation_cannot_replace_published_statistics(self):
        self.manifest['completed_games'] = 49999
        self.write()
        with self.assertRaisesRegex(ValueError, 'complete frozen'):
            publish(self.campaign, self.evaluation, new_cohort=True)
        self.assertFalse((self.campaign / 'balance-statistics-final.json').exists())

    def test_mismatched_policy_cannot_publish(self):
        self.snapshot['scope']['policy_sha256'] = 'old-policy'
        self.write()
        with self.assertRaisesRegex(ValueError, 'does not match'):
            publish(self.campaign, self.evaluation, new_cohort=True)


if __name__ == '__main__':
    unittest.main()
