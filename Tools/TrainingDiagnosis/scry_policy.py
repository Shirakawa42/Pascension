"""Shared Scry-only calibration for sampling, PPO likelihoods and evaluation."""
import math
import torch
from torch import nn


def validate_calibration(temperature=1.,finish_bias=0.):
    if not math.isfinite(temperature) or not 1<=temperature<=64:
        raise ValueError('Scry temperature must be finite and in [1,64]')
    if not math.isfinite(finish_bias) or abs(finish_bias)>80:
        raise ValueError('Scry Finish bias must be finite and within 80 logits')
    return dict(scry_temperature=float(temperature),scry_finish_bias=float(finish_bias))


def calibrate(logits,obs,candidates,mask,*,context_count,scry_context,temperature=1.,finish_bias=0.):
    if temperature==1. and finish_bias==0.:return logits
    scry=(obs[:,144]>.5)&((obs[:,157]*context_count).round()==scry_context)
    changed=logits if temperature==1. else logits/temperature
    if finish_bias:
        finish=(candidates[:,:,13]>.5)&mask.bool()
        changed=torch.where(finish,changed+finish_bias,changed)
    changed=changed.masked_fill(~mask.bool(),-1.e9)
    return torch.where(scry[:,None],changed,logits)


class ScryPolicy(nn.Module):
    """No new learned parameters; the base policy receives the exact gradient."""
    def __init__(self,base,*,temperature=1.,finish_bias=0.):
        super().__init__();self.base=base
        self.calibration=validate_calibration(temperature,finish_bias)
        self.temperature=float(temperature);self.finish_bias=float(finish_bias)
        self.catalog=base.catalog;self.config=base.config
        self.context_count=len(self.catalog['contexts']);self.scry_context=self.catalog['contexts'].index('soi.scry')

    def cache_frozen_table(self):self.base.cache_frozen_table()

    def forward(self,obs,candidates,mask):
        logits,value=self.base(obs,candidates,mask)
        return calibrate(logits,obs,candidates,mask,context_count=self.context_count,scry_context=self.scry_context,
                         temperature=self.temperature,finish_bias=self.finish_bias),value


def unwrap(policy):return policy.base if isinstance(policy,ScryPolicy) else policy


def require_calibration(manifest,temperature=1.,finish_bias=0.):
    actual=validate_calibration(temperature,finish_bias)
    required=manifest.get('required_inference_calibration')
    if required is not None and required!=actual:
        raise ValueError('This checkpoint requires its recorded Scry calibration; refusing a different behavior distribution')
    return actual
