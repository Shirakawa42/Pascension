"""Check complete hero tactical coverage; exit 2 for weak policy decisions.

The scenarios are constructed challenges, not a representative win-rate sample.
This command validates evaluations only. It never changes or trains a policy.
"""
import argparse
import json
from collections import Counter
from pathlib import Path


def evaluate(data, threshold=0.95):
    cases = data['cases']
    coverage = Counter(c['hero'] for c in cases)
    if coverage != {'decima': 6, 'tetra': 6, 'volos': 7, 'kosynwu': 6, 'rez': 7}:
        raise ValueError(f'Incomplete hero coverage: {dict(coverage)}')
    if len({(c['hero'], c['id']) for c in cases}) != 32:
        raise ValueError('Duplicate or missing case')
    rows = []
    for case in cases:
        n = case['variants']
        if n < 2 or n % 2 or case['verified_goal_lines'] != n:
            raise ValueError('Unverified or unbalanced fixture variants')
        if Counter(r['seat'] for r in case['roots']) != {0: n//2, 1: n//2}:
            raise ValueError('Seats not balanced')
        if any(not r['private_invariant'] or r['permutation_error'] > 0.0001 for r in case['roots']):
            raise ValueError('Information or action identity regression')
        if case['sampled_games'] != n*8:
            raise ValueError('Acceptance requires eight sampled rollouts per position')
        greedy = case['greedy_wins']/n
        sampled = case['sampled_wins']/case['sampled_games']
        rows.append(dict(hero=case['hero'], id=case['id'], greedy_success=greedy,
                         sampled_success=sampled, passed=min(greedy, sampled) >= threshold))
    return dict(passed=all(r['passed'] for r in rows), threshold=threshold,
                cases=len(rows), passing_cases=sum(r['passed'] for r in rows),
                constructed_positions=sum(c['variants'] for c in cases),
                policy_rollouts=sum(c['variants']+c['sampled_games'] for c in cases),
                rows=rows)


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('evaluation', type=Path)
    parser.add_argument('--output', type=Path)
    args = parser.parse_args()
    result = evaluate(json.loads(args.evaluation.read_text()))
    if args.output:
        args.output.write_text(json.dumps(result, indent=2)+'\n')
    print(json.dumps({k: v for k, v in result.items() if k != 'rows'}))
    for row in result['rows']:
        if not row['passed']:
            print(f"FAIL {row['hero']}/{row['id']}: greedy={row['greedy_success']:.1%}, sampled={row['sampled_success']:.1%}")
    raise SystemExit(0 if result['passed'] else 2)
