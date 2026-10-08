"""Paired-seed depth versus the same frozen network without lookahead."""
from pathlib import Path
import os,sys,time,json,argparse
HERE=Path(__file__).resolve().parent
sys.path.insert(0,str(HERE.parent/'ZeroDepthTraining'));sys.path.insert(0,str(HERE))
import torch,numpy as np
from league import load_frozen_policy
from client import SearchHost
from learning import inputs
p=argparse.ArgumentParser();p.add_argument('--games',type=int,default=80);p.add_argument('--depth',type=int,default=24);p.add_argument('--output',required=True);args=p.parse_args()
os.environ['SHARDS_DEPTH_EVAL']='1';torch.set_num_threads(1)
policy,_=load_frozen_policy('/home/lva/.local/share/shards-zero-depth/2026-10-04/stall-intervention/after-policy',device='cuda')
@torch.no_grad()
def infer(packet,meta):
    logits,value=policy(*inputs(packet,'cuda'));return logits.cpu().numpy(),value.cpu().numpy()
results=[];started=time.monotonic()
with SearchHost(batch=16,depth=args.depth,width=4,worlds=2) as host:
    host.deadline=started+1200
    for offset in range(0,args.games,16):
        collection=host.collect(92330000+offset//2,infer)
        for g in collection['report']['games']:
            g['search_seat']=g['lane']%2;results.append(g)
        del collection
        print(json.dumps({'games':len(results),'search_wins':sum(g['completed'] and g['winner']==g['search_seat'] for g in results),'seconds':time.monotonic()-started}),flush=True)
summary={'games':len(results),'search_wins':sum(g['completed'] and g['winner']==g['search_seat'] for g in results),'censored':sum(not g['completed'] for g in results),'seconds':time.monotonic()-started,'results':results,'settings':{'depth':args.depth,'candidates':4,'worlds':2},'selection':'categorical policy on both sides; search uses public sampled worlds and sampled continuations'}
Path(args.output).write_text(json.dumps(summary,indent=2));print(json.dumps({k:v for k,v in summary.items() if k!='results'}))
