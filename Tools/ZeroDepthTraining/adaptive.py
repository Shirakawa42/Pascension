"""Pack live lanes into frozen inference buckets; never evaluate a game branch.

All buckets are captured during construction, before the trainer restores any
checkpoint RNG. Bucket changes preserve behavior probabilities, not identical
sampled trajectories compared with the full-batch actor. Padding never enters
experience.
"""
from __future__ import annotations

import copy

import numpy as np
import torch

from model import Actor


class AdaptiveActor:
    def __init__(self, policy, batch, *, graph=True, device=None, min_bucket=32, compiled=False, packed=False):
        if type(batch) is not int or batch < 1 or type(min_bucket) is not int or min_bucket < 1:
            raise ValueError("Positive integer batch and bucket minimum required")
        self.batch = batch
        self.device = torch.device(device or next(policy.parameters()).device)
        self.cuda = self.device.type == "cuda"
        if graph and not self.cuda:
            raise ValueError("CUDA graphs require a CUDA device")
        self.graph_enabled = graph
        self.policy = copy.deepcopy(policy).to(self.device).eval().requires_grad_(False)
        # Each child maintains its own cached table and stable graph addresses.
        self.policy.cached_table = None
        sizes = []
        size = min_bucket
        while size < batch:
            sizes.append(size)
            size *= 2
        self.buckets = tuple([*sizes, batch])
        self.children = {size: Actor(self.policy, size, graph=graph, device=self.device, compiled=compiled, packed=packed)
                         for size in self.buckets}
        self.active_actor = None
        self._source = np.full(batch, -1, np.int64)
        self.last_active_count = 0

    @torch.inference_mode()
    def refresh(self, policy):
        self.policy.load_state_dict(policy.state_dict(), strict=True)
        self.policy.cached_table = None
        for child in self.children.values():
            child.refresh(policy)

    def source_rows(self, lanes):
        """Map global host lanes to the current child's packed device rows."""
        lanes = np.asarray(lanes)
        if lanes.ndim != 1 or lanes.size and lanes.dtype.kind not in "iu":
            raise ValueError("Global source lanes must be a one-dimensional integer vector")
        if not lanes.size:
            return np.empty(0, np.int64)
        if np.any(lanes < 0) or np.any(lanes >= self.batch):
            raise ValueError("Global source lane outside actor batch")
        rows = self._source[lanes]
        if np.any(rows < 0):
            raise ValueError("A retained lane was absent from the current packed actor inference")
        return rows.copy()

    def _borrow(self, name):
        if self.active_actor is None:
            raise RuntimeError("No active inference bucket; empty lanes must not enter experience")
        return getattr(self.active_actor, name)

    @property
    def obs(self):
        return self._borrow("obs")

    @property
    def candidates(self):
        return self._borrow("candidates")

    @property
    def mask(self):
        return self._borrow("mask")

    @property
    def packet(self):
        return self._borrow("packet")

    @torch.inference_mode()
    def act(self, host, active=None):
        done = np.asarray(host.done)
        if done.shape != (self.batch,):
            raise ValueError("Host terminal vector differs from actor batch")
        if active is None:
            active = done == 0
        else:
            active = np.asarray(active)
            if active.shape != (self.batch,) or active.dtype.kind != "b":
                raise ValueError("Active mask must be a boolean vector over all host lanes")
            if np.any(active & (done != 0)):
                raise ValueError("Terminal lanes cannot be selected for inference")
        lanes = np.flatnonzero(active)
        self._source.fill(-1)
        self._source[lanes] = np.arange(lanes.size)
        self.last_active_count = lanes.size
        actions = np.full(self.batch, -1, np.int32)
        packet = np.zeros((self.batch, 3), np.float32)
        if not lanes.size:
            self.active_actor = None
            return actions, packet
        if not host.mask[lanes].any(axis=1).all():
            raise RuntimeError("Selected live lane has no legal action")
        bucket = next(size for size in self.buckets if size >= lanes.size)
        child = self.children[bucket]
        self.active_actor = child
        if child.packed_transport is not None:
            packed_actions, packed_packet = child.act_from_rows(host, lanes)
            actions[lanes] = packed_actions[:lanes.size]
            packet[lanes] = packed_packet[:lanes.size]
            return actions, packet
        for staging, source in ((child.obs_host, host.obs), (child.candidates_host, host.candidates),
                                (child.mask_host, host.mask)):
            if source.shape[0] != self.batch:
                raise ValueError("Host feature array differs from actor batch")
            destination = staging.numpy()
            # All indices came from this batch's validated boolean mask. Clip
            # therefore changes no index and avoids NumPy's buffered raise mode.
            np.take(source, lanes, axis=0, out=destination[:lanes.size], mode="clip")
            destination[lanes.size:].fill(0)
        packed_actions, packed_packet = child.act_staged()
        actions[lanes] = packed_actions[:lanes.size]
        packet[lanes] = packed_packet[:lanes.size]
        return actions, packet
