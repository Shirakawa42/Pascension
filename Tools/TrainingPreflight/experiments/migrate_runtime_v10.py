"""CPU-only V9→V10: shared semantic features, old weights/Adam/RNG preserved.

New residual outputs start at zero. Explicit changes outside model dimensions:
disable duplicate in-session evaluations and import authorized overnight ceiling.
"""
import copy
from dataclasses import asdict,replace
import json
from pathlib import Path
import time
from unittest.mock import patch
import torch
import campaign_state as persistence
import migrate_runtime_v5 as validator
from migrate_runtime_v5 import _validate_state
from migrate_checkpoint import _inactive_ledger,_publish_new,_tree_digest,canonical_sha256
from train_campaign import TrainConfig
from learning_model import PolicyConfig
from choice_policy_v9 import ChoicePolicy as OldPolicy
from choice_policy_v10 import ChoicePolicy
import variant_v9_runtime as old_runtime
import variant_v10_runtime as runtime
from extended_budget_v10 import authorization,install as install_budget

PADDED={'core.information.weight':768,'core.decision_head.weight':896}
ADDED=['core.effect_encoder.0.weight','core.effect_encoder.2.weight','core.effect_state.weight']

def policies(state):
    return [state['learner']['policy'],state['initial_policy'],state['champion']['policy'],*[r['policy'] for r in state['archive']]]

def expanded_state(old,template):
    result={k:v.clone() for k,v in template.items()}
    for k,v in old.items():
        if k in PADDED:
            result[k].zero_();result[k][:,:PADDED[k]].copy_(v)
        else:result[k]=v.clone()
    return result

def prepare(source_checkpoint):
    if torch.cuda.is_initialized() or runtime._installed:raise RuntimeError('Use a fresh CPU-only migration process')
    source_checkpoint=Path(source_checkpoint).resolve(strict=True)
    source=json.loads((source_checkpoint.parent/'identity.json').read_text())
    config=TrainConfig(**source['configuration']);current,catalog=old_runtime.variant_identity(config)
    if source!=current:raise RuntimeError('Source is not exact frozenV9')
    target_config=replace(config,eval_seconds=0)
    target,new_catalog=runtime.variant_identity(target_config)
    if new_catalog['cards']!=catalog['cards'] or target.get('engine_correction_v10')!='Explicit end-turn banish takes precedence over temporary fast-play return; no balance tuning':
        raise RuntimeError('Unexpected card ordering or unreviewed engine correction')
    with source_checkpoint.open('rb') as f:
        payload=persistence.load_checkpoint('/proc/self/fd/'+str(f.fileno()),expected_identity=source)
    # V9 added an integer readiness lookup buffer. The older validator assumes
    # every saved tensor is FP32; validate exact native shapes/dtypes first,
    # then give that validator a disposable floating-buffer view only.
    with torch.random.fork_rng(devices=[]):
        expected = OldPolicy(PolicyConfig(**payload['state']['policy_config'])).state_dict()
    validation_state = copy.deepcopy(payload['state'])
    for original, view in zip(policies(payload['state']), policies(validation_state)):
        if original.keys() != expected.keys():raise RuntimeError('Source policy keys differ')
        for key, tensor in original.items():
            reference=expected[key]
            if tensor.shape != reference.shape or tensor.dtype != reference.dtype or not torch.isfinite(tensor).all():
                raise RuntimeError('Source policy shape/dtype/value mismatch: '+key)
            if not tensor.is_floating_point():view[key]=tensor.float()
    with patch.object(validator,'LearningPolicy',OldPolicy):_validate_state(validation_state,source)
    if 'generic_effect_migration' in payload:raise RuntimeError('Already migrated')
    payload.pop('file_sha256');before=copy.deepcopy(payload);state=payload['state']
    with torch.random.fork_rng(devices=[]):
        torch.manual_seed(100927)
        old=OldPolicy(PolicyConfig(**state['policy_config']));new=ChoicePolicy(old.config)
    old_names=list(dict(old.named_parameters()));new_names=list(dict(new.named_parameters()))
    if new_names!=old_names+ADDED:raise RuntimeError('Optimizer parameter order changed')
    template=new.state_dict();extras=set(template)-set(old.state_dict())
    if extras!=set(ADDED)|{'card_effects'}:raise RuntimeError('Unexpected new state')
    original_digest=_tree_digest(state)
    for policy in policies(state):
        expanded=expanded_state(policy,template);policy.clear();policy.update(expanded)
        new.load_state_dict(policy,strict=True)
    optimizer=state['learner']['optimizer'];optimizer['param_groups'][0]['params']=list(range(len(new_names)))
    for k,columns in PADDED.items():
        moments=optimizer['state'][old_names.index(k)]
        for m in ('exp_avg','exp_avg_sq'):
            padded=torch.zeros_like(template[k]);padded[:,:columns].copy_(moments[m]);moments[m]=padded
    for i,k in enumerate(ADDED,len(old_names)):
        optimizer['state'][i]={'step':torch.zeros_like(optimizer['state'][0]['step']),
            'exp_avg':torch.zeros_like(template[k]),'exp_avg_sq':torch.zeros_like(template[k])}
    state['configuration']=asdict(target_config)
    normalized=copy.deepcopy(state)
    for policy in policies(normalized):
        for k in extras:policy.pop(k)
        for k,columns in PADDED.items():policy[k]=policy[k][:,:columns].contiguous()
    opt=normalized['learner']['optimizer'];opt['param_groups'][0]['params']=list(range(len(old_names)))
    for k,columns in PADDED.items():
        for m in ('exp_avg','exp_avg_sq'):opt['state'][old_names.index(k)][m]=opt['state'][old_names.index(k)][m][:,:columns].contiguous()
    for i in range(len(old_names),len(new_names)):opt['state'].pop(i)
    normalized['configuration']=source['configuration']
    if _tree_digest(normalized)!=original_digest:raise RuntimeError('Existing continuation state changed')
    receipt=authorization()
    if payload['budget']['campaign_id']!=receipt['campaign_id'] or payload['budget']['limit_seconds']!=receipt['old_limit_seconds']:
        raise RuntimeError('Source budget not covered by explicit extension')
    payload['budget']['limit_seconds']=receipt['new_limit_seconds']
    protected=copy.deepcopy(payload['budget']);protected['limit_seconds']=before['budget']['limit_seconds']
    if _tree_digest(protected)!=_tree_digest(before['budget']) or _tree_digest(payload['rng'])!=_tree_digest(before['rng']):
        raise RuntimeError('Budget counters or RNG changed')
    payload['identity']=target;payload['identity_sha256']=canonical_sha256(target)
    report=dict(schema='shards-v9-v10-generic-effects-migration-v1',prepared_wall=time.time(),source_identity=source,
        target_identity=target,generation=state['generations'],policies_migrated=len(policies(state)),
        added_parameters=ADDED,preserved_old_columns=PADDED,old_parameters_optimizer_rng_counters_preserved=True,
        all_saved_policy_priors_preserved=True,new_residual_outputs_zero=True,
        config_change={'eval_seconds':[config.eval_seconds,0],'reason':'Independent20minute externalGPUcomparisons; no duplicate in-session tests'},
        budget_extension=receipt,charged_time_preserved=payload['budget']['charged_seconds'],
        target_state_sha256=_tree_digest(state),training_started=False)
    payload['generic_effect_migration']=copy.deepcopy(report)
    return payload,report

def publish(source,destination,ledger):
    install_budget()
    with _inactive_ledger(ledger) as (budget,ledger_sha):
        payload,report=prepare(source)
        if payload['budget']['charged_seconds']>budget['charged_seconds']+1e-6:raise RuntimeError('Checkpoint budget ahead')
        destination=Path(destination).absolute();_publish_new(destination,payload,payload['identity'])
        loaded=persistence.load_checkpoint(destination,expected_identity=payload['identity'])
        if _tree_digest(loaded['state'])!=report['target_state_sha256']:raise RuntimeError('Published state mismatch')
        identity_path=destination.parent/'identity.json'
        if identity_path.exists() and json.loads(identity_path.read_text())!=payload['identity']:raise RuntimeError('Identity conflict')
        persistence._atomic_write(identity_path,json.dumps(payload['identity'],indent=2).encode())
        return dict(report,checkpoint=str(destination),strict_reload_passed=True,ledger_sha256_at_migration=ledger_sha)
