"""Compare old/new Rez against identical frozen old opponents and paired seeds."""
import argparse
import hashlib
import json
import math
import os
from pathlib import Path
import sys
import time

p=argparse.ArgumentParser();p.add_argument('--runtime',type=Path,required=True)
p.add_argument('--checkpoint',type=Path,required=True);p.add_argument('--games',type=int,default=4096)
p.add_argument('--output',type=Path,required=True);a=p.parse_args()
assert a.games%256==0
sys.path.insert(0,str(a.runtime/'Tools/TrainingPreflight'))
import rez_runtime as runtime
runtime.install();os.environ['SHARDS_REZ_TRAINING']='1'
import numpy as np
import torch
import evaluate_checkpoints as e
import learning_model
from choice_policy_v11 import ChoicePolicy as OriginalPolicy
from wire_v10 import LearningHost
from bench_common import save_json

e.identity=runtime.variant_identity;e.LearningPolicy=learning_model.LearningPolicy
selection=e.load_selection(a.checkpoint,'learner')
assert hashlib.sha256(runtime.SOURCE.read_bytes()).hexdigest()==selection.metadata['run_identity']['rez_repair']['source_sha256']
identity=json.loads((runtime.SOURCE.parent/'identity.json').read_text())
payload=runtime.persistence.load_checkpoint(runtime.SOURCE,expected_identity=identity)
torch.set_num_threads(1);torch.set_num_interop_threads(1)
torch.backends.fp32_precision='ieee';torch.backends.cuda.matmul.fp32_precision='tf32'
new=e.materialize_policy(selection,'cuda')
old=OriginalPolicy(learning_model.PolicyConfig(**payload['state']['policy_config']))
old.load_state_dict(payload['state']['learner']['policy']);old=old.eval().requires_grad_(False).cuda()
hashes=[e.policy_hash(new),e.policy_hash(old)]
actors=[learning_model.LearningActor(policy,256,graph=True) for policy in (old,new)]
scores={name:np.zeros(a.games) for name in ('old','new')};started=time.monotonic()
for offset in range(0,a.games,256):
    for name,candidate in zip(('old','new'),actors):
        torch.manual_seed(123700+offset)
        host=LearningHost(256,8,seed=0x7d20000000000000+offset,pinned=True,transport='shared',split_branches=8)
        try:
            assert np.all(host.seats==0)
            rez=np.where(host.obs[:,1663]==1,0,1)
            assert (rez==0).sum()==128
            active=np.ones(256,dtype=bool)
            for step in range(30000):
                other_actions,_=actors[0].act(host);rez_actions,_=candidate.act(host)
                actions=np.where(host.seats==rez,rez_actions,other_actions)
                host.advance_active(actions,active)
                if np.any(host.done==2):raise RuntimeError('Censored fixed-opponent game')
                ended=active&(host.done==1);lanes=np.flatnonzero(ended)
                scores[name][offset+lanes]=(host.rewards[lanes,rez[lanes]]+1)/2
                active[ended]=False
                if not active.any():break
            else:raise RuntimeError('Fixed-opponent game exceeded safety bound')
        finally:host.close()
    print(json.dumps(dict(completed_per_model=offset+256,old_score=float(scores['old'][:offset+256].mean()),new_score=float(scores['new'][:offset+256].mean()))),flush=True)
assert hashes==[e.policy_hash(new),e.policy_hash(old)]
difference=scores['new']-scores['old'];margin=math.sqrt(2*math.log(40)/a.games)
report=dict(games_per_model=a.games,total_games=a.games*2,training=False,seconds=time.monotonic()-started,
    old_rez_score=float(scores['old'].mean()),new_rez_score=float(scores['new'].mean()),
    paired_score_change=float(difference.mean()),conservative_paired_change_bound_95=[float(difference.mean()-margin),float(difference.mean()+margin)],
    opponents='Identical pre-repair frozen policy in both cohorts; four opponents and both seats balanced',
    environment='Both policies use the repaired memory ledger and identical patched rules',
    native_policy_hashes=hashes,checkpoint=str(a.checkpoint),baseline=str(runtime.SOURCE))
save_json(a.output,report);print(json.dumps(report),flush=True)
