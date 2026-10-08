"""Preserve the frozen1977 anchor with zero readiness prior; no learner updates."""
import sys,json,copy
from pathlib import Path
sys.path.insert(0,str(Path(__file__).resolve().parents[1]))
from migrate_runtime_v9 import prepare,policies,PADDED
from migrate_checkpoint import _inactive_ledger,_publish_new,_tree_digest
import campaign_state as persistence
from bench_common import save_json
root=Path('/home/lva/.local/share/shards-training/2026-09-26')
source=root/'migrated-v8/anchor1977.soicp';destination=root/'migrated-v9/anchor1977.soicp'
with _inactive_ledger(root/'budget.json') as (ledger,ledger_sha):
    payload,report=prepare(source)
    saved=payload['budget']
    assert saved['campaign_id']==ledger['campaign_id'] and saved['limit_seconds']==ledger['limit_seconds']
    assert saved['charged_seconds']<=ledger['charged_seconds']+1.e-6
    payload['state']['learner']['policy']['readiness_strength'].zero_()
    report.update(learner_soft_prior=0.,purpose='Frozen1977 anchor; existing policy retained without new timing intervention',target_state_sha256=_tree_digest(payload['state']),ledger_sha256_at_migration=ledger_sha)
    payload['readiness_prior_migration']=copy.deepcopy(report)
    _publish_new(destination,payload,payload['identity'])
    loaded=persistence.load_checkpoint(destination,expected_identity=payload['identity'])
    assert _tree_digest(loaded['state'])==report['target_state_sha256']
    assert all(float(p['readiness_strength'])==0 for p in policies(loaded['state']))
    report.update(strict_reload_passed=True,checkpoint=str(destination))
    save_json('Tools/TrainingPreflight/results/v9-anchor-migration.json',report)
    print(json.dumps({'generation':report['generation'],'all_policy_readiness_priors':0,'strict_reload_passed':True}))
