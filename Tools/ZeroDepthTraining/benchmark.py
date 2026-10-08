#!/usr/bin/env python3
"""Bounded real-engine throughput checks; never updates a policy.

Run CPU automation parity first. GPU sweeps use the complete-input host and a
fresh frozen model with the same initialization/seed in every configuration.
Setup, warmup, timed collection and isolated actor stage profiling are reported
separately. No games here enter a training campaign or strength evaluation.
"""
from __future__ import annotations

import argparse
import hashlib
import json
import os
from pathlib import Path
import platform
import statistics
import subprocess
import threading
import time

import numpy as np

HERE = Path(__file__).resolve().parent
ROOT = HERE.parents[1]
DEFAULT_BINARY = HERE / "Host/bin/Release/net8.0/ZeroDepthHost.dll"
DOTNET = os.environ.get("SHARDS_DOTNET", "/home/lva/.dotnet/dotnet")


def distribution(values):
    if not values:
        return {}
    ordered = sorted(float(v) for v in values)
    return {"count": len(ordered), "median": statistics.median(ordered),
            "min": ordered[0], "max": ordered[-1],
            "p95": ordered[min(len(ordered)-1, int(.95*len(ordered)))]}


def gpu_status():
    try:
        output = subprocess.check_output([
            "nvidia-smi", "--query-gpu=name,memory.total,memory.used,utilization.gpu,power.draw,temperature.gpu",
            "--format=csv,noheader,nounits"], text=True, timeout=3).strip()
        fields = [v.strip() for v in output.splitlines()[0].split(",")]
        return {"name": fields[0], **dict(zip(
            ("total_mib", "memory_mib", "utilization_percent", "power_w", "temperature_c"),
            map(float, fields[1:])))}
    except (OSError, ValueError, subprocess.SubprocessError, IndexError):
        return {}


class ResourceMonitor:
    def __init__(self):
        self.samples = []
        self.stop = threading.Event()
        self.thread = threading.Thread(target=self._sample, daemon=True)

    def _sample(self):
        while not self.stop.is_set():
            self.samples.append({"monotonic_seconds": time.monotonic(), **gpu_status()})
            self.stop.wait(.5)

    def __enter__(self):
        self.thread.start()
        return self

    def __exit__(self, *args):
        self.stop.set()
        self.thread.join(timeout=4)

    def summary(self):
        return {key: distribution([row[key] for row in self.samples if key in row])
                for key in ("memory_mib", "utilization_percent", "power_w", "temperature_c")}


def save_json(path, result):
    path = Path(path)
    path.parent.mkdir(parents=True, exist_ok=True)
    temporary = path.with_suffix(path.suffix+".tmp")
    temporary.write_text(json.dumps(result, indent=2, allow_nan=False)+"\n")
    temporary.replace(path)


def file_hash(path):
    return hashlib.sha256(Path(path).read_bytes()).hexdigest()


def canonical_json_hash(value):
    return hashlib.sha256(json.dumps(value, sort_keys=True, separators=(",", ":")).encode()).hexdigest()


def read_json_output(completed):
    if completed.returncode:
        raise RuntimeError(f"Host benchmark failed ({completed.returncode}): {completed.stderr[-8000:]}")
    lines = [line for line in completed.stdout.splitlines() if line.lstrip().startswith("{")]
    if not lines:
        raise RuntimeError("Host benchmark did not return a JSON object")
    # Host may emit an indented object. Prefer parsing the full stdout first.
    try:
        return json.loads(completed.stdout)
    except json.JSONDecodeError:
        return json.loads(lines[-1])


def cpu_automation_parity(args):
    reports = []
    for workers in args.workers:
        pair = {}
        for automation in ("none", "singleton"):
            command = [DOTNET, str(args.binary), "benchmark", str(args.parity_games),
                       str(workers), automation, str(args.seed)]
            tick = time.perf_counter()
            completed = subprocess.run(command, text=True, capture_output=True,
                                       timeout=args.host_timeout)
            report = read_json_output(completed)
            report["command"] = command
            report["process_seconds_including_setup"] = time.perf_counter()-tick
            pair[automation] = report
        # Digest is computed by the host from each game winner, final rule hash
        # and engine submissions; wrapper/automated counts deliberately differ.
        digest_name = "final_outcome_digest"
        if digest_name not in pair["none"] or digest_name not in pair["singleton"]:
            raise RuntimeError("Host parity benchmark omitted final_outcome_digest")
        matched = pair["none"][digest_name] == pair["singleton"][digest_name]
        if not matched:
            raise AssertionError("Forced automation changed deterministic outcomes/state/submissions")
        fields = ("completed", "censored", "completed_games", "censored_games", "engine_submissions")
        for field in fields:
            if field in pair["none"] and pair["none"][field] != pair["singleton"].get(field):
                raise AssertionError(f"Forced automation changed {field}")
        reports.append({"workers": workers, "digest_matched": True, "runs": pair,
                        "speedup": pair["none"]["seconds"] / pair["singleton"]["seconds"]})
    return {"scope": "paired deterministic exercise games; no policy training/strength evidence",
            "games_per_configuration": args.parity_games, "reports": reports}


def parse_int_list(value):
    try:
        values = [int(v) for v in value.split(",")]
    except ValueError as exc:
        raise argparse.ArgumentTypeError("Expected comma-separated integers") from exc
    if not values or any(v < 1 for v in values) or len(set(values)) != len(values):
        raise argparse.ArgumentTypeError("Expected distinct positive integers")
    return values


def host_metrics(host):
    """Read the host's per-response telemetry without making timing assumptions."""
    metrics = getattr(host, "metrics", {})
    if isinstance(metrics, dict):
        return {key: float(value) for key, value in metrics.items()
                if isinstance(value, (int, float, np.integer, np.floating))}
    if metrics is not None and np.asarray(metrics).ndim == 1:
        names = getattr(host, "metric_names", ())
        return dict(zip(names, map(float, metrics)))
    return {}


def parameter_hash(policy):
    digest = hashlib.sha256()
    for name, value in policy.state_dict().items():
        digest.update(name.encode())
        digest.update(value.detach().cpu().contiguous().numpy().tobytes())
    return digest.hexdigest()


def save_failure_trace(args, host, history, cohort_seed, batch, workers, mode, automation, error, cohort_reports=None):
    path = args.output.with_name(f"{args.output.stem}-failure-b{batch}-w{workers}-{mode}-{automation}.npz")
    path.parent.mkdir(parents=True, exist_ok=True)
    metadata = {"cohort_seed": cohort_seed, "batch": batch, "workers": workers,
                "mode": mode, "automation": automation, "error": str(error),
                "host_binary_sha256": file_hash(args.binary),
                "catalog_sha256": canonical_json_hash(host.catalog),
                "actions_include_failed_final_submission": True,
                "completed_cohorts": cohort_reports or []}
    np.savez_compressed(path, actions=np.asarray(history, dtype=np.int32),
                        last_scalars=host.obs[:, :256].copy(), done=host.done.copy(),
                        actors=host.actors.copy(), metadata=np.asarray(json.dumps(metadata)))
    print(json.dumps({"failure_trace": str(path), "cohort_seed": cohort_seed,
                      "wrapper_batches": len(history)}), flush=True)
    return path


def actor_profile(actor, host, repeats=8):
    """CUDA-event timings on one retained input, separate from the live loop.

    Includes H2D/forward+sampling/D2H GPU intervals. CPU repacking, output
    validation/copy and host service are absent; live actor wall time includes
    them. Graph/eager selected likelihood parity is checked on the real input.
    """
    import torch
    if not actor.cuda:
        return {"available": False, "reason": "CUDA event profiling requires a CUDA device"}
    if hasattr(actor, "source_rows") and not np.any(host.done == 0):
        return {"available": False, "reason": "No active adaptive actor rows at probe stop"}
    actor.act(host)
    actor = getattr(actor, "active_actor", actor)
    with torch.inference_mode():
        reference_logits, reference_values = actor.policy(actor.obs, actor.candidates, actor.mask)
        selected = actor.packet[:, 0].long()
        reference_logp = reference_logits.log_softmax(-1).gather(1, selected[:, None]).squeeze(1)
        logp_error = (reference_logp - actor.packet[:, 1]).abs().max().item()
        value_error = (reference_values - actor.packet[:, 2]).abs().max().item()
        if logp_error > 1.e-4 or value_error > 1.e-4:
            raise AssertionError(f"Actor eager probability/value parity failed: {logp_error}, {value_error}")
        samples = {key: [] for key in ("upload_ms", "inference_sampling_ms", "download_ms")}
        events = [torch.cuda.Event(enable_timing=True) for _ in range(4)]
        for _ in range(repeats):
            events[0].record()
            if actor.packed_transport is None:
                actor.obs.copy_(actor.obs_host, non_blocking=True)
                actor.candidates.copy_(actor.candidates_host, non_blocking=True)
                actor.mask.copy_(actor.mask_host, non_blocking=True)
            else:
                actor.packed_transport.upload_into(actor.obs, actor.candidates, actor.mask)
            events[1].record()
            if actor.graph is None:
                actor.packet = actor.compute()
            else:
                actor.graph.replay()
            events[2].record()
            actor.output_host.copy_(actor.packet, non_blocking=True)
            events[3].record()
            events[3].synchronize()
            for key, index in zip(samples, range(3)):
                samples[key].append(events[index].elapsed_time(events[index+1]))
    return {"scope": "isolated repeated retained-input GPU event intervals; excludes CPU staging; packed upload includes lossless expansion",
            "available": True, "repeats": repeats,
            "selected_logp_eager_max_abs_error": logp_error,
            "value_eager_max_abs_error": value_error,
            "timings": {key: distribution(value) for key, value in samples.items()}}


def disposable_optimizer_smoke(policy, actor, host, args):
    """Exercise real input shapes with fabricated outcomes, then discard updates."""
    import copy
    import torch
    from train import Learner, Rollout, TrainConfig
    active = np.flatnonzero(host.done == 0)
    if not len(active):
        return {"available": False, "reason": "No active real-engine rows at probe stop"}
    original_hash = parameter_hash(policy)
    config = TrainConfig(batch=host.batch, workers=host.workers, width=args.width,
                         capacity=len(active), minibatch=min(1024, len(active)), epochs=1)
    disposable = copy.deepcopy(policy).requires_grad_(True)
    learner = Learner(disposable, config)
    store = Rollout(len(active), host.catalog, device=str(next(policy.parameters()).device))
    _, packet = actor.act(host)
    store.append(host, packet, active, actor=actor)
    # Alternating fabricated zero-sum utilities deliberately do not reflect
    # these unfinished games. They never enter a campaign or saved checkpoint.
    fabricated_rewards = np.empty((host.batch, 2), dtype=np.float32)
    fabricated_rewards[:, 0] = np.where(np.arange(host.batch) % 2, 1., -1.)
    fabricated_rewards[:, 1] = -fabricated_rewards[:, 0]
    rows, returns, advantages, excluded = store.seal(np.ones(host.batch, dtype=np.int32), fabricated_rewards)
    if excluded:
        raise AssertionError("Fabricated optimizer smoke unexpectedly excluded rows")
    started = time.perf_counter()
    report = learner.update(store, rows, returns, advantages)
    if actor.cuda:
        torch.cuda.synchronize(actor.device)
    elapsed = time.perf_counter()-started
    tensors = list(disposable.parameters()) + [value for state in learner.optimizer.state.values()
               for value in state.values() if torch.is_tensor(value)]
    if not all(bool(torch.isfinite(value).all()) for value in tensors):
        raise AssertionError("Disposable optimizer produced nonfinite parameters/moments")
    if parameter_hash(policy) != original_hash:
        raise AssertionError("Disposable optimizer changed the frozen benchmark model")
    return {"available": True, "scope": "disposable optimizer with real input shapes and fabricated utilities; no training/strength evidence",
            "rows": len(rows), "seconds": elapsed, "frozen_original_unchanged": True,
            "finite_parameters_and_optimizer_state": True, **report}


def gpu_configuration(args, batch, workers, mode, automation):
    import gc
    import torch
    from host import Host
    from model import Actor, Policy, PolicyConfig, dimensions

    torch.manual_seed(args.seed)
    if args.device == "cuda":
        torch.cuda.manual_seed_all(args.seed)
    tick = time.perf_counter()
    host = Host(binary=args.binary, batch=batch, workers=workers, seed=args.seed,
                automation=automation == "singleton", timeout=args.host_timeout)
    try:
        obs_dim, max_actions, action_dim = dimensions(host.catalog)
        policy = Policy(host.catalog, PolicyConfig(width=args.width)).to(args.device).eval().requires_grad_(False)
        initial_hash = parameter_hash(policy)
        if args.adaptive:
            from adaptive import AdaptiveActor
            actor = AdaptiveActor(policy, batch, graph=mode == "graph", compiled=args.compiled, packed=args.packed)
        else:
            actor = Actor(policy, batch, graph=mode == "graph", compiled=args.compiled, packed=args.packed)
        setup_seconds = time.perf_counter()-tick
        if args.device == "cuda":
            torch.cuda.reset_peak_memory_stats()
        cohort = 0
        active = host.done == 0
        for _ in range(args.warmup_steps):
            if not active.any():
                cohort += 1
                host.reset(args.seed + cohort*batch)
                active = host.done == 0
            actions, packet = actor.act(host)
            actions[~active] = -1
            host.advance(actions)
            active = host.done == 0
        # Timed collection starts at a fresh full cohort. Reset cost for this
        # initial cohort is setup; subsequent cohort resets are timed.
        cohort += 1
        tick = time.perf_counter()
        host.reset(args.seed + cohort*batch)
        initial_reset_seconds = time.perf_counter()-tick
        active = host.done == 0
        initial_actor_profile = actor_profile(actor, host)
        # Completed-cohort probes end with no active lanes. Validate optimizer
        # ownership on the fresh real inputs before timing frozen collection.
        optimizer_smoke = disposable_optimizer_smoke(policy, actor, host, args) if args.optimizer_smoke else None
        previous_metrics = host_metrics(host)
        action_history = []
        cohort_reports = []
        cohort_policy_rows = 0
        cohort_started = time.perf_counter()
        def record_cohort():
            report = {"seed": args.seed + cohort*batch,
                "seconds": time.perf_counter()-cohort_started,
                "policy_rows": cohort_policy_rows,
                "completed_games": int((host.done == 1).sum()),
                "censored_games": int((host.done == 2).sum()),
                "unresolved_games": int((host.done == 0).sum()),
                "all_resolved": bool(np.all(host.done != 0)),
                "wrapper_action_sha256": hashlib.sha256(np.asarray(action_history, dtype=np.int32).tobytes()).hexdigest(),
                "terminal_rewards_sha256": hashlib.sha256(host.rewards.tobytes()).hexdigest()}
            cohort_reports.append(report)
            print(json.dumps({"cohort": report, "batch": batch, "adaptive": args.adaptive}), flush=True)
        actor_seconds = host_seconds = reset_seconds = 0.
        completed = censored = decisions = padded_rows = steps = resets = 0
        legal_histogram = np.zeros(max_actions+1, dtype=np.int64)
        metric_sums = {}
        actor_latencies = []
        transport_bytes = {"dense_bytes": 0, "uploaded_bytes": 0}
        response_bytes = sum(getattr(host, name).nbytes for name in
                             ("obs", "candidates", "mask", "actors", "rewards", "done"))
        with ResourceMonitor() as resources:
            started = time.perf_counter()
            while (time.perf_counter()-started < args.seconds or
                   ((completed == 0 or (args.complete_cohort and active.any())) and
                    time.perf_counter()-started < args.first_completion_timeout)):
                if not active.any():
                    record_cohort()
                    cohort += 1
                    tick = time.perf_counter()
                    host.reset(args.seed + cohort*batch)
                    reset_seconds += time.perf_counter()-tick
                    resets += 1
                    active = host.done == 0
                    previous_metrics = host_metrics(host)
                    action_history.clear()
                    cohort_policy_rows = 0
                    cohort_started = time.perf_counter()
                before = active.copy()
                legal_counts = host.mask[before].sum(axis=1).astype(np.int64)
                legal_histogram += np.bincount(legal_counts, minlength=max_actions+1)
                tick = time.perf_counter()
                actions, packet = actor.act(host)
                duration = time.perf_counter()-tick
                actor_seconds += duration
                actor_latencies.append(duration*1000)
                if not np.isfinite(packet).all():
                    raise AssertionError("Nonfinite actor packet")
                indices = np.flatnonzero(before)
                if not host.mask[indices, actions[indices]].all():
                    raise AssertionError("Actor sampled an illegal active-lane action")
                actions[~before] = -1
                action_history.append(actions.copy())
                tick = time.perf_counter()
                try:
                    host.advance(actions)
                except BaseException as error:
                    save_failure_trace(args, host, action_history, args.seed + cohort*batch,
                                       batch, workers, mode, automation, error, cohort_reports)
                    raise
                host_seconds += time.perf_counter()-tick
                completed += int(((host.done == 1) & before).sum())
                censored += int(((host.done == 2) & before).sum())
                decisions += int(before.sum())
                cohort_policy_rows += int(before.sum())
                selected_actor = getattr(actor, "active_actor", actor)
                if selected_actor.packed_transport is not None:
                    for key in transport_bytes:
                        transport_bytes[key] += selected_actor.packed_transport.telemetry[key]
                padded_rows += getattr(selected_actor, "batch", batch)
                steps += 1
                current_metrics = host_metrics(host)
                for key, value in current_metrics.items():
                    if key.endswith("_seconds") or key.endswith("_ms") or key in (
                            "engine_submissions", "automated_steps", "automatically_applied", "wrapper_choices"):
                        metric_sums[key] = metric_sums.get(key, 0.) + value
                    elif key.endswith("_cumulative"):
                        difference = value - previous_metrics.get(key, 0.)
                        if difference < 0:
                            raise AssertionError("Host cumulative counter decreased without reset")
                        name = key.removesuffix("_cumulative")
                        metric_sums[name] = metric_sums.get(name, 0.) + difference
                previous_metrics = current_metrics
                active = host.done == 0
            elapsed = time.perf_counter()-started
        record_cohort()
        profile = actor_profile(actor, host)
        peak = {} if args.device != "cuda" else {
            "allocated_bytes": torch.cuda.max_memory_allocated(),
            "reserved_bytes": torch.cuda.max_memory_reserved()}
        return {"batch": batch, "workers": workers, "mode": mode, "automation": automation,
                "adaptive": args.adaptive,
                "compiled_actor": args.compiled, "packed_inputs": args.packed,
                "transport_bytes": transport_bytes,
                "policy_sha256": initial_hash, "catalog_sha256": canonical_json_hash(host.catalog),
                "observation_dimensions": [obs_dim, max_actions, action_dim],
                "setup_seconds": setup_seconds, "initial_timed_cohort_reset_seconds": initial_reset_seconds,
                "seconds": elapsed, "completed_games": completed, "censored_games": censored,
                "extended_to_first_completed_game": elapsed > args.seconds + .05,
                "unresolved_games_at_stop": int(active.sum()), "completed_cohort_resets": resets,
                "games_per_second": completed/elapsed, "policy_decisions": decisions,
                "policy_decisions_per_second": decisions/elapsed, "gpu_rows_including_padding": padded_rows,
                "gpu_rows_including_padding_per_second": padded_rows/elapsed,
                "cohorts": cohort_reports,
                "max_observed_cohort_policy_rows": max(row["policy_rows"] for row in cohort_reports),
                "rollout_capacity_scope": "all active same-policy rows before archive exclusion; observed workload upper bound, not a guarantee for future policies",
                "steps": steps, "response_bytes_per_step": response_bytes,
                "estimated_response_bytes": response_bytes*steps,
                "host_roundtrip_seconds": host_seconds, "actor_service_seconds": actor_seconds,
                "cohort_reset_seconds": reset_seconds,
                "loop_other_seconds": max(0., elapsed-host_seconds-actor_seconds-reset_seconds),
                "host_reported_per_response_metric_sums": metric_sums,
                "host_metric_semantics": "per-response service sums and cumulative counter differences, restarted after resets; fused service includes rules+encoding, unavailable separately",
                "actor_latency_ms": distribution(actor_latencies),
                "legal_candidate_histogram": legal_histogram.tolist(),
                "cuda_peak": peak, "gpu_samples": resources.summary(), "isolated_actor_profile": profile,
                "initial_active_actor_profile": initial_actor_profile,
                "disposable_optimizer_smoke": optimizer_smoke,
                "scope": "frozen direct-action selfplay; no optimizer or strength claim; unfinished games not counted"}
    finally:
        host.close()
        # Graph/policy buffers must be released between shapes. Do not flush the
        # driver allocator cache, which would bias every next setup cold.
        if "actor" in locals():
            del actor
        if "policy" in locals():
            del policy
        gc.collect()


def gpu_sweep(args, result):
    import torch
    torch.set_num_threads(1)
    torch.set_num_interop_threads(1)
    torch.backends.fp32_precision = "ieee"
    torch.backends.cuda.matmul.fp32_precision = "ieee"
    if args.device == "cuda" and not torch.cuda.is_available():
        raise RuntimeError("CUDA is unavailable; use --device cpu --modes eager for a CPU smoke")
    if args.device == "cpu" and args.modes != "eager":
        raise ValueError("CPU actor probes require --modes eager")
    modes = ["eager", "graph"] if args.modes == "both" else [args.modes]
    automations = ["none", "singleton"] if args.automation == "both" else [args.automation]
    result["metadata"].update(torch=torch.__version__, cuda=torch.version.cuda,
                              precision="ieee-fp32")
    runs = []
    for batch in args.batches:
        for workers in args.workers:
            for automation in automations:
                for mode in modes:
                    try:
                        report = gpu_configuration(args, batch, workers, mode, automation)
                    except BaseException as error:
                        result.update(status="failed", failure=str(error))
                        save_json(args.output, result)
                        raise
                    runs.append(report)
                    result["gpu_runs"] = runs
                    save_json(args.output, result)
                    print(json.dumps({key: report[key] for key in (
                        "batch", "workers", "mode", "automation", "games_per_second", "policy_decisions_per_second")}))
    return runs


def build_parser():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--binary", type=Path, default=DEFAULT_BINARY)
    parser.add_argument("--batches", type=parse_int_list, default=[128, 256, 512, 1024])
    parser.add_argument("--workers", type=parse_int_list, default=[4, 8])
    parser.add_argument("--modes", choices=("eager", "graph", "both"), default="both")
    parser.add_argument("--automation", choices=("none", "singleton", "both"), default="both")
    parser.add_argument("--seconds", type=float, default=3.)
    parser.add_argument("--warmup-steps", type=int, default=8)
    parser.add_argument("--parity-games", type=int, default=32)
    parser.add_argument("--host-timeout", type=float, default=60.)
    parser.add_argument("--first-completion-timeout", type=float, default=30.,
                        help="Extend a short zero-completion probe, bounded to 120 seconds")
    parser.add_argument("--complete-cohort", action="store_true",
                        help="Extend collection to finish its current cohort within --first-completion-timeout")
    parser.add_argument("--seed", type=int, default=17000)
    parser.add_argument("--width", type=int, default=512)
    parser.add_argument("--device", choices=("cpu", "cuda"), default="cuda")
    parser.add_argument("--cpu-only", action="store_true")
    parser.add_argument("--skip-parity", action="store_true")
    parser.add_argument("--optimizer-smoke", action="store_true",
                        help="Discarded fabricated-target PPO checks on the real encoded input shapes")
    parser.add_argument("--adaptive", action="store_true",
                        help="Pack active lanes into captured actor buckets and exclude finished-lane uploads")
    parser.add_argument("--compiled", action="store_true", help="Fuse the FP32 actor forward with torch.compile")
    parser.add_argument("--packed", action="store_true", help="Upload lossless table prefixes and expand on CUDA")
    parser.add_argument("--output", type=Path, default=HERE / "results/throughput.json")
    return parser


def main():
    args = build_parser().parse_args()
    if not .1 <= args.seconds <= 30 or not 1 <= args.parity_games <= 4096:
        raise ValueError("Bounded probes require seconds in [.1,30] and parity-games in [1,4096]")
    if not 0 <= args.warmup_steps <= 256 or not 1 <= args.host_timeout <= 120:
        raise ValueError("Warmup steps or host timeout exceeds bounded probe limits")
    if not args.seconds <= args.first_completion_timeout <= 120:
        raise ValueError("First completion timeout must be at least --seconds and at most 120")
    if not args.binary.is_file():
        raise FileNotFoundError(f"Build the ZeroDepthHost first: {args.binary}")
    result = {"kind": "zero_depth_frozen_policy_real_engine_throughput", "training_started": False, "status": "running",
              "configuration": {key: str(value) if isinstance(value, Path) else value
                                for key, value in vars(args).items()},
              "metadata": {"python": platform.python_version(), "platform": platform.platform(),
                           "cpu_logical_processors": os.cpu_count(), "gpu_before": gpu_status(),
                           "host_binary_sha256": file_hash(args.binary),
                           "runtime_source_sha256": {name: file_hash(HERE / name) for name in
                               ("benchmark.py", "host.py", "model.py", "adaptive.py", "packed_transport.py", "train.py")
                               if (HERE / name).is_file()}}, "gpu_runs": []}
    save_json(args.output, result)
    if not args.skip_parity:
        result["automation_parity"] = cpu_automation_parity(args)
        save_json(args.output, result)
    if not args.cpu_only:
        result["gpu_runs"] = gpu_sweep(args, result)
    result["status"] = "complete"
    save_json(args.output, result)
    print(json.dumps({"output": str(args.output), "gpu_configurations": len(result["gpu_runs"]),
                      "training_started": False}))


if __name__ == "__main__":
    main()
