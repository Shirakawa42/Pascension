"""Audited restore of accepted policy/Adam, without erasing spent training history."""
import copy,json,torch
from pathlib import Path
from dataclasses import replace,asdict
from migrate_checkpoint import _inactive_ledger,_publish_new,_tree_digest,canonical_sha256
from extended_budget_v10 import install as install_budget
import campaign_state as persistence
from train_campaign import TrainConfig
import variant_v11_runtime as old
import variant_v12_runtime as new

def publish(latest,best,destination,ledger,learning_rate=1e-4):
    if torch.cuda.is_initialized():raise RuntimeError('CPU recovery only')
    if not 0 < learning_rate < .0003:raise RuntimeError('Recovery requires a smaller explicit learning rate')
    install_budget()
    with _inactive_ledger(ledger) as (budget,ledger_sha):
        def read(path):
            path=Path(path);identity=json.loads((path.parent/'identity.json').read_text())
            if identity!=old.variant_identity(TrainConfig(**identity['configuration']))[0]:raise RuntimeError('Not frozen V11')
            return persistence.load_checkpoint(path,expected_identity=identity)
        payload=read(latest);accepted=read(best);payload.pop('file_sha256',None)
        if payload['identity']!=accepted['identity']:raise RuntimeError('Recovery policies differ in rules/configuration')
        before=copy.deepcopy(payload);state=payload['state'];a=accepted['state']
        # Restore policy-associated learning state, retaining actual campaign progress,
        # random streams and fresh engine seed counters from the latest attempt.
        for key in ('policy','optimizer'):state['learner'][key]=copy.deepcopy(a['learner'][key])
        for key in ('initial_policy','champion','archive'):state[key]=copy.deepcopy(a[key])
        config=replace(TrainConfig(**state['configuration']),learning_rate=learning_rate)
        state['configuration']=asdict(config)
        state['learner']['ppo_config']['learning_rate']=learning_rate
        for group in state['learner']['optimizer']['param_groups']:group['lr']=learning_rate
        # Verify every other counter, RNG and metadata field stayed unchanged.
        normalized=copy.deepcopy(payload)
        for key in ('initial_policy','champion','archive','configuration'):normalized['state'][key]=before['state'][key]
        for key in ('policy','optimizer','ppo_config'):normalized['state']['learner'][key]=before['state']['learner'][key]
        if _tree_digest(normalized)!=_tree_digest(before):raise RuntimeError('Unrelated continuation state changed')
        if payload['budget']['campaign_id']!=budget['campaign_id'] or payload['budget']['charged_seconds']>budget['charged_seconds']:
            raise RuntimeError('Invalid budget continuation')
        target,_=new.variant_identity(config);payload['identity']=target;payload['identity_sha256']=canonical_sha256(target)
        report=dict(schema='shards-v12-conservative-recovery-v1',failed_checkpoint=str(latest),accepted_checkpoint=str(best),
            reason='V11 pilot9388 scored42.9199% against9344, paired repeated-test upper bound46.1907%; halt and test smaller steps',
            restored_policy_generation=a['generations'],continued_counter_generation=state['generations'],learning_rate=learning_rate,
            all_spent_time_counters_and_rng_preserved=True,policy_optimizer_restored_without_time_refund=True,
            learner_weights_equal_accepted=_tree_digest(state['learner']['policy'])==_tree_digest(a['learner']['policy']))
        payload['conservative_recovery']=copy.deepcopy(report)
        destination=Path(destination);_publish_new(destination,payload,target)
        persistence._atomic_write(destination.parent/'identity.json',json.dumps(target,indent=2).encode())
        loaded=persistence.load_checkpoint(destination,expected_identity=target)
        if _tree_digest(loaded['state'])!=_tree_digest(state):raise RuntimeError('Reload mismatch')
        return dict(report,checkpoint=str(destination),strict_reload_passed=True,configuration=asdict(config))
