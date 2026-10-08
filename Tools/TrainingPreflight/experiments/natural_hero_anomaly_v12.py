"""Bounded frozen natural-draft trace audit. No interventions or optimization."""
import sys,json,time
from pathlib import Path
sys.path.insert(0,str(Path(__file__).resolve().parents[1]))
import torch,numpy as np
import variant_v12_runtime as runtime
import evaluate_checkpoints as checkpoints
import learning_model
from wire_v10 import LearningHost
from bench_common import save_json
root=Path('/home/lva/.local/share/shards-training/2026-09-26/overnight-v12')
torch.set_num_threads(1);runtime.install();checkpoints.identity=runtime.variant_identity;checkpoints.LearningPolicy=learning_model.LearningPolicy
selection=checkpoints.load_selection(root/'retained/segment-0029/latest.soicp','learner');policy=checkpoints.materialize_policy(selection,'cpu');before=checkpoints.policy_hash(policy)
from learning_model import LearningActor
actor=LearningActor(policy,128,device='cpu',graph=False,validate_inputs=True)
torch.manual_seed(270927);started=time.monotonic();records=[];drafts=[];examined=0
for batch in range(256):
 if len(records)>=8 or time.monotonic()-started>120:break
 seed=0xC200000000000000+batch*128
 host=LearningHost(128,1,seed,pinned=False,transport='shared',split_branches=8)
 try:
  actions_log=[]
  for stage in range(2):
   actions,packet=actor.act(host)
   if batch==0:
    with torch.no_grad():
     o=torch.from_numpy(host.obs.copy());c=torch.from_numpy(host.candidates.copy());m=torch.from_numpy(host.mask.copy()).bool();l,v=policy(o,c,m)
    drafts.append({'stage':stage,'acting_seat':int(host.seats[0]),'choices':[{'ordinal':round(float(c[0,i,25])*128),'kind':int(c[0,i,:16].argmax()),'probability':float(l.softmax(-1)[0,i])} for i in torch.where(m[0])[0]]})
   actions_log.append(actions.copy());host.advance(actions)
  examined+=128
  own=np.rint(host.obs[:,22]*5).astype(int);enemy=np.rint(host.obs[:,70]*5).astype(int)
  active=(own==4)|(enemy==4)
  lanes=np.flatnonzero(active);entries={}
  for lane in lanes:
   seat=int(host.seats[lane]);heros=[None,None];heros[seat]=int(own[lane]);heros[1-seat]=int(enemy[lane]);entries[lane]={'seed':seed+int(lane),'heroes':heros,'trace':[],'hero_draft_actions':[int(a[lane]) for a in actions_log]}
  for step in range(1800):
   if not active.any():break
   actions,_=actor.act(host)
   for lane in np.flatnonzero(active):
    a=int(actions[lane]);kind=int(host.candidates[lane,a,:16].argmax());entry=entries[lane]
    if len(entry['trace'])<8 or kind==11:
     entry['trace'].append({'step':step,'seat':int(host.seats[lane]),'kind':kind,'round':round(float(host.obs[lane,2])*100),'own_hp':round(float(host.obs[lane,16])*50),'enemy_hp':round(float(host.obs[lane,64])*50),'hero':round(float(host.obs[lane,22])*5)})
   host.advance_active(actions,active)
   for lane in np.flatnonzero(active&(host.done!=0)):
    entry=entries[lane];entry.update(done=int(host.done[lane]),rewards=host.rewards[lane].tolist(),steps=step+1);records.append(entry)
   active &=host.done==0
  if active.any():raise RuntimeError('Bounded trace failed to finish observed rare game')
 finally:host.close()
report={'schema':'shards-natural-hero-anomaly-v12','examined_natural_starts':examined,'rare_kosynwu_games':records,'first_draft_distributions':drafts,'seconds':time.monotonic()-started,'optimizer_updates':0,'frozen_weights_unchanged':before==checkpoints.policy_hash(policy),'cuda_initialized':torch.cuda.is_initialized(),'scope':'Frozen final learner natural selfplay; search stops after8rare games or120seconds; discovery sample, not winrate estimate'}
save_json('Tools/TrainingPreflight/results/v12-natural-hero-anomaly-2026-09-27.json',report)
print(json.dumps(report))
