"""Bounded actor-head imitation; the representation and critic remain exactly frozen."""
import argparse
import copy
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
from league import load_frozen_policy,sha256_file,save_json
from model import cpu_state
from fit_teacher import load_data
from search_experience import dense_rows


def run(a):
    os.sched_setaffinity(0,select_cpus(os.sched_getaffinity(0),6));torch.set_num_threads(1)
    torch.backends.fp32_precision='ieee';torch.backends.cuda.matmul.fp32_precision='ieee'
    rng=np.random.default_rng(771203);torch.manual_seed(771203)
    a.output.mkdir(exist_ok=False)
    policy,manifest=load_frozen_policy(a.policy,device='cuda');policy.requires_grad_(False);policy.cache_frozen_table()
    for module in (policy.query,policy.candidate_bias):module.requires_grad_(True)
    if a.candidate_layer:policy.candidate.requires_grad_(True)
    trainable={name for name,p in policy.named_parameters() if p.requires_grad}
    expected={'query.weight','query.bias','candidate_bias.weight'}
    if a.candidate_layer:expected|={'candidate.0.weight','candidate.0.bias'}
    if trainable!=expected:raise ValueError('Unexpected trainable parameters')
    original=cpu_state(policy)
    context=policy.catalog['contexts'].index('soi.scry');context_count=len(policy.catalog['contexts'])
    result=json.loads((a.experiment/'result.json').read_text())
    if result['state']!='complete':raise ValueError('Search data is incomplete')
    packets=[];labels=[];overrides=[];seeds=[]
    for item in result['experience_files']:
        file=a.experiment/item['file']
        if sha256_file(file)!=item['sha256']:raise ValueError('Search experience changed')
        with np.load(file) as z:data={k:z[k] for k in z.files}
        packets.append(dense_rows(data,np.arange(len(data['actions']))));labels.extend(data['actions']);overrides.extend(data['improved']);seeds.extend(int(x) for x in data['seeds'])
    packet=np.concatenate(packets);del packets
    ns=len(packet);scry=(packet[:,144]>.5)&(np.rint(packet[:,157]*context_count)==context)
    unique=np.array(sorted(set(seeds)),dtype=np.uint64);rng.shuffle(unique);held=set(int(x) for x in unique[::5]);valid=np.array([s in held for s in seeds])
    source=json.loads((a.teacher/'teacher-data.json').read_text())
    if not source['passed'] or not source['full_state_hash_parity']:raise ValueError('Teacher observations were not verified')
    teacher,tvalid=load_data(a.teacher/'teacher-data',source)
    keep=(teacher['obs'][:,144]>.5)&(np.rint(teacher['obs'][:,157]*context_count)==context)
    if not a.teacher_general:teacher=teacher[keep];tvalid=tvalid[keep];keep=np.ones(len(teacher),dtype=bool)
    tpacket=np.concatenate((teacher['obs'],teacher['candidates'].reshape(len(teacher),-1),teacher['mask']),1)
    x=torch.from_numpy(np.concatenate((packet,tpacket))).to('cuda');del packet,tpacket
    target=torch.tensor(np.r_[labels,teacher['action']],device='cuda',dtype=torch.long)
    general=np.flatnonzero(np.asarray(overrides).astype(bool)&~scry&~valid)
    special=np.flatnonzero(~tvalid&keep)+ns;anchor=np.flatnonzero(~scry&~valid)
    general_val=np.flatnonzero(np.asarray(overrides).astype(bool)&~scry&valid)
    special_val=np.flatnonzero(tvalid&keep)+ns;anchor_val=np.flatnonzero(~scry&valid)
    if a.teacher_general:
        general=np.flatnonzero(~tvalid&~keep)+ns;general_val=np.flatnonzero(tvalid&~keep)+ns
    if any(len(ix)==0 for ix in (general,special,anchor,general_val,special_val,anchor_val)):raise ValueError('Missing training or validation group')
    def forward(indices):
        p=x[indices]
        return policy(p[:,:24576],p[:,24576:-64].reshape(-1,64,48),p[:,-64:])
    with torch.no_grad():
        old_z=[];old_v=[]
        for start in range(0,len(x),128):
            z,v=forward(torch.arange(start,min(start+128,len(x)),device='cuda'));old_z.append(z);old_v.append(v)
        old_logp=torch.cat(old_z).log_softmax(-1);old_p=old_logp.exp();old_v=torch.cat(old_v)
    validation_values={}
    def evaluate():
        metrics={}
        with torch.no_grad():
            for name,indices in [('general',general_val),('scry',special_val),('anchor',anchor_val)]:
                nll=[];agree=[];kl=[]
                for start in range(0,len(indices),128):
                    ix=torch.tensor(indices[start:start+128],device='cuda');z,v=forward(ix);q=z.log_softmax(-1)
                    key=name,start
                    if key not in validation_values:validation_values[key]=v.detach().clone()
                    elif not torch.equal(v,validation_values[key]):raise RuntimeError('Actor fitting changed critic output at the identical batch shape')
                    nll.extend((-q.gather(1,target[ix,None]).squeeze(1)).cpu().tolist())
                    agree.extend((z.argmax(-1)==target[ix]).cpu().tolist());kl.extend((old_p[ix]*(old_logp[ix]-q)).sum(-1).cpu().tolist())
                metrics[name]=dict(rows=len(indices),nll=float(np.mean(nll)),agreement=float(np.mean(agree)),kl=float(np.mean(kl)))
        return metrics
    baseline=evaluate();history=[];best=None;best_state=None
    optimizer=torch.optim.Adam([p for p in policy.parameters() if p.requires_grad],lr=3e-4)
    started=time.monotonic()
    for step in range(1,151):
        if time.monotonic()-started>90:break
        ix=torch.tensor(np.r_[rng.choice(general,64),rng.choice(special,32),rng.choice(anchor,64)],device='cuda')
        z,_=forward(ix);logp=z.log_softmax(-1)
        ce=-logp[:96].gather(1,target[ix[:96],None]).squeeze(1)
        loss=ce[:64].mean()+.25*ce[64:].mean()+a.anchor_weight*(old_p[ix[96:]]*(old_logp[ix[96:]]-logp[96:])).sum(-1).mean()
        if not bool(torch.isfinite(loss)):raise RuntimeError('Nonfinite actor objective')
        optimizer.zero_grad(set_to_none=True);loss.backward();torch.nn.utils.clip_grad_norm_([p for p in policy.parameters() if p.requires_grad],.5,error_if_nonfinite=True);optimizer.step()
        if step%25==0:
            metrics=evaluate();history.append(dict(step=step,metrics=metrics))
            eligible=(metrics['general']['nll']<baseline['general']['nll'] and metrics['scry']['nll']<baseline['scry']['nll']*.9
                      and metrics['anchor']['kl']<.03 and metrics['scry']['agreement']>=baseline['scry']['agreement'])
            if a.teacher_general:eligible=eligible and metrics['general']['agreement']>=baseline['general']['agreement']
            score=metrics['general']['nll']/baseline['general']['nll']+metrics['scry']['nll']/baseline['scry']['nll']
            if eligible and (best is None or score<best['score']):
                best=dict(step=step,score=score,metrics=metrics);best_state={k:v.detach().cpu().clone() for k,v in policy.state_dict().items() if k in trainable}
    report=dict(parent_policy_sha256=manifest['policy_file_sha256'],trained_parameters=sorted(trainable),trainable_parameter_count=sum(p.numel() for p in policy.parameters() if p.requires_grad),
        baseline=baseline,history=history,selected=best,anchor_weight=a.anchor_weight,strength_proven=False,critic_and_representation_unchanged=True,
        teacher_general=a.teacher_general,candidate_layer=a.candidate_layer,
        training_data=('Verified deployed-teacher actions; historical searched roots used only as a KL anchor' if a.teacher_general else 'Completed historical searched-root overrides plus verified deployed-teacher Scry choices')+'; all final benchmark seeds excluded',
        validation='Whole engine seeds held out for searched roots; whole teacher games held out for teacher actions',elapsed_seconds=time.monotonic()-started)
    if best is not None:
        state=policy.state_dict();state.update(best_state);policy.load_state_dict(state)
        for name,value in cpu_state(policy).items():
            if name not in trainable and not torch.equal(value,original[name]):raise RuntimeError('Frozen parameter or buffer changed: '+name)
        report['final_validation']=evaluate()
        payload=torch.load(a.policy/'policy.pt',map_location='cpu',weights_only=True);payload['policy']=cpu_state(policy)
        payload['identity']['actor_head_imitation']=dict(parent_policy_sha256=manifest['policy_file_sha256'],step=best['step'],source_sha256=sha256_file(Path(__file__)))
        model=a.output/'learner';model.mkdir();torch.save(payload,model/'policy.pt')
        meta=copy.deepcopy(manifest);meta.update(identity=payload['identity'],bytes=(model/'policy.pt').stat().st_size,policy_file_sha256=sha256_file(model/'policy.pt'),actor_head_imitation=report)
        save_json(model/'manifest.json',meta);load_frozen_policy(model)
    save_json(a.output/'result.json',report);print(json.dumps(report,indent=2))


if __name__=='__main__':
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--anchor-weight',type=float,default=4.)
    parser.add_argument('--teacher-general',action='store_true',help='Use verified deployed-teacher actions for ordinary choices as well as Scry')
    parser.add_argument('--candidate-layer',action='store_true',help='Also train the small action-feature projection, which never enters the critic')
    for name in ('policy','experiment','teacher','output'):parser.add_argument('--'+name,type=Path,required=True)
    run(parser.parse_args())
