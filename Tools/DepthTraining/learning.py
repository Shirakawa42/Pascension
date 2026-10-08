"""Expert iteration: searched root-action imitation and terminal outcome values.

Search actions are not on-policy samples. No PPO ratios or fictitious likelihoods
are used. Branches never become real-game outcome training rows.
"""
import numpy as np
import torch
import torch.nn.functional as F
from client import OBS, ACTIONS, FEATURES

def inputs(packet, device):
    x = torch.as_tensor(packet, device=device)
    return x[:, :OBS], x[:, OBS:OBS+ACTIONS*FEATURES].reshape(-1,ACTIONS,FEATURES), x[:, -ACTIONS:]

def labels(collection):
    games = {g['lane']: g for g in collection['report']['games']}
    keep = np.array([games[int(lane)]['completed'] for lane in collection['lanes']], dtype=bool)
    outcome = np.array([0. if games[int(lane)]['winner'] < 0 else 1. if games[int(lane)]['winner'] == int(actor) else -1.
        for lane, actor in zip(collection['lanes'], collection['actors'])], dtype=np.float32)
    return keep, outcome

def update(policy, optimizer, collection, *, epochs=2, minibatch=256, kl_weight=.2):
    keep, outcome = labels(collection)
    indices = np.flatnonzero(keep)
    if not len(indices): return {'rows':0, 'optimizer_steps':0}
    device = next(policy.parameters()).device
    policy.train(); totals=[];steps=0
    for _ in range(epochs):
        np.random.shuffle(indices)
        for offset in range(0, len(indices), minibatch):
            ix=indices[offset:offset+minibatch]
            logits, value=policy(*inputs(collection['rows'][ix],device))
            target=torch.as_tensor(collection['targets'][ix], device=device, dtype=torch.long)
            returns=torch.as_tensor(outcome[ix],device=device)
            old=torch.as_tensor(collection['priors'][ix],device=device).softmax(-1)
            # Keep the full prior distribution unless search found an improvement.
            # This avoids repeatedly sharpening unchanged greedy choices.
            old=torch.as_tensor(collection['priors'][ix],device=device).softmax(-1)
            changed=torch.as_tensor(collection['improved'][ix],device=device,dtype=torch.float32).unsqueeze(-1)
            teacher=old*(1-.5*changed)+.5*changed*F.one_hot(target,logits.shape[-1])
            ce=-(teacher*logits.log_softmax(-1)).sum(-1).mean()
            kl=(old*(old.clamp_min(1e-30).log()-logits.log_softmax(-1))).sum(-1).mean()
            vf=F.mse_loss(value,returns)
            loss=ce+.5*vf+kl_weight*kl
            if not torch.isfinite(loss): raise RuntimeError('Nonfinite expert loss')
            optimizer.zero_grad(set_to_none=True);loss.backward()
            norm=torch.nn.utils.clip_grad_norm_(policy.parameters(),1.)
            if not torch.isfinite(norm): raise RuntimeError('Nonfinite expert gradients')
            optimizer.step();steps+=1;totals.append((float(ce.detach()),float(vf.detach()),float(kl.detach())))
    policy.eval()
    mean=np.mean(totals,axis=0)
    return {'rows':len(indices),'optimizer_steps':steps,'search_cross_entropy':mean[0], 'terminal_value_mse':mean[1],'distillation_kl':mean[2]}
