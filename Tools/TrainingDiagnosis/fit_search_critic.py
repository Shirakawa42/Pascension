"""Development-only value-head repair from completed searched games.

All action and representation weights remain bitwise fixed. Hyperparameters are
selected by engine-seed cross-validation; strength still needs fresh games.
"""
import argparse
import collections
import copy
import hashlib
import json
import os
from pathlib import Path
import sys
import time

ROOT=Path(__file__).resolve().parents[2]
sys.path[:0]=[str(ROOT/'Tools/ZeroDepthTraining'),str(ROOT/'Tools/MatchupBenchmark')]
import numpy as np
import torch
from cpu_affinity import select_cpus
from league import load_frozen_policy,sha256_file
from search_experience import dense_rows


def fit(x,base,y,weight,penalty):
    weight=weight/weight.sum()
    mean=(weight[:,None]*x).sum(0)
    scale=((weight[:,None]*(x-mean).square()).sum(0)+1e-4).sqrt()
    z=torch.cat(((x-mean)/scale,torch.ones((len(x),1))),1)
    delta=torch.zeros(z.shape[1],requires_grad=True)
    opt=torch.optim.LBFGS([delta],lr=1,max_iter=60,line_search_fn='strong_wolfe',tolerance_grad=1e-7)
    def closure():
        opt.zero_grad()
        logits=2*(base+z@delta)
        loss=(weight*torch.nn.functional.binary_cross_entropy_with_logits(logits,y,reduction='none')).sum()
        loss=loss+penalty*delta.square().sum()
        loss.backward();return loss
    opt.step(closure)
    with torch.no_grad():
        w=delta[:-1]/scale;b=delta[-1]-mean@w
    return w,b


def run(a):
    started=time.monotonic();a.output.mkdir(parents=True,exist_ok=False)
    os.sched_setaffinity(0,select_cpus(os.sched_getaffinity(0),6));torch.set_num_threads(1)
    torch.backends.fp32_precision='ieee';torch.backends.cuda.matmul.fp32_precision='ieee'
    policy,manifest=load_frozen_policy(a.policy,device='cuda');policy.cache_frozen_table()
    progress=json.loads((a.experiment/'progress.json').read_text());plan=json.loads((a.experiment/'plan.json').read_text())
    if progress['state']!='complete':raise ValueError('Only a complete predeclared collection can train this repair')
    if manifest['policy_file_sha256']!=plan['candidate']['policy_file_sha256']:raise ValueError('Behavior policy mismatch')
    captured=[]
    hook=policy.value.register_forward_pre_hook(lambda module,args:captured.append(args[0].detach().cpu()))
    base=[];outcomes=[];keys=[];seeds=[]
    with torch.inference_mode():
        for item in progress['experience_files']:
            path=a.experiment/item['file']
            if sha256_file(path)!=item['sha256']:raise ValueError('Experience hash mismatch')
            with np.load(path) as archive:data={k:archive[k] for k in archive.files}
            n=len(data['actions'])
            for start in range(0,n,128):
                ix=np.arange(start,min(start+128,n));x=torch.as_tensor(dense_rows(data,ix),device='cuda')
                policy(x[:,:24576],x[:,24576:-64].reshape(-1,64,48),x[:,-64:])
                base.append((captured[-1]@policy.value.weight.detach().cpu().T+policy.value.bias.detach().cpu()).squeeze(1))
                outcomes.extend((data['outcomes'][ix]+1)/2)
                keys.extend((int(data['seeds'][i]),int(data['meta'][i,1])) for i in ix)
                seeds.extend(int(data['seeds'][i]) for i in ix)
    hook.remove();x=torch.cat(captured);base=torch.cat(base);y=torch.tensor(outcomes)
    counts=collections.Counter(keys);weight=torch.tensor([1/counts[k] for k in keys])
    unique=sorted(set(seeds));rng=np.random.default_rng(721007);rng.shuffle(unique)
    fold_by_seed={seed:i%4 for i,seed in enumerate(unique)};fold=torch.tensor([fold_by_seed[s] for s in seeds])
    if len(unique)<20:raise ValueError('Too few independent seed pairs')
    def metrics(logits):
        p=(2*logits).sigmoid();w=weight/weight.sum()
        return dict(brier=float(w@(p-y).square()),nll=float(w@torch.nn.functional.binary_cross_entropy_with_logits(2*logits,y,reduction='none')))
    baseline=metrics(base);candidates=[]
    for penalty in (100.,10.,1.,.1,.01):
        prediction=torch.empty_like(base)
        for heldout in range(4):
            train=fold!=heldout;test=~train
            w,b=fit(x[train],base[train],y[train],weight[train],penalty)
            prediction[test]=base[test]+x[test]@w+b
        candidates.append(dict(penalty=penalty,**metrics(prediction)))
    best=min(candidates,key=lambda c:c['brier'])
    accepted=best['brier']<baseline['brier']*.98 and best['nll']<baseline['nll']
    report=dict(parent_policy_sha256=manifest['policy_file_sha256'],experiment=str(a.experiment.resolve()),
        experience_files=progress['experience_files'],games=len(counts),seed_pairs=len(unique),decisions=len(x),
        cross_validation='Four folds split by engine seed; both seats stay together; equal weight per actual game',
        baseline=baseline,candidates=candidates,selected=best,accepted_for_strength_screen=accepted,
        strength_proven=False,actor_unchanged=True,trained_parameters=['value.weight','value.bias'],
        elapsed_seconds=time.monotonic()-started,training_seeds=unique)
    if accepted:
        w,b=fit(x,base,y,weight,best['penalty'])
        payload=torch.load(a.policy/'policy.pt',map_location='cpu',weights_only=True)
        original=copy.deepcopy(payload['policy'])
        payload['policy']['value.weight']+=w[None];payload['policy']['value.bias']+=b
        for key in original:
            if key not in ('value.weight','value.bias') and not torch.equal(original[key],payload['policy'][key]):
                raise RuntimeError('Critic repair changed policy/representation weights')
        report['maximum_parameter_change']=max(float((payload['policy'][k]-original[k]).abs().max()) for k in ('value.weight','value.bias'))
        provenance=dict(parent_policy_sha256=manifest['policy_file_sha256'],method='frozen-features-ridge-logistic-value-repair',
            penalty=best['penalty'],data_manifest_sha256=hashlib.sha256(json.dumps(progress['experience_files'],sort_keys=True).encode()).hexdigest(),
            source_sha256=sha256_file(Path(__file__)))
        payload['identity']['critic_repair']=provenance
        folder=a.output/'learner';folder.mkdir();torch.save(payload,folder/'policy.pt')
        meta=copy.deepcopy(manifest)
        for key in ('checkpoint','checkpoint_sha256','checkpoint_payload_sha256'):meta.pop(key,None)
        meta.update(identity=payload['identity'],bytes=(folder/'policy.pt').stat().st_size,
            policy_file_sha256=sha256_file(folder/'policy.pt'),diagnostic_finetuning=report)
        (folder/'manifest.json').write_text(json.dumps(meta,indent=2));load_frozen_policy(folder)
    (a.output/'result.json').write_text(json.dumps(report,indent=2));print(json.dumps(report,indent=2))


if __name__=='__main__':
    p=argparse.ArgumentParser(description=__doc__)
    p.add_argument('--experiment',type=Path,required=True);p.add_argument('--policy',type=Path,required=True)
    p.add_argument('--output',type=Path,required=True);run(p.parse_args())
