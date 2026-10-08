"""Graph for unchanged full-minibatch behavior verification.

Every owned row is verified. Partial minibatches stay on the original eager
path. Frozen capture references the learned policy's existing tensor addresses,
so refreshed parameters and effect/card tables are read each time.
"""
import torch

class VerificationSetupStopped(RuntimeError):pass

def tensor_signature(value):
 return (value.data_ptr(),value.dtype,value.device,tuple(value.shape),tuple(value.stride()))

class BehaviorGraph:
 def __init__(self,policy,store,rows,*,stop_check=None,heartbeat=None):
  self.policy=policy;self.store=store;self.device=next(policy.parameters()).device
  self.rows=rows.detach().clone();self.maximum=torch.zeros(2,device=self.device)
  self.source_tensors=[getattr(store,name) for name in ('obs','candidates','mask','packet')]
  self.source_signatures=[tensor_signature(value) for value in self.source_tensors]
  self.policy_tensors=dict(policy.named_parameters())|dict(policy.named_buffers())
  self.policy_signatures={name:tensor_signature(value) for name,value in self.policy_tensors.items()}
  if not store.gpu or self.device.type!='cuda':raise ValueError('CUDA owned inputs required')
  stream=torch.cuda.Stream(device=self.device);stream.wait_stream(torch.cuda.current_stream(self.device))
  try:
   with torch.cuda.stream(stream),torch.no_grad():
    for _ in range(3):
     if stop_check is not None and stop_check():raise VerificationSetupStopped()
     self.compute()
     if heartbeat is not None:heartbeat()
  finally:
   torch.cuda.current_stream(self.device).wait_stream(stream)
   torch.cuda.current_stream(self.device).synchronize()
  if stop_check is not None and stop_check():raise VerificationSetupStopped()
  self.graph=torch.cuda.CUDAGraph()
  with torch.cuda.device(self.device),torch.cuda.graph(self.graph),torch.no_grad():self.compute()
  if heartbeat is not None:heartbeat()
  if stop_check is not None and stop_check():raise VerificationSetupStopped()
  self.maximum.zero_()

 def compute(self):
  obs,candidates,mask,packet=[value.index_select(0,self.rows) for value in self.source_tensors]
  logits,values=self.policy(obs,candidates,mask)
  logp=logits.log_softmax(-1).gather(1,packet[:,:1].long()).squeeze(1)
  errors=torch.stack(((logp-packet[:,1]).abs().max(),(values-packet[:,2]).abs().max()))
  self.maximum.copy_(torch.maximum(self.maximum,errors))

 def validate_sources(self,policy,store):
  if self.policy is not policy or self.store is not store:raise ValueError('Captured policy or owned storage changed')
  if [tensor_signature(getattr(store,name)) for name in ('obs','candidates','mask','packet')]!=self.source_signatures:
   raise ValueError('Captured owned input tensor addresses/schema changed')
  current=dict(policy.named_parameters())|dict(policy.named_buffers())
  if {name:tensor_signature(value) for name,value in current.items()}!=self.policy_signatures:
   raise ValueError('Captured policy tensor addresses/schema changed')

 def run(self,rows):
  if rows.shape!=self.rows.shape:raise ValueError('Exact full minibatch required')
  self.rows.copy_(rows);self.graph.replay()

@torch.no_grad()
def verify_behavior(learner,store,indices,stop_check=None,heartbeat=None):
 import numpy as np
 if not store.gpu:raise ValueError('CUDA owned inputs required')
 indices=torch.as_tensor(indices,device=learner.device)
 graph=getattr(learner,'behavior_graph',None)
 if graph is not None:
  graph.validate_sources(learner.policy,store)
  graph.maximum.zero_()
 maximum=None
 for start in range(0,len(indices),learner.config.minibatch):
  if stop_check is not None and stop_check():return {'behavior_verification_complete':False,'deadline_stop':True}
  rows=indices[start:start+learner.config.minibatch]
  if len(rows)==learner.config.minibatch:
   if graph is None:
    try:graph=BehaviorGraph(learner.policy,store,rows,stop_check=stop_check,heartbeat=heartbeat)
    except VerificationSetupStopped:return {'behavior_verification_complete':False,'deadline_stop':True}
    learner.behavior_graph=graph
   graph.run(rows);maximum=graph.maximum
  else:
   obs,candidates,mask,packet=learner.batch(store,rows)
   logits,values=learner.policy(obs,candidates,mask)
   logp=logits.log_softmax(-1).gather(1,packet[:,:1].long()).squeeze(1)
   errors=torch.stack(((logp-packet[:,1]).abs().max(),(values-packet[:,2]).abs().max()))
   maximum=errors if maximum is None else torch.maximum(maximum,errors)
  if heartbeat is not None:heartbeat()
 if maximum is None:maximum=torch.zeros(2,device=learner.device)
 maximum_logp,maximum_value=maximum.cpu().tolist()
 if not np.isfinite([maximum_logp,maximum_value]).all() or maximum_logp>.002 or maximum_value>.001:
  raise RuntimeError(f'Actor/learner behavior mismatch {maximum_logp=} {maximum_value=}')
 return {'behavior_logp_error':maximum_logp,'behavior_value_error':maximum_value,'behavior_verification_complete':True}
