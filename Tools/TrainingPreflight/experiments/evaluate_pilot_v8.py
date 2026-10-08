"""Frozen V8 pre/post pilot screen, balanced heroes and paired seats."""
import argparse,time
from pathlib import Path
import cpu_shadow_eval as shadow
from hero_curriculum.balanced_evaluation import run_balanced_matrix,evaluation_plan
import hero_coverage_eval
import torch
from bench_common import save_json
p=argparse.ArgumentParser();p.add_argument('--a',type=Path,required=True);p.add_argument('--b',type=Path,required=True);p.add_argument('--output',type=Path,required=True);a=p.parse_args()
shadow.configure_cpu();shadow.configure_variant('v8')
from variant_v8_runtime import LearningHost
hero_coverage_eval.LearningHost=LearningHost
sa,sb=shadow.frozen_selections(a.a,'learner',a.b,'learner')
pa,pb=[shadow.checkpoints.materialize_policy(s,'cpu') for s in (sa,sb)]
result=run_balanced_matrix(pa,pb,pairs_per_cell=16,seed=0x8300000000000000,sampling_seed=830926,batch=16,max_seconds=650,host_factory=LearningHost)
result.update(a=sa.metadata,b=sb.metadata,cuda_initialized=torch.cuda.is_initialized(),scope='Pilot regression screen on sharedV8adapter, not proof of optimal play or superiority')
save_json(a.output,result);print({k:result.get(k) for k in ('complete','games','score_a','score_bound_95','censored_games')})
