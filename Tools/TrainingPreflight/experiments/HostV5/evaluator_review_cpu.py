import json
from unittest.mock import patch
import numpy as np
import torch
import evaluate_variants as evaluation
from test_evaluate_variants import PrefixHost

records=[]
class Host(PrefixHost):
    def __init__(self,batch,workers,seed,**kwargs):
        self.ages=np.zeros(batch,np.int32)
        self.ended=np.zeros(batch,bool)
        self.rewards=np.zeros((batch,2),np.float32)
        self.metrics=np.zeros(8,np.float64)
        super().__init__(batch,workers,seed,**kwargs)
    def show(self):
        super().show()
        if self.phase>=2:
            self.mask.fill(0);self.mask[:,:2]=1
            self.candidates.fill(0);self.candidates[:,0,0]=1;self.candidates[:,1,10]=1
    def advance_active(self,actions,active):
        self.metrics[2]+=int(active.sum());self.metrics[3]+=int(active.sum())
        if self.phase<2:
            return super().advance_active(actions,active)
        assert not (active & self.ended).any()
        self.done.fill(0);self.rewards.fill(0)
        self.ages[active]+=1
        seeds=self.seed+np.arange(self.batch)
        end=active & (self.ages>=1+seeds%2)
        caps=end & (seeds%17==0)
        terminal=end & ~caps
        self.done[caps]=2;self.done[terminal]=1
        for lane in np.flatnonzero(terminal):
            winner=0 if actions[lane]==1 else 1
            self.rewards[lane,winner]=1;self.rewards[lane,1-winner]=-1
        self.ended[end]=True
    def close(self):
        if not self.closed:
            records.append({'seed':self.seed,'batch':self.batch,'heroes':self.heroes[0].tolist(),
                            'all_ended':bool(self.ended.all()),'prefixes':len(self.trace)})
        super().close()
class Actor:
    def __init__(self,policy,batch,**kwargs):self.policy=policy;self.batch=batch
    def act(self,host):return np.full(self.batch,1 if self.policy=='a' else 0,np.int64),None

torch.set_num_threads(1)
with patch.object(torch.cuda,'_lazy_init',side_effect=AssertionError('CUDA forbidden')),patch.object(evaluation.learning_model,'LearningActor',Actor):
    result=evaluation.run_balanced_cuda('a','b',pairs_per_cell=100,seed=100,batch=64,host_factory=Host,max_seconds=30)
assert result['complete'] and result['games']==4000
expected_censored=2*sum(seed%17==0 for seed in range(100,2100))
assert result['wins_a']==4000-expected_censored and result['losses_a']==0 and result['draws']==0
assert result['censored_games']==expected_censored and result['score_a'] is None
assert len(records)==80 and all(r['all_ended'] and r['prefixes']==2 for r in records)
for i,cell in enumerate(result['plan']['cells']):
    a=evaluation.HEROES.index(cell['policy_a_hero'])+1;b=evaluation.HEROES.index(cell['policy_b_hero'])+1
    group=records[4*i:4*i+4]
    assert [r['batch'] for r in group]==[64,64,36,36]
    assert [r['seed'] for r in group]==[cell['seed_start'],cell['seed_start'],cell['seed_start']+64,cell['seed_start']+64]
    assert [r['heroes'] for r in group]==[[a,b],[b,a],[a,b],[b,a]]
assert not torch.cuda.is_initialized()
print(json.dumps({'passed':True,'scope':'Actual frozen production evaluate_match loop, fake CPU actors/hosts only; no real games/GPU',
                  'planned_games':result['games'],'unique_paired_seeds':2000,'host_launches':len(records),
                  'wins_a':result['wins_a'],'censored_games':result['censored_games'],
                  'score_a':result['score_a'],'pairing_and_partial_batch_parity':True,'cuda_initialized':False},indent=2))
