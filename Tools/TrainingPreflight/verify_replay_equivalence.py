"""Compare frozen natural-game transcripts for a proposed search optimization."""
import argparse
import hashlib
import json
from pathlib import Path


def sha(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def gameplay(row):
    ignored = {'value', 'legal', 'search', 'extendedSearch', 'counterfactual', 'fallback', 'findings'}
    result = {k: v for k, v in row.items() if k not in ignored}
    if 'legal' in row:
        result['legal'] = [{k: v for k, v in action.items() if k != 'probability'} for action in row['legal']]
    return result


def compare(reference, candidate):
    manifests = [json.loads((directory / 'manifest.json').read_text()) for directory in (reference, candidate)]
    if any(not m.get('completed') or m['args']['mode'] != 'replay' for m in manifests):
        raise ValueError('Both replay cohorts must be completed')
    if manifests[0]['policy_sha256'] != manifests[1]['policy_sha256']:
        raise ValueError('Models differ')
    schedules = [json.loads((directory / 'runtime/replay.json').read_text()) for directory in (reference, candidate)]
    if schedules[0] != schedules[1]:
        raise ValueError('Replay seeds or hero schedules differ')
    configs = [json.loads((directory / 'games.json').read_text())['config'] for directory in (reference, candidate)]
    for key in set(configs[0]) | set(configs[1]):
        if key == 'ShareRolloutStates':
            continue
        # These experimental properties were added after the frozen reference.
        default = 4 if key == 'FutureScryDepth' else False
        if configs[0].get(key, default) != configs[1].get(key, default):
            raise ValueError(f'Search configuration differs: {key}')
    archives = [json.loads((directory / 'compiled-source-manifest.json').read_text())['sha256'] for directory in (reference, candidate)]
    rule_prefixes = ('Assets/Scripts/Core/', 'Assets/Scripts/Shards/Engine/', 'Assets/Scripts/Shards/Content/')
    rules = [{k: v for k, v in a.items() if k.startswith(rule_prefixes)} for a in archives]
    if not rules[0] or rules[0] != rules[1]:
        raise ValueError('Game-rule source archives differ')
    files = [sorted((directory / 'reviews').glob('game-*.jsonl')) for directory in (reference, candidate)]
    if len(files[0]) < 20 or [p.name for p in files[0]] != [p.name for p in files[1]]:
        raise ValueError('Require at least 20 matching natural games')
    decisions = 0
    for left, right in zip(*files):
        expected = [json.loads(line) for line in left.read_text().splitlines()]
        actual = [json.loads(line) for line in right.read_text().splitlines()]
        if len(expected) != len(actual):
            raise ValueError(f'{left.name}: action count differs')
        for index, (a, b) in enumerate(zip(expected, actual)):
            if gameplay(a) != gameplay(b):
                raise ValueError(f'{left.name}, record {index}: selected action, public state, legal actions or result differs')
            decisions += int('selected' in a)
        if not actual[-1].get('finished'):
            raise ValueError(f'{left.name}: missing terminal result')
    return dict(passed=True, reference=str(reference.resolve()), candidate=str(candidate.resolve()),
        reference_host_sha256=manifests[0]['host_sha256'], candidate_host_sha256=manifests[1]['host_sha256'],
        policy_sha256=manifests[0]['policy_sha256'], games=len(files[0]), decisions=decisions,
        method='Exact selected actions, recorded game state, legal actions and terminal outcomes; diagnostic values excluded.',
        limitation='Equivalence on this fixed natural cohort, not a formal proof for all positions.',
        reference_manifest_sha256=sha(reference / 'manifest.json'), candidate_manifest_sha256=sha(candidate / 'manifest.json'))


if __name__ == '__main__':
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument('--reference', type=Path, required=True)
    p.add_argument('--candidate', type=Path, required=True)
    p.add_argument('--output', type=Path, required=True)
    a = p.parse_args()
    result = compare(a.reference, a.candidate)
    a.output.write_text(json.dumps(result, indent=2) + '\n')
    print(json.dumps(result))
