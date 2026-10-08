"""CPU-only V8->V9: preserve learned tensors, Adam, RNG and budget.

Append public-collection input columns, preserving old weights and Adam moments.
Add saved public-readiness lookup and prior strength buffers. Learner enables
strength1.5; every retained old policy gets0 and preserves its distribution.
Publishing requires the original inactive campaign ledger.
"""
import copy
from unittest.mock import patch
import migrate_runtime_v5 as state_validator
import json
from pathlib import Path
import time
import torch
import campaign_state as persistence
from migrate_checkpoint import _inactive_ledger, _publish_new, _tree_digest, canonical_sha256
from migrate_runtime_v5 import _validate_state
from train_campaign import TrainConfig
from choice_policy_v9 import ChoicePolicy, READINESS_STRENGTH
from choice_policy_v8 import ChoicePolicy as OldChoicePolicy
from learning_model import LearningPolicy, PolicyConfig
import variant_v8_runtime as v6
import variant_v9_runtime as v7


PADDED = {"core.information.weight": 512, "core.decision_head.weight": 640}

def expanded_state(old_state, template):
    result={key:value.clone() for key,value in template.items()}
    for key,value in old_state.items():
        if key in PADDED:
            result[key].zero_(); result[key][:,:PADDED[key]].copy_(value)
        else:result[key]=value.clone()
    return result

def policies(state):
    return [state['learner']['policy'],state['initial_policy'],state['champion']['policy'],
            *[row['policy'] for row in state['archive']]]


def prepare(source_checkpoint, *, learner_exploration=True):
    if torch.cuda.is_initialized() or v7._installed:
        raise RuntimeError('Migration requires fresh CPU-only process before installing V9')
    source_checkpoint=Path(source_checkpoint).resolve(strict=True)
    source=json.loads((source_checkpoint.parent/'identity.json').read_text())
    config=TrainConfig(**source['configuration'])
    current,catalog=v6.variant_identity(config)
    if source!=current:raise RuntimeError('Source is not exact frozen V8')
    target,new_catalog=v7.variant_identity(config)
    if new_catalog['cards']!=catalog['cards'] or target['rules_sha256']!=source['rules_sha256']:
        raise RuntimeError('V8 must retain catalog and engine rules')
    with source_checkpoint.open('rb') as handle:
        payload=persistence.load_checkpoint(f'/proc/self/fd/{handle.fileno()}',expected_identity=source)
    with patch.object(state_validator,'LearningPolicy',OldChoicePolicy):
        _validate_state(payload['state'],source)
    if 'readiness_prior_migration' in payload:raise RuntimeError('Already migrated')
    payload.pop('file_sha256')
    before=copy.deepcopy(payload)
    state=payload['state']
    with torch.random.fork_rng(devices=[]):
        old=OldChoicePolicy(PolicyConfig(**state['policy_config']))
        new=ChoicePolicy(PolicyConfig(**state['policy_config']))
    old_names=list(dict(old.named_parameters()))
    new_names=list(dict(new.named_parameters()))
    added=[]
    if new_names!=old_names+added:raise RuntimeError('Optimizer parameter ordering changed')
    new_state=new.state_dict()
    extras=set(new_state)-set(old.state_dict())
    if extras!={'readiness_index','readiness_strength'}:raise RuntimeError('Unexpected new policy state')
    original_digest=_tree_digest(state)
    for index,policy in enumerate(policies(state)):
        expanded=expanded_state(policy,new_state)
        policy.clear();policy.update(expanded)
        policy['readiness_strength'].fill_(READINESS_STRENGTH if index==0 else 0.)
        new.load_state_dict(policy,strict=True)
    optimizer=state['learner']['optimizer']
    optimizer['param_groups'][0]['params']=list(range(len(new_names)))
    for key,columns in PADDED.items():
        moments=optimizer['state'][old_names.index(key)]
        for moment in ('exp_avg','exp_avg_sq'):
            padded=torch.zeros_like(new_state[key]);padded[:,:columns].copy_(moments[moment]);moments[moment]=padded
    for index,key in enumerate(added,len(old_names)):
        optimizer['state'][index]={'step':torch.zeros_like(optimizer['state'][0]['step']),
            'exp_avg':torch.zeros_like(new_state[key]),'exp_avg_sq':torch.zeros_like(new_state[key])}
    normalized=copy.deepcopy(state)
    for policy in policies(normalized):
        for key in extras:policy.pop(key)
        for key,columns in PADDED.items():policy[key]=policy[key][:,:columns].contiguous()
    for key,columns in PADDED.items():
        moments=normalized['learner']['optimizer']['state'][old_names.index(key)]
        for moment in ('exp_avg','exp_avg_sq'):moments[moment]=moments[moment][:,:columns].contiguous()
    normalized['learner']['optimizer']['param_groups'][0]['params']=list(range(len(old_names)))
    for index in range(len(old_names),len(new_names)):normalized['learner']['optimizer']['state'].pop(index)
    if _tree_digest(normalized)!=original_digest:raise RuntimeError('An existing tensor or continuation counter changed')
    payload['identity']=target;payload['identity_sha256']=canonical_sha256(target)
    report={'schema':'shards-v8-v9-public-deck-migration-v1','prepared_wall':time.time(),
        'source_identity':source,'target_identity':target,'generation':state['generations'],
        'policies_migrated':len(policies(state)),'new_parameters':added,'zero_padded_columns':PADDED,'learner_soft_prior':READINESS_STRENGTH,'old_policy_soft_prior':0.,
        'old_parameters_and_optimizer_verified_unchanged':True,
        'all_existing_exploration_buffers_preserved':True,
        'preserved_rng_sha256':_tree_digest(payload['rng']),
        'preserved_budget_sha256':_tree_digest(payload['budget']),
        'target_state_sha256':_tree_digest(state),'training_started':False}
    if _tree_digest(payload['rng'])!=_tree_digest(before['rng']) or _tree_digest(payload['budget'])!=_tree_digest(before['budget']):
        raise RuntimeError('RNG or budget changed')
    payload['readiness_prior_migration']=copy.deepcopy(report)
    return payload,report


def publish(source,destination,ledger_path,*,learner_exploration=True):
    destination=Path(destination).absolute()
    if destination.exists():raise FileExistsError(destination)
    with _inactive_ledger(ledger_path) as (ledger,ledger_sha):
        payload,report=prepare(source,learner_exploration=learner_exploration)
        saved=payload['budget']
        if saved['campaign_id']!=ledger['campaign_id'] or saved['limit_seconds']!=ledger['limit_seconds']:
            raise RuntimeError('Different campaign budget')
        if saved['charged_seconds']>ledger['charged_seconds']+1.e-6:raise RuntimeError('Checkpoint budget ahead of ledger')
        payload['readiness_prior_migration']['ledger_sha256_at_migration']=ledger_sha
        _publish_new(destination,payload,payload['identity'])
        loaded=persistence.load_checkpoint(destination,expected_identity=payload['identity'])
        if _tree_digest(loaded['state'])!=report['target_state_sha256']:raise RuntimeError('Published state mismatch')
        identity_path=destination.parent/'identity.json'
        if identity_path.exists() and json.loads(identity_path.read_text())!=payload['identity']:raise RuntimeError('Identity conflict')
        persistence._atomic_write(identity_path,json.dumps(payload['identity'],indent=2).encode())
        return dict(report,checkpoint=str(destination),strict_reload_passed=True,ledger_sha256_at_migration=ledger_sha)
