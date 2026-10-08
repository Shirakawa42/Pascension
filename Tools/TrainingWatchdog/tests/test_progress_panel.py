import copy
import json
from pathlib import Path
import sys
import tempfile
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from progress_panel import fixed_snapshot, recent_snapshot, render, publish, START, END
from fixed_opponent_comparison import compare_fixed_opponent
from paired_followup import merge_blocks
from test_paired_followup import block


class ProgressPanelTests(unittest.TestCase):
    def blocks(self):
        values = [block(index) for index in range(2)]
        for value in values:
            value['candidate']['training'] = {'games': 9000000}
            value['baseline']['training'] = {'games': 8000000}
        return values

    def test_inconclusive_hero_check_does_not_claim_progress(self):
        older = merge_blocks(self.blocks())
        newer = copy.deepcopy(older)
        fixed = compare_fixed_opponent(older, newer)
        panel = render('<campaign>', fixed=fixed, published_at='2026-10-05T06:00:00Z',
                       deadline_wall=1791183983.3615928)
        self.assertIn('Rez improvement is not established', panel)
        self.assertIn('&lt;campaign&gt;', panel)
        self.assertNotIn('<campaign>', panel)
        self.assertIn('Joint 95% interval', panel)
        self.assertIn('2026-10-05 09:06:23 Paris', panel)
        self.assertIn('even if charged-time allocation remains', panel)

    def test_fixed_snapshot_recomputes_raw_results_and_rejects_changed_claim(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            values = self.blocks()
            combined = merge_blocks(values)
            (root / 'plan.json').write_text(json.dumps(dict(games_per_version=8000,
                seed_base=combined['seed_base'], policy_seed=combined['policy_seed'])))
            for arm in ('older', 'newer'):
                for index, value in enumerate(values):
                    (root / f'{arm}-block-{index + 1}.json').write_text(json.dumps(value))
                (root / f'{arm}.json').write_text(json.dumps(combined))
            fixed = compare_fixed_opponent(combined, combined)
            (root / 'comparison.json').write_text(json.dumps(fixed))
            self.assertEqual(fixed_snapshot(root), fixed)
            fixed['rez_primary']['paired_difference'] = .2
            (root / 'comparison.json').write_text(json.dumps(fixed))
            with self.assertRaises(ValueError):
                fixed_snapshot(root)

    def test_recent_snapshot_rejects_changed_plan_or_partial_arena(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            values = self.blocks()
            combined = merge_blocks(values)
            plan = dict(games=8000, seed_base=combined['seed_base'], policy_seed=combined['policy_seed'])
            (root / 'plan.json').write_text(json.dumps(plan))
            for index, value in enumerate(values):
                (root / f'comparison-block-{index + 1}.json').write_text(json.dumps(value))
            (root / 'comparison.json').write_text(json.dumps(combined))
            self.assertEqual(recent_snapshot(root), combined)
            plan['seed_base'] += 20
            (root / 'plan.json').write_text(json.dumps(plan))
            with self.assertRaises(ValueError):
                recent_snapshot(root)
            plan['seed_base'] -= 20
            (root / 'plan.json').write_text(json.dumps(plan))
            values[1]['results'].pop()
            (root / 'comparison-block-2.json').write_text(json.dumps(values[1]))
            with self.assertRaises(ValueError):
                recent_snapshot(root)

    def test_publish_updates_one_panel_without_changing_runtime_script(self):
        with tempfile.TemporaryDirectory() as folder:
            page = Path(folder) / 'dashboard.html'
            source = '<section class="panel"><h2>Retained champion</h2></section><script>refresh()</script>'
            page.write_text(source)
            publish(page, START + 'first' + END)
            publish(page, START + 'second' + END)
            document = page.read_text()
            self.assertEqual(document.count(START), 1)
            self.assertNotIn('first', document)
            self.assertTrue(document.endswith(source))

    def test_ambiguous_insertion_is_rejected_without_overwriting_file(self):
        with tempfile.TemporaryDirectory() as folder:
            page = Path(folder) / 'dashboard.html'
            page.write_text(START + START + END)
            with self.assertRaises(ValueError):
                publish(page, START + END)
            self.assertEqual(page.read_text(), START + START + END)


if __name__ == '__main__':
    unittest.main()
