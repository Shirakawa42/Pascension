"""Bucketed frozen inference over explicitly selected authorized host lanes.

Only selected lanes are packed; padding repeats a valid selected observation.
Returned packets and exposed raw GPU views omit every padded row. This does not
prune legal actions or change policy logits, sampling, or behavior likelihoods.
"""
from __future__ import annotations

import numpy as np
import torch

from learning_model import (ACTION_DIM, MAX_ACTIONS, OBS_DIM, UPLOAD_FLOATS_PER_ROW,
                            InvalidLearningBatch, LearningActor, LearningPolicy)


class AdaptiveLearningActor:
    """Select the smallest captured actor that fits a nonempty lane subset.

    ``obs/candidates/mask/packet`` are borrowed device views with precisely
    ``valid_rows`` rows, numbered locally from zero. ``original_lanes`` maps them
    back to episodes. Copy these views into owned rollout storage before the next
    act_subset call. Returned NumPy actions and packets own their storage.
    The full host buffer is read only; only authorized encoded fields are used.
    """

    def __init__(self, policy: LearningPolicy, max_batch: int = 256, *, graph: bool = True,
                 buckets=None, device=None, validate_inputs: bool = True):
        if not 1 <= max_batch <= 4096:
            raise ValueError("max_batch must be in [1,4096]")
        if buckets is None:
            buckets = [size for size in (32, 64, 128, 256, 512, 1024, 2048, 4096) if size < max_batch] + [max_batch]
        if not buckets or any(not isinstance(size, int) or size < 1 or size > max_batch for size in buckets):
            raise ValueError("Buckets must be positive integer sizes bounded by max_batch")
        self.buckets = tuple(sorted(set(buckets)))
        if self.buckets[-1] != max_batch:
            raise ValueError("Largest actor bucket must equal max_batch")
        self.batch = self.max_batch = max_batch
        self.actors = {size: LearningActor(policy, size, graph=graph, device=device, validate_inputs=validate_inputs)
                       for size in self.buckets}
        self.device = self.actors[max_batch].device
        self.version = 0
        self.valid_rows = self.bucket_batch = 0
        self.original_lanes = None
        self.active_actor = None
        self._destinations = {}
        for size, actor in self.actors.items():
            upload = actor.upload.numpy()
            self._destinations[size] = (
                upload[:size*OBS_DIM].reshape(size, OBS_DIM),
                upload[size*OBS_DIM:size*(OBS_DIM+MAX_ACTIONS*ACTION_DIM)].reshape(size, MAX_ACTIONS, ACTION_DIM),
                upload[size*(OBS_DIM+MAX_ACTIONS*ACTION_DIM):].reshape(size, MAX_ACTIONS),
            )

    def _source(self, host):
        upload = host.upload
        if not torch.is_tensor(upload) or upload.device.type != "cpu" or upload.dtype != torch.float32 or upload.ndim != 1 or not upload.is_contiguous():
            raise InvalidLearningBatch("Adaptive host upload must be a contiguous flat CPU FP32 tensor")
        if upload.numel() == 0 or upload.numel() % UPLOAD_FLOATS_PER_ROW:
            raise InvalidLearningBatch("Adaptive host upload has an invalid schema length")
        batch = upload.numel() // UPLOAD_FLOATS_PER_ROW
        values = upload.numpy()
        return batch, (
            values[:batch*OBS_DIM].reshape(batch, OBS_DIM),
            values[batch*OBS_DIM:batch*(OBS_DIM+MAX_ACTIONS*ACTION_DIM)].reshape(batch, MAX_ACTIONS, ACTION_DIM),
            values[batch*(OBS_DIM+MAX_ACTIONS*ACTION_DIM):].reshape(batch, MAX_ACTIONS),
        )

    def act_subset(self, host, original_lanes):
        host_batch, source = self._source(host)
        lanes = np.asarray(original_lanes)
        if lanes.ndim != 1 or lanes.dtype.kind not in "iu" or not 1 <= len(lanes) <= self.max_batch:
            raise InvalidLearningBatch("Select a nonempty 1D integer lane subset bounded by max_batch")
        if np.any(lanes < 0) or np.any(lanes >= host_batch) or len(np.unique(lanes)) != len(lanes):
            raise InvalidLearningBatch("Selected lanes must be unique and within the host batch")
        lanes = np.ascontiguousarray(lanes, dtype=np.int64)
        size = next(size for size in self.buckets if size >= len(lanes))
        actor = self.actors[size]
        if size == host_batch == len(lanes) and np.array_equal(lanes, np.arange(host_batch)):
            # Already-complete ordered input needs no CPU repacking.
            actions, packet = actor.act(host)
        else:
            for original, destination in zip(source, self._destinations[size]):
                # mode='clip' avoids NumPy's unconditional raise-mode out buffering.
                # Explicit bounds validation above happens before any destination write.
                np.take(original, lanes, axis=0, out=destination[:len(lanes)], mode="clip")
                if len(lanes) < size:
                    destination[len(lanes):] = destination[0]
            actions, packet = actor.act()
        self.active_actor = actor
        self.valid_rows, self.bucket_batch = len(lanes), size
        self.original_lanes = lanes.copy()
        self.obs = actor.obs[:self.valid_rows]
        self.candidates = actor.candidates[:self.valid_rows]
        self.mask = actor.mask[:self.valid_rows]
        self.packet = actor.packet[:self.valid_rows]
        return actions[:self.valid_rows].copy(), packet[:self.valid_rows].copy()

    @torch.no_grad()
    def refresh(self, policy: LearningPolicy, version: int | None = None):
        next_version = self.version + 1 if version is None else int(version)
        for actor in self.actors.values():
            actor.refresh(policy, version=next_version)
        self.version = next_version

    def compute_logits(self):
        if self.active_actor is None:
            raise RuntimeError("act_subset must select a bucket before compute_logits")
        logits, values = self.active_actor.compute_logits()
        return logits[:self.valid_rows], values[:self.valid_rows]

    def state_dict(self):
        # All buckets contain identical frozen weights/configuration and version.
        state = self.actors[self.max_batch].state_dict()
        state.update(buckets=list(self.buckets), max_batch=self.max_batch)
        return state
