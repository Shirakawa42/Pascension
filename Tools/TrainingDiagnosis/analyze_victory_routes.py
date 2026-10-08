"""Describe observed winning routes without treating route choice as strength."""
import argparse
from collections import Counter
import hashlib
import json
from pathlib import Path
import time


def describe(games):
    if not games or any(not game['completed'] for game in games):
        raise ValueError('Only completed games can describe observed outcomes')
    keys = {(game['seed'], game['learner_seat']) for game in games}
    if len(keys) != len(games):
        raise ValueError('Repeated seed/seat records')
    allowed = {'mastery', 'normal_damage', 'comet', 'other_health_loss', 'concession', 'unknown'}
    roles = {}
    for role in ('learner', 'incumbent'):
        rows = []
        for game in games:
            seat = game['learner_seat'] if role == 'learner' else 1-game['learner_seat']
            if seat not in (0, 1) or game['winner'] not in (-1, 0, 1):
                raise ValueError('Invalid seat or winner')
            cause = game.get('victoryCause', 'unknown')
            if game['winner'] != -1 and cause not in allowed:
                raise ValueError('Unknown victory cause: ' + cause)
            rows.append(dict(hero=game['heroes'][seat], seat=seat,
                won=game['winner'] == seat, lost=game['winner'] == 1-seat,
                cause=cause, mastery=game['mastery'][seat], rounds=game['rounds']))

        def summarize(selected):
            wins = [row for row in selected if row['won']]
            losses = [row for row in selected if row['lost']]
            causes = Counter(row['cause'] for row in wins)
            return dict(games=len(selected), wins=len(wins), losses=len(losses),
                draws=len(selected)-len(wins)-len(losses),
                win_rate=len(wins)/len(selected), victory_causes=dict(causes),
                mastery_win_share=causes['mastery']/len(wins) if wins else None,
                mastery_wins_per_game=causes['mastery']/len(selected),
                losses_at_mastery25_or_more=sum(row['mastery'] >= 25 for row in losses),
                mean_rounds=sum(row['rounds'] for row in selected)/len(selected))

        roles[role] = dict(overall=summarize(rows),
            by_hero={hero: summarize([row for row in rows if row['hero'] == hero])
                for hero in sorted({row['hero'] for row in rows})},
            by_seat={str(seat): summarize([row for row in rows if row['seat'] == seat])
                for seat in (0, 1) if any(row['seat'] == seat for row in rows)})
    return roles


def run(source, output):
    raw = source.read_bytes()
    data = json.loads(raw)
    snapshot = output.with_suffix('.source.json')
    report = dict(source=str(source.resolve()), source_sha256=hashlib.sha256(raw).hexdigest(),
        source_snapshot=str(snapshot.resolve()),
        analysis_script_sha256=hashlib.sha256(Path(__file__).read_bytes()).hexdigest(),
        observed_wall=time.time(), completed=data['completed'], planned=data['planned'],
        complete_evaluation=data['state'] == 'complete' and data['completed'] == data['planned'],
        roles=describe(data['completed_games']),
        interpretation='Describes observed routes against the same opponent, not a causal learning experiment. '
            'A higher Infinity fraction is not itself better play. Hero or seat differences and interim results '
            'are descriptive; they do not change the frozen strength gate or justify a promotion.')
    if len(data['completed_games']) != data['completed']:
        raise ValueError('Completed count differs from actual records')
    if report['roles']['learner']['overall']['wins'] != data['summary']['wins']:
        raise ValueError('Recount differs from published win count')
    for role, stats in report['roles'].items():
        if 'victory_causes' in data:
            published = {key: value for key, value in data['victory_causes'][role].items() if value}
            if stats['overall']['victory_causes'] != published:
                raise ValueError('Victory route recount differs from published events')
    snapshot.write_bytes(raw)
    output.write_text(json.dumps(report, indent=2)+'\n')
    print(json.dumps({role: stats['overall'] for role, stats in report['roles'].items()}, indent=2))


if __name__ == '__main__':
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument('--source', type=Path, required=True)
    p.add_argument('--output', type=Path, required=True)
    args = p.parse_args()
    run(args.source, args.output)
