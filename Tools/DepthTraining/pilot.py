from pathlib import Path
import sys,time,json,argparse
HERE=Path(__file__).resolve().parent
sys.path.insert(0,str(HERE.parent/'ZeroDepthTraining'))
sys.path.insert(0,str(HERE))
import torch,numpy as np
from league import load_frozen_policy
from client import SearchHost
from learning import inputs,update
p=argparse.ArgumentParser();p.add_argument('--batch',type=int,default=8);p.add_argument('--depth',type=int,default=8);p.add_argument('--width',type=int,default=4);p.add_argument('--worlds',type=int,default=2);p.add_argument('--output',required=True);p.add_argument('--seed',type=int,default=92310400);args=p.parse_args()
torch.set_num_threads(1);torch.manual_seed(913);np.random.seed(913)
policy,manifest=load_frozen_policy('/home/lva/.local/share/shards-zero-depth/2026-10-04/stall-intervention/after-policy',device='cuda');policy.requires_grad_(True)
started=time.monotonic();calls=0;rootsteps=0
@torch.no_grad()
def infer(packet,meta):
    global calls,rootsteps
    logits,value=policy(*inputs(packet,'cuda'));calls+=1
    if meta is not None:
        rootsteps+=1
        if rootsteps%100==0:print(json.dumps({'seconds':time.monotonic()-started,'root_steps':rootsteps,'inference_calls':calls}),flush=True)
    return logits.cpu().numpy(),value.cpu().numpy()
with SearchHost(batch=args.batch,depth=args.depth,width=args.width,worlds=args.worlds) as host:
    host.deadline=started+900
    collection=host.collect(args.seed,infer)
seconds=time.monotonic()-started
optimizer=torch.optim.AdamW(policy.parameters(),lr=1e-5)
learn=update(policy,optimizer,collection,epochs=1)
result={'config':vars(args),'seconds':seconds,'games_per_second':args.batch/seconds,'report':collection['report'],'learning':learn,'rows':len(collection['rows']),'parameters':sum(x.numel() for x in policy.parameters())}
Path(args.output).write_text(json.dumps(result,indent=2));print(json.dumps(result),flush=True)
