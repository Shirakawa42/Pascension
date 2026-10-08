"""Replay native IEEE float32 PPO work without per-kernel CPU launches.

Warmup/capture changes gradients only; policy and Adam state are never stepped.
The learner checks likelihood, loss, gradient norm and cancellation before Adam.
Exact partial minibatches keep the ordinary eager path and never get padded.
"""
from __future__ import annotations
import torch

# The first four positions preserve the existing guard/readback contract.
DIAGNOSTIC_FIELDS = ("approx_kl", "loss", "entropy", "gradient_norm", "clip_fraction",
                     "normalized_entropy", "mean_legal_actions", "value_mse",
                     "target_mean", "target_second_moment", "residual_mean")

@torch.no_grad()
def ppo_diagnostics(kl,loss,entropy,norm,entropy_per_row,ratio,values,targets,mask,value_mse):
    """Detached row means; caller weights them by accepted minibatch size.

    Target/residual moments support explained variance across ALL accepted rows,
    including short minibatches. A mean of minibatch explained variances would
    be incorrect. This tensor joins the existing single graph scalar readback.
    """
    legal=mask.sum(-1).to(dtype=entropy_per_row.dtype)
    normalized=torch.where(legal>1,entropy_per_row/legal.clamp_min(2).log(),0.)
    clipped=((ratio<.8)|(ratio>1.2)).to(dtype=entropy_per_row.dtype)
    residual=values-targets
    return torch.stack((kl.detach(),loss.detach(),entropy.detach(),norm.detach(),
                        clipped.mean(),normalized.mean(),legal.mean(),value_mse.detach(),
                        targets.mean(),targets.square().mean(),residual.mean()))

class GraphSetupStopped(RuntimeError):
    pass

class GpuPpoStep:
    def __init__(self,policy,config,inputs,optimizer,stop_check=None,heartbeat=None):
        self.policy=policy;self.config=config
        self.device=next(policy.parameters()).device
        self.inputs=[value.detach().clone() for value in inputs]
        self.parameters=list(policy.parameters())
        stream=torch.cuda.Stream(device=next(policy.parameters()).device)
        stream.wait_stream(torch.cuda.current_stream(self.device))
        try:
            with torch.cuda.stream(stream):
                for _ in range(3):
                    if stop_check is not None and stop_check():raise GraphSetupStopped()
                    optimizer.zero_grad(set_to_none=True)
                    self.compute()
                    if heartbeat is not None:heartbeat()
        finally:
            # Inputs were allocated on the calling stream. They must remain
            # owned until every side-stream warmup reader finishes, on errors
            # and cancellation as well as normal construction.
            torch.cuda.current_stream(self.device).wait_stream(stream)
            torch.cuda.current_stream(self.device).synchronize()
        if stop_check is not None and stop_check():raise GraphSetupStopped()
        optimizer.zero_grad(set_to_none=True)
        self.graph=torch.cuda.CUDAGraph()
        with torch.cuda.device(self.device), torch.cuda.graph(self.graph):self.result=self.compute()
        if heartbeat is not None:heartbeat()
        if stop_check is not None and stop_check():raise GraphSetupStopped()
        self.gradients=[parameter.grad for parameter in self.parameters]
        assert all(gradient is not None for gradient in self.gradients)
    def compute(self):
        obs,candidates,mask,packet,targets,advantage=self.inputs
        logits,values=self.policy(obs,candidates,mask)
        all_logp=logits.log_softmax(-1);logp=all_logp.gather(1,packet[:,:1].long()).squeeze(1)
        log_ratio=logp-packet[:,1];ratio=log_ratio.exp();kl=((ratio-1)-log_ratio).mean()
        actor_loss=-torch.minimum(ratio*advantage,ratio.clamp(.8,1.2)*advantage).mean()
        value_loss=.5*(values-targets).square().mean()
        entropy_per_row=-(all_logp.exp()*all_logp).sum(-1)
        entropy=entropy_per_row.mean()
        loss=actor_loss+.5*value_loss-self.config.entropy*entropy
        loss.backward()
        norm=torch.nn.utils.clip_grad_norm_(self.parameters,.5,error_if_nonfinite=False)
        return ppo_diagnostics(kl,loss,entropy,norm,entropy_per_row,ratio,values,targets,mask,2*value_loss.detach())
    def run(self,inputs):
        for destination,source in zip(self.inputs,inputs):destination.copy_(source)
        for parameter,gradient in zip(self.parameters,self.gradients):parameter.grad=gradient
        self.graph.replay()
        return self.result.cpu().tolist()
