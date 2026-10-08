"""Experimental enqueue/finish inference; never installed into a live trainer.

Both policies enqueue on the existing current stream. The last completion event
can then cover both jobs, avoiding an intermediate host wait. This changes no
model, sampling, padding, or ownership semantics. Callers must finish every job
before mutating host inputs, refreshing weights, or reusing either actor.
"""
from __future__ import annotations

from dataclasses import dataclass

import numpy as np
import torch

from learning_model import InvalidLearningBatch, LearningNumericalError, MAX_ACTIONS


@dataclass
class PendingActor:
    actor: object
    masks: np.ndarray
    consumed: bool = False


@torch.inference_mode()
def enqueue_actor(actor, host=None):
    if getattr(actor, "_queued_inference", None) is not None:
        raise RuntimeError("Finish the previous inference before reusing this actor")
    source = actor.upload if host is None else host.upload
    masks = actor._validate_host(source)
    if actor.cuda and not source.is_pinned():
        actor.upload.copy_(source)
        source = actor.upload
    pending = PendingActor(actor, masks)
    # Install the guard before any asynchronous work. Exceptions leave the actor
    # unusable instead of allowing an unsafe retry while a transfer may be live.
    actor._queued_inference = pending
    actor.input.copy_(source, non_blocking=actor.cuda)
    if actor.graph is None:
        actor.packet = actor.compute()
    else:
        actor.graph.replay()
    actor.output_host.copy_(actor.packet, non_blocking=actor.cuda)
    if actor.cuda:
        actor.completion.record()
    return pending


def finish_actor(pending, *, wait=True):
    actor = pending.actor
    if pending.consumed or getattr(actor, "_queued_inference", None) is not pending:
        raise RuntimeError("Inference completion may only be consumed once")
    if actor.cuda:
        if wait:
            actor.completion.synchronize()
        elif not actor.completion.query():
            raise RuntimeError("Cannot read a packet before its CUDA event completes")
    packet = actor.output_host.numpy().copy()
    if not np.isfinite(packet).all() or not np.equal(packet[:, 0], np.rint(packet[:, 0])).all():
        raise LearningNumericalError("Nonfinite actor packet or nonintegral sampled action")
    actions = packet[:, 0].astype(np.int64)
    if np.any(actions < 0) or np.any(actions >= MAX_ACTIONS) or not pending.masks[np.arange(actor.batch), actions].all():
        raise LearningNumericalError("Actor sampled an illegal candidate slot")
    if np.any(packet[:, 1] > 1e-5):
        raise LearningNumericalError("Actor log-probability exceeds zero")
    pending.consumed = True
    actor._queued_inference = None
    return actions, packet


@dataclass
class PendingSubset:
    adaptive: object
    pending: PendingActor
    lanes: np.ndarray
    bucket: int
    consumed: bool = False


def enqueue_subset(adaptive, host, original_lanes):
    if getattr(adaptive, "_queued_subset", None) is not None:
        raise RuntimeError("Finish the previous subset before reusing this actor")
    host_batch, source = adaptive._source(host)
    lanes = np.asarray(original_lanes)
    if lanes.ndim != 1 or lanes.dtype.kind not in "iu" or not 1 <= len(lanes) <= adaptive.max_batch:
        raise InvalidLearningBatch("Select a nonempty 1D integer lane subset bounded by max_batch")
    if np.any(lanes < 0) or np.any(lanes >= host_batch) or len(np.unique(lanes)) != len(lanes):
        raise InvalidLearningBatch("Selected lanes must be unique and within the host batch")
    lanes = np.ascontiguousarray(lanes, dtype=np.int64)
    size = next(size for size in adaptive.buckets if size >= len(lanes))
    actor = adaptive.actors[size]
    if getattr(actor, "_queued_inference", None) is not None:
        raise RuntimeError("Finish the bucket's previous inference before packing its pinned input")
    if size == host_batch == len(lanes) and np.array_equal(lanes, np.arange(host_batch)):
        pending = enqueue_actor(actor, host)
    else:
        for original, destination in zip(source, adaptive._destinations[size]):
            np.take(original, lanes, axis=0, out=destination[:len(lanes)], mode="clip")
            if len(lanes) < size:
                destination[len(lanes):] = destination[0]
        pending = enqueue_actor(actor)
    result = PendingSubset(adaptive, pending, lanes.copy(), size)
    adaptive._queued_subset = result
    return result


def finish_subset(pending, *, wait=True):
    adaptive = pending.adaptive
    if pending.consumed or getattr(adaptive, "_queued_subset", None) is not pending:
        raise RuntimeError("Subset completion may only be consumed once")
    actions, packet = finish_actor(pending.pending, wait=wait)
    actor = pending.pending.actor
    adaptive.active_actor = actor
    adaptive.valid_rows, adaptive.bucket_batch = len(pending.lanes), pending.bucket
    adaptive.original_lanes = pending.lanes
    adaptive.obs = actor.obs[:adaptive.valid_rows]
    adaptive.candidates = actor.candidates[:adaptive.valid_rows]
    adaptive.mask = actor.mask[:adaptive.valid_rows]
    adaptive.packet = actor.packet[:adaptive.valid_rows]
    pending.consumed = True
    adaptive._queued_subset = None
    return actions[:adaptive.valid_rows].copy(), packet[:adaptive.valid_rows].copy()


def paired_actions(host, learner, opponent, learning_lanes, opposing_lanes):
    """Collect disjoint valid subsets with one blocking wait when both are used.

    The enqueue order is the production sampling order: learner, then opponent.
    Finished packets own their CPU arrays; learner's device views remain borrowed
    and must be appended before its next use, exactly as in the original actor.
    """
    if learner is opponent:
        raise ValueError("Paired inference requires distinct frozen actors")
    learning_lanes = np.asarray(learning_lanes)
    opposing_lanes = np.asarray(opposing_lanes)
    for lanes in (learning_lanes, opposing_lanes):
        if lanes.ndim != 1 or (len(lanes) and lanes.dtype.kind not in "iu"):
            raise InvalidLearningBatch("Paired lanes must be one-dimensional integer arrays")
        if len(lanes) and (np.any(lanes < 0) or np.any(lanes >= host.batch) or len(np.unique(lanes)) != len(lanes)):
            raise InvalidLearningBatch("Paired lanes must be unique valid host rows")
    learning_lanes = learning_lanes.astype(np.int64, copy=False)
    opposing_lanes = opposing_lanes.astype(np.int64, copy=False)
    if learner.device != opponent.device:
        raise ValueError("Paired policies must share one device and current stream")
    if np.intersect1d(learning_lanes, opposing_lanes).size:
        raise ValueError("A lane cannot belong to both policies")
    actions = np.zeros(host.batch, dtype=np.int64)
    jobs = []
    if len(learning_lanes):
        jobs.append((learning_lanes, enqueue_subset(learner, host, learning_lanes)))
    if len(opposing_lanes):
        jobs.append((opposing_lanes, enqueue_subset(opponent, host, opposing_lanes)))
    # On the same stream, the final event covers all preceding inputs and output
    # copies. Earlier jobs still query their own event before reading host RAM.
    for index in range(len(jobs)-1, -1, -1):
        lanes, pending = jobs[index]
        actions[lanes], _ = finish_subset(pending, wait=index == len(jobs)-1)
    return actions


def collect_queued(host, actor, store, **kwargs):
    """Use the unchanged production lifecycle/credit loop with queued inference.

    The bridge only coordinates the loop's two consecutive act_subset calls.
    An active-lane mirror follows the real host's terminal/censor flags and is
    checked against the exact opposing lanes requested by the production loop.
    No engine stepping, credit assignment, or stopping rule is replaced.
    """
    from learning_rollout import collect_episodes

    opponent = kwargs.get("opponent")
    if opponent is None or not hasattr(actor, "act_subset") or not hasattr(opponent, "act_subset"):
        return collect_episodes(host, actor, store, **kwargs)
    if actor is opponent or actor.device != opponent.device:
        raise ValueError("Queued collection requires distinct actors on one device")
    n = host.batch
    flags, seats = kwargs.get("opponent_lanes"), kwargs.get("learner_seats")
    opponent_lanes = np.asarray(np.zeros(n, dtype=bool) if flags is None else flags, dtype=bool)
    learner_seats = np.asarray(np.arange(n) % 2 if seats is None else seats, dtype=np.int32)
    active = np.ones(n, dtype=bool)
    cached = None

    class HostBridge:
        def __getattr__(self, name):
            return getattr(host, name)

        def advance_active(self, actions, requested_active):
            if not np.array_equal(active, requested_active):
                raise RuntimeError("Queued active-lane mirror differs from collector")
            if cached is not None:
                raise RuntimeError("Unconsumed queued opponent packet before host advance")
            host.advance_active(actions, requested_active)
            active[(host.done == 1) | (host.done == 2)] = False

    class LearnerBridge:
        def __getattr__(self, name):
            return getattr(actor, name)

        def act_subset(self, view, lanes):
            nonlocal cached
            if cached is not None:
                raise RuntimeError("Unconsumed queued opponent packet before next learner")
            other_lanes = np.flatnonzero(active & opponent_lanes & (host.seats != learner_seats))
            if not len(other_lanes):
                return actor.act_subset(view, lanes)
            left = enqueue_subset(actor, view, lanes)
            right = enqueue_subset(opponent, view, other_lanes)
            cached = (other_lanes, finish_subset(right))
            return finish_subset(left, wait=False)

    class OpponentBridge:
        def __getattr__(self, name):
            return getattr(opponent, name)

        def act_subset(self, view, lanes):
            nonlocal cached
            if cached is None:
                return opponent.act_subset(view, lanes)
            if not np.array_equal(cached[0], lanes):
                raise RuntimeError("Queued opponent lanes differ from collector")
            result = cached[1]
            cached = None
            return result

    result = collect_episodes(HostBridge(), LearnerBridge(), store, **{**kwargs, "opponent": OpponentBridge()})
    if cached is not None:
        raise RuntimeError("Collector returned with an unconsumed opponent packet")
    return result
