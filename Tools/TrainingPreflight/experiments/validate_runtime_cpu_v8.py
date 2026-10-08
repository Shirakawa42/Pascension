"""Read-only migration plus real complete CPU episodes; never updates a policy."""
import os,sys,json,time
from pathlib import Path
os.environ['CUDA_VISIBLE_DEVICES']='';os.environ['OMP_NUM_THREADS']='1'
sys.path.insert(0,str(Path(__file__).resolve().parents[1]))
import torch
from unittest.mock import patch
from migrate_runtime_v8 import prepare
import variant_v8_runtime as runtime
import learning_eval,learning_model
from choice_policy_v8 import ChoicePolicy
from learning_model import PolicyConfig
from bench_common import save_json

def main():
    torch.set_num_threads(1)
    payload,migration=prepare('/home/lva/.local/share/shards-training/2026-09-26/strategy-audit-v7/latest.soicp')
    runtime.install()
    p=ChoicePolicy(PolicyConfig(**payload['state']['policy_config']));p.load_state_dict(payload['state']['learner']['policy']);p.eval()
    actor=learning_model.LearningActor;host=learning_eval.LearningHost
    def cpu_actor(policy,batch,**kw):kw.update(graph=False,device='cpu');return actor(policy,batch,**kw)
    def cpu_host(*a,**kw):kw['pinned']=False;return host(*a,**kw)
    with patch.object(learning_model,'LearningActor',cpu_actor),patch.object(learning_eval,'LearningHost',cpu_host):
        result=learning_eval.evaluate_match(p,p,games=64,batch=32,workers=1,seed=0x8200000000000000,censor_truncated=True)
    assert result['complete'] and result['censored_games']==0
    report={'passed':True,'cuda_initialized':torch.cuda.is_initialized(),'migration':migration,'selfplay':result}
    save_json('Tools/TrainingPreflight/results/v8-cpu-integration.json',report)
    print(json.dumps(result))
if __name__=='__main__':main()
