"""Bounded training and screening; never promotes weights or publishes statistics."""
import collections
import json
import os
from pathlib import Path
import subprocess
import sys
import time


def main():
    root = Path(__file__).resolve().parents[3]
    directory = Path(sys.argv[1]).resolve()
    directory.mkdir(parents=True, exist_ok=False)
    data = Path(sys.argv[2]).resolve()
    python = sys.executable
    os.sched_setaffinity(0, {0, 2, 4, 6, 8, 10, 12, 14})
    env = dict(os.environ, DOTNET_PROCESSOR_COUNT='8', OMP_NUM_THREADS='1', OPENBLAS_NUM_THREADS='1')
    report = dict(schema='hybrid-retraining-trials-v1', state='awaiting_completed_experience', rows=[])
    def save():
        (directory/'trials.json').write_text(json.dumps(report, indent=2)+'\n')
    save()
    while not json.loads((data/'manifest.json').read_text()).get('completed'):
        time.sleep(5)
    def run(name, command):
        report['state'] = name
        save()
        with (directory/f'{name}.log').open('w') as output:
            subprocess.run([python]+command, cwd=root, env=env, stdout=output, stderr=subprocess.STDOUT, check=True)
    policy = str(data/'runtime/shards-policy.bytes')
    run('learning', ['Tools/TrainingPreflight/hybrid_learning.py', '--data', str(data/'experience.bin'),
                     '--policy', policy, '--output', str(directory/'learning'), '--epochs', '8'])
    common = ['Tools/TrainingPreflight/gpu_search.py', '--hybrid', '1', '--candidates', '4', '--depth', '24',
              '--workers', '8', '--copy', 'compiled', '--batch', '80']
    for kind in ('actor', 'value', 'combined'):
        candidate = directory/'learning'/('combined.bytes' if kind == 'combined' else f'{kind}-best.bytes')
        run(kind+'-tactics', common+['--policy', str(candidate), '--mode', 'tactics', '--variants', '8', '--samples', '3',
                                    '--games', '40', '--output', str(directory/(kind+'-tactics'))])
        tactical = json.loads((directory/(kind+'-tactics')/'games.json').read_text())['rows']
        failures = dict(collections.Counter(row['id'] for row in tactical if not row['passed']))
        row = dict(kind=kind, policy=str(candidate), tactical_trials=len(tactical), tactical_failures=failures)
        report['rows'].append(row)
        save()
        if failures:
            continue
        # Common pilot seeds, disjoint from training and the eventual final test.
        run(kind+'-paired', common+['--policy', str(candidate), '--mode', 'search-paired', '--games', '160',
              '--reference-policy', policy, '--reference-hybrid', '1', '--reference-candidates', '4',
              '--reference-depth', '24', '--seed', '9020000000000000000', '--output', str(directory/(kind+'-paired'))])
        from hybrid_compare import read, paired
        row['strength'] = paired(read(directory/(kind+'-paired')/'games.json'))
        save()
        print(json.dumps(row), flush=True)
    report['state'] = 'completed'
    save()


if __name__ == '__main__':
    main()
