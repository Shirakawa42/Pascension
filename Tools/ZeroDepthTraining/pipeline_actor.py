"""Enqueue both frozen policies before waiting for their GPU packets.

Selected host rows are still owned by the existing lossless packed transport.
Every actor retains its own stable graph, pinned staging, and source mapping.
The shared stream preserves the original learner-before-opponent RNG ordering.
"""
from __future__ import annotations

import numpy as np
import torch
from adaptive import AdaptiveActor

class PipelineActor(AdaptiveActor):
    def __init__(self,*args,**kwargs):
        super().__init__(*args,**kwargs)
        self._pending=None
        if not self.cuda or not all(child.packed_transport is not None for child in self.children.values()):
            raise ValueError('Pipelined inference requires CUDA packed transport')

    def _idle(self):
        if self._pending is not None:
            raise RuntimeError('Finish queued inference before reusing its owned staging or weights')

    @torch.inference_mode()
    def refresh(self,policy):
        self._idle()
        return super().refresh(policy)

    @torch.inference_mode()
    def enqueue(self,host,active=None):
        self._idle()
        self._source.fill(-1);self.active_actor=None;self.last_active_count=0
        done=np.asarray(host.done)
        if done.shape!=(self.batch,):raise ValueError('Host terminal vector differs from actor batch')
        if active is None:active=done==0
        else:
            active=np.asarray(active)
            if active.shape!=(self.batch,) or active.dtype.kind!='b':raise ValueError('Active mask must be a boolean vector over all host lanes')
            if np.any(active & (done!=0)):raise ValueError('Terminal lanes cannot be selected for inference')
        lanes=np.flatnonzero(active)
        if not lanes.size:
            self.active_actor=None;self._pending=(lanes,None,None);return
        if not host.mask[lanes].any(axis=1).all():raise RuntimeError('Selected live lane has no legal action')
        self._source[lanes]=np.arange(lanes.size);self.last_active_count=lanes.size
        bucket=next(size for size in self.buckets if size>=lanes.size)
        child=self.children[bucket];self.active_actor=child
        stream=torch.cuda.current_stream(self.device)
        # Own the queue before any operation can raise after scheduling readers.
        # Error cleanup joins it before the pinned buffers may be reused.
        self._pending=(lanes,child,stream)
        try:
            child.packed_transport.stage(host,lanes)
            child.packed_transport.upload_into(child.obs,child.candidates,child.mask)
            if child.graph is None:child.packet=child.compute()
            else:child.graph.replay()
            child.output_host.copy_(child.packet,non_blocking=True)
        except BaseException:
            self.abandon()
            raise

    def abandon(self):
        pending=self._pending
        try:
            if pending is not None and pending[2] is not None:pending[2].synchronize()
        finally:
            self._pending=None;self._source.fill(-1);self.active_actor=None;self.last_active_count=0

    def finish(self):
        if self._pending is None:raise RuntimeError('No queued inference to finish')
        lanes,child,stream=self._pending
        actions=np.full(self.batch,-1,np.int32);packet=np.zeros((self.batch,3),np.float32)
        try:
            if stream is not None:stream.synchronize()
            if child is not None:
                values=child.output_host.numpy()
                actions[lanes]=values[:lanes.size,0].astype(np.int32)
                packet[lanes]=values[:lanes.size]
            return actions,packet
        except BaseException:
            self._source.fill(-1);self.active_actor=None;self.last_active_count=0
            raise
        finally:self._pending=None

    def act(self,host,active=None):
        self.enqueue(host,active)
        return self.finish()


def act_pair(actor,opponent,host,active,other):
    """Preserve every real row; wait after both policies have been queued."""
    if actor.device!=opponent.device:raise ValueError('Paired actors must use the same CUDA device')
    active,other=np.asarray(active),np.asarray(other)
    if active.shape!=(actor.batch,) or other.shape!=active.shape or active.dtype.kind!='b' or other.dtype.kind!='b':
        raise ValueError('Paired active masks must be boolean vectors over the actor batch')
    if np.any(other & ~active):raise ValueError('Opponent lanes must be active host lanes')
    try:
        actor.enqueue(host,active & ~other)
        opponent.enqueue(host,other)
        left=actor.finish()
        right=opponent.finish()
        return left,right
    except BaseException:
        # A failed companion must invalidate both completed and queued source
        # mappings; successful packets stay borrowable until Rollout.append.
        try:actor.abandon()
        finally:opponent.abandon()
        raise
