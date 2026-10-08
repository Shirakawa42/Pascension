"""Bounded, isolated search distillation experiment; never deploys a policy.

Consumes public observations from --search-curriculum, not the tactical test set
or published balance games. Search labels are auxiliary policy advice, not PPO
returns or guaranteed wins. Original-policy KL/value retention limits forgetting.
Whole games are held out; seeds for final strength tests are a separate range.
"""
import argparse, gzip, json, struct, sys, time, hashlib
from pathlib import Path
import numpy as np
import torch


def read_native(path):
    arrays = {}
    with Path(path).open('rb') as f:
        magic, width, clip, tanh, count = struct.unpack('<iif?i', f.read(17))
        if magic != 0x534f4931: raise ValueError('Unknown policy format')
        for _ in range(count):
            name = f.read(struct.unpack('<i', f.read(4))[0]).decode()
            n = struct.unpack('<i', f.read(4))[0]
            arrays[name] = np.frombuffer(f.read(n*4), dtype='<f4').copy()
        if f.read(): raise ValueError('Trailing policy data')
    return arrays, width, clip, tanh


def write_native(policy, path):
    with torch.no_grad():
        tensors = {k: v.detach().float().cpu().numpy() for k,v in policy.state_dict().items()}
        table = policy._compute_semantic_table().detach().cpu()
        tensors['semantic_table'] = table.numpy()
        tensors['combined_embeddings'] = (table + policy.core.card_embedding.weight.detach().cpu()).numpy()
    with path.open('wb') as f:
        f.write(struct.pack('<iif?i', 0x534f4931, policy.config.width, policy.config.input_clip, policy.config.value_tanh, len(tensors)))
        for name, array in tensors.items():
            raw = name.encode(); f.write(struct.pack('<i', len(raw))); f.write(raw)
            f.write(struct.pack('<i', array.size)); f.write(array.astype('<f4').tobytes())


def main():
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument('--policy', type=Path, required=True)
    p.add_argument('--data', type=Path, required=True)
    p.add_argument('--output', type=Path, required=True)
    p.add_argument('--seconds', type=int, default=120)
    p.add_argument('--updates', type=int, default=800)
    p.add_argument('--allow-small-pilot', action='store_true', help='Explicitly allow a diagnostic experiment below coverage/pass limits; still never deploys')
    a = p.parse_args()
    if not 1 <= a.seconds <= 180 or not 1 <= a.updates <= 2000:
        raise ValueError('This pilot is limited to 180 seconds / 2000 updates')
    a.output.mkdir(parents=True, exist_ok=False)
    torch.set_num_threads(1); torch.manual_seed(270927)
    arrays, width, clip, tanh = read_native(a.policy)
    frozen = Path('/home/lva/.local/share/shards-training/2026-09-26/frozen-evaluation/Tools/TrainingPreflight')
    sys.path[:0] = [str(Path(__file__).parent), str(frozen/'experiments'), str(frozen)]
    import effect_catalog_v10
    # Load the exact frozen effect facts; no content subprocess or stale catalogue.
    effect_catalog_v10.effect_matrix = lambda: arrays['card_effects'].reshape(193,512)
    from rez_policy import ChoicePolicy
    from learning_model import PolicyConfig
    policy = ChoicePolicy(PolicyConfig(width=width,input_clip=clip,value_tanh=tanh))
    state = policy.state_dict()
    policy.load_state_dict({k: torch.as_tensor(arrays[k].reshape(v.shape),dtype=v.dtype) for k,v in state.items()}, strict=True)
    policy = policy.cuda().train()
    with gzip.open(a.data, 'rt') as f: rows = [json.loads(line) for line in f]
    if not rows or any(r['schema'] != 'public-search-curriculum-v1' for r in rows):
        raise ValueError('Invalid curriculum')
    # Repeated copies of one position cannot satisfy coverage, and identical
    # observed positions in held-out games must not also become training examples.
    def position_key(r):
        digest=hashlib.sha256()
        for field in ('observation','candidates','mask'):
            digest.update(np.asarray(r[field],dtype='<f4').tobytes())
        return digest.digest()
    held_positions={position_key(r) for r in rows if (r['game']//2)%5==0}
    unique=[];seen=set()
    for r in rows:
        key=position_key(r);held_game=(r['game']//2)%5==0
        if not held_game and key in held_positions:continue
        identity=(held_game,key,tuple(r['targets']))
        if identity in seen:continue
        seen.add(identity);unique.append(r)
    rows=unique
    device='cuda'
    obs=torch.tensor([r['observation'] for r in rows],device=device)
    cand=torch.tensor([r['candidates'] for r in rows],device=device).reshape(-1,64,32)
    mask=torch.tensor([r['mask'] for r in rows],device=device,dtype=torch.bool)
    old=torch.tensor([r['probabilities'] for r in rows],device=device)
    values=torch.tensor([r['value'] for r in rows],device=device)
    targets=torch.tensor([r['targets'][0] if r['targets'] else 0 for r in rows],device=device)
    train=[i for i,r in enumerate(rows) if (r['game']//2)%5!=0]
    held=[i for i,r in enumerate(rows) if (r['game']//2)%5==0]
    train_search=[i for i in train if rows[i]['targets']]
    held_search=[i for i in held if rows[i]['targets']]
    if len(train_search)<50 or len(held_search)<10: raise ValueError('Insufficient independent search labels')
    for i,r in enumerate(rows):
        if r['targets'] and not mask[i,targets[i]]: raise ValueError('Illegal search advice')
    train_ix=torch.tensor(train,device=device); held_ix=torch.tensor(held,device=device)
    ts=torch.tensor(train_search,device=device); hs=torch.tensor(held_search,device=device)
    hero_groups=[torch.tensor([i for i in train_search if rows[i]['hero']==hero],device=device) for hero in sorted({rows[i]['hero'] for i in train_search})]
    hero_counts={h:sum(rows[i]['hero']==h for i in train_search) for h in ['decima','tetra','volos','kosynwu','rez']}
    if not a.allow_small_pilot and min(hero_counts.values())<200:
        raise ValueError(f'Need at least 200 distinct training labels per hero before another distillation run: {hero_counts}')
    # Balanced heroes must not mean thousands of repeats of three rare examples.
    update_limit=a.updates if a.allow_small_pilot else min(a.updates, 5*min(hero_counts.values())*8//64)
    checkpoint_every=min(200,update_limit)
    @torch.no_grad()
    def measure():
        results={}
        for name, ix in [('train',ts),('heldout',hs),('retention',held_ix)]:
            probabilities=[]; predictions=[]; kls=[]; errors=[]
            for part in ix.split(256):
                logits,value=policy(obs[part],cand[part],mask[part]); logp=logits.log_softmax(-1)
                probabilities.append(logp.gather(1,targets[part,None]).exp().flatten())
                predictions.append((logits.argmax(-1)==targets[part]).float())
                kls.append((old[part]*(old[part].clamp_min(1e-30).log()-logp)).sum(-1))
                errors.append((value-values[part]).square())
            results[name]={'advice_probability':torch.cat(probabilities).mean().item(),
                'advice_top1':torch.cat(predictions).mean().item(), 'kl':torch.cat(kls).mean().item(),
                'value_mse':torch.cat(errors).mean().item()}
        return results
    with torch.no_grad():
        logits,value=policy(obs[:64],cand[:64],mask[:64]); parity=(logits.softmax(-1)-old[:64]).abs().max().item()
        if parity>1e-4: raise ValueError(f'Native/GPU policy mismatch: {parity}')
    report={'schema':'search-distillation-pilot-v1','source_policy_sha256':hashlib.sha256(a.policy.read_bytes()).hexdigest(),
        'data_sha256':hashlib.sha256(a.data.read_bytes()).hexdigest(),'training_source_sha256':hashlib.sha256(Path(__file__).read_bytes()).hexdigest(),
        'small_pilot':a.allow_small_pilot,'update_limit':update_limit,'rows':len(rows),'train_labels':len(ts),'heldout_labels':len(hs),
        'heroes':{h:sum(rows[i]['hero']==h for i in train_search) for h in sorted({r['hero'] for r in rows})},
        'native_gpu_parity_error':parity,'baseline':measure(),'checkpoints':[],'deployed':False}
    optimizer=torch.optim.AdamW(policy.parameters(),lr=1e-5,weight_decay=0)
    timer=time.monotonic()
    for update in range(1,update_limit+1):
        if time.monotonic()-timer >= a.seconds: break
        # Equal hero contribution, plus broad old-policy retention on all phases.
        group=hero_groups[(update-1)%len(hero_groups)]
        si=group[torch.randint(len(group),(64,),device=device)]
        ri=train_ix[torch.randint(len(train_ix),(256,),device=device)]
        optimizer.zero_grad(set_to_none=True)
        logits,_=policy(obs[si],cand[si],mask[si])
        supervised=torch.nn.functional.cross_entropy(logits,targets[si])
        logits,value=policy(obs[ri],cand[ri],mask[ri]); logp=logits.log_softmax(-1)
        kl=(old[ri]*(old[ri].clamp_min(1e-30).log()-logp)).sum(-1).mean()
        loss=.25*supervised+kl+.5*(value-values[ri]).square().mean()
        if not torch.isfinite(loss): raise RuntimeError('Nonfinite distillation loss')
        loss.backward(); norm=torch.nn.utils.clip_grad_norm_(policy.parameters(),.5)
        if not torch.isfinite(norm): raise RuntimeError('Nonfinite gradient')
        optimizer.step()
        if update%checkpoint_every==0 or update==update_limit:
            metrics=measure(); path=a.output/f'candidate-{update:04d}.bytes';write_native(policy,path)
            report['checkpoints'].append({'update':update,'seconds':time.monotonic()-timer,'policy':str(path),'metrics':metrics})
            (a.output/'report.json').write_text(json.dumps(report,indent=2)+'\n')
            print(json.dumps(report['checkpoints'][-1]),flush=True)
            # Stop this candidate if broad retained policy drift is already substantial.
            if metrics['retention']['kl']>.08: break
    report['elapsed_seconds']=time.monotonic()-timer
    (a.output/'report.json').write_text(json.dumps(report,indent=2)+'\n')
    print(json.dumps({'report':str(a.output/'report.json'),'deployed':False,'seconds':report['elapsed_seconds']}))

if __name__=='__main__':main()
