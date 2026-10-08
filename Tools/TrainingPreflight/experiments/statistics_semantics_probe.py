"""Read-only live statistics reconciliation; stdlib only, no games or learner."""
from __future__ import annotations

import argparse
import json
from pathlib import Path
import sys
import time
import urllib.request

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import balance_host_statistics as pool
import balance_statistics as frozen


def audit(campaign, url):
    with urllib.request.urlopen(url, timeout=5) as response:
        snapshot = json.load(response)
    catalog = {row['id']: row for row in snapshot['catalog']['cards']}
    replaced = {row['replaces_id'] for row in catalog.values() if row.get('replaces_id')}
    result = {'audited_wall': time.time(), 'sources': {}, 'training_modified': False,
              'gpu_used': False}
    for source, stats in snapshot['balance_statistics_sources'].items():
        if not stats:
            continue
        total = stats['totals']
        real = total['resolved_games']
        assert total['seat0_wins'] + total['seat1_wins'] + total['draws'] == real
        expected = real if source == 'frozen' else 2 * real
        assert sum(r['games'] for r in stats['rankings']['heroes']) == expected
        assert sum(r['games'] for r in stats['hero_seats']) == expected
        for rows in stats['rankings'].values():
            for row in rows:
                assert row['games'] == row['wins'] + row['draws'] + row['losses']
                if row['censored_games']:
                    assert row['score'] is None
                elif row['games']:
                    assert row['score'] == (row['wins'] + .5 * row['draws']) / row['games']
        entry = {'real_completed': real, 'hero_outcomes': expected,
                 'draws': total['draws'], 'censored': total['censored_games']}
        if source != 'frozen':
            windows = []
            for record in stats['source_files']:
                current = json.loads(Path(record['source_file']).read_text())
                anchor = json.loads(Path(record['anchor_file']).read_text()) if record['anchor_file'] else None
                window = pool.delta(current, anchor)
                window['run_id'] = record['run_id']
                windows.append(window)
            # A live source could publish during this probe; identify this instead
            # of attributing a newer raw counter to an older API window.
            seen = [(w['session_id'], w['latest_completed_games'], w['anchor_completed_games']) for w in windows]
            expected_window = [(w['session_id'], w['latest_completed_games'], w['anchor_completed_games'])
                               for w in stats['window']['sessions']]
            assert seen == expected_window, 'Publication changed during probe; rerun against a stable snapshot'
            metadata_path = pool.CATALOG.with_name('balance-card-catalog-v6.json') if source != 'training_pool' else pool.CATALOG
            rebuilt = pool.combine(windows, json.loads(metadata_path.read_text()), run_id=stats['scope']['run_id'])
            for field in ('totals', 'rankings', 'hero_choice_rows', 'hero_seats', 'hero_matchups', 'final_state_sums'):
                assert rebuilt[field] == stats[field], 'Raw/API mismatch in ' + field
            entry['raw_reconciliation'] = True
            entry['contributing_runs'] = [{'run_id': w['run_id'], 'completed': w['totals']['completed_games']} for w in windows]
            eligible = [r for r in stats['rankings']['relics'] if r['id'].split(':')[0] not in replaced]
            entry['active_relics_with_outcomes'] = sum(r['games'] > 0 for r in eligible)
            entry['active_relic_count'] = len(eligible)
            sparse = {(r['hero_id'], r['id'].split(':')[0], r['choice_kind']) for r in stats['hero_choice_rows']}
            missing = []
            for hero in stats['rankings']['heroes']:
                for row in stats['rankings']['cards']:
                    definition = row['id'].split(':')[0]
                    if (definition not in replaced and row['type'] not in ('Starter', 'Monster') and
                            row['games'] > 0 and (hero['id'], definition, row['choice_kind']) not in sparse):
                        missing.append({'hero': hero['id'], 'card_mode': row['id'], 'global_outcomes': row['games']})
            entry['sparse_perhero_absent_rows'] = len(missing)
            entry['sparse_perhero_examples'] = missing[:5]
        else:
            reports = [(name, json.loads((campaign / 'evaluations' / name).read_text())) for name in stats['source_reports']]
            identity = json.loads((campaign / stats['scope']['run_id'] / 'identity.json').read_text())
            metadata_path = pool.CATALOG.with_name('balance-card-catalog-v6.json') if identity['schema'].endswith(('v6', 'v7')) else pool.CATALOG
            rebuilt = frozen.build_statistics(reports, json.loads(metadata_path.read_text()), expected_identity=identity)
            for field in ('totals', 'rankings', 'hero_seats', 'hero_matchups'):
                assert rebuilt[field] == stats[field], 'Frozen raw/API mismatch in ' + field
            entry['raw_reconciliation'] = True
            entry['accepted_reports'] = len(stats['source_reports'])
            entry['excluded_reports'] = len(stats['excluded_reports'])
        result['sources'][source] = entry
    result['passed'] = True
    return result


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--campaign', type=Path, required=True)
    parser.add_argument('--url', default='http://localhost:8768/api/statistics')
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    evidence = audit(args.campaign, args.url)
    args.output.write_text(json.dumps(evidence, indent=2) + '\n')
    print(json.dumps(evidence, indent=2))
