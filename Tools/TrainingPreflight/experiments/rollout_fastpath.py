"""Isolated, unadopted candidates; never patch the running training process.

ContiguousEpisodeStore keeps the existing storage/credit/checkpoint interface.
Only append(source_rows=0..n-1) changes: copy those views into independent owned
arrays directly. Arbitrary indices and aliased buffers use the original gather.
Implicit copy_ dtype conversion replaces bool()/long() temporary tensors.

validate_host_fast preserves LearningActor's guards, tolerance and errors. Its
NumPy reductions avoid the general-purpose isin/allclose helpers; this is not
native fused code and does not weaken validation or skip padded rows.

CPU differential tests establish exact data and rejection parity on their test
corpus, not CUDA equivalence or an end-to-end speedup. Before any deployment:
1. In a separately allocated GPU window, compare owned rows/masks/packets,
   censoring, credit and minibatches using the same frozen policy and inputs.
2. Replay each append at counts 1/3/7/32/64/128/256, changed inputs and reused
   actor buffers, including fallback, capacity and sealed-store boundaries.
3. Interleave append-only completed timings, then identical frozen checkpoint,
   seed/opponent/action-stream collection timings with the full real pipeline.
   Include host packing, transfers, store sealing and verification; report
   retained decisions/s, phase times, allocation and CUDA initialized state.
4. Benchmark append and validation separately and together. Reject an integrated
   regression even if a microbenchmark wins. Preserve all identity/checkpoint
   gates through an explicitly reviewed new source identity; no hot patching.

Main-run storage was 0.427/2.797 seconds per generation in the supplied sample.
Halving *all* storage would imply about 8.3% overall throughput improvement;
eliminating it completely is an unrealistic 18.0% upper bound. This candidate
does not eliminate data movement or seal/compaction work, so neither is a speed
prediction. CPU timings while the main run is active cannot rank GPU candidates.
"""
from __future__ import annotations

import numpy as np
import torch

from learning_model import (ACTION_DIM, MAX_ACTIONS, OBS_DIM, InvalidLearningBatch,
                            LearningActor)
from learning_rollout import EpisodeStore


class ContiguousEpisodeStore(EpisodeStore):
    """Exact owned prefix copies with the original indexed path as fallback.

The inherited fixed backing arrays, reset/seal/retain_completed/minibatch,
episode metadata, value targets and capacity semantics remain unchanged.
Actor buffers must retain the existing raw fixed schema. Fast copying checks
that every source contains the requested prefix before making any write, so a
short slice cannot silently broadcast or introduce padded experience.
"""

    def __init__(self, capacity=524288, device="cuda"):
        super().__init__(capacity, device)
        # The base store's backing arrays never move across reset/compaction.
        self._owned_storage_pointers = frozenset(
            getattr(self, name).untyped_storage().data_ptr()
            for name in ("obs", "candidates", "mask", "actions", "old_logp", "old_values"))

    @torch.no_grad()
    def append(self, actor, packet_gpu, lanes, seats, source_rows=None):
        if self.sealed:
            raise RuntimeError("Cannot append to an update batch")
        lanes = np.asarray(lanes, dtype=np.int64)
        seats = np.asarray(seats, dtype=np.int32)
        count = len(lanes)
        if seats.shape != lanes.shape or not np.isin(seats, [0, 1]).all():
            raise ValueError("Invalid learning-seat ownership")
        if self.rows + count > self.capacity:
            raise RuntimeError("Unresolved rollout storage full; stop without overwriting or training partial episodes")
        if not count:
            return
        source_rows = lanes if source_rows is None else np.asarray(source_rows, dtype=np.int64)
        if source_rows.shape != lanes.shape:
            raise ValueError("Source rows must match retained episode lanes")
        if source_rows.ndim != 1 or not np.array_equal(source_rows, np.arange(count, dtype=np.int64)):
            return super().append(actor, packet_gpu, lanes, seats, source_rows)

        fields = ((actor.obs, (OBS_DIM,)),
                  (actor.candidates, (MAX_ACTIONS, ACTION_DIM)),
                  (actor.mask, (MAX_ACTIONS,)), (packet_gpu, (3,)))
        for source, trailing_shape in fields:
            if (not torch.is_tensor(source) or source.ndim != len(trailing_shape)+1 or
                    tuple(source.shape[1:]) != trailing_shape or source.shape[0] < count or
                    source.device != self.obs.device):
                raise ValueError("Malformed contiguous actor buffers")
        if any(source.untyped_storage().data_ptr() in self._owned_storage_pointers for source, _ in fields):
            # Retain the original temporary-gather semantics for any unusual
            # source backed by this store, including overlapping slice ranges.
            return super().append(actor, packet_gpu, lanes, seats, source_rows)

        target = slice(self.rows, self.rows+count)
        self.obs[target].copy_(actor.obs[:count])
        self.candidates[target].copy_(actor.candidates[:count])
        self.mask[target].copy_(actor.mask[:count])
        self.actions[target].copy_(packet_gpu[:count, 0])
        self.old_logp[target].copy_(packet_gpu[:count, 1])
        self.old_values[target].copy_(packet_gpu[:count, 2])
        self.episode_ids[target] = lanes
        self.seats[target] = seats
        self.rows += count


def validate_host_fast(actor, source):
    """Drop-in unbound _validate_host candidate, including outer schema checks.

Valid inputs return the same borrowed NumPy mask view. Numeric checks can be
disabled only through the same pre-existing validate_inputs flag; binary and
nonempty masks are always checked. Error classes/messages match the baseline.
"""
    if (source.device.type != "cpu" or source.dtype != torch.float32 or
            source.shape != actor.upload.shape or not source.is_contiguous()):
        raise InvalidLearningBatch("Actor host upload must be contiguous flat CPU float32 with the fixed schema")
    arrays = source.numpy()
    masks = arrays[actor.batch*(OBS_DIM+MAX_ACTIONS*ACTION_DIM):].reshape(actor.batch, MAX_ACTIONS)
    if not ((masks == 0.) | (masks == 1.)).all() or not masks.any(axis=1).all():
        raise InvalidLearningBatch("Actor mask must be binary with at least one legal action per row")
    if actor.validate_inputs:
        if not np.isfinite(arrays).all():
            raise InvalidLearningBatch("Actor input contains a nonfinite feature")
        candidates = arrays[actor.batch*OBS_DIM:actor.batch*(OBS_DIM+MAX_ACTIONS*ACTION_DIM)].reshape(
            actor.batch, MAX_ACTIONS, ACTION_DIM)
        ids = candidates[..., 16]*192
        # All inputs are finite at this point; rtol=0 in the original allclose.
        if not ((ids >= 0) & (ids <= 192)).all() or not (np.abs(ids-np.rint(ids)) <= 2e-4).all():
            raise InvalidLearningBatch("Actor candidate card IDs violate the categorical schema")
    return masks


class FastValidationLearningActor(LearningActor):
    """Opt-in subclass for a future isolated comparison; no actor math changes."""

    _validate_host = validate_host_fast
