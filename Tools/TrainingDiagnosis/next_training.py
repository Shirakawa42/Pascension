"""Prepare or explicitly launch a bounded, matched learning experiment.

The candidate changes only temporal credit assignment. Neither arm replaces the
game AI. All expensive jobs run sequentially in private background sessions.
"""
from __future__ import annotations

import argparse
from dataclasses import asdict
import hashlib
import json
import math
import os
from pathlib import Path
import signal
import subprocess
import sys
import time

ROOT = Path(__file__).resolve().parents[2]
sys.path[:0] = [str(ROOT / 'Tools/ZeroDepthTraining'), str(ROOT / 'Tools/TrainingPreflight'),
               str(ROOT / 'Tools/MatchupBenchmark')]
from league import sha256_file, save_json


def read(path):
    return json.loads(Path(path).read_text())


def prepare(directory, source, *, smoke=False):
    from host import catalog
    from train import TrainConfig, identity
    directory, source = Path(directory).resolve(), Path(source).resolve()
    if directory.exists():
        raise FileExistsError('Use an unused experiment directory')
    parent = read(source / 'learner/manifest.json')
    host_catalog = catalog()
    configs = {}
    for name, mode in (('control', 'decision'), ('candidate', 'round')):
        config = TrainConfig(**{**parent['identity']['configuration'],
            'batch': 64, 'workers': 6, 'hero_mode': 'balanced_random',
            'trace_mode': mode, 'trace_decay': .95, 'optional_exploration': 0.,
            'engine_seed': 0x6200000000000000})
        config.validate()
        configs[name] = (asdict(config), identity(config, host_catalog))
    directory.mkdir(parents=True)
    for name, (config, pinned) in configs.items():
        arm = directory / name
        arm.mkdir()
        save_json(arm / 'config.json', config)
        save_json(arm / 'identity.json', pinned)
        save_json(arm / 'catalog.json', host_catalog)
    plan = dict(schema='shards-matched-training-v1', source=str(source),
        parent_checkpoint_sha256=sha256_file(source / 'snapshot.soicp'),
        parent_manifest_sha256=sha256_file(source / 'learner/manifest.json'),
        parent_policy_sha256=sha256_file(source / 'learner/policy.pt'),
        controller_sha256=sha256_file(__file__),
        games_per_arm_per_review=64 if smoke else 16384,
        arena_games=40 if smoke else 2000,
        max_training_block_seconds=600, max_arena_seconds=180,
        max_failed_reviews=3, smoke=smoke, training_started=False,
        experimental_change='lambda=.95 per completed round instead of per decision',
        repeated_testing_alpha=.05, promotion_allowed=False,
        instructions='start requires --hours; STOP halts; no automatic restart or final searched benchmark',
        identities={name: pair[1] for name, pair in configs.items()})
    save_json(directory / 'plan.json', plan)
    validate(directory, full=True)
    return plan


def validate(directory, *, full=False):
    from host import catalog
    from train import TrainConfig, identity
    directory = Path(directory)
    plan = read(directory / 'plan.json')
    source = Path(plan['source'])
    if plan['schema'] != 'shards-matched-training-v1' or plan['controller_sha256'] != sha256_file(__file__):
        raise ValueError('Prepared controller changed; prepare a new experiment')
    for relative, key in (('snapshot.soicp', 'parent_checkpoint_sha256'),
                          ('learner/manifest.json', 'parent_manifest_sha256'),
                          ('learner/policy.pt', 'parent_policy_sha256')):
        if sha256_file(source / relative) != plan[key]:
            raise ValueError('Pinned parent artifact changed: ' + relative)
    host_catalog = catalog()
    for name in ('control', 'candidate'):
        config = TrainConfig(**read(directory / name / 'config.json'))
        config.validate()
        actual = identity(config, host_catalog)
        if actual != plan['identities'][name] or actual != read(directory / name / 'identity.json'):
            raise ValueError('Prepared rules, source, or configuration changed')
    if full:
        import torch
        from campaign_state import load_checkpoint
        from model import Policy, PolicyConfig, cpu_state, legacy_exploration_state
        from opponent_archive import OpponentArchive
        torch.set_num_threads(1)
        parent = read(source / 'learner/manifest.json')
        payload = load_checkpoint(source / 'snapshot.soicp', expected_identity=parent['identity'])
        state = payload['state']
        if state['catalog'] != host_catalog:
            raise ValueError('Parent observation catalog changed')
        policy = Policy(host_catalog, PolicyConfig(**state['policy_config']))
        policy.load_state_dict(state['policy'], strict=True)
        cfg = TrainConfig(**read(directory / 'candidate/config.json'))
        if cfg.width != policy.config.width or float(policy.optional_exploration) != 0.:
            raise ValueError('This experiment requires the unchanged legacy model')
        from train import Learner
        learner = Learner(policy, cfg)
        learner.restore_optimizer(state['optimizer'])
        learner.verify_finite_state()
        OpponentArchive(cfg.archive_strategy, limit=cfg.archive_limit, every=cfg.archive_every,
            generation=state['generations'], current=cpu_state(policy),
            archive=[legacy_exploration_state(w) for w in state['archive']],
            metadata=state['opponent_archive'])
        save_json(directory / 'validation.json', dict(valid=True, training_started=False,
            parameters=sum(p.numel() for p in policy.parameters()), parent_games=state['games'],
            optimizer_restored=True, archive_restored=True, catalog_exact=True))
    return plan


def migrate(directory, seconds):
    """New allocation, old learned tensors/Adam/archive/RNG; explicit lineage."""
    from campaign_state import CampaignBudget, load_checkpoint
    from model import legacy_exploration_state
    from performance_upgrade import write_payload_atomic
    directory = Path(directory)
    plan = read(directory / 'plan.json')
    source = Path(plan['source'])
    parent_meta = read(source / 'learner/manifest.json')
    parent = load_checkpoint(source / 'snapshot.soicp', expected_identity=parent_meta['identity'])
    for name in ('control', 'candidate'):
        arm = directory / name
        if (arm / 'budget.json').exists() or (arm / 'latest.soicp').exists():
            raise FileExistsError('Never replace an existing allocation')
        config = read(arm / 'config.json')
        pinned = read(arm / 'identity.json')
        state = dict(parent['state'])
        state.update(configuration=config, next_engine_seed=config['engine_seed'],
            policy=legacy_exploration_state(state['policy']),
            archive=[legacy_exploration_state(w) for w in state['archive']],
            reason='explicit_matched_experiment_migration')
        with CampaignBudget(arm / 'budget.json', limit_seconds=seconds) as budget:
            payload = dict(parent, state=state, identity=pinned, budget=budget.snapshot(),
                identity_sha256=hashlib.sha256(json.dumps(pinned, sort_keys=True,
                    separators=(',', ':'), allow_nan=False).encode()).hexdigest(),
                created_wall=time.time())
            payload.pop('file_sha256', None)
            write_payload_atomic(arm / 'latest.soicp', payload)
            # Read back the actual persisted checkpoint with its new allocation.
            loaded = load_checkpoint(arm / 'latest.soicp', expected_identity=pinned, budget=budget)
            if loaded['state']['games'] != parent['state']['games']:
                raise ValueError('Migration changed the parent game counter')
            del loaded
        save_json(arm / 'migration.json', dict(parent_checkpoint_sha256=plan['parent_checkpoint_sha256'],
            policy_tensors_changed=False, new_disabled_buffer='optional_exploration',
            optimizer_and_archive_preserved=True, rng_preserved=True, training_seed_reset=config['engine_seed'],
            new_campaign_allocation=True))
    return parent['state']['games']


def alpha_for_review(review):
    if type(review) is not int or review < 1:
        raise ValueError('Review numbers start at one')
    # Three comparisons per review; union bound across all future reviews <= .05.
    return .05 / (3 * review * (review + 1))


def verdict(results, expected_games):
    if len(results) != 3:
        return 'incomplete'
    for result in results:
        summary = result['summary']
        low, high = summary['paired_hoeffding_score_interval']
        if (not summary['evaluation_finished'] or summary['recorded_games'] != expected_games
                or summary['censored'] or summary.get('missing_games', 0)
                or not 0 <= low <= high <= 1):
            return 'incomplete'
    if any(r['summary']['paired_hoeffding_score_interval'][1] < .5 for r in results):
        return 'regression'
    if all(r['summary']['paired_hoeffding_score_interval'][0] > .5 for r in results):
        return 'progress'
    return 'not_demonstrated'


def review_transition(decision, failures, limit):
    if decision not in ('progress', 'incomplete', 'regression', 'not_demonstrated'):
        raise ValueError('Unknown review decision')
    failures = 0 if decision == 'progress' else failures + 1
    return failures, decision in ('incomplete', 'regression') or failures >= limit


def child(command, logfile, *, deadline, stop_check):
    """Own a private process session; stop and join it before starting another."""
    from launch import _service_identity, _service_members, _join_service_descendants
    if stop_check() or time.monotonic() >= deadline:
        raise TimeoutError('Experiment stopped before child launch')
    with Path(logfile).open('a') as stream:
        process = subprocess.Popen(command, stdin=subprocess.DEVNULL, stdout=stream,
            stderr=subprocess.STDOUT, start_new_session=True,
            env={**os.environ, 'OMP_NUM_THREADS': '1', 'MKL_NUM_THREADS': '1',
                 'SHARDS_DIAG_PARENT': str(os.getpid())})
        owner = _service_identity(process.pid)
        known = []
        try:
            while process.poll() is None:
                if owner:
                    known = _service_members(owner, known)
                if stop_check() or time.monotonic() >= deadline:
                    process.terminate()
                    try:
                        process.wait(timeout=30)
                    except subprocess.TimeoutExpired:
                        process.kill()
                        process.wait(timeout=5)
                    raise TimeoutError('Experiment child reached stop/deadline')
                time.sleep(.25)
            if process.returncode:
                raise RuntimeError(f'Experiment child exited {process.returncode}; see {logfile}')
        finally:
            if process.poll() is None:
                process.terminate()
                try:
                    process.wait(timeout=30)
                except subprocess.TimeoutExpired:
                    process.kill(); process.wait(timeout=5)
            if owner:
                _join_service_descendants(owner, known)


def worker(jobfile):
    # An abruptly killed controller cannot leave learning running all night.
    import ctypes
    expected_parent = int(os.environ['SHARDS_DIAG_PARENT'])
    if ctypes.CDLL(None, use_errno=True).prctl(1, signal.SIGTERM, 0, 0, 0) != 0:
        raise OSError('Cannot install parent-death signal')
    if os.getppid() != expected_parent:
        raise RuntimeError('Controller exited before worker startup')
    from cpu_affinity import select_cpus
    os.sched_setaffinity(0, select_cpus(os.sched_getaffinity(0), 6))
    job = read(jobfile)
    if job['kind'] == 'train':
        from train import TrainConfig, train
        arm = Path(job['arm'])
        train(TrainConfig(**read(arm / 'config.json')), arm, job['seconds'],
            max_games=job['target_games'], resume=arm / 'latest.soicp')
        from league import export_frozen_policy
        export_frozen_policy(arm / 'latest.soicp', job['export'], identity_path=arm / 'identity.json')
    else:
        from league import run_neural_arena
        run_neural_arena(job['candidate'], job['baseline'], job['output'],
            games=job['games'], batch=40, workers=6, seed_base=job['seed'],
            max_seconds=job['max_seconds'], alpha=job['alpha'])


def run(directory, hours):
    import torch
    torch.set_num_threads(1)
    directory = Path(directory).resolve()
    seconds = hours * 3600
    started = time.monotonic()
    deadline = started + seconds
    if (directory / 'STOP').exists() or (directory / 'run-state.json').exists():
        raise RuntimeError('Stopped or already used experiment; prepare a fresh directory')
    # Exclusive ownership remains on disk even after a crash. No restart loop.
    with (directory / 'RUN-CLAIM').open('x') as claim:
        claim.write(str(os.getpid()))
    stopped = False
    def request_stop(*_):
        nonlocal stopped
        stopped = True
    signal.signal(signal.SIGTERM, request_stop)
    signal.signal(signal.SIGINT, request_stop)
    def should_stop():
        return stopped or (directory / 'STOP').exists() or time.monotonic() >= deadline - 35
    state = dict(state='validating', hours=hours, started_wall=time.time(),
        deadline_wall=time.time() + seconds, reviews=[], promotion_allowed=False)
    def publish(**updates):
        state.update(updates)
        save_json(directory / 'run-state.json', state)
    publish()
    try:
        plan = validate(directory)
        if should_stop():
            raise TimeoutError('Allocation exhausted during validation')
        parent_games = migrate(directory, seconds)
        champion = str(Path(plan['source']) / 'learner')
        failures = 0
        for review in range(1, 10000):
            # Never begin a review that cannot fit inside the remaining bound.
            reserve = 2 * plan['max_training_block_seconds'] + 3 * plan['max_arena_seconds'] + 45
            if should_stop() or (not plan['smoke'] and deadline - time.monotonic() < reserve):
                publish(state='time_limit'); break
            target = parent_games + review * plan['games_per_arm_per_review']
            folder = directory / f'review-{review:03}'
            folder.mkdir()
            for name in ('control', 'candidate') if review % 2 else ('candidate', 'control'):
                publish(state='training', review=review, active_arm=name, target_games=target)
                job = dict(kind='train', arm=str(directory / name), seconds=seconds,
                    target_games=target, export=str(folder / name))
                jobpath = folder / f'{name}-job.json'; save_json(jobpath, job)
                child([sys.executable, __file__, '_worker', '--job', str(jobpath)],
                    folder / f'{name}.log', deadline=min(deadline - 35,
                        time.monotonic() + plan['max_training_block_seconds']), stop_check=should_stop)
                actual = read(folder / name / 'manifest.json')['training']['games']
                if actual != target:
                    raise RuntimeError('Matched training block incomplete; stop instead of comparing unequal training')
            publish(state='evaluating', active_arm=None)
            results = []
            comparisons = [('parent', str(Path(plan['source']) / 'learner')),
                           ('control', str(folder / 'control')), ('champion', champion)]
            for index, (label, baseline) in enumerate(comparisons):
                output = folder / f'against-{label}.json'
                seed = (0xA200000000000000 // 20 + review * 3000 + index * 1000) * 20
                job = dict(kind='arena', candidate=str(folder / 'candidate'), baseline=baseline,
                    output=str(output), games=plan['arena_games'], seed=seed,
                    max_seconds=plan['max_arena_seconds'], alpha=alpha_for_review(review))
                jobpath = folder / f'{label}-job.json'; save_json(jobpath, job)
                publish(comparison=label)
                child([sys.executable, __file__, '_worker', '--job', str(jobpath)],
                    folder / f'{label}.log', deadline=min(deadline - 35,
                        time.monotonic() + plan['max_arena_seconds'] + 10), stop_check=should_stop)
                results.append(read(output))
            decision = verdict(results, plan['arena_games'])
            if decision == 'progress':
                champion = str(folder / 'candidate')
            failures, stop_for_review = review_transition(decision, failures, plan['max_failed_reviews'])
            state['reviews'].append(dict(review=review, decision=decision,
                games_added_per_arm=review * plan['games_per_arm_per_review'],
                alpha_per_comparison=alpha_for_review(review),
                scores={label: result['summary']['resolved_game_score']
                    for (label, _), result in zip(comparisons, results)}))
            publish(champion=champion, failed_reviews=failures)
            if plan['smoke'] or stop_for_review:
                publish(state='smoke_complete' if plan['smoke'] else 'stopped_for_review'); break
        else:
            publish(state='review_limit')
    except BaseException as error:
        publish(state='stopped' if isinstance(error, TimeoutError) else 'failed', error=str(error))
        raise
    finally:
        publish(finished_wall=time.time(), elapsed_seconds=time.monotonic() - started, training_running=False)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('command', choices=['prepare', 'validate', 'start', '_run', '_worker'])
    parser.add_argument('--directory', type=Path)
    parser.add_argument('--source', type=Path)
    parser.add_argument('--hours', type=float)
    parser.add_argument('--smoke', action='store_true')
    parser.add_argument('--job', type=Path)
    args = parser.parse_args()
    if args.command == '_worker':
        worker(args.job); return
    if args.directory is None:
        parser.error('--directory is required')
    if args.command == 'prepare':
        if args.source is None: parser.error('--source is required')
        prepare(args.directory, args.source, smoke=args.smoke)
    elif args.command == 'validate':
        validate(args.directory, full=True)
    else:
        if args.hours is None or not math.isfinite(args.hours) or not 0 < args.hours <= 12:
            parser.error('An explicit --hours in (0,12] is required')
        if args.command == '_run':
            run(args.directory, args.hours)
        else:
            validate(args.directory)
            if any((args.directory / name).exists() for name in ('STOP', 'RUN-CLAIM', 'run-state.json')):
                parser.error('Experiment stopped or already used; prepare a fresh directory')
            with (args.directory / 'controller.log').open('a') as logfile:
                process = subprocess.Popen([sys.executable, __file__, '_run', '--directory',
                    str(args.directory.resolve()), '--hours', str(args.hours)], stdin=subprocess.DEVNULL,
                    stdout=logfile, stderr=subprocess.STDOUT, start_new_session=True,
                    env={**os.environ, 'OMP_NUM_THREADS': '1', 'MKL_NUM_THREADS': '1'})
            print(json.dumps(dict(pid=process.pid, log=str(args.directory / 'controller.log'))))


if __name__ == '__main__':
    main()
