"""Frozen CPU balanced continuation screen; zero-prior migrated source comparator."""
import sys,os,argparse,copy,json
from pathlib import Path
sys.path.insert(0,str(Path(__file__).resolve().parents[1]))
import cpu_shadow_eval as shadow
import torch
from hero_curriculum.balanced_evaluation import run_balanced_matrix
from wire_v9 import LearningHost
from bench_common import save_json
p=argparse.ArgumentParser();p.add_argument('--a',type=Path,required=True);p.add_argument('--b',type=Path,required=True);p.add_argument('--output',type=Path,required=True);args=p.parse_args()
shadow.configure_cpu();shadow.configure_variant('v9')
a,b=shadow.frozen_selections(args.a,'learner',args.b,'learner')
pa=shadow.checkpoints.materialize_policy(a,'cpu');pb=shadow.checkpoints.materialize_policy(b,'cpu');pb.readiness_strength.zero_()
before=[shadow.checkpoints.policy_hash(x) for x in (pa,pb)]
r=run_balanced_matrix(pa,pb,pairs_per_cell=16,seed=0x8900000000000000,sampling_seed=890927,batch=16,max_seconds=650,host_factory=LearningHost)
r.update(source_a=a.metadata,source_b=b.metadata,comparator='Same original V8 weights migrated to V9 with zero readiness prior',frozen_weights_unchanged=before==[shadow.checkpoints.policy_hash(x) for x in (pa,pb)],cuda_initialized=torch.cuda.is_initialized())
r['passes_predeclared_screen']=bool(r.get('complete') and r.get('censored_games')==0 and r.get('score_a',0)>=.45)
save_json(args.output,r);print(json.dumps({k:r.get(k) for k in ('complete','games','score_a','score_bound_95','censored_games','passes_predeclared_screen','seconds')}))
