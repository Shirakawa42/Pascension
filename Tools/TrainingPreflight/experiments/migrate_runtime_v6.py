"""CPU-only V5->V6 migration. Preserve all state except twenty unused input columns.

Preview is read-only and can run while V5 trains. Publishing requires the
original inactive budget lock and a new destination. No optimizer/RNG reset.
"""
import copy
import hashlib
import json
from pathlib import Path
import time

import torch
import campaign_state as persistence
from migrate_checkpoint import _inactive_ledger, _publish_new, _tree_digest, canonical_sha256
from migrate_runtime_v5 import _validate_state
from train_campaign import TrainConfig
import variant_v5_runtime as v5
import variant_v6_runtime as v6

KEY = 'core.trunk.0.weight'


def policies(state):
    return [state['learner']['policy'], state['initial_policy'], state['champion']['policy'],
            *[entry['policy'] for entry in state['archive']]]


def prepare(source_checkpoint):
    if torch.cuda.is_initialized() or v6._installed:
        raise RuntimeError('Prepare migration in a fresh CPU-only process before installing V6')
    source_checkpoint = Path(source_checkpoint).resolve(strict=True)
    source = json.loads((source_checkpoint.parent/'identity.json').read_text())
    config = TrainConfig(**source['configuration'])
    current, old_catalog = v5.variant_identity(config)
    if source != current:
        raise RuntimeError('Source is not the exact frozen V5 identity')
    target, catalog = v6.variant_identity(config)
    if target['rules_sha256'] != source['rules_sha256'] or target['configuration'] != source['configuration']:
        raise RuntimeError('Rules/configuration changed during hero migration')
    if any(slot in v5.v4.v3.SLOTS or not (128 <= slot < 1856) or (slot-128)%192 < 189 for slot in v6.SLOTS):
        raise RuntimeError('Migration column is not proven-zero padding')
    if len(old_catalog['cards']) != 189 or catalog['cards'] != old_catalog['cards']:
        raise RuntimeError('Zero-column proof requires exact card ordering/catalog')
    with source_checkpoint.open('rb') as handle:
        payload = persistence.load_checkpoint(f'/proc/self/fd/{handle.fileno()}', expected_identity=source)
    _validate_state(payload['state'], source)
    if 'hero_fixes_migration' in payload:
        raise RuntimeError('Already migrated')
    payload.pop('file_sha256')
    before = copy.deepcopy(payload)
    state = payload['state']
    for policy in policies(state):
        policy[KEY][:, list(v6.SLOTS)] = 0
    # First trunk weight is parameter 0; verify this contract explicitly.
    from learning_model import LearningPolicy, PolicyConfig
    with torch.random.fork_rng(devices=[]):
        template = LearningPolicy(PolicyConfig(**state['policy_config']))
    if next(iter(dict(template.named_parameters()))) != KEY:
        raise RuntimeError('Unexpected optimizer parameter mapping')
    for name in ('exp_avg', 'exp_avg_sq'):
        moment = state['learner']['optimizer']['state'][0][name]
        if torch.count_nonzero(moment[:, list(v6.SLOTS)]):
            raise RuntimeError('Previously unused input columns have nonzero Adam moments')
    expected = copy.deepcopy(before)
    for policy in policies(expected['state']):
        policy[KEY][:, list(v6.SLOTS)] = 0
    if _tree_digest(payload) != _tree_digest(expected):
        raise RuntimeError('Migration changed more than declared policy columns')
    payload['identity'] = target
    payload['identity_sha256'] = canonical_sha256(target)
    report = {'schema': 'shards-v5-v6-hero-migration-v1', 'prepared_wall': time.time(),
        'source_identity': source, 'target_identity': target, 'generation': state['generations'],
        'zeroed_input_columns': list(v6.SLOTS), 'policies_migrated': len(policies(state)),
        'preserved_rng_sha256': _tree_digest(payload['rng']),
        'preserved_budget_sha256': _tree_digest(payload['budget']),
        'preserved_optimizer_sha256': _tree_digest(state['learner']['optimizer']),
        'target_state_sha256': _tree_digest(state), 'training_started': False,
        'scope': 'Zero columns preserve network function on original observations; new AI action staging is an intentional behavior change.'}
    payload['hero_fixes_migration'] = copy.deepcopy(report)
    return payload, report


def publish(source, destination, ledger_path):
    destination = Path(destination).absolute()
    if destination.exists():
        raise FileExistsError(destination)
    with _inactive_ledger(ledger_path) as (ledger, ledger_sha256):
        payload, report = prepare(source)
        saved = payload['budget']
        if saved['campaign_id'] != ledger['campaign_id'] or saved['limit_seconds'] != ledger['limit_seconds']:
            raise RuntimeError('Different campaign budget')
        if saved['charged_seconds'] > ledger['charged_seconds'] + 1e-6:
            raise RuntimeError('Checkpoint is ahead of authoritative charged time')
        payload['hero_fixes_migration']['ledger_sha256_at_migration'] = ledger_sha256
        _publish_new(destination, payload, payload['identity'])
        loaded = persistence.load_checkpoint(destination, expected_identity=payload['identity'])
        if _tree_digest(loaded['state']) != report['target_state_sha256'] or _tree_digest(loaded['rng']) != report['preserved_rng_sha256']:
            raise RuntimeError('Published state/RNG mismatch')
        identity_path = destination.parent/'identity.json'
        if identity_path.exists() and json.loads(identity_path.read_text()) != payload['identity']:
            raise RuntimeError('Destination identity conflict')
        persistence._atomic_write(identity_path, json.dumps(payload['identity'], indent=2).encode())
        return dict(report, checkpoint=str(destination), strict_reload_passed=True,
                    ledger_sha256_at_migration=ledger_sha256, cuda_initialized=torch.cuda.is_initialized())
