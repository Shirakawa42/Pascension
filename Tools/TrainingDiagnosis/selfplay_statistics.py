"""Frozen full-information AI self-play, with live statistics from real games only."""
import argparse
from collections import Counter
from contextlib import ExitStack
import hashlib
import json
import os
from pathlib import Path
import shutil
import signal
import subprocess
import sys
import threading
import time

from search_candidate import ROOT, Inference, client, load_frozen_policy, require_calibration
from league import heroes_for_seed, sha256_file

sys.path.append(str(ROOT/'Tools/TrainingPreflight'))
from balance_host_statistics import combine, delta
from strategy_statistics import build as build_strategies
from balance_analysis import build as build_balance


def save(path, value):
    temporary = path.with_suffix(path.suffix+'.tmp')
    temporary.write_text(json.dumps(value, indent=2, allow_nan=False)+'\n')
    temporary.replace(path)


def validate_games(rows, seed, games):
    if len(rows) != games or {r['seed'] for r in rows} != set(range(seed, seed+games)):
        raise ValueError('Missing or duplicated self-play seed')
    if any(not r['completed'] or r['winner'] not in (-1, 0, 1) or
           tuple(r['heroes']) != heroes_for_seed(r['seed']) for r in rows):
        raise ValueError('Invalid result or hero assignment')
    if games % 20 == 0 and seed % 20 == 0:
        pairs = Counter(tuple(r['heroes']) for r in rows)
        if len(pairs) != 20 or set(pairs.values()) != {games//20}:
            raise ValueError('Unbalanced distinct-hero matchups')


def validate_traces(lines, rows):
    traced = [json.loads(line) for line in lines]
    seeds = [int(g['seed'], 16) for g in traced]
    if len(seeds) != len(rows) or len(set(seeds)) != len(seeds) or set(seeds) != {g['seed'] for g in rows}:
        raise ValueError('Traces contain different or duplicate seeds')
    records = {r['seed']: r for r in rows}
    for seed, game in zip(seeds, traced):
        row = records[seed]
        if game['winner'] != row['winner'] or [p['hero'] for p in game['players']] != list(row['heroes']):
            raise ValueError('Trace outcomes or heroes disagree with game records')
        if 'rounds' in row and game['round'] != row['rounds']:
            raise ValueError('Trace game lengths disagree with game records')


def read_resume(directory, manifest, runtime_audit=None):
    """Only a stopped producer's complete cohorts can be continued.

    The original files remain untouched. Never silently discard completed
    individual games which have not reached the cohort report yet.
    """
    from monitor_experiment import process_alive
    owner = directory/'owner.json'
    if owner.exists() and process_alive(json.loads(owner.read_text())):
        raise ValueError('Cannot resume statistics from a live producer')
    original = json.loads((directory/'manifest.json').read_text())
    if not owner.exists() and original.get('state') in ('starting', 'running'):
        raise ValueError('Running producer has no verifiable owner identity')
    if original['host_sha256'] != manifest['host_sha256']:
        if not runtime_audit or not runtime_audit.get('passed') or any(runtime_audit.get(key) != value for key, value in
                [('original_host_sha256', original['host_sha256']), ('optimized_host_sha256', manifest['host_sha256']),
                 ('policy_sha256', manifest['policy_sha256'])]):
            raise ValueError('Changed runtime requires a matching completed parity audit')
    for key in ('seed', 'games', 'policy_sha256',
                'settings_source_manifest_sha256', 'both_seats_same_new_ai',
                'scry_temperature', 'depth', 'width', 'worlds', 'rollout_styles', 'fixed_inference_batch'):
        if original[key] != manifest[key]:
            raise ValueError('Resume provenance differs: '+key)
    rows = json.loads((directory/'games.json').read_text())['rows']
    validate_games(rows, manifest['seed'], len(rows))
    if not rows or len(rows) >= manifest['games'] or len(rows) % manifest['batch']:
        raise ValueError('Resume requires complete cohorts below the target')
    rawdir = directory/'raw/forced-random'
    files = sorted([*rawdir.glob('session-*.json'), *rawdir.glob('worker-*/session-*.json')])
    if len(files) != original.get('streams', 1) or any(p.name.endswith('.error.json') for p in files):
        raise ValueError('Resume requires exactly the declared passive statistics sessions')
    sessions = [json.loads(p.read_text()) for p in files]
    if any(raw['host_binary_sha256'] != original['host_sha256'] or raw['purpose'] != 'final_evaluation' for raw in sessions):
        raise ValueError('Resume collector provenance differs')
    trace = []
    if original.get('resumed_games'):
        prefix = json.loads((directory/'resume/raw.json').read_text())
        sessions = prefix.get('sessions', [prefix])+sessions
        trace.extend((directory/'resume/strategy-games.jsonl').read_text(encoding='utf-8-sig').splitlines())
    raw = dict(sessions=sessions, totals={key:sum(s['totals'][key] for s in sessions) for key in sessions[0]['totals']})
    if raw['totals']['completed_games'] != len(rows):
        raise ValueError('Completed individual games differ from saved cohorts; refusing to lose results')
    if raw['totals']['censored_games'] or raw['totals']['unfinished_discarded_games']:
        raise ValueError('Resume cannot include censored or discarded games')
    for path in files:
        trace.extend((path.parent/'strategy-games.jsonl').read_text(encoding='utf-8-sig').splitlines())
    if len(trace) != len(rows):
        raise ValueError('Resume trace and completed cohorts disagree')
    validate_traces(trace, rows)
    for seat in range(2):
        if sum(g['winner'] == seat for g in rows) != raw['totals'][f'seat{seat}_wins']:
            raise ValueError('Resume outcomes disagree with passive statistics')
    return original, rows, raw, ('\n'.join(trace)+'\n').encode()


def publish(directory, manifest, metadata, *, final=False, force=False):
    raw_directory = directory/'raw/forced-random'
    files = sorted([*raw_directory.glob('session-*.json'), *raw_directory.glob('worker-*/session-*.json')])
    if any(p.name.endswith('.error.json') for p in files):
        raise RuntimeError('The passive statistics collector reported an error')
    if not files:
        return None
    if len(files) > manifest.get('streams', 1) or len({p.parent for p in files}) != len(files):
        raise ValueError('Unexpected additional statistics producer')
    raw_sessions = [json.loads(p.read_text()) for p in files]
    if any(raw['host_binary_sha256'] != manifest['host_sha256'] or raw['purpose'] != 'final_evaluation' for raw in raw_sessions):
        raise ValueError('Statistics provenance mismatch')
    current_n = sum(raw['totals']['completed_games'] for raw in raw_sessions)
    prefix_n = manifest.get('resumed_games', 0)
    n = prefix_n+current_n
    if any(raw['totals']['censored_games'] or raw['totals']['unfinished_discarded_games'] for raw in raw_sessions):
        raise ValueError('Unfinished or censored games cannot enter this statistics run')
    previous = directory/'statistics.json'
    if previous.exists() and not (force or final):
        if json.loads(previous.read_text())['totals']['resolved_games'] == n:
            return None
    prefix = json.loads((directory/'resume/raw.json').read_text()) if prefix_n else None
    windows = [delta(raw) for raw in prefix.get('sessions', [prefix])] if prefix else []
    windows.extend(delta(raw) for raw in raw_sessions)
    snapshot = combine(windows, metadata, run_id=directory.name, target_games=manifest['games'])
    snapshot['state'] = 'ready'
    snapshot['card_catalog'] = metadata
    snapshot['scope'].update(label=f"New AI self-play · {manifest['games']:,} games",
        policy_label='Frozen 512-width AI · 13.75M parameters · search on both sides',
        opponent_label='Identical frozen new AI and search on both seats',
        inference='GPU policy/value · depth 24 · width 4 · 2 worlds · 4 rollout styles',
        policy_sha256=manifest['policy_sha256'], final_evaluation=True,
        confidence_method='Descriptive game-cluster bounds; self-play is not a strength test',
        notes=['Only this new frozen self-play run is included; zero training updates.',
               'All five heroes, including Rez; randomized balanced distinct-hero pairs.',
               'Real-game events only. Hypothetical search branches never enter statistics.',
               'Card and hero associations describe this AI, not optimal balance or causal effects.'])
    elapsed = max(0., time.time()-manifest['started_wall'])
    snapshot['refresh'].update(every_games=1, evaluation_sample_count=n,
        snapshot_training_games=0, total_games=n)
    snapshot['evaluation_progress'] = dict(completed=n, target=manifest['games'],
        state='completed' if final else manifest.get('state', 'running'),
        elapsed_seconds=elapsed, games_per_second=current_n/elapsed if elapsed else 0,
        estimated_games_per_hour=3600*current_n/elapsed if elapsed else 0,
        rate_basis='Current execution segment; preserved earlier games excluded from rate',
        preserved_games=prefix_n)
    if n:
        # The writer may already have finished more games than its most recent
        # aggregate publication. Analyze the exact matching completed prefix.
        captured = []
        for path, raw in zip(files, raw_sessions):
            count = raw['totals']['completed_games']
            lines = (path.parent/'strategy-games.jsonl').read_bytes().removeprefix(b'\xef\xbb\xbf').splitlines(keepends=True)
            if len(lines) < count:
                raise ValueError('Published games are missing their strategy traces')
            captured.extend(lines[:count])
        prefix = directory/'strategy-prefix.jsonl'
        saved_trace = (directory/'resume/strategy-games.jsonl').read_bytes() if prefix_n else b''
        prefix.write_bytes(saved_trace+b''.join(captured))
        snapshot['strategies'] = build_strategies(prefix, snapshot, metadata)
        snapshot['balance_analysis'] = build_balance(prefix, snapshot)
        snapshot['strategy_provenance'] = dict(games=n, optimizer_updates=0,
            policy_sha256=manifest['policy_sha256'], host_sha256=manifest['host_sha256'],
            capture_kind='new_cohort', evaluation_directory=str(directory))
    if final:
        if n != manifest['games'] or not all(raw['final'] for raw in raw_sessions):
            raise ValueError('Final statistics do not cover the requested game count')
        records = json.loads((directory/'games.json').read_text())['rows']
        validate_games(records, manifest['seed'], manifest['games'])
        validate_traces((directory/'strategy-prefix.jsonl').read_text(encoding='utf-8-sig').splitlines(), records)
        for seat in range(2):
            if sum(g['winner'] == seat for g in records) != sum(w['totals'][f'seat{seat}_wins'] for w in windows):
                raise ValueError('Game outcomes disagree with passive statistics')
    save(previous, snapshot)
    save(directory/'balance-statistics-final.json', snapshot)
    if manifest.get('dashboard_directory'):
        dashboard = Path(manifest['dashboard_directory'])
        save(dashboard/'balance-statistics-final.json', snapshot)
        save(dashboard/'final-statistics-mode.json', dict(enabled=True,
            evaluation_directory=str(directory), target_games=manifest['games']))
    save(directory/'final-statistics-mode.json', dict(enabled=True,
        evaluation_directory=str(directory), target_games=manifest['games']))
    return snapshot


def publisher_loop(directory, parent):
    """Keep growing trace analysis off the search/GPU request path."""
    done = False
    def stopped(*_):
        nonlocal done
        done = True
    signal.signal(signal.SIGTERM, stopped)
    metadata = json.loads((directory/'card-catalog.json').read_text())
    while not done and os.getppid() == parent:
        try:
            publish(directory, json.loads((directory/'manifest.json').read_text()), metadata)
        except BaseException as error:
            save(directory/'publisher-error.json', dict(error=str(error)))
            raise
        for _ in range(10):
            if done or os.getppid() != parent:return
            time.sleep(.5)


def run(a):
    import torch
    os.sched_setaffinity(0, {0, 2, 4, 6, 8, 10})
    torch.set_num_threads(1)
    if a.output.exists():
        raise ValueError('Use a fresh output directory; statistics runs are never appended implicitly')
    if not 1 <= a.batch <= 64 or a.games < 1 or a.games % a.batch or a.seed % 20 or a.batch % a.streams:
        raise ValueError('Require batch <=64 dividing the game target, and a seed aligned to20')
    a.output.mkdir(parents=True)
    a.output = a.output.resolve()
    save(a.output/'owner.json', dict(pid=os.getpid(),
        startticks=int(Path('/proc/self/stat').read_text().rsplit(')', 1)[1].split()[19]),
        boot_id=Path('/proc/sys/kernel/random/boot_id').read_text().strip(),
        command=[part.decode() for part in Path('/proc/self/cmdline').read_bytes().rstrip(b'\0').split(b'\0')]))
    runtime = a.output/'runtime'
    shutil.copytree(a.runtime, runtime)
    client.BINARY = runtime/'DepthHost.dll'
    model_dir = a.output/'policy'
    shutil.copytree(a.policy, model_dir)
    # Avoid inherited evaluation/training flags changing this frozen controller.
    for key in list(os.environ):
        if key.startswith('SHARDS_DEPTH_') or key.startswith('SHARDS_STATS_'):
            os.environ.pop(key)
    os.environ.update(SHARDS_DEPTH_INCUMBENT=str(a.incumbent.resolve()),
        SHARDS_DEPTH_SELFPLAY='1', SHARDS_ZERO_HERO_MODE='balanced_random',
        SHARDS_DEPTH_HYBRID='1', SHARDS_DEPTH_PRIOR='.015', SHARDS_DEPTH_PRIOR_CAP='6',
        SHARDS_DEPTH_MARGIN='.02', SHARDS_DEPTH_HORIZON_TURNS='1', SHARDS_DEPTH_ROLLOUT_STYLES='4',
        SHARDS_DEPTH_REZ_COVERAGE='1', SHARDS_DEPTH_FUTURE_SCRY='1',
        SHARDS_DEPTH_DROP_ENCODER_CACHES='1', SHARDS_DEPTH_POOLED_INFERENCE='1',
        SHARDS_DEPTH_SHARED_INFERENCE='1', SHARDS_STRATEGY_TRACE='1')
    if not a.no_statistics:
        os.environ['SHARDS_DEPTH_STATISTICS'] = str(a.output/'raw/forced-random')
    catalog = json.loads(subprocess.check_output([client.DOTNET, str(client.BINARY), 'catalog']))
    metadata = json.loads(subprocess.check_output([client.DOTNET, str(client.BINARY), 'statistics-catalog']))
    policy, policy_meta = load_frozen_policy(model_dir, device='cuda', allow_external_calibration=True)
    require_calibration(policy_meta, 64., 0.)
    for key in ('card_ids', 'card_features', 'contexts', 'tables', 'obs_dim', 'max_actions', 'action_dim'):
        if policy.catalog[key] != catalog[key]:
            raise ValueError('Model/runtime catalog mismatch: '+key)
    started = time.time()
    manifest = dict(schema='shards-full-information-selfplay-statistics-v1', state='starting',
        games=a.games, batch=a.batch, workers=6, streams=a.streams, seed=a.seed, started_wall=started,
        policy_sha256=sha256_file(model_dir/'policy.pt'), host_sha256=sha256_file(client.BINARY),
        settings_source_manifest_sha256=sha256_file(a.incumbent/'manifest.json'),
        incumbent_is_settings_source_only=True, both_seats_same_new_ai=True, training_updates=0,
        scry_temperature=64, depth=24, width=4, worlds=2, rollout_styles=4,
        fixed_inference_batch=a.fixed_inference_batch, shared_capacity=8192,
        active_transfer=a.active_transfer, prediction_cache_enabled=a.prediction_cache,
        native_prediction_cache=a.native_prediction_cache,
        asynchronous_statistics=a.async_statistics,
        capture_statistics=not a.no_statistics,
        source_sha256=sha256_file(Path(__file__)),
        implementation_sha256={name:sha256_file(Path(__file__).with_name(name)) for name in
            ('search_candidate.py', 'cached_inference.py', 'prediction_cache.cpp', 'inference_pool.py')},
        client_sha256=sha256_file(ROOT/'Tools/DepthTraining/client.py'),
        dashboard_directory=str(a.dashboard_directory.resolve()) if a.dashboard_directory else None)
    metadata.update(host_binary_sha256=manifest['host_sha256'])
    save(a.output/'card-catalog.json', metadata)
    save(a.output/'manifest.json', manifest)
    stop = False
    def stopped(*_):
        nonlocal stop
        stop = True
    signal.signal(signal.SIGTERM, stopped)
    signal.signal(signal.SIGINT, stopped)
    rows = []
    if a.resume_from:
        audit = json.loads(a.resume_runtime_audit.read_text()) if a.resume_runtime_audit else None
        original, rows, raw, trace = read_resume(a.resume_from, manifest, audit)
        (a.output/'resume').mkdir()
        save(a.output/'resume/manifest.json', original)
        save(a.output/'resume/raw.json', raw)
        if audit:save(a.output/'resume/runtime-audit.json', audit)
        (a.output/'resume/strategy-games.jsonl').write_bytes(trace)
        save(a.output/'games.json', dict(rows=rows))
        manifest.update(resumed_games=len(rows), resume_directory=str(a.resume_from.resolve()),
            resume_manifest_sha256=sha256_file(a.resume_from/'manifest.json'),
            resumed_trace_sha256=sha256_file(a.output/'resume/strategy-games.jsonl'),
            resumed_statistics_sha256=sha256_file(a.output/'resume/raw.json'))
    digest = hashlib.sha256()
    actor_roots = Counter()
    capture_lock = threading.Lock()
    game_digests = {}
    infer = Inference(policy, a.output, scry_temperature=64, fixed_batch=a.fixed_inference_batch,
        active_transfer=a.active_transfer)
    cached = None
    if a.prediction_cache:
        from cached_inference import CachedInference, NativeCachedInference
        cached = (NativeCachedInference if a.native_prediction_cache else CachedInference)(infer, audit_every=a.cache_audit_every)
    from inference_pool import InferencePool
    pool = InferencePool(cached or infer)
    last_publish = 0.
    manifest['state'] = 'running'
    save(a.output/'manifest.json', manifest)
    publisher = None
    publisher_log = None
    def close_publisher():
        nonlocal publisher
        if publisher is not None:
            publisher.terminate()
            try:publisher.wait(timeout=30)
            except subprocess.TimeoutExpired:publisher.kill();publisher.wait()
            publisher = None
            publisher_log.close()
    if not a.no_statistics and a.async_statistics:
        publisher_log = (a.output/'publisher.log').open('w')
        publisher = subprocess.Popen([sys.executable, str(Path(__file__).resolve()),
            'publisher', str(a.output), str(os.getpid())], stdout=publisher_log, stderr=subprocess.STDOUT)
    def heartbeat(_, choices):
        nonlocal last_publish
        now = time.time()
        if now-last_publish < 3:
            return
        last_publish = now
        save(a.output/'progress.json', dict(state=manifest['state'], completed_cohort_games=len(rows),
            target=a.games, updated_wall=now, elapsed_seconds=now-started,
            inference_calls=infer.calls, inference_rows=infer.rows, inference_pool=pool.diagnostics(),
            prediction_cache=cached.diagnostics() if cached else None))
        if publisher is not None:
            if publisher.poll() is not None:
                raise RuntimeError('Statistics publisher exited; see publisher.log')
        elif not a.no_statistics:
            publish(a.output, manifest, metadata)
    def capture(packet, meta, actions, changed):
        with capture_lock:
            actor_roots.update(int(seat) for seat in meta[:, 1])
            digest.update(meta.tobytes()); digest.update(actions.tobytes())
            if a.capture_digest:
                digest.update(packet.tobytes())
    try:
        with ExitStack() as stack:
            hosts = []
            lane_batch = a.batch//a.streams
            for stream in range(a.streams):
                log = stack.enter_context((a.output/f'host-{stream}.log').open('w'))
                host = stack.enter_context(client.SearchHost(batch=lane_batch, depth=24, width=4, worlds=2,
                    log=log, shared_capacity=8192, workers=6//a.streams+int(stream < 6%a.streams),
                    statistics_directory=(a.output/f'raw/forced-random/worker-{stream}') if not a.no_statistics else None))
                host.stop = lambda: stop or (a.output/'STOP').exists()
                hosts.append(host)
                save(a.output/f'host-owner-{stream}.json', dict(pid=host.process.pid,
                    startticks=int(Path(f'/proc/{host.process.pid}/stat').read_text().rsplit(')',1)[1].split()[19]),
                    boot_id=Path('/proc/sys/kernel/random/boot_id').read_text().strip()))
            for offset in range(len(rows), a.games, a.batch):
                seeds = [a.seed+offset+i*lane_batch for i in range(a.streams)]
                def recorder(seed):
                    def record(packet, meta, actions, changed):
                        capture(packet, meta, actions, changed)
                        if a.capture_digest:
                            for row, position, action in zip(packet, meta, actions):
                                h=game_digests.setdefault(seed+int(position[0]), hashlib.sha256())
                                h.update(position[1:].tobytes());h.update(row.tobytes());h.update(action.tobytes())
                    return record
                collections = pool.collect(hosts, seeds, heartbeat=heartbeat,
                    captures=[recorder(seed) for seed in seeds])
                batch_rows = [r for c in collections for r in c['report']['games']]
                save(a.output/'host-performance.json', [c['report'] for c in collections])
                validate_games(batch_rows, a.seed+offset, a.batch)
                rows.extend(batch_rows)
                save(a.output/'games.json', dict(rows=rows))
                heartbeat(0, 0)
                print(json.dumps(dict(completed=len(rows), target=a.games,
                    elapsed_wall_seconds=time.time()-started)), flush=True)
            # EOF lets the host dispose and flush the final passive snapshot.
            for host in hosts:host.process.stdin.close()
            for host in hosts:
                host.process.wait(timeout=30)
                if host.process.returncode:
                    raise RuntimeError('Statistics host failed on final flush')
        validate_games(rows, a.seed, a.games)
        close_publisher()
        if sha256_file(model_dir/'policy.pt') != manifest['policy_sha256']:
            raise ValueError('Frozen model changed')
        manifest.update(state='completed', completed_games=len(rows), frozen_weights_unchanged=True,
            completed_wall=time.time(), action_observation_digest=digest.hexdigest(),
            actor_root_counts=dict(actor_roots), numerical_parity=infer.parity_max,
            prediction_cache=cached.diagnostics() if cached else None,
            inference_pool=pool.diagnostics(),
            game_digests={str(seed): h.hexdigest() for seed,h in game_digests.items()})
        save(a.output/'manifest.json', manifest)
        if not a.no_statistics:
            publish(a.output, manifest, metadata, final=True)
        save(a.output/'progress.json', dict(state='completed', completed=len(rows), target=a.games,
            updated_wall=time.time(), elapsed_seconds=time.time()-started))
    except BaseException as error:
        close_publisher()
        manifest.update(state='stopped' if stop or isinstance(error, InterruptedError) else 'failed',
            error=str(error), completed_cohort_games=len(rows), stopped_wall=time.time())
        save(a.output/'manifest.json', manifest)
        save(a.output/'progress.json', dict(state=manifest['state'], error=str(error),
            completed_cohort_games=len(rows), target=a.games, updated_wall=time.time()))
        if not a.no_statistics:
            try: publish(a.output, manifest, metadata, force=True)
            except Exception as publication_error:
                save(a.output/'publication-error.json', dict(error=str(publication_error)))
                if (a.output/'statistics.json').exists():
                    snapshot = json.loads((a.output/'statistics.json').read_text())
                    snapshot['evaluation_progress'].update(state=manifest['state'], error=str(error))
                    snapshot['updated_wall'] = time.time()
                    save(a.output/'statistics.json', snapshot)
                    save(a.output/'balance-statistics-final.json', snapshot)
                    if a.dashboard_directory:
                        save(a.dashboard_directory/'balance-statistics-final.json', snapshot)
        raise


if __name__ == '__main__':
    if len(sys.argv) == 4 and sys.argv[1] == 'publisher':
        publisher_loop(Path(sys.argv[2]), int(sys.argv[3]))
        sys.exit(0)
    p = argparse.ArgumentParser(description=__doc__)
    for name in ('output', 'policy', 'incumbent', 'runtime'):
        p.add_argument('--'+name, type=Path, required=True)
    p.add_argument('--games', type=int, default=10000)
    p.add_argument('--batch', type=int, default=40)
    p.add_argument('--seed', type=int, default=7998392938210000000)
    p.add_argument('--no-statistics', action='store_true')
    p.add_argument('--capture-digest', action='store_true')
    p.add_argument('--active-transfer', action='store_true')
    p.add_argument('--prediction-cache', action='store_true')
    p.add_argument('--cache-audit-every', type=int, default=0)
    p.add_argument('--fixed-inference-batch', type=int, choices=(0, 512), default=512)
    p.add_argument('--resume-from', type=Path)
    p.add_argument('--dashboard-directory', type=Path)
    p.add_argument('--resume-runtime-audit', type=Path)
    p.add_argument('--streams', type=int, choices=(1, 2, 3, 4, 6), default=1)
    p.add_argument('--async-statistics', action='store_true')
    p.add_argument('--native-prediction-cache', action='store_true')
    run(p.parse_args())
