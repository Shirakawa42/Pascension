"""Read-only reconciliation of recorded training generations and budget sessions.

No Torch, games, GPU work, locks, or training mutations. An incomplete final JSONL
record is excluded and the exact inspected prefix is hashed for reproducibility.
Historical runs are reported individually; checkpoint branches are not summed as
though they were one retained model lineage.
"""
import argparse
import hashlib
import json
import math
from pathlib import Path
import time


def validate_generation(row):
    errors = []

    def check(condition, name):
        if not condition:
            errors.append(name)

    n = row['attempted_games']
    complete, censored = row['completed_games'], row['censored_games']
    check(n == complete + censored, 'terminal/censor partition')
    check(0 <= row['seat0_wins'] + row['draws'] <= complete, 'outcome bounds')
    check(sum(row['action_kind_counts']) == row['learning_rows'], 'action histogram/retained rows')
    check(all(x >= 0 for x in row['action_kind_counts']), 'nonnegative action counts')
    check(row['attempted_learning_rows'] == row['learning_rows'] + row['censored_learning_rows'], 'retained/censored rows')
    check(row['engine_submissions'] <= row['wrapper_decisions'], 'engine/wrapper counts')
    check(row['learning_rows'] <= row['wrapper_decisions'], 'learner/wrapper counts')
    check(0 < row['lane_occupancy'] <= 1, 'lane occupancy range')
    check(row['behavior_version'] + 1 == row['generation'], 'behavior version boundary')
    check(row['archive_attempted_games'] == row['archive_completed_games'] + row['archive_censored_games'], 'archive partition')
    check(0 <= row['archive_attempted_games'] <= n, 'archive subset')
    check(row['archive_score'] is None or 0 <= row['archive_score'] <= 1, 'archive score range')
    check(row['finite']['parameters_finite'] and row['finite']['optimizer_state_finite'], 'finite state')
    for key, value in row['metrics'].items():
        check(math.isfinite(value), 'finite metric: ' + key)
    if row['metrics']:
        check(0 <= row['metrics']['clip_fraction'] <= 1, 'clip fraction range')
        check(0 <= row['metrics']['entropy'] <= math.log(64) + 1e-5, 'entropy range')
    else:
        check(row['accepted_optimizer_steps'] == row['rejected_minibatches'] == 0, 'empty metrics require no attempted minibatches')
    for key in ('seconds', 'collection_seconds', 'learning_seconds', 'verification_seconds', 'actor_seconds', 'host_seconds', 'storage_seconds'):
        check(math.isfinite(row[key]) and row[key] >= 0, 'nonnegative timing: ' + key)
    check(row['actor_seconds'] + row['host_seconds'] + row['storage_seconds'] <= row['collection_seconds'] + .01, 'collection timing components')
    return errors


def audit(campaign):
    runs = {}
    for path in sorted(campaign.glob('*/metrics.jsonl')):
        raw = path.read_bytes()
        prefix = raw[:raw.rfind(b'\n') + 1]
        events = [json.loads(x) for x in prefix.splitlines() if x.strip()]
        rows = [r for r in events if r.get('event') == 'generation']
        # Earliest pre-accounting runs use a different metrics contract.
        current = [r for r in rows if 'attempted_learning_rows' in r]
        violations = []
        for row in current:
            for error in validate_generation(row):
                violations.append({'generation': row['generation'], 'error': error})
        for a, b in zip(current, current[1:]):
            for key, increment in [('generation', 1), ('games', b['completed_games']), ('decisions_total', b['learning_rows'])]:
                if b[key] != a[key] + increment:
                    violations.append({'generation': b['generation'], 'error': 'cumulative ' + key})
            if b['charged_seconds'] < a['charged_seconds']:
                violations.append({'generation': b['generation'], 'error': 'charged time reversal'})
        latest = [r for r in current[-100:] if r['metrics']]
        runs[path.parent.name] = {
            'sha256': hashlib.sha256(prefix).hexdigest(), 'prefix_bytes': len(prefix),
            'partial_tail_bytes': len(raw) - len(prefix), 'checked_generations': len(current),
            'legacy_generations_not_checked': len(rows) - len(current), 'violations': violations,
            'generation_range': [current[0]['generation'], current[-1]['generation']] if current else None,
            'completed_games': sum(r['completed_games'] for r in current),
            'censored_games': sum(r['censored_games'] for r in current),
            'accepted_steps': sum(r['accepted_optimizer_steps'] for r in current),
            'rejected_minibatches': sum(r['rejected_minibatches'] for r in current),
            'last100_metric_ranges': {k: [min(r['metrics'][k] for r in latest), max(r['metrics'][k] for r in latest)] for k in latest[0]['metrics']} if latest else {},
            'max_logp_error': max((r['behavior_parity']['max_abs_behavior_logp_error'] for r in current), default=None),
            'max_value_error': max((r['behavior_parity']['max_abs_behavior_value_error'] for r in current), default=None),
            'discarded_collections': [r for r in events if r.get('event') == 'discarded_collection'],
        }
    budget = json.loads((campaign / 'budget.json').read_text())
    accounting = budget['collection_accounting']
    checks = {
        'budget_within_limit': 0 <= budget['charged_seconds'] <= budget['limit_seconds'],
        'ledger_outcome_partition': accounting['attempted_games'] == accounting['completed_games'] + accounting['censored_games'],
        'discarded_episode_sum': budget['discarded_episodes'] == sum(x['episodes'] for x in budget['discard_events']),
        'discarded_decision_sum': budget['discarded_decisions'] == sum(x['decisions'] for x in budget['discard_events']),
        'closed_sessions_no_overrun': all(s['overrun_seconds'] == 0 for s in budget['sessions']),
    }
    # Active progress is charged separately from closed sessions. The tiny
    # inactive accounting tail is recorded, not rounded into an invented exact equality.
    closed = sum(s['charged_seconds'] for s in budget['sessions'])
    return {'captured_wall': time.time(), 'passed': all(checks.values()) and all(not r['violations'] for r in runs.values()),
            'checks': checks, 'runs': runs, 'ledger': {'revision': budget['revision'], 'charged_seconds': budget['charged_seconds'],
            'remaining_seconds': budget['limit_seconds'] - budget['charged_seconds'], 'active': budget['active'],
            'closed_session_seconds': closed, 'charged_minus_closed_seconds': budget['charged_seconds'] - closed,
            'collection_accounting': accounting}, 'scope': 'Recorded generations only; no claim that every legal strategy is learned or every engine rule is correct.'}


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--campaign', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    result = audit(args.campaign)
    args.output.write_text(json.dumps(result, indent=2, allow_nan=False) + '\n')
    print(json.dumps({'passed': result['passed'], 'checks': result['checks'], 'runs': {k: {'checked': v['checked_generations'], 'violations': v['violations'][:10]} for k, v in result['runs'].items()}}))
    raise SystemExit(0 if result['passed'] else 1)
