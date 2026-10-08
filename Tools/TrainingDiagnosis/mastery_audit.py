"""Read-only mastery audit of exact checkpointed terminal-game records.

Mastery at the terminal state is exact. Negative-health thresholds are only
damage proxies: this ring does not retain the effect responsible for victory.
"""
import argparse
import json
from pathlib import Path
import sys

sys.path[:0] = [str(Path(__file__).resolve().parents[1] / 'ZeroDepthTraining'),
               str(Path(__file__).resolve().parents[1] / 'TrainingPreflight')]


def audit(checkpoint, identity):
    import torch
    from campaign_state import load_checkpoint
    from game_stats import RollingGameStats, _RECORD
    torch.set_num_threads(1)
    loaded = load_checkpoint(checkpoint, expected_identity=identity)
    state = loaded['state']
    ring = RollingGameStats.from_state(state['catalog'], state['rolling_game_stats'])
    shard = ring.cards.index('infinity_shard')
    rows = []
    for index in range(ring.count):
        offset = index * ring.stride
        (winner, archive, learner, hero0, hero1, flags, rounds,
         m0, m1, h0, h1, deck0, deck1) = _RECORD.unpack_from(ring.records, offset)
        if not flags & 1:
            continue
        cards = [int.from_bytes(ring.records[offset + _RECORD.size + seat * ring.presence_bytes:
                    offset + _RECORD.size + (seat + 1) * ring.presence_bytes], 'little')
                 for seat in (0, 1)]
        rows.append(dict(winner=winner, archive=archive, learner=learner, heroes=[hero0, hero1],
                         mastery=[m0, m1], health=[h0, h1], rounds=rounds,
                         shard=[bool(bits & (1 << shard)) for bits in cards]))
    def summary(selected):
        wins = [r for r in selected if r['winner'] >= 0]
        return dict(games=len(selected), decisive=len(wins),
            winner_at_30=sum(r['mastery'][r['winner']] >= 30 for r in wins),
            winner_at_30_fraction=sum(r['mastery'][r['winner']] >= 30 for r in wins)/len(wins) if wins else None,
            either_at_30=sum(max(r['mastery']) >= 30 for r in selected),
            both_at_30=sum(min(r['mastery']) >= 30 for r in selected),
            loser_health_below_minus_9900=sum(r['health'][1-r['winner']] < -9900 for r in wins),
            winner_30_without_shard=sum(r['mastery'][r['winner']] >= 30 and not r['shard'][r['winner']] for r in wins),
            winner_mastery_histogram={str(i):sum(r['mastery'][r['winner']] == i for r in wins) for i in range(31)},
            mean_rounds=sum(r['rounds'] for r in selected)/len(selected) if selected else None)
    heroes = {}
    for index, hero in enumerate(ring.heroes):
        observed = [(r, seat) for r in rows for seat in (0, 1) if r['heroes'][seat] == index]
        winners = [(r, seat) for r, seat in observed if r['winner'] == seat]
        heroes[hero] = dict(observations=len(observed), wins=len(winners),
            wins_at_30=sum(r['mastery'][seat] >= 30 for r, seat in winners),
            wins_at_30_fraction=sum(r['mastery'][seat] >= 30 for r, seat in winners)/len(winners) if winners else None,
            observations_at_30=sum(r['mastery'][seat] >= 30 for r, seat in observed))
    return dict(checkpoint=str(checkpoint), checkpoint_payload_sha256=loaded['file_sha256'],
        trained_games=state['games'], recorded_games=ring.count,
        all=summary(rows), selfplay=summary([r for r in rows if not r['archive']]),
        archive=summary([r for r in rows if r['archive']]), heroes=heroes,
        limitation='Terminal mastery is exact; health thresholds do not prove the victory cause. Historical rolling windows overlap.')


if __name__ == '__main__':
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument('--checkpoint', type=Path, required=True)
    p.add_argument('--identity', type=Path, required=True)
    p.add_argument('--output', type=Path, required=True)
    a = p.parse_args()
    result = audit(a.checkpoint, json.loads(a.identity.read_text()))
    with a.output.open('x') as out:
        json.dump(result, out, indent=2)
    print(json.dumps({k:v for k,v in result.items() if k in ('trained_games','all','heroes')}))
