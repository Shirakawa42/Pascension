"""Supplement self-play with explicit, engine-verified tactical demonstrations.

This is supervised learning, not fabricated self-play returns. Every 16 accepted
PPO updates one bounded auxiliary update teaches a varied legal-action example.
All work runs inside the same training budget. Frozen actors/evaluation never
load demonstrations or apply an action override.
"""
import json
from pathlib import Path
import torch
from learning_model import PPOLearner as BaseLearner, LearningNumericalError

DATA = None

class TacticalLearner(BaseLearner):
    def __init__(self, *args, **kwargs):
        super().__init__(*args, **kwargs)
        if DATA is None: raise RuntimeError('Tactical curriculum path was not configured')
        samples=json.loads(Path(DATA).read_text())['samples']
        self.tactical_obs=torch.tensor([r['observation'] for r in samples],device=self.device,dtype=torch.float32)
        self.tactical_candidates=torch.tensor([r['candidates'] for r in samples],device=self.device,dtype=torch.float32).reshape(-1,64,32)
        self.tactical_masks=torch.tensor([r['mask'] for r in samples],device=self.device,dtype=torch.bool)
        good=torch.zeros(len(samples),64,dtype=torch.bool)
        for i,r in enumerate(samples):good[i,r['targets']]=True
        self.tactical_good=good.to(self.device)
        if not bool((self.tactical_good & ~self.tactical_masks).any().logical_not()):raise RuntimeError('Illegal tactical target')
        self.tactical_updates=0
        self.tactical_last_loss=None

    def step(self, *args, **kwargs):
        result=super().step(*args, **kwargs)
        if result['accepted'] and self.updates%16==0:
            indices=torch.randint(len(self.tactical_obs),(64,),device=self.device)
            self.optimizer.zero_grad(set_to_none=False)
            logits,_=self.policy(self.tactical_obs[indices],self.tactical_candidates[indices],self.tactical_masks[indices])
            target=logits.log_softmax(-1).masked_fill(~self.tactical_good[indices],-torch.inf).logsumexp(-1)
            loss=-target.mean()
            if not bool(torch.isfinite(loss)):raise LearningNumericalError('Nonfinite tactical objective')
            loss.backward()
            norm=torch.nn.utils.clip_grad_norm_(self.policy.parameters(),self.config.max_grad_norm,foreach=True)
            if not bool(torch.isfinite(norm)):raise LearningNumericalError('Nonfinite tactical gradient')
            self.optimizer.step()
            self.tactical_updates+=1;self.tactical_last_loss=float(loss.detach())
            result=dict(result,tactical_loss=self.tactical_last_loss,tactical_updates=self.tactical_updates)
        return result

    def state_dict(self):
        result=super().state_dict()
        result.update(tactical_updates=self.tactical_updates,tactical_last_loss=self.tactical_last_loss)
        return result

    def load_state_dict(self,state,restore_rng=True):
        super().load_state_dict(state,restore_rng=restore_rng)
        self.tactical_updates=int(state.get('tactical_updates',0))
        self.tactical_last_loss=state.get('tactical_last_loss')
