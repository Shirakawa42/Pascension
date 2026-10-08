#!/usr/bin/env python3
"""Bounded isolated actor/learner microbenchmarks; never a training campaign.

Every case is a subprocess with a timeout. Synthetic or recorded observations
test numerical and systems behavior with fabricated learner targets. Results
say nothing about playing strength.
"""

from __future__ import annotations

import argparse
import contextlib
import copy
import hashlib
import io
import itertools
import json
import math
import os
from pathlib import Path
import platform
import signal
import statistics
import subprocess
import sys
import time
import traceback

import numpy as np
import torch

from model import CandidatePolicy, ppo_loss, sample_actions, validate_legal_mask


def set_precision(precision):
    # Do not mix this API with deprecated allow_tf32 settings.
    torch.backends.fp32_precision = "ieee"
    torch.backends.cuda.matmul.fp32_precision = "tf32" if precision == "tf32" else "ieee"


def amp_context(precision, device):
    if precision == "bf16":
        return torch.autocast(device_type=device, dtype=torch.bfloat16)
    return contextlib.nullcontext()


def synchronize(device):
    if device == "cuda":
        torch.cuda.synchronize()


def percentile(values, q):
    ordered = sorted(values)
    position = (len(ordered) - 1) * q
    low = int(position)
    high = min(low + 1, len(ordered) - 1)
    return ordered[low] + (ordered[high] - ordered[low]) * (position - low)


def measure(step, seconds, repeats, batch, device):
    """Synchronous batch latency, including Python dispatch and completion wait.

    These are request/response timings, not optimistic queued GEMM throughput.
    Every repeat has the requested duration; per-call tails are recorded too.
    """
    rates, latency_ms, iterations = [], [], []
    for _ in range(repeats):
        synchronize(device)
        start = time.perf_counter()
        stop = start + seconds
        count = 0
        while time.perf_counter() < stop or count < 3:
            call_start = time.perf_counter()
            step()
            synchronize(device)
            latency_ms.append((time.perf_counter() - call_start) * 1e3)
            count += 1
        elapsed = time.perf_counter() - start
        rates.append(batch * count / elapsed)
        iterations.append(count)
    return {
        "samples_per_second_median": statistics.median(rates),
        "samples_per_second_repeats": rates,
        "batch_latency_ms_median": statistics.median(latency_ms),
        "batch_latency_ms_p95": percentile(latency_ms, 0.95),
        "batch_latency_ms_p99": percentile(latency_ms, 0.99),
        "iterations_per_repeat": iterations,
        "repeat_seconds": seconds,
        "repeats": repeats,
    }


def load_recorded_fixture(config):
    """Read inert arrays, validate the entire corpus, then select batch rows.

    Hash and parse the same bytes so a changing file cannot silently mix corpora
    within a sweep. Object/pickled arrays are never loaded.
    """
    path = Path(config["fixture_path"])
    raw = path.read_bytes()
    digest = hashlib.sha256(raw).hexdigest()
    if config.get("fixture_sha256") not in (None, digest):
        raise ValueError("Fixture changed after the sweep manifest was created")
    with np.load(io.BytesIO(raw), allow_pickle=False) as corpus:
        obs, candidates, mask = (corpus[key] for key in ("obs", "candidates", "mask"))
        recorded_actions = corpus["actions"] if "actions" in corpus else None
    rows = obs.shape[0] if obs.ndim else 0
    if rows == 0 or obs.shape != (rows, config["obs_dim"]):
        raise ValueError(f"Invalid fixture obs shape: {obs.shape}")
    if candidates.shape != (rows, config["actions"], config["action_dim"]):
        raise ValueError(f"Invalid fixture candidates shape: {candidates.shape}")
    if mask.shape != (rows, config["actions"]):
        raise ValueError(f"Invalid fixture mask shape: {mask.shape}")
    if obs.dtype != np.float32 or candidates.dtype != np.float32:
        raise ValueError("Recorded obs and candidates must be float32; no silent lossy conversion")
    if not np.isfinite(obs).all() or not np.isfinite(candidates).all():
        raise ValueError("Nonfinite recorded observation/candidate feature")
    if mask.dtype.kind not in "bifu" or not np.isfinite(mask).all() or not np.isin(mask, (0, 1)).all():
        raise ValueError("Recorded mask must contain finite binary values")
    mask = mask.astype(np.bool_, copy=False)
    if not mask.any(axis=1).all():
        raise ValueError("Recorded corpus contains an all-invalid action row")
    card_ids = candidates[..., 16] * 192
    if not ((card_ids >= 0) & (card_ids <= 192)).all() or not np.allclose(card_ids, np.rint(card_ids), atol=2e-4, rtol=0):
        raise ValueError("Recorded candidate card IDs are outside the adapter-v1 categorical contract")
    if recorded_actions is not None:
        if recorded_actions.shape != (rows,) or recorded_actions.dtype.kind not in "iu":
            raise ValueError("Recorded actions must be an integer array of shape [N]")
        if np.any(recorded_actions < 0) or np.any(recorded_actions >= config["actions"]):
            raise ValueError("Recorded action index outside candidate bounds")
        if not mask[np.arange(rows), recorded_actions].all():
            raise ValueError("Recorded corpus includes an illegal selected action")
    selection = np.arange(config["batch"]) % rows
    host = tuple(torch.from_numpy(np.ascontiguousarray(value[selection])) for value in (obs, candidates, mask))
    selected_actions = None if recorded_actions is None else torch.from_numpy(
        np.ascontiguousarray(recorded_actions[selection], dtype=np.int64)
    )
    counts = mask.sum(axis=1)
    metadata = {
        "path": str(path.resolve()), "sha256": digest, "corpus_rows": rows,
        "selected_unique_rows": min(rows, config["batch"]), "selection": "row i % corpus_rows, starting at zero",
        "all_corpus_rows_validated": True, "recorded_actions_provided": recorded_actions is not None,
        "corpus_legal_count_min": int(counts.min()), "corpus_legal_count_mean": float(counts.mean()),
        "corpus_legal_count_max": int(counts.max()), "corpus_unique_legal_card_ids": int(np.unique(np.rint(card_ids[mask])).size),
    }
    return host, selected_actions, metadata


def make_fixture(config):
    device = config["device"]
    batch, actions = config["batch"], config["actions"]
    recorded_actions, metadata = None, None
    if config.get("fixture_path"):
        (host_obs, host_candidates, host_mask), recorded_actions, metadata = load_recorded_fixture(config)
    else:
        # CPU seed makes fixture identical across precision/execution variants.
        generator = torch.Generator().manual_seed(config["seed"])
        host_obs = torch.randn(batch, config["obs_dim"], generator=generator)
        host_candidates = torch.randn(batch, actions, config["action_dim"], generator=generator)
        host_candidates[..., 16] = torch.randint(0, 193, (batch, actions), generator=generator).float() / 192
        legal_count = torch.randint(1, actions + 1, (batch, 1), generator=generator)
        host_mask = torch.arange(actions).unsqueeze(0) < legal_count
    validate_legal_mask(host_mask)
    if device == "cuda":
        host_obs, host_candidates, host_mask = (
            tensor.pin_memory() for tensor in (host_obs, host_candidates, host_mask)
        )
    return (host_obs, host_candidates, host_mask), tuple(
        tensor.to(device) for tensor in (host_obs, host_candidates, host_mask)
    ), None if recorded_actions is None else recorded_actions.to(device), metadata


def correctness(model, inputs, config, recorded_actions=None):
    """Compare forward probabilities and PPO gradients against strict FP32."""
    device, precision = config["device"], config["precision"]
    checked_rows = config["batch"] if config.get("fixture_path") else min(64, config["batch"])
    obs, candidates, mask = (tensor[:checked_rows] for tensor in inputs)
    reference = copy.deepcopy(model)
    set_precision("fp32")
    reference_logits, reference_values = reference(obs, candidates, mask)
    action = reference_logits.detach().argmax(dim=-1) if recorded_actions is None else recorded_actions[:checked_rows]
    old_logp = reference_logits.detach().log_softmax(-1).gather(1, action[:, None]).squeeze(1)
    advantage = torch.linspace(-1, 1, obs.shape[0], device=device)
    returns = torch.linspace(1, -1, obs.shape[0], device=device)
    reference_loss = ppo_loss(reference_logits, reference_values, action, old_logp, advantage, returns)
    reference_loss.backward()
    reference_gradient = torch.cat([parameter.grad.flatten() for parameter in reference.parameters()])
    set_precision(precision)
    with amp_context(precision, device):
        logits, values = model(obs, candidates, mask)
        loss = ppo_loss(logits, values, action, old_logp, advantage, returns)
    loss.backward()
    gradient = torch.cat([parameter.grad.flatten() for parameter in model.parameters()])
    logits_error = (logits[mask] - reference_logits[mask]).abs().max().item()
    probability_error = (logits.softmax(-1) - reference_logits.softmax(-1)).abs().max().item()
    value_error = (values - reference_values).abs().max().item()
    gradient_cosine = torch.nn.functional.cosine_similarity(gradient, reference_gradient, dim=0).item()
    gradient_relative_l2 = ((gradient - reference_gradient).norm() / reference_gradient.norm().clamp_min(1e-12)).item()
    if not bool(torch.isfinite(gradient).all() and torch.isfinite(values).all()):
        raise AssertionError("Nonfinite values/gradients")
    if logits_error > 0.03 or probability_error > 0.003 or gradient_cosine < 0.99 or gradient_relative_l2 > 0.1:
        raise AssertionError(f"Precision check failed: logits={logits_error}, probability={probability_error}, gradient_cosine={gradient_cosine}, gradient_relative_l2={gradient_relative_l2}")
    model.zero_grad(set_to_none=True)
    try:
        validate_legal_mask(torch.zeros(1, config["actions"], dtype=torch.bool))
    except ValueError:
        invalid_rejected = True
    else:
        raise AssertionError("All-invalid row was accepted")
    return {
        "fp32_reference_max_legal_logit_abs_error": logits_error,
        "fp32_reference_max_probability_abs_error": probability_error,
        "fp32_reference_max_value_abs_error": value_error,
        "fp32_reference_gradient_cosine": gradient_cosine,
        "fp32_reference_gradient_relative_l2": gradient_relative_l2,
        "all_invalid_row_rejected": invalid_rejected,
        "finite_values_and_gradients": True,
        "precision_checked_rows": checked_rows,
        "precision_action_source": "recorded legal actions" if recorded_actions is not None else "legal argmax",
    }


def capture(step):
    # Capture against stable inputs/model/optimizer buffers. Only this process
    # owns this CUDA context. Warmup uses a side stream as documented by PyTorch.
    stream = torch.cuda.Stream()
    stream.wait_stream(torch.cuda.current_stream())
    with torch.cuda.stream(stream):
        for _ in range(4):
            step()
    torch.cuda.current_stream().wait_stream(stream)
    torch.cuda.synchronize()
    graph = torch.cuda.CUDAGraph()
    with torch.cuda.graph(graph):
        output = step()

    def replay():
        graph.replay()
        return output

    return replay, graph


def actor_runner(model, inputs, config):
    precision, device, mode = config["precision"], config["device"], config["mode"]
    model.eval()

    def forward(obs, candidates, mask):
        with torch.no_grad(), amp_context(precision, device):
            logits, values = model(obs, candidates, mask)
            return sample_actions(logits, values)

    if mode == "compile":
        forward = torch.compile(forward, mode="reduce-overhead", fullgraph=True, dynamic=False)

    def step():
        if mode == "compile":
            torch.compiler.cudagraph_mark_step_begin()
        return forward(*inputs)

    owner = None
    if mode == "graph":
        step, owner = capture(step)
    for _ in range(5):
        step()
    synchronize(device)
    # Clones are mandatory: graph-owned output storage is overwritten by replay.
    draws = [step().detach().clone() for _ in range(8)]
    synchronize(device)
    action = torch.stack([draw[:, 0].long() for draw in draws])
    legal = inputs[2].unsqueeze(0).expand(8, -1, -1).gather(2, action.unsqueeze(-1))
    if not bool(legal.all()) or not all(bool(torch.isfinite(draw).all()) for draw in draws):
        raise AssertionError("Invalid sampled action or nonfinite actor packet")
    eligible = inputs[2].sum(-1) > 1
    # Batch=1 can be a forced action, where deterministic draws are required.
    stochastic = bool((action[:, eligible] != action[0, eligible]).any()) if bool(eligible.any()) else None
    # Repeated samples from a small/peaked policy are not proof of broken RNG.
    # Independently test graph RNG on a uniform, sufficiently large fixture.
    uniform_rng_check = None
    if mode == "graph":
        uniform_logits = torch.zeros(128, 8, device=device)
        uniform_values = torch.zeros(128, device=device)
        rng_step, rng_owner = capture(lambda: sample_actions(uniform_logits, uniform_values))
        first = rng_step().clone()
        second = rng_step().clone()
        uniform_rng_check = bool((first[:, 0] != second[:, 0]).any())
        if not uniform_rng_check:
            raise AssertionError("CUDA graph RNG frozen on uniform sampling fixture")
    with torch.no_grad(), amp_context(precision, device):
        eager_logits, eager_values = model(*inputs)
    expected_logp = eager_logits.log_softmax(-1).gather(1, action[0, :, None]).squeeze(1)
    logp_error = (draws[0][:, 1] - expected_logp).abs().max().item()
    value_error = (draws[0][:, 2] - eager_values).abs().max().item()
    if logp_error > 0.03 or value_error > 0.03:
        raise AssertionError(f"Actor execution parity failed: logp={logp_error}, value={value_error}")
    return step, owner, {
        "sampled_actions_legal": True, "rng_varies_across_replays": stochastic,
        "uniform_graph_rng_varies": uniform_rng_check,
        "execution_eager_logp_max_abs_error": logp_error,
        "execution_eager_value_max_abs_error": value_error,
    }


def learner_runner(model, inputs, config, recorded_actions=None):
    precision, device, mode = config["precision"], config["device"], config["mode"]
    model.train()
    with torch.no_grad():
        with amp_context(precision, device):
            logits, values = model(*inputs)
        actions = logits.argmax(-1) if recorded_actions is None else recorded_actions
        old_logp = logits.log_softmax(-1).gather(1, actions[:, None]).squeeze(1).clone()
    generator = torch.Generator(device=device).manual_seed(config["seed"] + 1)
    advantages = torch.randn(config["batch"], device=device, generator=generator)
    advantages = (advantages - advantages.mean()) / advantages.std(unbiased=False).clamp_min(1e-6)
    returns = torch.randn(config["batch"], device=device, generator=generator)
    optimizer = torch.optim.AdamW(model.parameters(), lr=3e-4, fused=device == "cuda", capturable=mode == "graph")

    def objective(obs, candidates, mask):
        with amp_context(precision, device):
            logits, values = model(obs, candidates, mask)
            return ppo_loss(logits, values, actions, old_logp, advantages, returns)

    if mode == "compile":
        objective = torch.compile(objective, mode="reduce-overhead", fullgraph=True, dynamic=False)

    def step():
        if mode == "compile":
            torch.compiler.cudagraph_mark_step_begin()
        optimizer.zero_grad(set_to_none=True)
        loss = objective(*inputs)
        loss.backward()
        torch.nn.utils.clip_grad_norm_(model.parameters(), 0.5, foreach=device == "cuda")
        optimizer.step()
        return loss

    owner = None
    if mode == "graph":
        step, owner = capture(step)
    for _ in range(5):
        step()
    synchronize(device)
    # Compare a real optimizer update after capture/compile warmup with eager.
    # Clone optimizer state; sharing Adam moments would invalidate the comparison.
    reference = copy.deepcopy(model)
    reference_optimizer = torch.optim.AdamW(reference.parameters(), lr=3e-4, fused=device == "cuda")
    reference_optimizer.load_state_dict(copy.deepcopy(optimizer.state_dict()))
    reference_optimizer.zero_grad(set_to_none=True)
    with amp_context(precision, device):
        ref_logits, ref_values = reference(*inputs)
        ref_loss = ppo_loss(ref_logits, ref_values, actions, old_logp, advantages, returns)
    ref_loss.backward()
    torch.nn.utils.clip_grad_norm_(reference.parameters(), 0.5, foreach=device == "cuda")
    reference_optimizer.step()
    actual_loss = step().detach().clone()
    synchronize(device)
    parameter_error = max((a - b).abs().max().item() for a, b in zip(model.parameters(), reference.parameters()))
    loss_error = (actual_loss - ref_loss.detach()).abs().item()
    if parameter_error > 0.001 or loss_error > 0.03:
        raise AssertionError(f"Learner execution parity failed: parameters={parameter_error}, loss={loss_error}")
    return step, (owner, optimizer), {
        "optimizer": "fused AdamW" if device == "cuda" else "AdamW", "gradient_clip_norm": 0.5,
        "execution_eager_optimizer_parameter_max_abs_error": parameter_error,
        "execution_eager_loss_abs_error": loss_error,
    }


def run_case(config):
    torch.set_num_threads(config["cpu_threads"])
    torch.set_num_interop_threads(1)
    torch.manual_seed(config["seed"])
    device = config["device"]
    if device == "cuda" and not torch.cuda.is_available():
        raise RuntimeError("CUDA unavailable")
    if device == "cpu" and (config["mode"] != "eager" or config["precision"] != "fp32"):
        raise ValueError("CPU crossover is intentionally FP32 eager only")
    external = None
    if device == "cuda":
        free, total = torch.cuda.mem_get_info()
        external = {"device_free_before_bytes": free, "device_total_bytes": total, "device_used_before_bytes": total - free}
        torch.cuda.reset_peak_memory_stats()
    start = time.perf_counter()
    host, inputs, recorded_actions, fixture_metadata = make_fixture(config)
    model = CandidatePolicy(config["obs_dim"], config["action_dim"], config["width"], config["scorer"]).to(device)
    checks = correctness(model, inputs, config, recorded_actions)
    setup_seconds = time.perf_counter() - start
    start = time.perf_counter()
    if config["kind"] == "actor":
        step, owner, runner_checks = actor_runner(model, inputs, config)
    else:
        step, owner, runner_checks = learner_runner(model, inputs, config, recorded_actions)
    cold_seconds = time.perf_counter() - start
    checks.update(runner_checks)
    resident = measure(step, config["seconds"], config["repeats"], config["batch"], device)
    result = {
        "config": config, "status": "ok",
        "fixture": "recorded-observation-corpus-v1-not-strength-evidence" if fixture_metadata else "synthetic-random-v1-not-strength-evidence",
        "parameters": sum(parameter.numel() for parameter in model.parameters()),
        "setup_and_reference_seconds": setup_seconds,
        "compile_capture_and_warmup_seconds": cold_seconds,
        "resident": resident, "correctness": checks,
    }
    if fixture_metadata:
        result["fixture_metadata"] = fixture_metadata
    if config["kind"] == "actor":
        output = torch.empty(config["batch"], 3, pin_memory=device == "cuda")

        def roundtrip():
            for target, source in zip(inputs, host):
                target.copy_(source, non_blocking=device == "cuda")
            packet = step()
            output.copy_(packet, non_blocking=device == "cuda")

        result["host_roundtrip"] = measure(roundtrip, config["seconds"], config["repeats"], config["batch"], device)
        result["bytes_uploaded_per_batch"] = sum(tensor.numel() * tensor.element_size() for tensor in host)
        result["bytes_downloaded_per_batch"] = output.numel() * output.element_size()
    else:
        final_loss = step().detach().clone()
        synchronize(device)
        if not bool(torch.isfinite(final_loss)) or not all(bool(torch.isfinite(parameter).all()) for parameter in model.parameters()):
            raise AssertionError("Nonfinite synthetic learner loss/parameters after measurement")
        result["correctness"]["final_synthetic_loss_finite"] = True
        result["learner_transfer_scope"] = "resident minibatch; upload/amortization belongs to integrated pipeline"
    if device == "cuda":
        result["memory"] = {**external, "process_peak_allocated_bytes": torch.cuda.max_memory_allocated(), "process_peak_reserved_bytes": torch.cuda.max_memory_reserved()}
    return result


def split_values(value, transform=str):
    return [transform(item) for item in value.split(",") if item]


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--kinds", default="actor,learner")
    parser.add_argument("--widths", default="128,256,512")
    parser.add_argument("--batches", default="32,256,1024")
    parser.add_argument("--precisions", default="fp32,tf32,bf16")
    parser.add_argument("--modes", default="eager,compile,graph")
    parser.add_argument("--scorers", default="embedded")
    parser.add_argument("--device", choices=("cpu", "cuda"), default="cuda")
    parser.add_argument("--obs-dim", type=int, default=2048)
    parser.add_argument("--action-dim", type=int, default=32)
    parser.add_argument("--actions", type=int, default=64)
    parser.add_argument("--fixture", type=Path, help="Recorded NPZ: float32 obs/candidates, binary mask; optional integer actions")
    parser.add_argument("--seconds", type=float, default=0.2, help="Duration per repeat and measurement phase")
    parser.add_argument("--repeats", type=int, default=3)
    parser.add_argument("--cpu-threads", type=int, default=1)
    parser.add_argument("--seed", type=int, default=20260925)
    parser.add_argument("--max-cases", type=int, default=36)
    parser.add_argument("--case-timeout", type=float, default=180)
    parser.add_argument("--max-total-seconds", type=float, default=600)
    parser.add_argument("--output", type=Path)
    parser.add_argument("--single-case", help=argparse.SUPPRESS)
    args = parser.parse_args()
    if args.single_case:
        config = json.loads(args.single_case)
        try:
            result = run_case(config)
        except Exception as error:
            result = {"config": config, "status": "error", "error": str(error), "traceback": traceback.format_exc()}
        print(json.dumps(result, allow_nan=False), flush=True)
        return
    if args.seconds <= 0 or args.repeats < 1 or args.max_cases < 1:
        parser.error("seconds, repeats and max-cases must be positive")
    if max(split_values(args.batches, int)) > 2048:
        parser.error("Preflight safety bound: maximum batch is 2048")
    fixture = None
    if args.fixture:
        if (args.obs_dim, args.actions, args.action_dim) != (2048, 64, 32):
            parser.error("Recorded adapter-v1 fixtures require obs=2048, actions=64 and action-dim=32")
        if not args.fixture.is_file():
            parser.error(f"Fixture does not exist: {args.fixture}")
        fixture = {"path": str(args.fixture.resolve()), "sha256": hashlib.sha256(args.fixture.read_bytes()).hexdigest()}
    products = itertools.product(
        split_values(args.kinds), split_values(args.widths, int), split_values(args.batches, int),
        split_values(args.scorers), split_values(args.precisions), split_values(args.modes),
    )
    manifest = {
        "schema": "shards-training-preflight-gpu-v1",
        "scope": "Recorded-observation systems microbenchmark; fabricated learner targets; no playing-strength claim" if fixture else "Synthetic systems microbenchmark, no playing-strength claim",
        "torch": torch.__version__, "cuda_runtime": torch.version.cuda,
        "python": platform.python_version(), "platform": platform.platform(),
        "gpu": torch.cuda.get_device_name() if torch.cuda.is_available() else None,
        "started_utc": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime()),
        "command": sys.argv, "results": [],
        "source_sha256": {name: hashlib.sha256(Path(__file__).with_name(name).read_bytes()).hexdigest() for name in ("model.py", "gpu_bench.py")},
    }
    if fixture:
        manifest["fixture"] = fixture
    started = time.perf_counter()
    for kind, width, batch, scorer, precision, mode in itertools.islice(products, args.max_cases):
        remaining = args.max_total_seconds - (time.perf_counter() - started)
        if remaining <= 1:
            manifest["stopped_at_time_limit"] = True
            break
        config = dict(kind=kind, width=width, batch=batch, scorer=scorer, precision=precision, mode=mode,
                      device=args.device, obs_dim=args.obs_dim, action_dim=args.action_dim, actions=args.actions,
                      seconds=args.seconds, repeats=args.repeats, seed=args.seed, cpu_threads=args.cpu_threads)
        if fixture:
            config.update(fixture_path=fixture["path"], fixture_sha256=fixture["sha256"])
        print(f"Case {len(manifest['results']) + 1}: {config}", file=sys.stderr, flush=True)
        try:
            child_process = subprocess.Popen(
                [sys.executable, str(Path(__file__).resolve()), "--single-case", json.dumps(config)],
                stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True, start_new_session=True,
            )
            child_stdout, child_stderr = child_process.communicate(timeout=min(args.case_timeout, remaining))
            # Framework warnings go to stderr; preserve diagnostics without flooding.
            result = json.loads(child_stdout.strip().splitlines()[-1])
            if child_stderr:
                result["stderr_tail"] = child_stderr[-6000:]
            if child_process.returncode:
                result["process_returncode"] = child_process.returncode
        except subprocess.TimeoutExpired:
            os.killpg(child_process.pid, signal.SIGKILL)
            child_process.communicate()
            result = {"config": config, "status": "timeout", "timeout_seconds": min(args.case_timeout, remaining)}
        except Exception as error:
            result = {"config": config, "status": "error", "error": str(error)}
            if "child_stdout" in locals():
                result["stdout_tail"] = child_stdout[-2000:]
                result["stderr_tail"] = child_stderr[-6000:]
        manifest["results"].append(result)
        manifest["elapsed_seconds"] = time.perf_counter() - started
        if args.output:
            args.output.parent.mkdir(parents=True, exist_ok=True)
            temporary = args.output.with_suffix(args.output.suffix + ".tmp")
            temporary.write_text(json.dumps(manifest, indent=2, allow_nan=False) + "\n")
            temporary.replace(args.output)
        print(f"  {result['status']}" + (f" resident={result['resident']['samples_per_second_median']:.0f}/s" if result["status"] == "ok" else f" {result.get('error', '')}"), file=sys.stderr, flush=True)
    if not args.output:
        print(json.dumps(manifest, indent=2, allow_nan=False))
    if any(result["status"] != "ok" for result in manifest["results"]):
        raise SystemExit(1)


if __name__ == "__main__":
    main()
