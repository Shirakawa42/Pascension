"""Transfer a verified checkpoint into an explicitly authorized bounded allocation.

The old authoritative budget is recovered normally, never refunded. A caller
may fund a new allocation only within its separately recorded user mandate.
No CUDA context is created and learned state/RNG are preserved byte for byte.
"""
from __future__ import annotations

import argparse
import copy
import hashlib
import json
import math
import os
from pathlib import Path
import shutil
import sys
import time

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / 'Tools/ZeroDepthTraining'))
sys.path.append(str(ROOT / 'Tools/TrainingPreflight'))


def read(path):
    path = Path(path)
    if path.is_symlink():
        raise ValueError('Metadata symlinks are not allowed')
    value = json.loads(path.read_text())
    if not isinstance(value, dict):
        raise ValueError('Metadata must be an object')
    return value


def digest(path):
    result = hashlib.sha256()
    with Path(path).open('rb') as stream:
        for block in iter(lambda: stream.read(1 << 20), b''):
            result.update(block)
    return result.hexdigest()


def durable_copy(source, destination):
    shutil.copyfile(source, destination)
    with Path(destination).open('rb') as stream:
        os.fsync(stream.fileno())
    descriptor = os.open(Path(destination).parent, os.O_RDONLY | os.O_DIRECTORY)
    try:
        os.fsync(descriptor)
    finally:
        os.close(descriptor)


def preserve_league_plan(parent, destination):
    """Keep the same immutable reference through boot recovery; never rebase it."""
    source = Path(parent) / 'league-plan.json'
    if not source.exists():
        return False
    if source.is_symlink() or source.stat().st_size > 32768:
        raise ValueError('Invalid recovered checkpoint league plan')
    target = Path(destination) / 'league-plan.json'
    if target.exists():
        raise ValueError('Cannot overwrite a recovery league plan')
    from league import validate_plan, verify_frozen_policy
    plan = validate_plan(read(source))
    if not plan.get('baseline_manifest_sha256'):
        raise ValueError('Recovered league must retain its exact frozen baseline pin')
    verify_frozen_policy(plan['baseline'], manifest_sha256=plan['baseline_manifest_sha256'])
    durable_copy(source, target)
    if digest(target) != digest(source):
        raise ValueError('Checkpoint league plan changed during recovery')
    return True


def preserve_evaluation_history(parent, destination):
    """Retain champion trials/seeds and fixed-reference history across allocations."""
    parent, destination = Path(parent), Path(destination)
    from league import read_champion
    champion = parent / 'champion'
    state = read_champion(parent) if champion.exists() else None
    sources = list((parent / 'league').glob('*.json'))
    if champion.exists():
        sources.extend(p for p in champion.rglob('*') if not p.is_dir())
        if champion.is_symlink() or any(p.is_symlink() for p in champion.rglob('*')):
            raise ValueError('Evaluation history must not contain symlinks')
    for name in ('learning-rate-improvement.json', 'curriculum-improvement.json', 'learning-intervention.json', 'review-service.json'):
        if (parent / name).exists():
            sources.append(parent / name)
    hashes = {}
    for source in sources:
        if source.is_symlink() or not source.is_file():
            raise ValueError('Evaluation history must contain only regular files')
        relative = source.relative_to(parent)
        target = destination / relative
        if target.exists():
            raise ValueError('Cannot overwrite recovered evaluation history')
        target.parent.mkdir(parents=True, exist_ok=True)
        before = digest(source)
        durable_copy(source, target)
        if digest(target) != before or digest(source) != before:
            raise ValueError('Evaluation history changed during transfer')
        hashes[str(relative)] = before
    if state is not None and read_champion(destination) != state:
        raise ValueError('Champion identity or trial accounting changed during transfer')
    return hashes


def recover(parent, destination, seconds, reason):
    if not math.isfinite(seconds) or not 1 <= seconds <= 43200:
        raise ValueError('The authorized allocation must be between 1 and 43200 seconds')
    if not reason.strip():
        raise ValueError('An explicit recovery reason is required')
    parent, destination = Path(parent).resolve(), Path(destination).resolve()
    if destination.exists() or parent == destination:
        raise ValueError('Recovery requires an unused, distinct directory')
    from campaign_state import CampaignBudget, load_checkpoint, _atomic_write, _canonical
    from evaluate import verify_bundle
    from host import catalog
    from performance_upgrade import write_payload_atomic, _same_state
    from train import TrainConfig, checkpoint_cpu_threads, identity
    import torch

    def publish(path, value):
        _atomic_write(path, _canonical(value) + b'\n')

    config = TrainConfig(**read(parent / 'config.json'))
    pinned = read(parent / 'identity.json')
    current_catalog = catalog()
    if identity(config, current_catalog) != pinned or current_catalog != read(parent / 'catalog.json'):
        raise ValueError('Prepared source, binary, configuration, or full-information catalog changed')
    original_manifest = verify_bundle(parent / 'incumbent', repo_root=ROOT)
    plan = read(parent / 'prepared.json')
    if plan.get('post_training_pairs', 0) < 1:
        raise ValueError('Post-training incumbent comparison is missing')
    prior = read(parent / 'budget.json')
    active = prior.get('active')
    current_boot = Path('/proc/sys/kernel/random/boot_id').read_text().strip()
    if active and active['boot_id'] == current_boot and Path('/proc', str(active['pid'])).exists():
        raise ValueError('The previous trainer may still be alive; cannot transfer its allocation')
    destination.mkdir(parents=True)
    evidence = destination / 'recovery-evidence'
    evidence.mkdir()
    # Retain the unmodified ledger/checkpoint before normal conservative recovery.
    for name in ['budget.json', 'latest.soicp', 'status.json', 'supervisor.json', 'deadline.json']:
        if (parent / name).exists():
            durable_copy(parent / name, evidence / ('parent-before-' + name))
    old_checkpoint_digest = digest(parent / 'latest.soicp')
    with checkpoint_cpu_threads(config.workers):
        with CampaignBudget(parent / 'budget.json', limit_seconds=prior['limit_seconds']) as old:
            payload = load_checkpoint(parent / 'latest.soicp', expected_identity=pinned, budget=old)
            parent_snapshot = old.snapshot()
            parent_recovery = old.last_recovery
            after = read(parent / 'budget.json')
        if after['active'] is not None:
            raise ValueError('Prior allocation did not close')
        if digest(parent / 'latest.soicp') != old_checkpoint_digest:
            raise ValueError('Prior checkpoint changed during recovery')
        state = payload['state']
        if state['configuration'] != pinned['configuration'] or state['catalog'] != current_catalog:
            raise ValueError('Learned checkpoint configuration/catalog is inconsistent')
        if 'performance_upgrade' in state:
            raise ValueError('Checkpoint contains an unresolved performance migration')
        if payload['boundary'] != 'episode_free':
            raise ValueError('Checkpoint contains unresolved games')
        parameters = sum(t.numel() for name, t in state['policy'].items()
                         if name not in {'effect_table', 'kind_prior', 'known_top_anchor', 'known_top_enabled'})
        for name in ['config.json', 'identity.json', 'catalog.json', 'prepared.json']:
            durable_copy(parent / name, destination / name)
        league_preserved = preserve_league_plan(parent, destination)
        evaluation_history = preserve_evaluation_history(parent, destination)
        shutil.copytree(parent / 'incumbent', destination / 'incumbent', copy_function=durable_copy)
        if verify_bundle(destination / 'incumbent', repo_root=ROOT) != original_manifest:
            raise ValueError('Frozen incumbent changed during transfer')
        with CampaignBudget(destination / 'budget.json', limit_seconds=seconds) as fresh:
            new_id = fresh.campaign_id
        fresh_ledger = read(destination / 'budget.json')
        for key in ['collection_accounting', 'discarded_episodes', 'discarded_decisions', 'discard_events']:
            fresh_ledger[key] = copy.deepcopy(after[key])
        publish(destination / 'budget.json', fresh_ledger)
        with CampaignBudget(destination / 'budget.json', limit_seconds=seconds) as fresh:
            transferred = dict(payload)
            transferred.pop('file_sha256')
            transferred['budget'] = fresh.snapshot()
            new_digest = write_payload_atomic(destination / 'latest.soicp', transferred)
            restored = load_checkpoint(destination / 'latest.soicp', expected_identity=pinned, budget=fresh)
            if not _same_state(payload['state'], restored['state']) or not _same_state(payload['rng'], restored['rng']):
                raise ValueError('Learned state or random generators changed during transfer')
            for key in payload.keys() - {'file_sha256', 'budget'}:
                if not _same_state(payload[key], restored[key]):
                    raise ValueError('Unexpected checkpoint mutation: ' + key)
        proof = {
            'schema': 'shards-bounded-watchdog-recovery-v1', 'state': 'prepared',
            'created_wall': time.time(), 'reason': reason, 'new_allocation_seconds': seconds,
            'campaign': str(destination), 'campaign_id': new_id,
            'parent_campaign': str(parent), 'parent_campaign_id': prior['campaign_id'],
            'parent_checkpoint_sha256': old_checkpoint_digest, 'initial_checkpoint_sha256': new_digest,
            'parent_budget_before_sha256': digest(evidence / 'parent-before-budget.json'),
            'parent_budget_after_sha256': digest(parent / 'budget.json'),
            'parent_budget_recovery': parent_recovery, 'parent_budget_snapshot': parent_snapshot,
            'source_fingerprint': pinned['source_fingerprint'], 'model_parameters': parameters,
            'complete_state_byte_exact': True, 'all_rng_byte_exact': True,
            'health_accounting_and_recent_censor_window_preserved': True,
            'baseline_counters': {key: state[key] for key in
                ['games', 'generations', 'decisions', 'optimizer_steps', 'attempts', 'censored',
                 'example_passes', 'next_engine_seed']},
            'post_training_pairs': plan['post_training_pairs'],
            'checkpoint_league_plan_preserved': league_preserved,
            'evaluation_history_sha256': evaluation_history,
        }
        if torch.cuda.is_initialized():
            raise RuntimeError('CPU checkpoint recovery unexpectedly initialized CUDA')
        publish(evidence / 'parent-after-budget.json', after)
        publish(destination / 'continuation.json', proof)
    return proof


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--parent', type=Path, required=True)
    parser.add_argument('--destination', type=Path, required=True)
    parser.add_argument('--seconds', type=float, required=True)
    parser.add_argument('--reason', required=True)
    args = parser.parse_args()
    print(json.dumps(recover(args.parent, args.destination, args.seconds, args.reason)), flush=True)


if __name__ == '__main__':
    main()
