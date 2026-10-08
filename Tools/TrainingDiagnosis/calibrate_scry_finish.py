"""Test a Scry-only Finish-logit correction using independent teacher games."""
import argparse
import collections
import json
import os
from pathlib import Path
import sys

ROOT=Path(__file__).resolve().parents[2]
sys.path[:0]=[str(ROOT/'Tools/ZeroDepthTraining'),str(ROOT/'Tools/MatchupBenchmark')]
import numpy as np
import torch
from cpu_affinity import select_cpus
from fit_teacher import load_data
from league import load_frozen_policy


def run(a):
    os.sched_setaffinity(0,select_cpus(os.sched_getaffinity(0),6));torch.set_num_threads(1)
    torch.backends.fp32_precision='ieee';torch.backends.cuda.matmul.fp32_precision='ieee'
    source=json.loads((a.teacher/'teacher-data.json').read_text())
    if not source['passed'] or not source['full_state_hash_parity']:raise ValueError('Unverified teacher corpus')
    data,test=load_data(a.teacher/'teacher-data',source)
    model,manifest=load_frozen_policy(a.policy,device='cuda');model.cache_frozen_table()
    contexts=model.catalog['contexts'];scry=contexts.index('soi.scry')
    eligible=(data['obs'][:,144]>.5)&(np.rint(data['obs'][:,157]*len(contexts))==scry)
    data=data[eligible];test=test[eligible]
    scores=[]
    with torch.inference_mode():
        for start in range(0,len(data),128):
            batch=data[start:start+128]
            z,_=model(*[torch.tensor(np.array(batch[k]),device='cuda') for k in ('obs','candidates','mask')])
            scores.append(z.cpu().numpy())
    logits=np.concatenate(scores).astype(np.float64);finish=(data['candidates'][:,:,13]>.5)&(data['mask']>.5)
    if not np.all(finish.sum(1)==1):raise ValueError('Scry teacher row does not contain exactly one Finish')
    labels=data['action'];target=finish[np.arange(len(data)),labels].astype(float)
    odds=np.logaddexp.reduce(np.where(finish,logits,-np.inf),axis=1)-np.logaddexp.reduce(np.where(~finish,logits,-np.inf),axis=1)
    def weights(mask):
        count=collections.Counter(int(g) for g in data['game'][mask]);w=np.array([1/count[int(g)] for g in data['game'][mask]])
        return w/w.sum()
    train=~test;w=weights(train)
    def fit_bias(temperature):
        scaled=logits/temperature
        odds=np.logaddexp.reduce(np.where(finish,scaled,-np.inf),axis=1)-np.logaddexp.reduce(np.where(~finish,scaled,-np.inf),axis=1)
        lo,hi=-80.,80.
        for _ in range(80):
            middle=(lo+hi)/2;p=1/(1+np.exp(-np.clip(odds[train]+middle,-100,100)))
            if w@(p-target[train])>0:hi=middle
            else:lo=middle
        return (lo+hi)/2
    def report(mask,bias,temperature=1.):
        z=logits[mask]/temperature+bias*finish[mask];logp=z-np.logaddexp.reduce(z,axis=1)[:,None];p=np.exp(logp)
        ix=np.arange(mask.sum());action=labels[mask];ww=weights(mask);prediction=z.argmax(1)
        return dict(states=int(mask.sum()),games=len(set(data['game'][mask].tolist())),
            nll=float(ww@(-logp[ix,action])),agreement=float(ww@(prediction==action)),
            teacher_finish_fraction=float(ww@target[mask]),predicted_finish_fraction=float(ww@finish[mask][ix,prediction]),
            mean_finish_probability=float(ww@(p*finish[mask]).sum(1)))
    choices=[dict(temperature=t,correction=fit_bias(t)) for t in ((1.,2.,4.,8.,16.,32.,64.) if a.temperature_grid else (1.,))]
    for choice in choices:choice['train_nll']=report(train,choice['correction'],choice['temperature'])['nll']
    chosen=min(choices,key=lambda c:c['train_nll']);temperature=chosen['temperature'];correction=chosen['correction']
    result=dict(parent_policy_sha256=manifest['policy_file_sha256'],correction=correction,temperature=temperature,
        trained_parameters=2 if a.temperature_grid else 1,scope='Legal Scry logits only; all other contexts and critic unchanged',
        temperature_selection='Training games only; held-out games do not select temperature',temperature_candidates=choices,
        split='Whole teacher games; no adjacent decisions cross validation',
        train_before=report(train,0),train_after=report(train,correction,temperature),
        validation_before=report(test,0),validation_after=report(test,correction,temperature),
        by_selection_count={str(k):dict(before=report(test&(np.rint(data['obs'][:,150]*1000)==k),0),
            after=report(test&(np.rint(data['obs'][:,150]*1000)==k),correction,temperature))
            for k in (0,1,2) if np.any(test&(np.rint(data['obs'][:,150]*1000)==k))},
        strength_proven=False)
    result['accepted_for_strength_screen']=result['validation_after']['nll']<result['validation_before']['nll']*.8 and result['validation_after']['agreement']>result['validation_before']['agreement']
    a.output.write_text(json.dumps(result,indent=2));print(json.dumps(result,indent=2))


if __name__=='__main__':
    p=argparse.ArgumentParser(description=__doc__);p.add_argument('--teacher',type=Path,required=True)
    p.add_argument('--policy',type=Path,required=True);p.add_argument('--output',type=Path,required=True)
    p.add_argument('--temperature-grid',action='store_true');run(p.parse_args())
