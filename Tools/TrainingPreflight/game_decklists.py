"""Browse recorded terminal collections, without running or reconstructing games."""
import argparse
import hashlib
import json
from pathlib import Path
import re
import threading


def export(run, output):
    manifest = json.loads((run/'manifest.json').read_text())
    paths = ([run/'resume/strategy-games.jsonl'] if manifest.get('resumed_games') else [])
    paths += sorted((run/'raw/forced-random').glob('strategy-games.jsonl'))
    paths += sorted((run/'raw/forced-random').glob('worker-*/strategy-games.jsonl'))
    games = [json.loads(line) for path in paths for line in path.read_text(encoding='utf-8-sig').splitlines()]
    seeds = [g['seed'] for g in games]
    if len(set(seeds)) != len(seeds):
        raise ValueError('Duplicate completed game seed')
    catalog = json.loads((run/'card-catalog.json').read_text())
    cards = {c['id'] for c in catalog['cards']}
    for game in games:
        if not re.fullmatch('[0-9a-f]{16}', game['seed']) or game['winner'] not in (-1, 0, 1) or len(game['players']) != 2:
            raise ValueError('Invalid terminal game')
        if not manifest['seed'] <= int(game['seed'], 16) < manifest['seed']+manifest['games']:
            raise ValueError('Game outside this run')
        for player in game['players']:
            if not isinstance(player.get('collection'), dict):
                raise ValueError('Final collection was not recorded')
            if any(card not in cards or type(n) is not int or n <= 0 for card, n in player['collection'].items()):
                raise ValueError('Unknown card or invalid multiplicity')
        game['number'] = int(game['seed'], 16)-manifest['seed']+1
    data = dict(schema='shards-completed-game-decklists-v1', state=manifest['state'],
        policy_sha256=manifest['policy_sha256'], count=len(games), catalog=catalog,
        games=sorted(games, key=lambda g:g['seed']),
        sources=[dict(path=str(p), sha256=hashlib.sha256(p.read_bytes()).hexdigest()) for p in paths])
    temporary = output.with_suffix('.tmp')
    temporary.write_text(json.dumps(data, ensure_ascii=False, separators=(',', ':'), allow_nan=False)+'\n')
    temporary.replace(output)
    return data


class GameDecklists:
    def __init__(self, path):
        self.path, self.stamp, self.data = path, None, None
        self.lock = threading.Lock()

    def query(self, parameters):
        with self.lock:
            if self.path.is_symlink():raise ValueError('Invalid decklist artifact')
            stat = self.path.stat()
            if stat.st_size > 64*1024*1024:raise ValueError('Decklist artifact exceeds size limit')
            stamp = (stat.st_mtime_ns, stat.st_size)
            if stamp != self.stamp:
                data = json.loads(self.path.read_text())
                if data.get('schema') != 'shards-completed-game-decklists-v1':raise ValueError('Unsupported decklist schema')
                self.data, self.stamp = data, stamp
            data = self.data
        def value(name, default=''):return parameters.get(name, [default])[0]
        seed = value('seed')
        if seed:
            if not re.fullmatch('[0-9a-f]{16}', seed):raise ValueError('Invalid game seed')
            game = next((g for g in data['games'] if g['seed'] == seed), None)
            if game is None:raise ValueError('Unknown completed game')
            return dict(game=game, catalog=data['catalog'])
        offset, limit = int(value('offset', '0')), int(value('limit', '25'))
        minimum, maximum = int(value('min_round', '1')), int(value('max_round', '400'))
        if not 0 <= offset <= data['count'] or not 1 <= limit <= 100 or not 1 <= minimum <= maximum <= 400:
            raise ValueError('Invalid page or round range')
        hero, card, victory, search = value('hero'), value('card'), value('victory'), value('search').strip().lower()
        games = [g for g in data['games'] if minimum <= g['round'] <= maximum
            and (not hero or any(p['hero'] == hero for p in g['players']))
            and (not card or any(card in p['collection'] for p in g['players']))
            and (not victory or g['victory'] == victory)
            and (not search or search in g['seed'] or search == str(g['number']))]
        sort = value('sort', 'round_asc')
        if sort not in ('round_asc', 'round_desc', 'number'):raise ValueError('Invalid sort')
        games.sort(key=lambda g:(g['round'] if sort=='round_asc' else -g['round'] if sort=='round_desc' else g['number'], g['number']))
        def summary(g):
            return {**{k:g[k] for k in ('seed', 'number', 'round', 'winner', 'victory')},
                'players':[dict(hero=p['hero'], health=p['health'], mastery=p['mastery'],
                    cards=sum(p['collection'].values())) for p in g['players']]}
        return dict(total=data['count'], matched=len(games), state=data['state'], offset=offset,
            games=[summary(g) for g in games[offset:offset+limit]], catalog=data['catalog'])


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--run', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    data = export(args.run, args.output)
    print(json.dumps(dict(completed_games=data['count'], output=str(args.output))))
