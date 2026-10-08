"""Wait for complete development comparisons, freeze one candidate, then audit its final test."""
import argparse
from collections import Counter
import json
import os
from pathlib import Path
import signal
import subprocess
import sys
import time

from audit_goal_evaluation import audit, heroes_for_seed, search_profile, sha256_file
from league import save_json
from monitor_experiment import process_alive

ROOT = Path(__file__).resolve().parents[2]


def validated_development(directory, games, seed):
    plan = json.loads((directory/'plan.json').read_text())
    result = json.loads((directory/'result.json').read_text())
    if (plan['games'] != games or plan['seed'] != seed or plan.get('start_pair', 0)
            or plan.get('smoke') or plan.get('learning_updates')
            or plan.get('incumbent_inference_backend', 'native') != 'native'):
        raise ValueError('Development settings do not match the declared comparison')
    rows = result['completed_games']
    if result['state'] != 'complete' or result['completed'] != games or result['planned'] != games or len(rows) != games:
        raise ValueError('Development comparison did not complete')
    expected = {(seed+i, seat) for i in range(games//2) for seat in (0, 1)}
    if Counter((g['seed'], g['learner_seat']) for g in rows) != Counter(expected):
        raise ValueError('Development comparison has missing or duplicated games')
    for game in rows:
        if not game['completed'] or game['winner'] not in (-1, 0, 1):
            raise ValueError('Development comparison has a censored or invalid outcome')
        if tuple(game['heroes']) != heroes_for_seed(game['seed']):
            raise ValueError('Development hero assignment changed')
    if sha256_file(directory/'runtime/DepthHost.dll') != plan['binary_sha256']:
        raise ValueError('Development runtime changed')
    return plan, sum(g['winner'] == g['learner_seat'] for g in rows)


def select_candidate(config):
    candidates = []
    for entry in config['candidates']:
        folder = Path(entry['experiment'])
        plan, wins = validated_development(folder, config['development_games'], config['development_seed'])
        if sha256_file(Path(entry['policy'])/'policy.pt') != plan['candidate']['policy_file_sha256']:
            raise ValueError('Candidate weights changed after development')
        candidates.append((entry, plan, wins))
    if len({p['binary_sha256'] for _, p, _ in candidates}) != 1 or len({p['incumbent_manifest_sha256'] for _, p, _ in candidates}) != 1:
        raise ValueError('Development comparisons used different runtimes or incumbents')
    # The ordered config declares the tie break before results are complete.
    return max(candidates, key=lambda item: item[2]), candidates


def owner_for(process, command):
    return dict(pid=process.pid, startticks=int(Path(f'/proc/{process.pid}/stat').read_text().rsplit(')', 1)[1].split()[19]),
                command=command, boot_id=Path('/proc/sys/kernel/random/boot_id').read_text().strip())


def run(config_path):
    config = json.loads(config_path.read_text())
    folder = config_path.parent
    state_path = folder/'final-handoff-state.json'
    claim = folder/'final-handoff-claim.json'
    with claim.open('x') as stream:
        json.dump(dict(pid=os.getpid(), created_wall=time.time(), config_sha256=sha256_file(config_path)), stream)
    stopped = False
    def stop(*_):
        nonlocal stopped
        stopped = True
    signal.signal(signal.SIGTERM, stop)
    signal.signal(signal.SIGINT, stop)
    def cancelled():
        return stopped or (folder/'STOP-FINAL-HANDOFF').exists() or time.time() >= config['deadline_wall']
    def publish(phase, **extra):
        save_json(state_path, dict(phase=phase, updated_wall=time.time(), **extra))
    child = None
    try:
        publish('waiting for both complete development comparisons')
        while not all((Path(e['experiment'])/'result.json').exists() for e in config['candidates']):
            if cancelled():
                publish('stopped before final benchmark'); return
            for entry in config['candidates']:
                experiment = Path(entry['experiment'])
                if not (experiment/'result.json').exists():
                    owner = json.loads((experiment/'owner.json').read_text())
                    if not process_alive(owner):
                        raise RuntimeError('Development process exited without completing: '+str(experiment))
            time.sleep(5)
        selected, candidates = select_candidate(config)
        entry, source_plan, wins = selected
        if wins < config.get('minimum_development_wins',0):
            publish('no candidate met the declared development threshold',wins=wins,
                    minimum_wins=config['minimum_development_wins'])
            return
        if cancelled():
            publish('stopped before final benchmark'); return
        protocol_path = Path(config['protocol'])
        protocol = json.loads(protocol_path.read_text())
        if protocol.get('candidate') is not None:
            raise RuntimeError('Final protocol already selected; refusing another launch')
        output = Path(config['output'])
        if output.exists():
            raise RuntimeError('Final output already exists; refusing to overwrite evidence')
        protocol['candidate'] = dict(policy_sha256=source_plan['candidate']['policy_file_sha256'],
            binary_sha256=source_plan['binary_sha256'], incumbent_manifest_sha256=source_plan['incumbent_manifest_sha256'],
            search_profile=search_profile(source_plan))
        protocol['status'] = 'Candidate frozen before final evaluation'
        protocol['selection_evidence'] = [dict(experiment=e['experiment'], wins=w,
            result_sha256=sha256_file(Path(e['experiment'])/'result.json')) for e, _, w in candidates]
        protocol['selected_experiment'] = entry['experiment']
        protocol['frozen_wall'] = time.time()
        save_json(protocol_path, protocol)
        command = json.loads((Path(entry['experiment'])/'owner.json').read_text())['command'][:]
        for flag, value in {'--output':str(output), '--policy':entry['policy'], '--games':str(protocol['games']),
                '--batch':str(protocol['batch']), '--seed':str(protocol['seed']), '--seconds':'10800'}.items():
            command[command.index(flag)+1] = value
        command += ['--runtime', str(Path(entry['experiment'])/'runtime'), '--final-protocol', str(protocol_path)]
        with (folder/'final-benchmark-launcher.log').open('ab') as log:
            child = subprocess.Popen(command, cwd=ROOT, stdin=subprocess.DEVNULL, stdout=log,
                                     stderr=subprocess.STDOUT, start_new_session=True)
        owner = owner_for(child, command)
        save_json(folder/'final-benchmark-owner.json', owner)
        publish('final benchmark running', selected=entry['experiment'], development_wins=wins, owner=owner)
        goal_path = folder/'goal-state.json'
        goal = json.loads(goal_path.read_text())
        goal.update(phase='Frozen final benchmark against the in-game AI: 600 fresh games, Rez included',
                    active_job=str(output), updated_wall=time.time())
        goal['tracked_jobs'] = [e['experiment'] for e in config['candidates']] + [str(output)]
        save_json(goal_path, goal)
        while child.poll() is None:
            if output.exists() and not (output/'owner.json').exists():
                save_json(output/'owner.json', owner)
            if cancelled():
                child.terminate()
                try: child.wait(timeout=20)
                except subprocess.TimeoutExpired: child.kill(); child.wait()
                publish('final benchmark stopped at requested stop or deadline'); return
            time.sleep(5)
        if child.returncode:
            raise RuntimeError(f'Final benchmark exited with status {child.returncode}; inspect launcher log')
        plan = json.loads((output/'plan.json').read_text())
        if plan['final_protocol_sha256'] != sha256_file(protocol_path):
            raise RuntimeError('Final protocol changed after launch')
        if sha256_file(output/'runtime/DepthHost.dll') != plan['binary_sha256']:
            raise RuntimeError('Final runtime changed after launch')
        result = json.loads((output/'result.json').read_text())
        report = audit(protocol, plan, result)
        report['evidence'] = {str(path):sha256_file(path) for path in
            (protocol_path, output/'plan.json', output/'result.json', output/'runtime/DepthHost.dll')}
        save_json(folder/'final-audit.json', report)
        publish('final benchmark audited', target_achieved=report['target_achieved'], wins=report['summary']['wins'])
    except BaseException as error:
        if child is not None and child.poll() is None:
            child.terminate()
        publish('failed; no automatic retry', error=str(error))
        raise


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--config', type=Path, required=True)
    run(parser.parse_args().config.resolve())
