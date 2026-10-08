"""Seat-swapped frozen strength check on the corrected engine, no learning."""
import argparse
import json
import os
from pathlib import Path
import sys

p=argparse.ArgumentParser();p.add_argument('--runtime',type=Path,required=True)
p.add_argument('--a',type=Path,required=True);p.add_argument('--b',type=Path,required=True)
p.add_argument('--games',type=int,default=8192);p.add_argument('--rez-only',action='store_true')
p.add_argument('--original-baseline',action='store_true')
p.add_argument('--output',type=Path,required=True);a=p.parse_args()
sys.path.insert(0,str(a.runtime/'Tools/TrainingPreflight'))
import rez_runtime as runtime
runtime.install()
if a.rez_only:os.environ['SHARDS_REZ_TRAINING']='1'
else:os.environ.pop('SHARDS_REZ_TRAINING',None)
import evaluate_checkpoints as e
import learning_model
e.identity=runtime.variant_identity;e.LearningPolicy=learning_model.LearningPolicy
if a.original_baseline:
    import hashlib
    import torch
    from choice_policy_v11 import ChoicePolicy as OriginalPolicy
    from learning_model import PolicyConfig
    from learning_eval import evaluate_match
    from bench_common import save_json
    selected=e.load_selection(a.a,'learner')
    expected=selected.metadata['run_identity']['rez_repair']['source_sha256']
    if a.b.resolve()!=runtime.SOURCE or hashlib.sha256(a.b.read_bytes()).hexdigest()!=expected:
        raise RuntimeError('Original baseline does not match the pinned migration source')
    saved=json.loads((a.b.parent/'identity.json').read_text())
    payload=runtime.persistence.load_checkpoint(a.b,expected_identity=saved)
    torch.set_num_threads(1);torch.set_num_interop_threads(1)
    torch.backends.fp32_precision='ieee';torch.backends.cuda.matmul.fp32_precision='tf32';torch.manual_seed(927771)
    new=e.materialize_policy(selected,'cuda')
    old=OriginalPolicy(PolicyConfig(**payload['state']['policy_config']))
    old.load_state_dict(payload['state']['learner']['policy']);old=old.eval().requires_grad_(False).cuda()
    before=[e.policy_hash(new),e.policy_hash(old)]
    result=evaluate_match(new,old,games=a.games,seed=0x7d10000000000000,batch=256,workers=8,censor_truncated=True)
    if before!=[e.policy_hash(new),e.policy_hash(old)]:raise RuntimeError('Frozen comparison changed weights')
    result.update(new_checkpoint=str(a.a),baseline_checkpoint=str(a.b),policy_hashes=before,
        rez_only=a.rez_only,both_use_corrected_memory=True,training=False)
    save_json(a.output,result);print(json.dumps(result));raise SystemExit(0 if result['complete'] else 1)
sys.argv=[sys.argv[0],'--a',str(a.a),'--b',str(a.b),'--games',str(a.games),
          '--batch','256','--workers','8','--seed','0x7d00000000000000','--output',str(a.output)]
raise SystemExit(e.main())
