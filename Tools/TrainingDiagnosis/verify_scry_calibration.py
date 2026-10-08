"""Verify a conditional Scry correction on real teacher observations and GPU graphs."""
import argparse
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
from search_candidate import Inference,OBS,ACTIONS,FEATURES


def run(a):
    os.sched_setaffinity(0,select_cpus(os.sched_getaffinity(0),6));torch.set_num_threads(1)
    torch.backends.fp32_precision='ieee';torch.backends.cuda.matmul.fp32_precision='ieee'
    calibration=json.loads(a.calibration.read_text());policy,manifest=load_frozen_policy(a.policy,device='cuda')
    if manifest['policy_file_sha256']!=calibration['parent_policy_sha256']:raise ValueError('Calibration parent changed')
    data,_=load_data(a.teacher/'teacher-data',json.loads((a.teacher/'teacher-data.json').read_text()))
    contexts=policy.catalog['contexts'];scry=(data['obs'][:,144]>.5)&(np.rint(data['obs'][:,157]*len(contexts))==contexts.index('soi.scry'))
    indices=np.r_[np.flatnonzero(scry),np.flatnonzero(~scry)[::max(1,int((~scry).sum())//128)]]
    data=data[indices];packet=np.concatenate((data['obs'],data['candidates'].reshape(len(data),-1),data['mask']),axis=1)
    infer=Inference(policy,scry_finish_bias=calibration['correction'])
    with torch.inference_mode():
        x=torch.tensor(packet,device='cuda');candidates=x[:,OBS:OBS+ACTIONS*FEATURES].reshape(-1,ACTIONS,FEATURES)
        raw,value=policy(x[:,:OBS],candidates,x[:,-ACTIONS:]);corrected,after_value=infer.forward(x)
        eligible=(x[:,144]>.5)&((x[:,157]*len(contexts)).round()==contexts.index('soi.scry'))
        changed=eligible[:,None]&(candidates[:,:,13]>.5)&(x[:,-ACTIONS:]>0)
        if not torch.equal(value,after_value):raise RuntimeError('Scry correction changed the critic')
        if not torch.equal(raw[~changed],corrected[~changed]):raise RuntimeError('Scry correction changed another action logit')
        if not torch.equal(corrected[changed],raw[changed]+calibration['correction']):raise RuntimeError('Incorrect Scry correction')
        infer.scry_finish_bias=0
        disabled,disabled_value=infer.forward(x)
        if not torch.equal(disabled,raw) or not torch.equal(disabled_value,value):raise RuntimeError('Disabled correction changed legacy behavior')
        infer.scry_finish_bias=calibration['correction'];infer(packet,None)
    result=dict(passed=True,observations=len(data),scry_observations=int(eligible.sum()),
        unrelated_logits_bitwise_unchanged=True,critic_bitwise_unchanged=True,disabled_bitwise_unchanged=True,
        graph_parity=infer.parity_max)
    a.output.write_text(json.dumps(result,indent=2));print(json.dumps(result,indent=2))


if __name__=='__main__':
    p=argparse.ArgumentParser(description=__doc__)
    for name in ('policy','teacher','calibration','output'):p.add_argument('--'+name,type=Path,required=True)
    run(p.parse_args())
