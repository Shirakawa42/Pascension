"""Combined lossless-expand/ordinary-policy/packet-readback CUDA graph.

Dynamic packed payload/plan H2D copies remain eager and preserve float32 bytes.
One stream still owns all inference and the existing finish() staging lease.
No model, sampling math, legal action, public schema or rollout policy changes.
"""
from __future__ import annotations

import numpy as np
import torch
from pipeline_actor import PipelineActor


class CombinedGraphActor(PipelineActor):
    def __init__(self, *args, **kwargs):
        if kwargs.get("graph", True) is not True:
            raise ValueError("Combined actor graph requires enabled CUDA graphs")
        super().__init__(*args, **kwargs)
        self.combined_graphs = {}
        self.combined_addresses = {}
        self.combined_policy_addresses = {}
        self.combined_references = {}
        for size, child in self.children.items():
            transport = child.packed_transport
            # Discard superseded graphs before allocating the combined pool.
            child.graph = None
            transport.graph = None
            transport.graph_enabled = False
            transport._destinations = tuple(value.data_ptr() for value in
                                             (child.obs, child.candidates, child.mask))
            current = torch.cuda.current_stream(self.device)
            stream = torch.cuda.Stream(device=self.device)
            stream.wait_stream(current)
            try:
                with torch.cuda.stream(stream), torch.inference_mode():
                    for _ in range(3):
                        transport._expand_into(child.obs, child.candidates, child.mask)
                        child.packet = child.compute()
                        child.output_host.copy_(child.packet, non_blocking=True)
                current.wait_stream(stream)
                current.synchronize()
                graph = torch.cuda.CUDAGraph()
                with torch.inference_mode(), torch.cuda.graph(graph, stream=stream):
                    transport._expand_into(child.obs, child.candidates, child.mask)
                    child.packet = child.compute()
                    child.output_host.copy_(child.packet, non_blocking=True)
            finally:
                # Side-stream readers must finish even if warmup/capture raises.
                current.wait_stream(stream)
                current.synchronize()
            self.combined_graphs[size] = graph
            self.combined_addresses[size] = self._addresses(child)
            self.combined_policy_addresses[size] = self._policy_addresses(child)
            self.combined_references[size] = self._tensors(child) + self._policy_tensors(child)

    @staticmethod
    def _signature(value):
        return (value.data_ptr(), tuple(value.shape), value.stride(), value.dtype,
                value.device, value.is_pinned() if value.device.type == "cpu" else False)

    @staticmethod
    def _tensors(child):
        transport = child.packed_transport
        return (transport.packed, transport.metadata, child.obs, child.candidates,
                child.mask, child.packet, child.output_host)

    @classmethod
    def _addresses(cls, child):
        return tuple(cls._signature(value) for value in cls._tensors(child))

    @staticmethod
    def _policy_tensors(child):
        return tuple(child.policy.parameters()) + tuple(child.policy.buffers()) + (child.policy.cached_table,)

    @classmethod
    def _policy_addresses(cls, child):
        return tuple(cls._signature(value) for value in cls._policy_tensors(child))

    @torch.inference_mode()
    def refresh(self, policy):
        self._idle()
        for size, child in self.children.items():
            if self._policy_addresses(child) != self.combined_policy_addresses[size]:
                raise ValueError("Captured policy/table layouts or addresses changed")
        super().refresh(policy)
        for size, child in self.children.items():
            if self._policy_addresses(child) != self.combined_policy_addresses[size]:
                raise ValueError("Policy refresh replaced a captured parameter/table")

    @torch.inference_mode()
    def enqueue(self, host, active=None):
        self._idle()
        self._source.fill(-1)
        self.active_actor = None
        self.last_active_count = 0
        done = np.asarray(host.done)
        if done.shape != (self.batch,):
            raise ValueError("Host terminal vector differs from actor batch")
        if active is None:
            active = done == 0
        else:
            active = np.asarray(active)
            if active.shape != (self.batch,) or active.dtype.kind != "b":
                raise ValueError("Active mask must be boolean over all host lanes")
            if np.any(active & (done != 0)):
                raise ValueError("Terminal lanes cannot be selected for inference")
        lanes = np.flatnonzero(active)
        if not lanes.size:
            self._pending = (lanes, None, None)
            return
        if not host.mask[lanes].any(axis=1).all():
            raise RuntimeError("Selected live lane has no legal action")
        self._source[lanes] = np.arange(lanes.size)
        self.last_active_count = lanes.size
        bucket = next(size for size in self.buckets if size >= lanes.size)
        child = self.children[bucket]
        self.active_actor = child
        stream = torch.cuda.current_stream(self.device)
        self._pending = (lanes, child, stream)
        try:
            if self._addresses(child) != self.combined_addresses[bucket]:
                raise ValueError("Captured actor/transport/readback addresses changed")
            transport = child.packed_transport
            transport.stage(host, lanes)
            if transport.used_elements <= 0:
                raise RuntimeError("Packed input must be staged before upload")
            transport.packed[:transport.used_elements].copy_(
                transport.packed_host[:transport.used_elements], non_blocking=True)
            transport.metadata.copy_(transport.metadata_host, non_blocking=True)
            self.combined_graphs[bucket].replay()
        except BaseException:
            self.abandon()
            raise
