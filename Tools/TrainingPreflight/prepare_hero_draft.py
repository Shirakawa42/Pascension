"""Stage a draft table from a completed balanced cohort; never mutates Assets."""
import argparse
import hashlib
import json
import re
from pathlib import Path

HEROES = ['decima', 'tetra', 'volos', 'kosynwu', 'rez']


def main():
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument('--evaluation', type=Path, required=True)
    p.add_argument('--output', type=Path, required=True)
    args = p.parse_args()
    if 'Assets' in args.output.resolve().parts:
        p.error('Use a staging directory outside Assets')
    manifest = json.loads((args.evaluation / 'manifest.json').read_text())
    snapshot = json.loads((args.evaluation / 'statistics.json').read_text())
    if not manifest.get('statistics_verified') or snapshot['evaluation_progress']['state'] != 'completed':
        raise ValueError('Draft values require a completed, verified cohort')
    files = list((args.evaluation / 'raw/forced-random').glob('session-*.json'))
    if len(files) != 1:
        raise ValueError('Expected exactly one isolated statistics session')
    raw = json.loads(files[0].read_text())
    rows = raw['matchup_rows']
    if len(rows) != 20 or len({r['games'] for r in rows}) != 1:
        raise ValueError('Require all 20 ordered distinct-hero matchups with equal counts')
    count = rows[0]['games']
    if count < 100 or count * 20 != snapshot['totals']['resolved_games']:
        raise ValueError('Insufficient or mismatched matchup data')
    table = [[0] * 5 for _ in HEROES]
    seen = set()
    for row in rows:
        i, j = [HEROES.index(row[k]) for k in ('seat0_hero_id', 'seat1_hero_id')]
        if i == j or (i, j) in seen or row['censored_games']:
            raise ValueError('Invalid matchup')
        if row['seat0_wins'] + row['seat1_wins'] + row['draws'] != count:
            raise ValueError('Unreconciled outcomes')
        seen.add((i, j))
        table[i][j] = 2 * row['seat0_wins'] + row['draws']
    source = Path('Assets/Scripts/Shards/AI/HeroDraftPolicy.cs').read_text()
    source, changed = re.subn(r'        private static readonly int\[,] Seat0HalfPoints = \{.*?\n        \};',
        '        private static readonly int[,] Seat0HalfPoints = {\n' +
        ',\n'.join('            { ' + ', '.join(map(str, row)) + ' }' for row in table) + '\n        };',
        source, flags=re.S)
    if changed != 1:
        raise ValueError('Draft table declaration changed; review source before staging')
    source, changed = re.subn(r'seat == 0 \? Seat0HalfPoints\[own, opponent\] : \d+ - Seat0HalfPoints\[opponent, own\];',
        f'seat == 0 ? Seat0HalfPoints[own, opponent] : {count * 2} - Seat0HalfPoints[opponent, own];', source)
    if changed != 1:
        raise ValueError('Draft score scale changed; review source before staging')
    source = re.sub(r'    /// Seat-aware draft values.*?    /// Draft by stable',
        f'    /// Seat-aware draft values from {count * 20:,} frozen semantic-hybrid games, {count:,} per\n'
        '    /// ordered distinct-hero pair. Values describe this gameplay policy and balance\n'
        '    /// version, not optimal play. Refresh this table after either changes.\n'
        '    /// Draft by stable', source, flags=re.S)
    source = re.sub(r'        // Source: .*',
        f'        // Source: {args.evaluation.name}, snapshot {snapshot["snapshot_id"]}.', source)
    def score(hero, opponent, seat):
        return table[hero][opponent] if seat == 0 else count * 2 - table[opponent][hero]
    responses = [[HEROES[max((h for h in range(5) if h != opponent), key=lambda h: (score(h, opponent, seat), -h))]
                  for opponent in range(5)] for seat in range(2)]
    first = [HEROES[max(range(5), key=lambda h: (min(score(h, other, seat) for other in range(5) if other != h), -h))]
             for seat in range(2)]
    args.output.mkdir(parents=True, exist_ok=True)
    target = args.output / 'HeroDraftPolicy.cs'
    target.write_text(source)
    evidence = dict(state='prepared_not_installed', evaluation=str(args.evaluation.resolve()),
        snapshot_id=snapshot['snapshot_id'], policy_sha256=manifest['policy_sha256'], games=count * 20,
        games_per_ordered_pair=count, heroes=HEROES, seat0_half_points=table,
        first_pick_by_seat=first, best_response_by_seat=responses,
        source_sha256=hashlib.sha256(source.encode()).hexdigest(),
        limitation='Empirical matchup estimates; 125 games per pair have substantial uncertainty.' if count == 125 else 'Empirical matchup estimates, not optimal-play values.')
    (args.output / 'hero-draft-provenance.json').write_text(json.dumps(evidence, indent=2) + '\n')
    print(json.dumps(evidence, indent=2))


if __name__ == '__main__':
    main()
