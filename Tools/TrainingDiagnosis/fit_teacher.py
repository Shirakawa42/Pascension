"""Small, isolated teacher-fit experiment; never overwrites a training checkpoint."""
import argparse, copy, hashlib, itertools, json, sys, time
from pathlib import Path
ROOT=Path(__file__).resolve().parents[2]
sys.path[:0]=[str(ROOT/'Tools/ZeroDepthTraining'),str(ROOT/'Tools/TrainingPreflight')]
import numpy as np
import torch
from model import Policy,PolicyConfig,cpu_state
from league import load_frozen_policy,sha256_file,POLICY_SCHEMA
from train import source_fingerprint

def load_data(folder, report):
    dtype=np.dtype([('game','<i4'),('seat','<i4'),('action','<i4'),
                    ('obs','<f4',(24576,)),('candidates','<f4',(64,48)),('mask','<f4',(64,))])
    parts=[]
    for game in report['reports']:
        path=folder/f"game-{game['index']:03}.bin"
        if path.stat().st_size != game['rows']*dtype.itemsize:
            raise ValueError('Incomplete teacher trajectory')
        rows=np.fromfile(path,dtype=dtype)
        if not np.all(rows['game']==game['index']):raise ValueError('Teacher trajectory identity mismatch')
        if not np.all(rows['mask'][np.arange(len(rows)),rows['action']]==1):raise ValueError('Illegal teacher label')
        parts.append(rows)
    data=np.concatenate(parts)
    # Whole games are held out; adjacent decisions never cross this split.
    # Select by public hero/context coverage, never by outcomes or model scores.
    # A modulo split can accidentally omit Rez/Scry from validation entirely.
    validation=None
    for selection in itertools.combinations(report['reports'],4):
        heroes={g[seat] for g in selection for seat in ('seat0','seat1')}
        if len(heroes)==5 and any(g['seat0']=='rez' for g in selection) and any(g['seat1']=='rez' for g in selection):
            if sum(g['contexts'].get('soi.scry',0) for g in selection)>0:
                validation={g['index'] for g in selection};break
    if validation is None:raise ValueError('Teacher corpus does not support all-hero validation including Scry')
    test=np.isin(data['game'],list(validation))
    if not test.any() or test.all():raise ValueError('Need independent training and validation games')
    return data,test

def run(work,source,steps=240):
    torch.set_num_threads(1);torch.manual_seed(619006)
    torch.backends.fp32_precision='ieee';torch.backends.cuda.matmul.fp32_precision='ieee'
    report=json.loads((work/'teacher-data.json').read_text());assert report['passed'] and report['full_state_hash_parity']
    data,test=load_data(work/'teacher-data',report)
    device='cuda';reference,meta=load_frozen_policy(source/'learner',device=device)
    arrays=[torch.tensor(np.array(data[k]),device=device) for k in ('obs','candidates','mask')]
    arrays[2]=arrays[2].bool();labels=torch.tensor(data['action'].astype(np.int64),device=device)
    context=np.where(data['obs'][:,144]>.5,np.rint(data['obs'][:,157]*26),0).astype(int)
    train=np.flatnonzero(~test);valid=np.flatnonzero(test)
    groups=[train[context[train]==c] for c in sorted(set(context[train]))]
    with torch.inference_mode():
        old_q=[];old_v=[]
        for start in range(0,len(data),128):
            z,v=reference(*[x[start:start+128] for x in arrays]);old_q.append(z.softmax(-1));old_v.append(v)
        old_q=torch.cat(old_q);old_v=torch.cat(old_v)
    def evaluate(policy,indices):
        nll=[];accuracy=[];prob=[]
        with torch.inference_mode():
            for start in range(0,len(indices),128):
                ix=torch.tensor(indices[start:start+128],device=device)
                z,_=policy(*[x[ix] for x in arrays]);logp=z.log_softmax(-1)
                nll.extend((-logp.gather(1,labels[ix,None]).squeeze(1)).cpu().tolist())
                accuracy.extend((z.argmax(-1)==labels[ix]).cpu().tolist())
                prob.extend(logp.exp().gather(1,labels[ix,None]).squeeze(1).cpu().tolist())
        per_context={str(int(c)):dict(rows=int((context[indices]==c).sum()),nll=float(np.array(nll)[context[indices]==c].mean()),
                                     agreement=float(np.array(accuracy)[context[indices]==c].mean())) for c in sorted(set(context[indices]))}
        return dict(rows=len(indices),nll=float(np.mean(nll)),agreement=float(np.mean(accuracy)),
                    mean_teacher_action_probability=float(np.mean(prob)),contexts=per_context)
    outputs=[]
    for name,width,warm in [('continued-512',512,True),('fresh-256',256,False),('fresh-512',512,False)]:
        torch.manual_seed(619006);rng=np.random.default_rng(619006)
        policy=copy.deepcopy(reference) if warm else Policy(reference.catalog,PolicyConfig(width=width)).to(device)
        if not warm and policy.known_top_supported:policy.known_top_enabled.fill_(1.)
        policy.train().requires_grad_(True)
        optimizer=torch.optim.Adam(policy.parameters(),lr=5e-5 if warm else 3e-4)
        before=evaluate(policy,valid);start=time.monotonic()
        for step in range(steps):
            if time.monotonic()-start>180:raise TimeoutError('Bounded teacher fit exceeded three minutes')
            # Half uniform, half context-balanced; final game gates remain unweighted.
            selected=np.r_[rng.choice(train,32),[rng.choice(groups[int(rng.integers(len(groups)))]) for _ in range(32)]]
            ix=torch.tensor(selected,device=device);z,v=policy(*[x[ix] for x in arrays]);logp=z.log_softmax(-1)
            imitation=-logp.gather(1,labels[ix,None]).mean()
            loss=imitation
            if warm:
                q=old_q[ix];kl=(q*(q.clamp_min(1e-30).log()-logp)).sum(-1).mean()
                loss=loss+.25*kl+.25*(v-old_v[ix]).square().mean()
            if not torch.isfinite(loss):raise RuntimeError('Nonfinite teacher loss')
            optimizer.zero_grad(set_to_none=True);loss.backward();torch.nn.utils.clip_grad_norm_(policy.parameters(),1.)
            optimizer.step()
        after=evaluate(policy,valid);fit=evaluate(policy,train)
        destination=work/name;destination.mkdir(exist_ok=False)
        parent=torch.load(source/'learner/policy.pt',map_location='cpu',weights_only=True)
        payload=copy.deepcopy({k:v for k,v in parent.items() if k!='policy'})
        payload['policy']=cpu_state(policy);payload['policy_config']['width']=width
        payload['identity']['configuration']['width']=width
        payload['identity']['source_fingerprint']=source_fingerprint()
        if not warm:payload['training']={k:0 for k in payload['training']}
        torch.save(payload,destination/'policy.pt')
        manifest=copy.deepcopy(meta);manifest.update(identity=payload['identity'],training=payload['training'],
            bytes=(destination/'policy.pt').stat().st_size,policy_file_sha256=sha256_file(destination/'policy.pt'),
            diagnostic_finetuning=dict(steps=steps,parent_policy_sha256=meta['policy_file_sha256'] if warm else None,
                                      teacher_games=report['games'],train_games=sorted(set(data['game'][~test].tolist())),
                                      validation_games=sorted(set(data['game'][test].tolist())),deployed=False))
        # Source-checkpoint fields identify ancestry, not this newly fitted export.
        for key in ('checkpoint','checkpoint_sha256','checkpoint_payload_sha256'):manifest.pop(key,None)
        (destination/'manifest.json').write_text(json.dumps(manifest,indent=2))
        loaded,_=load_frozen_policy(destination,device='cpu');del loaded
        result=dict(name=name,parameters=sum(x.numel() for x in policy.parameters()),steps=steps,
                    seconds=time.monotonic()-start,before=before,after=after,train=fit,
                    path=str(destination),retained_old_behavior_penalty=warm,
                    conclusion='Teacher imitation diagnostic only; strength must be measured in fresh games.')
        outputs.append(result);print(json.dumps({k:v for k,v in result.items() if k not in ('before','after','train')}),flush=True)
        (work/'teacher-fit.json').write_text(json.dumps(outputs,indent=2))
        del policy,optimizer
    return outputs

if __name__=='__main__':
    p=argparse.ArgumentParser();p.add_argument('--work',type=Path,required=True);p.add_argument('--source',type=Path,required=True)
    p.add_argument('--steps',type=int,default=240);a=p.parse_args()
    if not 1<=a.steps<=1000:p.error('Diagnostic fit is limited to 1..1000 steps per model')
    run(a.work,a.source,a.steps)
