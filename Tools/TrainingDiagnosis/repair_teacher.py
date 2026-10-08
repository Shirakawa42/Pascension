"""Test a small Scry-only teacher correction while bounding drift elsewhere."""
import argparse,copy,json,sys,time
from pathlib import Path
ROOT=Path(__file__).resolve().parents[2]
sys.path[:0]=[str(ROOT/'Tools/ZeroDepthTraining'),str(ROOT/'Tools/TrainingPreflight')]
import numpy as np
import torch
from fit_teacher import load_data
from model import cpu_state
from train import source_fingerprint
from league import load_frozen_policy,sha256_file

def run(work,source):
    torch.set_num_threads(1);torch.manual_seed(621006)
    torch.backends.fp32_precision='ieee';torch.backends.cuda.matmul.fp32_precision='ieee'
    data,test=load_data(work/'teacher-data',json.loads((work/'teacher-data.json').read_text()))
    ctx=np.where(data['obs'][:,144]>.5,np.rint(data['obs'][:,157]*26),0).astype(int)
    train=np.flatnonzero((ctx==18)&~test);valid=np.flatnonzero((ctx==18)&test)
    if not len(train) or not len(valid):raise ValueError('Need Scry in independent training and validation games')
    policy,meta=load_frozen_policy(source/'learner',device='cuda')
    # The critic and all state/card representations remain fixed in this pilot.
    policy.query.requires_grad_(True)
    teacher=[torch.tensor(np.array(data[k]),device='cuda') for k in ('obs','candidates','mask')]
    teacher[2]=teacher[2].bool();labels=torch.tensor(data['action'].astype(np.int64),device='cuda')
    d=np.load(work/'unbiased-public-corpus.npz')
    anchor=[torch.tensor(d[k],device='cuda') for k in ('obs','candidates','mask')];anchor[2]=anchor[2].bool()
    def predict(args):
        with torch.no_grad():return torch.cat([policy(*[x[i:i+128] for x in args])[0].log_softmax(-1) for i in range(0,len(args[0]),128)])
    old=predict(anchor);before=predict(teacher)
    opt=torch.optim.Adam(policy.query.parameters(),lr=1e-4);rng=np.random.default_rng(621006)
    accepted=cpu_state(policy);accepted_step=0;steps=400
    for step in range(steps):
        ix=torch.tensor(rng.choice(train,64),device='cuda');ai=torch.tensor(rng.integers(len(old),size=128),device='cuda')
        z,_=policy(*[x[ix] for x in teacher]);az,_=policy(*[x[ai] for x in anchor]);alp=az.log_softmax(-1)
        ce=-z.log_softmax(-1).gather(1,labels[ix,None]).mean()
        kl=(old[ai].exp()*(old[ai]-alp)).sum(-1).mean()
        loss=ce+100*kl
        if not torch.isfinite(loss):raise RuntimeError('Nonfinite correction loss')
        opt.zero_grad(set_to_none=True);loss.backward();torch.nn.utils.clip_grad_norm_(policy.query.parameters(),1.);opt.step()
        if (step+1)%20==0:
            logp=predict(anchor);drift=(old.exp()*(old-logp)).sum(-1)
            if float(drift.mean())>.005 or float(drift.max())>.1:
                policy.load_state_dict(accepted);break
            accepted=cpu_state(policy);accepted_step=step+1
    after=predict(teacher);now=predict(anchor);drift=(old.exp()*(old-now)).sum(-1)
    target=torch.tensor(valid,device='cuda')
    report=dict(accepted_steps=accepted_step,train_states=len(train),validation_states=len(valid),
        before_scry_nll=float(-before[target].gather(1,labels[target,None]).mean()),
        after_scry_nll=float(-after[target].gather(1,labels[target,None]).mean()),
        anchor_mean_kl=float(drift.mean()),anchor_max_kl=float(drift.max()),
        anchor_argmax_changes=int((old.argmax(-1)!=now.argmax(-1)).sum()),anchor_states=len(old),
        trained_parameters=['query.weight','query.bias'],critic_unchanged=True,
        strength_proven=False)
    folder=work/'targeted-scry';folder.mkdir(exist_ok=False)
    payload=torch.load(source/'learner/policy.pt',map_location='cpu',weights_only=True)
    payload['policy']=cpu_state(policy);payload['identity']['source_fingerprint']=source_fingerprint()
    torch.save(payload,folder/'policy.pt')
    meta.update(identity=payload['identity'],bytes=(folder/'policy.pt').stat().st_size,
        policy_file_sha256=sha256_file(folder/'policy.pt'),diagnostic_finetuning=report)
    for k in ('checkpoint','checkpoint_sha256','checkpoint_payload_sha256'):meta.pop(k,None)
    (folder/'manifest.json').write_text(json.dumps(meta,indent=2));(work/'targeted-scry.json').write_text(json.dumps(report,indent=2));print(json.dumps(report))

if __name__=='__main__':
    p=argparse.ArgumentParser();p.add_argument('--work',type=Path,required=True);p.add_argument('--source',type=Path,required=True)
    a=p.parse_args();run(a.work,a.source)
