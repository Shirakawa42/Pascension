"""Measure whether optional-action exploration gives collapsed Scry actions gradient."""
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
from model import optional_menu_logits


def describe(x):
    x=np.asarray(x);return dict(mean=float(x.mean()),median=float(np.median(x)),minimum=float(x.min()),maximum=float(x.max()))


def run(a):
    os.sched_setaffinity(0,select_cpus(os.sched_getaffinity(0),6));torch.set_num_threads(1)
    torch.backends.fp32_precision='ieee';torch.backends.cuda.matmul.fp32_precision='ieee'
    source=json.loads((a.teacher/'teacher-data.json').read_text())
    if not source['passed'] or not source['full_state_hash_parity']:raise ValueError('Unverified teacher data')
    data,_=load_data(a.teacher/'teacher-data',source)
    model,manifest=load_frozen_policy(a.policy,device='cuda');model.cache_frozen_table()
    if float(model.optional_exploration)!=0:raise ValueError('Probe requires an unmodified base distribution')
    contexts=model.catalog['contexts'];scry=contexts.index('soi.scry')
    keep=(data['obs'][:,144]>.5)&(np.rint(data['obs'][:,157]*len(contexts))==scry)&(data['mask'].sum(1)>1)
    data=data[keep];scores=[]
    with torch.inference_mode():
        for start in range(0,len(data),128):
            batch=data[start:start+128]
            z,_=model(*[torch.tensor(np.array(batch[k]),device='cuda') for k in ('obs','candidates','mask')]);scores.append(z.cpu().numpy())
    z=torch.tensor(np.concatenate(scores),dtype=torch.float64,requires_grad=True)
    candidates=torch.tensor(np.array(data['candidates']),dtype=torch.float64);mask=torch.tensor(np.array(data['mask']),dtype=torch.float64)
    finish=(candidates[:,:,13]>.5)&mask.bool()
    if not bool((finish.sum(1)==1).all()):raise ValueError('Scry Finish is not uniquely legal')
    p=z.softmax(-1);mixed=optional_menu_logits(z,candidates,mask,torch.tensor(.05,dtype=torch.float64))
    selected=mixed.log_softmax(-1)[finish];gradient=torch.autograd.grad(selected.sum(),z)[0][finish]
    baseline=p[finish].detach().numpy();mixed_prob=selected.detach().exp().numpy()
    expected=.95*baseline*(1-baseline)/mixed_prob
    np.testing.assert_allclose(gradient.detach().numpy(),expected,rtol=1e-6,atol=1e-14)
    corrected=(z.detach()+a.bias*finish).softmax(-1)[finish].numpy()
    result=dict(policy_sha256=manifest['policy_file_sha256'],rows=len(data),optional_exploration_buffer=0.,
        baseline_finish_probability=describe(baseline),uniform_mixture_finish_probability=describe(mixed_prob),
        uniform_mixture_log_probability_gradient=describe(gradient.detach().numpy()),
        mixture_gradient_below_one_millionth_fraction=float((gradient.detach().numpy()<1e-6).mean()),
        comparison_finish_logit_bias=a.bias,corrected_finish_probability=describe(corrected),
        corrected_log_probability_gradient=describe(1-corrected),analytic_gradient_matches_autograd=True,
        scope='Gradient with respect to the selected raw Finish logit, conditional on sampling Finish. This does not measure win strength or prove a training fix.')
    a.output.write_text(json.dumps(result,indent=2));print(json.dumps(result,indent=2))


if __name__=='__main__':
    p=argparse.ArgumentParser(description=__doc__)
    for name in ('teacher','policy','output'):p.add_argument('--'+name,type=Path,required=True)
    p.add_argument('--bias',type=float,default=29.466807163500626);run(p.parse_args())
