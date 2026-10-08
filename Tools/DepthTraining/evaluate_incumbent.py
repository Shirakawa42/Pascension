"""Final distilled-policy challenge against the frozen deployed search AI.

This is a stricter deployment check: the new network acts without its training
search, while the incumbent retains its deployed search. No automatic release.
"""
from pathlib import Path
import sys,time,json
HERE=Path(__file__).resolve().parent
sys.path.insert(0,str(HERE.parent/'ZeroDepthTraining'))
import numpy as np
import torch
from dataclasses import asdict
from league import load_frozen_policy,sha256_file
from evaluate import verify_bundle,GameResult,summarize_pairs
from host import Host
from model import Actor

def challenge(candidate,bundle,output,*,deadline,stop=lambda:False,pairs=2048,batch=16,progress=None):
    output=Path(output)
    if output.exists():raise FileExistsError(output)
    verify_bundle(bundle,repo_root=HERE.parents[1])
    policy,manifest=load_frozen_policy(candidate,device='cuda');torch.manual_seed(7712403)
    results=[];started=time.monotonic();seed_base=0x8500000000000000
    plan={'candidate':str(candidate),'candidate_manifest_sha256':sha256_file(Path(candidate)/'manifest.json'),
        'incumbent':str(bundle),'incumbent_manifest_sha256':sha256_file(Path(bundle)/'manifest.json'),
        'planned_pairs':pairs,'seed_base':seed_base,'learner_search_depth':0,'incumbent_search':True,'purpose':'fixed final incumbent check; no automatic deployment'}
    output.with_suffix('.plan.json').write_text(json.dumps(plan,indent=2))
    def publish(reason=None):
        data={**plan,'summary':summarize_pairs(results,planned_pairs=pairs),'elapsed_seconds':time.monotonic()-started,'results':[asdict(r) for r in results]}
        if reason:data['stop_reason']=reason
        tmp=output.with_suffix('.tmp');tmp.write_text(json.dumps(data,indent=2));tmp.replace(output)
        if progress:progress(data)
        return data
    try:
        for offset in range(0,pairs,batch//2):
            if stop() or time.monotonic()>=deadline:return publish('authorization boundary')
            count=min(batch,2*(pairs-offset))
            actor=Actor(policy,count,graph=True)
            with Host(batch=count,workers=8,seed=seed_base+offset,opponent_bundle=bundle,paired=True,automation=False,timeout=min(60,max(.1,deadline-time.monotonic()))) as host:
                seen=np.zeros(count,bool)
                while not seen.all():
                    for lane in np.flatnonzero((host.done!=0)&~seen):
                        censor=int(host.done[lane])==2
                        winner=None if censor else -1 if not host.rewards[lane].any() else int(host.rewards[lane].argmax())
                        results.append(GameResult(seed_base+offset+int(lane)//2,int(lane)%2,winner,censor));seen[lane]=True
                    publish()
                    if seen.all():break
                    if stop() or time.monotonic()>=deadline:return publish('authorization boundary')
                    if not np.all(host.actors[~seen]==np.arange(count)[~seen]%2):raise RuntimeError('Incumbent decision leaked to learner')
                    actions,_=actor.act(host);actions[seen]=-1
                    host.timeout=max(.1,min(60,deadline-time.monotonic()));host.advance(actions)
            del actor
        return publish()
    except (InterruptedError,TimeoutError):return publish('authorization boundary or host timeout')
