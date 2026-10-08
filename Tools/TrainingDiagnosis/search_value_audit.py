"""Measure a frozen critic and policy on completed, real searched games."""
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
from league import load_frozen_policy,sha256_file
from cpu_affinity import select_cpus
from search_experience import dense_rows


@torch.inference_mode()
def run(a):
    os.sched_setaffinity(0,select_cpus(os.sched_getaffinity(0),6));torch.set_num_threads(1)
    torch.backends.fp32_precision='ieee';torch.backends.cuda.matmul.fp32_precision='ieee'
    progress=json.loads((a.experiment/'progress.json').read_text())
    plan=json.loads((a.experiment/'plan.json').read_text())
    policy,manifest=load_frozen_policy(a.policy,device='cuda');policy.cache_frozen_table()
    if manifest['policy_file_sha256']!=plan['candidate']['policy_file_sha256']:raise ValueError('Behavior policy differs')
    predictions=[];outcomes=[];keys=[];heroes=[];mastery=[];contexts=[];chosen_prob=[];agreement=[];scry_first=[];scry_finish=[];finish_prob=[]
    context_names=policy.catalog['contexts'];files=progress['experience_files'];coverage=collections.Counter()
    card_ids=policy.catalog.get('card_ids',policy.catalog.get('cards'))
    resources={card_ids.index(name)+1 for name in ('crystal','blaster','infinity_shard') if name in card_ids}
    longshot=card_ids.index('longshot')+1 if 'longshot' in card_ids else -1
    for item in files:
        path=a.experiment/item['file']
        if sha256_file(path)!=item['sha256']:raise ValueError('Experience hash mismatch')
        with np.load(path) as archive:data={key:archive[key] for key in archive.files}
        n=len(data['actions'])
        for start in range(0,n,128):
            indices=np.arange(start,min(start+128,n));packet=dense_rows(data,indices);x=torch.as_tensor(packet,device='cuda')
            logits,value=policy(x[:,:24576],x[:,24576:-64].reshape(-1,64,48),x[:,-64:]);p=logits.softmax(-1).cpu().numpy()
            candidate=packet[:,24576:-64].reshape(-1,64,48);actions=data['actions'][indices]
            predictions.extend((value.cpu().numpy()+1)/2);outcomes.extend((data['outcomes'][indices]+1)/2)
            keys.extend((int(data['seeds'][i]),int(data['meta'][i,1])) for i in indices)
            heroes.extend(np.rint(packet[:,22]*5).astype(int)-1);mastery.extend(packet[:,17]*30)
            labels=[context_names[int(round(row[157]*len(context_names)))] if row[144] else 'turn' for row in packet]
            contexts.extend(labels);chosen_prob.extend(p[np.arange(len(indices)),actions]);agreement.extend(p.argmax(-1)==actions)
            first=np.array([c=='soi.scry' for c in labels])&(packet[:,150]==0)
            scry_first.extend(first);scry_finish.extend(candidate[np.arange(len(indices)),actions,13]>0)
            finish_prob.extend((p*candidate[:,:,13]).sum(-1))
            for row in range(len(indices)):
                if packet[row,144]:continue
                groups={}
                for action in np.flatnonzero(packet[row,-64:]):
                    kind=int(candidate[row,action,:16].argmax());card=int(round(candidate[row,action,16]*192))
                    key=('resource',card) if kind==0 and card in resources else ('action',int(action))
                    groups.setdefault(key,[]).append(int(action))
                ordered=sorted(groups.values(),key=lambda xs:-float(p[row,xs].sum()))
                for label,kind in [('focus',3),('ability',9)]:
                    choices=np.flatnonzero(candidate[row,:,kind]*packet[row,-64:])
                    if not len(choices):continue
                    index=int(choices[0]);rank=next(i for i,xs in enumerate(ordered) if index in xs)
                    coverage[label+'_legal']+=1;coverage[label+'_outside_top4']+=int(rank>=4)
                    if kind==9 and round(packet[row,22]*5)==5:
                        has_longshot=any(candidate[row,a,0] and round(candidate[row,a,16]*192)==longshot for a in np.flatnonzero(packet[row,-64:]))
                        coverage['rez_ability_legal']+=1;coverage['rez_ability_outside_top4']+=int(rank>=4)
                        coverage['rez_ability_with_longshot']+=int(has_longshot)
                        coverage['rez_ability_with_longshot_outside_top4']+=int(has_longshot and rank>=4)
    if not keys:raise ValueError('No completed game experience available')
    keys_count=collections.Counter(keys);weights=np.array([1/keys_count[k] for k in keys]);predictions=np.array(predictions);outcomes=np.array(outcomes)
    def report(mask):
        if not np.any(mask):return None
        w=weights[mask];w=w/w.sum();p=predictions[mask];y=outcomes[mask]
        return dict(decisions=int(np.sum(mask)),games=len({key for key,take in zip(keys,mask) if take}),
            mean_predicted_win_probability=float(w@p),mean_terminal_outcome=float(w@y),
            brier=float(w@((p-y)**2)),mean_probability_of_searched_action=float(w@np.array(chosen_prob)[mask]),
            agreement_with_policy_argmax=float(w@np.array(agreement)[mask]))
    first=np.array(scry_first,dtype=bool);heroes=np.array(heroes);mastery=np.array(mastery)
    result=dict(experiment=str(a.experiment.resolve()),completed_games=progress['completed'],files=len(files),
        note='Real root decisions only; each game receives equal total weight. Outcome is from this searched policy against the incumbent, not the original self-play policy. This is calibration evidence, not causal proof.',
        overall=report(np.ones(len(keys),dtype=bool)),
        shortlist_coverage_estimate=dict(coverage),
        by_hero={name:report(heroes==i) for i,name in enumerate(('decima','tetra','volos','kosynwu','rez'))},
        mastery={f'{lo}..{hi}':report((mastery>=lo-.01)&(mastery<hi+.01)) for lo,hi in [(0,4),(5,9),(10,19),(20,28),(29,30)]},
        scry_first_decision=dict(count=int(first.sum()),mean_finish_probability=float(np.array(finish_prob)[first].mean()) if first.any() else None,
            searched_finish_fraction=float(np.array(scry_finish)[first].mean()) if first.any() else None))
    a.output.write_text(json.dumps(result,indent=2));print(json.dumps(result,indent=2))


if __name__=='__main__':
    p=argparse.ArgumentParser(description=__doc__)
    p.add_argument('--experiment',type=Path,required=True);p.add_argument('--policy',type=Path,required=True)
    p.add_argument('--output',type=Path,required=True);run(p.parse_args())
