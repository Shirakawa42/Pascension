"""State-exact V10→V11 code-only migration after equivalent-gradient benchmark."""
import copy,json,torch
from pathlib import Path
from migrate_checkpoint import _inactive_ledger,_publish_new,_tree_digest,canonical_sha256
from extended_budget_v10 import install as install_budget
import campaign_state as persistence
from train_campaign import TrainConfig
from learning_model import PolicyConfig
from choice_policy_v11 import ChoicePolicy
from migrate_runtime_v10 import policies
import variant_v10_runtime as old
import variant_v11_runtime as new

def publish(source,destination,ledger):
    if torch.cuda.is_initialized():raise RuntimeError('CPU migration only')
    install_budget()
    with _inactive_ledger(ledger) as (budget,ledger_sha):
        source=Path(source);identity=json.loads((source.parent/'identity.json').read_text())
        config=TrainConfig(**identity['configuration']);expected,_=old.variant_identity(config)
        if identity!=expected:raise RuntimeError('Source not exact V10')
        payload=persistence.load_checkpoint(source,expected_identity=identity)
        payload.pop('file_sha256',None)
        protected=_tree_digest({k:v for k,v in payload.items() if k not in ('identity','identity_sha256')})
        with torch.random.fork_rng(devices=[]):model=ChoicePolicy(PolicyConfig(**payload['state']['policy_config']))
        for policy in policies(payload['state']):model.load_state_dict(policy,strict=True)
        target,_=new.variant_identity(config)
        payload['identity']=target;payload['identity_sha256']=canonical_sha256(target)
        if protected!=_tree_digest({k:v for k,v in payload.items() if k not in ('identity','identity_sha256')}):
            raise RuntimeError('Protected continuation changed')
        if payload['budget']['campaign_id']!=budget['campaign_id'] or payload['budget']['charged_seconds']>budget['charged_seconds']:
            raise RuntimeError('Invalid campaign continuation')
        report=dict(schema='shards-v10-v11-equivalent-effect-lookup-v1',source=str(source),generation=payload['state']['generations'],
            all_policy_optimizer_rng_budget_and_counters_preserved=True,protected_sha256=protected,identity=target)
        payload['effect_lookup_migration']=copy.deepcopy(report)
        destination=Path(destination);_publish_new(destination,payload,target)
        persistence._atomic_write(destination.parent/'identity.json',json.dumps(target,indent=2).encode())
        loaded=persistence.load_checkpoint(destination,expected_identity=target)
        if _tree_digest(loaded['state'])!=_tree_digest(payload['state']):raise RuntimeError('Reload mismatch')
        return dict(report,checkpoint=str(destination),strict_reload_passed=True)
