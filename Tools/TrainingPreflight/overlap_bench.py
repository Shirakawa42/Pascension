"""Bounded CPU-host/GPU-actor overlap experiment; never a training campaign.

One thread advances each independent C# host while the main thread acts for
another group. Each host has exactly one outstanding command and its own pinned
staging buffer. All dispatched work is drained and included in elapsed time.
Optional optimizer work uses disposable parameters and fabricated targets.
Use a caller-owned process-group timeout to bound setup and pipe failures too.
"""
from __future__ import annotations

import argparse
from concurrent.futures import Future, ThreadPoolExecutor
import hashlib
import inspect
import json
import os
import subprocess
import sys
import time

import numpy as np
import torch

from bench_common import Monitor, distribution, metadata, save_json
from pipeline_bench import Actor, Host, LearnerLoad


EXERCISE_BIAS = np.asarray(
    [4, 2, 2, 1, 3, 2, 1, 1, 0, 1, -4, -20, 0, 0, 0, 0], dtype=np.float32
)


def parameter_hash(model):
    digest = hashlib.sha256()
    for name, tensor in model.state_dict().items():
        digest.update(name.encode())
        digest.update(tensor.detach().cpu().contiguous().numpy().tobytes())
    return digest.hexdigest()


def exercise_action(host, rng):
    weights = np.exp(host.candidates[:, :, :16] @ EXERCISE_BIAS) * host.mask
    cumulative = weights.cumsum(axis=1)
    draws = rng.random(host.batch) * cumulative[:, -1]
    return (cumulative < draws[:, None]).sum(axis=1).astype(np.int32)


def advance_group(host, action):
    """Only this worker touches this host until the returned Future completes."""
    start = time.perf_counter()
    host.advance(action)
    return {
        "seconds": time.perf_counter() - start,
        "metrics": host.metrics.copy(),
        "completed": int((host.done == 1).sum()),
        "truncated": int((host.done == 2).sum()),
    }


def stop_children(hosts):
    """Unblock pending pipe readers on failure before joining their threads."""
    for host in hosts:
        if host.process.poll() is None:
            try:
                host.process.terminate()
            except ProcessLookupError:
                pass
    for host in hosts:
        if host.process.poll() is None:
            try:
                host.process.wait(timeout=2)
            except subprocess.TimeoutExpired:
                try:
                    host.process.kill()
                except ProcessLookupError:
                    pass
                host.process.wait(timeout=2)


def startup_modes(host):
    # pread does not change the file offset shared with the child's stderr.
    raw = os.pread(host.log.fileno(), 8192, 0).decode(errors="replace")
    return [json.loads(line) for line in raw.splitlines() if line.startswith("{")]


def run(args):
    torch.set_num_threads(1)
    torch.set_num_interop_threads(1)
    torch.manual_seed(args.seed)
    torch.backends.fp32_precision = "ieee"
    torch.backends.cuda.matmul.fp32_precision = "tf32" if args.precision == "tf32" else "ieee"
    result = {
        "metadata": metadata(),
        "configuration": vars(args),
        "kind": "frozen_policy_real_engine_cpu_gpu_overlap",
        "learner_targets": "Own-learner legal argmax and fixed bounded fabricated returns; no training claim",
        "learner_load_semantics": LearnerLoad.SEMANTIC_VERSION if args.learner_every else None,
        "timing_scope": "all dispatched batches plus final drain; startup and warm-up excluded",
        "total_resident_games": args.groups * args.batch_per_group,
        "total_simulation_workers": args.groups * args.workers_per_group,
        "actor_policy_version": "frozen-preflight-initial-weights",
        "training_campaign_started": False,
    }
    hosts, actors = [], []
    pending: list[Future | None] = [None] * args.groups
    executor = ThreadPoolExecutor(max_workers=args.groups, thread_name_prefix="shards-host")
    successful = False
    previous_copy = os.environ.get("SHARDS_SHARED_COPY")
    os.environ["SHARDS_SHARED_COPY"] = args.shared_copy
    setup_start = time.perf_counter()
    try:
        # Each host advances episode seeds by its own batch size. Wide separate
        # ranges avoid accidentally replaying another host's next episode.
        seeds = [args.seed + group * (1 << 40) for group in range(args.groups)]
        rngs = [np.random.default_rng(seed) for seed in seeds]
        for seed in seeds:
            kwargs = dict(pinned=True, server_gc=args.server_gc,
                          transport=args.transport, split_branches=args.split_branches)
            if "shared_copy" in inspect.signature(Host).parameters:
                kwargs["shared_copy"] = args.shared_copy
            host = Host(args.batch_per_group, args.workers_per_group, seed, **kwargs)
            hosts.append(host)
            actor = Actor(host, "cuda", args.width, args.scorer, args.precision, args.actor_mode)
            if actors:
                # load_state_dict copies in place: captured parameter addresses
                # remain valid, and every group uses identical frozen weights.
                actor.model.load_state_dict(actors[0].model.state_dict())
            actors.append(actor)
        torch.cuda.synchronize()
        initial_hash = parameter_hash(actors[0].model)
        if any(parameter_hash(actor.model) != initial_hash for actor in actors[1:]):
            raise RuntimeError("Actor groups do not share identical frozen parameters")
        learner = LearnerLoad(actors[0], args.learner_batch, args.learner_mode) if args.learner_every else None
        result["setup_seconds"] = time.perf_counter() - setup_start
        result["group_seeds"] = seeds
        result["actor_parameter_sha256"] = initial_hash
        result["cold_actor_seconds"] = [actor.cold_start for actor in actors]
        result["host_startup_modes"] = [startup_modes(host) for host in hosts]

        def wait_group(group):
            future = pending[group]
            if future is None:
                return None, 0.0
            start = time.perf_counter()
            response = future.result(timeout=args.host_timeout)
            waited = time.perf_counter() - start
            pending[group] = None
            return response, waited

        def act_group(group):
            # The caller must first consume this group's outstanding response.
            if pending[group] is not None:
                raise RuntimeError("Attempt to read a host with an outstanding response")
            action, stages = actors[group].act()
            if args.action_source == "exercise":
                action = exercise_action(hosts[group], rngs[group])
            return action, stages

        def learner_step(group):
            # One learner, one optimizer and persistent learner buffers are used
            # for all groups. Only the main thread touches CUDA. Switching this
            # input source is safe: group shapes/precision are identical, and
            # LearnerLoad copies GPU inputs into its own capture-owned buffers.
            learner.actor = actors[group]
            learner.run(args.learner_steps)

        with Monitor() as monitor:
            warmed = [0] * args.groups
            warm_dispatches = 0
            warm_deadline = time.perf_counter() + args.warmup
            while min(warmed) < args.warmup_steps and time.perf_counter() < warm_deadline:
                group = warm_dispatches % args.groups
                wait_group(group)
                action, _ = act_group(group)
                pending[group] = executor.submit(advance_group, hosts[group], action)
                if learner is not None and warm_dispatches % args.learner_every == 0:
                    learner_step(group)
                warmed[group] += 1
                warm_dispatches += 1
            for group in range(args.groups):
                wait_group(group)
            initial = [host.metrics.copy() for host in hosts]

            actor_service, actor_stages, response_seconds = [], [], []
            host_step_ms, host_encode_ms = [], []
            legal_histogram = np.zeros(65, dtype=np.int64)
            finished = truncated = dispatches = consumed = 0
            group_dispatches = [0] * args.groups
            wait_seconds = learner_seconds = 0.0
            optimizer_passes = 0

            def consume(group):
                nonlocal wait_seconds, finished, truncated, consumed
                response, waited = wait_group(group)
                wait_seconds += waited
                if response is not None:
                    response_seconds.append(response["seconds"])
                    host_step_ms.append(response["metrics"][0])
                    host_encode_ms.append(response["metrics"][1])
                    finished += response["completed"]
                    truncated += response["truncated"]
                    consumed += 1

            start = time.perf_counter()
            while time.perf_counter() - start < args.seconds:
                group = dispatches % args.groups
                consume(group)
                tick = time.perf_counter()
                action, stages = act_group(group)
                actor_service.append(time.perf_counter() - tick)
                actor_stages.append(stages)
                counts = hosts[group].mask.sum(axis=1).astype(np.int64)
                legal_histogram += np.bincount(counts, minlength=65)
                # act() synchronized its D2H completion event, so uploading the
                # pinned host source is complete before this worker overwrites it.
                pending[group] = executor.submit(advance_group, hosts[group], action)
                if learner is not None and dispatches % args.learner_every == 0:
                    tick = time.perf_counter()
                    learner_step(group)
                    learner_seconds += time.perf_counter() - tick
                    optimizer_passes += args.learner_batch * args.learner_steps
                group_dispatches[group] += 1
                dispatches += 1
            dispatch_elapsed = time.perf_counter() - start
            drain_start = time.perf_counter()
            for group in range(args.groups):
                consume(group)
            elapsed = time.perf_counter() - start
            drain_seconds = time.perf_counter() - drain_start

        deltas = [host.metrics - before for host, before in zip(hosts, initial)]
        delta = np.sum(deltas, axis=0)
        expected_rows = dispatches * args.batch_per_group
        if consumed != dispatches or int(delta[2]) != expected_rows:
            raise RuntimeError("Dispatched and completed wrapper batch counts disagree")
        if int(delta[4]) != finished or int(delta[5]) != truncated:
            raise RuntimeError("Outcome responses disagree with authoritative host counters")
        if any(parameter_hash(actor.model) != initial_hash for actor in actors):
            raise RuntimeError("Disposable learner changed frozen actor parameters")
        actor_seconds = sum(actor_service)
        result.update({
            "seconds": elapsed, "dispatch_seconds": dispatch_elapsed,
            "final_drain_seconds": drain_seconds, "warmup_steps_per_group": warmed,
            "dispatched_batches": dispatches, "completed_batches": consumed,
            "batches_per_group": group_dispatches,
            "wrapper_decisions": int(delta[2]), "engine_submissions": int(delta[3]),
            "completed_games": finished, "truncated_games": truncated,
            "wrapper_decisions_per_second": float(delta[2]) / elapsed,
            "engine_submissions_per_second": float(delta[3]) / elapsed,
            "response_rows_per_second": expected_rows / elapsed,
            "games_per_second": finished / elapsed,
            "actor_service_seconds": actor_seconds,
            "waiting_for_host_seconds": wait_seconds,
            "optimizer_seconds": learner_seconds, "optimizer_example_passes": optimizer_passes,
            "main_loop_other_seconds": elapsed - actor_seconds - wait_seconds - learner_seconds,
            "summed_host_roundtrip_seconds": sum(response_seconds),
            "summed_host_step_seconds": sum(host_step_ms) / 1000,
            "summed_host_encode_seconds": sum(host_encode_ms) / 1000,
            "host_timer_warning": "Host intervals overlap each other and main-thread work; do not add them to elapsed time",
            "actor_service_ms": distribution([x * 1000 for x in actor_service]),
            "host_roundtrip_ms": distribution([x * 1000 for x in response_seconds]),
            "host_step_ms": distribution(host_step_ms), "host_encode_ms": distribution(host_encode_ms),
            "gpu_stage_mean_ms": dict(zip(("upload", "forward_sample", "download"),
                                          np.mean(actor_stages, axis=0).tolist())),
            "legal_candidate_histogram": legal_histogram.tolist(),
            "single_legal_action_fraction": int(legal_histogram[1]) / max(1, expected_rows),
            "mean_legal_candidate_occupancy": float(legal_histogram @ np.arange(65)) / max(1, expected_rows) / 64,
            "resident_logged_events_end": sum(float(host.metrics[6]) for host in hosts),
            "managed_heap_bytes_end": sum(float(host.metrics[7]) for host in hosts),
            "actors_remained_frozen": True,
            "gpu_monitor": monitor.summary(),
        })
        save_json(args.output, result)
        successful = True
        return result
    finally:
        if not successful:
            stop_children(hosts)
        # Killing failed children makes blocking read_exact calls finish before
        # releasing mappings or pinned arrays owned by those worker threads.
        executor.shutdown(wait=True, cancel_futures=True)
        for host in hosts:
            try:
                host.close()
            except Exception as error:
                print(f"Host cleanup: {error}", file=sys.stderr)
        if previous_copy is None:
            os.environ.pop("SHARDS_SHARED_COPY", None)
        else:
            os.environ["SHARDS_SHARED_COPY"] = previous_copy


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--groups", type=int, choices=(1, 2, 3), default=2)
    parser.add_argument("--batch-per-group", type=int, default=256)
    parser.add_argument("--workers-per-group", type=int, default=2)
    parser.add_argument("--seed", type=int, default=17)
    parser.add_argument("--width", type=int, default=256)
    parser.add_argument("--scorer", choices=("embedded", "encoded", "bilinear"), default="embedded")
    parser.add_argument("--precision", choices=("fp32", "tf32", "bf16"), default="tf32")
    parser.add_argument("--actor-mode", choices=("eager", "graph"), default="graph")
    parser.add_argument("--transport", choices=("pipe", "shared"), default="shared")
    parser.add_argument("--shared-copy", choices=("span", "accessor"), default="span")
    parser.add_argument("--server-gc", action="store_true")
    parser.add_argument("--split-branches", type=int, choices=(2, 8, 32, 64), default=64)
    parser.add_argument("--action-source", choices=("model", "exercise"), default="exercise")
    parser.add_argument("--seconds", type=float, default=10)
    parser.add_argument("--warmup", type=float, default=2)
    parser.add_argument("--warmup-steps", type=int, default=32)
    parser.add_argument("--host-timeout", type=float, default=15)
    parser.add_argument("--learner-every", type=int, default=0)
    parser.add_argument("--learner-steps", type=int, default=1)
    parser.add_argument("--learner-batch", type=int, default=2048)
    parser.add_argument("--learner-mode", choices=("graph", "eager"), default="graph")
    parser.add_argument("--output", required=True)
    args = parser.parse_args()
    if not 1 <= args.batch_per_group <= 2048 or args.groups * args.batch_per_group > 4096:
        parser.error("Require batch-per-group 1..2048 and at most 4096 total resident games")
    if not 1 <= args.workers_per_group <= 8 or args.groups * args.workers_per_group > 16:
        parser.error("Require workers-per-group 1..8 and at most 16 total simulation workers")
    if not 0 < args.seconds <= 60 or not 0 <= args.warmup <= 10 or not 0 <= args.warmup_steps <= 1024:
        parser.error("Require seconds (0,60], warmup [0,10], warmup-steps 0..1024")
    if not 1 <= args.host_timeout <= 60 or not 0 <= args.seed < (1 << 63):
        parser.error("Require host-timeout 1..60 and a nonnegative seed below 2^63")
    if not 0 <= args.learner_every <= 100000 or not 1 <= args.learner_steps <= 16 or not 1 <= args.learner_batch <= 8192:
        parser.error("Require learner-every 0..100000, learner-steps 1..16, learner-batch 1..8192")
    if not 16 <= args.width <= 1024:
        parser.error("Require model width 16..1024")
    result = run(args)
    print(json.dumps({key: result[key] for key in (
        "total_resident_games", "total_simulation_workers", "wrapper_decisions_per_second",
        "engine_submissions_per_second", "completed_games", "truncated_games",
        "actor_service_seconds", "waiting_for_host_seconds", "optimizer_seconds", "final_drain_seconds",
    )}))


if __name__ == "__main__":
    main()
