"""Check exact context isolation and actor/evaluator parity on real game states."""
import argparse
import json
import os
from pathlib import Path
import numpy as np
import torch
from search_candidate import Inference,load_frozen_policy,select_cpus
from fit_teacher import load_data
from scry_policy import ScryPolicy
from search_parity import metrics,acceptable


@torch.inference_mode()
def run(a):
    os.sched_setaffinity(0,select_cpus(os.sched_getaffinity(0),6));torch.set_num_threads(1)
    torch.backends.fp32_precision='ieee';torch.backends.cuda.matmul.fp32_precision='ieee'
    source=json.loads((a.teacher/'teacher-data.json').read_text())
    if not source['passed'] or not source['full_state_hash_parity']:raise ValueError('Unverified observations')
    data,_=load_data(a.teacher/'teacher-data',source)
    model,manifest=load_frozen_policy(a.policy,device='cuda');model.cache_frozen_table()
    contexts=model.catalog['contexts'];scry=(data['obs'][:,144]>.5)&(np.rint(data['obs'][:,157]*len(contexts))==contexts.index('soi.scry'))
    indices=np.r_[np.flatnonzero(scry),np.flatnonzero(~scry)[::max(1,int((~scry).sum())//128)]]
    data=data[indices];scry=scry[indices]
    obs,candidates,mask=[torch.tensor(np.array(data[k]),device='cuda') for k in ('obs','candidates','mask')]
    original,value=model(obs,candidates,mask)
    wrapper=ScryPolicy(model,temperature=a.temperature);changed,changed_value=wrapper(obs,candidates,mask)
    assert torch.equal(original[~scry],changed[~scry]) and torch.equal(value,changed_value)
    assert torch.equal(original[~mask.bool()],changed[~mask.bool()])
    packet=np.concatenate((np.array(data['obs']),np.array(data['candidates']).reshape(len(data),-1),np.array(data['mask'])),1)
    evaluator=Inference(model,scry_temperature=a.temperature);z,v=evaluator(packet,None)
    actor=Inference(wrapper);az,av=actor(packet,None)
    first=metrics(torch.cat((changed,changed_value[:,None]),1).cpu().numpy(),np.c_[z,v],packet[:,-64:])
    second=metrics(np.c_[z,v],np.c_[az,av],packet[:,-64:])
    if not acceptable(first) or not acceptable(second):raise ValueError('Calibrated behavior parity failed')
    result=dict(passed=True,observations=len(data),scry_observations=int(scry.sum()),other_context_logits_bitwise_identical=True,
        critic_bitwise_identical=True,illegal_logits_bitwise_identical=True,temperature=a.temperature,
        evaluator_parity=first,actor_evaluator_parity=second,policy_sha256=manifest['policy_file_sha256'],
        new_learned_parameters=0,strength_proven=False)
    a.output.write_text(json.dumps(result,indent=2));print(json.dumps(result,indent=2))


if __name__=='__main__':
    p=argparse.ArgumentParser(description=__doc__)
    for name in ('teacher','policy','output'):p.add_argument('--'+name,type=Path,required=True)
    p.add_argument('--temperature',type=float,default=64.);run(p.parse_args())
