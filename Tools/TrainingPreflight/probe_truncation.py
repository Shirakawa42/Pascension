"""Frozen diagnostic on seeds from the failed pilot generation; no updates."""
from collections import deque
import json
from pathlib import Path
import time
import numpy as np
import torch
from adaptive_actor import AdaptiveLearningActor
from evaluate_checkpoints import load_selection, materialize_policy
from learning_rollout import LearningHost

ROOT = Path('/home/lva/.local/share/shards-training/2026-09-26')
OUT = ROOT/'truncation-probe'
OUT.mkdir(exist_ok=True)
torch.set_num_threads(1)
torch.set_num_interop_threads(1)
torch.backends.fp32_precision = 'ieee'
torch.backends.cuda.matmul.fp32_precision = 'tf32'
torch.manual_seed(9226)
selection = load_selection(ROOT/'repro128/latest.soicp')
policy = materialize_policy(selection, 'cuda')
actor = AdaptiveLearningActor(policy,256,graph=True)
start = time.monotonic()
seed = 0x1000000000000000+57*256
found = False
completed = 0
for attempt in range(60):
    if found or time.monotonic()-start>180:
        break
    host = LearningHost(256,8,seed,pinned=True,transport='shared',split_branches=8)
    active = np.ones(256,dtype=bool)
    history = deque(maxlen=256)
    actions_history = []
    try:
        for step in range(30000):
            rows=np.flatnonzero(active)
            actions=np.zeros(256,dtype=np.int64)
            actions[rows],_=actor.act_subset(host,rows)
            previous=host.obs[:,:112].copy()
            selected=host.candidates[np.arange(256),actions].copy()
            history.append((previous,selected))
            actions_history.append(np.where(active,actions,-1).astype(np.int16))
            host.advance_active(actions,active)
            bad=np.flatnonzero(host.done==2)
            if len(bad):
                found=True
                np.save(OUT/'actions.npy',np.stack(actions_history))
                evidence={'attempt':attempt,'step':step+1,'checkpoint':selection.metadata,
                    'lanes':[{'lane':int(lane),'seed':seed+int(lane),
                        'pre_reset_round':float(previous[lane,2]*100),
                        'trace':[{'scalars':o[lane].tolist(),'chosen':a[lane].tolist()} for o,a in history]}
                        for lane in bad]}
                (OUT/'evidence.json').write_text(json.dumps(evidence,indent=2)+'\n')
                print({'found':True,'attempt':attempt,'step':step+1,'lanes':bad.tolist(),
                       'rounds':[float(previous[l,2]*100) for l in bad]},flush=True)
                break
            ended=active&(host.done==1)
            completed+=int(ended.sum())
            active[ended]=False
            if not active.any():break
    finally:
        host.close()
    print({'attempt':attempt,'completed':completed,'seconds':time.monotonic()-start},flush=True)
(OUT/'summary.json').write_text(json.dumps({'found':found,'completed':completed,'seconds':time.monotonic()-start},indent=2)+'\n')
