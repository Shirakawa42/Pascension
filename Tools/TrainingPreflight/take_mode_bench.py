#!/usr/bin/env python3
"""Paired CPU replay-gather comparison, without simulation or GPU work.

Both modes use the SAME immutable FP32 store, destination buffers and cyclic
index stream. They include explicit row bounds checks before np.take. Negative
or oversized indices are rejected before clip can alter them. Repeats alternate
raise/clip order; raw repeat latencies and paired speedups are retained.

NumPy documents that mode='raise' always buffers the supplied out array:
https://numpy.org/doc/stable/reference/generated/numpy.take.html
The current rollout default remains raise until measured evidence is reviewed.
"""

from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path
import platform
import statistics
import sys
import time

import numpy as np
import torch

from gpu_bench import load_recorded_fixture, percentile
from rollout_bench import BatchBuffer, build_store, decode, source_checks


def verify_indices_rejected(store, mode):
    destination = BatchBuffer(2, take_mode=mode)
    for name, indices in (
        ("negative", np.array([-1, 0], dtype=np.int64)),
        ("out_of_range", np.array([0, len(store["metadata"])], dtype=np.int64)),
        ("noninteger", np.array([0., 1.], dtype=np.float32)),
        ("wrong_shape", np.array([0], dtype=np.int64)),
    ):
        destination.tensor.fill_(-12345)
        destination.metadata.fill(0)
        try:
            decode("dense_fp32", store, indices, destination)
        except ValueError:
            if not bool((destination.tensor == -12345).all()) or np.any(destination.metadata.view(np.uint8) != 0):
                raise AssertionError(f"{mode}: {name} was rejected after mutating output")
        else:
            raise AssertionError(f"{mode}: {name} indices were silently accepted")
    return {"negative_out_of_range_noninteger_wrong_shape_rejected": True,
            "invalid_indices_rejected_before_output_mutation": True}


def verify_batch(store, indices, destination):
    for mode in ("raise", "clip"):
        destination.take_mode = mode
        decode("dense_fp32", store, indices, destination)
        for key, actual in (("obs", destination.obs), ("candidates", destination.candidates),
                            ("mask", destination.mask), ("metadata", destination.metadata)):
            if not np.array_equal(actual, store[key][indices]):
                raise AssertionError(f"{mode} reconstruction changed {key}")
        actions = destination.metadata["action"]
        if not destination.mask[np.arange(indices.size), actions].all():
            raise AssertionError(f"{mode} reconstructed an illegal recorded action")


def timed_repeat(store, destination, stream, seconds, deadline):
    latencies = []
    cursor = 0
    start = time.perf_counter()
    stop = min(start + seconds, deadline)
    while time.perf_counter() < stop:
        tick = time.perf_counter()
        decode("dense_fp32", store, stream[cursor % len(stream)], destination)
        latencies.append((time.perf_counter() - tick) * 1000)
        cursor += 1
    elapsed = time.perf_counter() - start
    if not cursor:
        raise TimeoutError("Paired comparison deadline reached")
    return {
        "iterations": cursor, "elapsed_seconds": elapsed,
        "rows_per_second": destination.batch * cursor / elapsed,
        "batch_latency_ms_mean": elapsed * 1000 / cursor,
        "batch_latency_ms_median": statistics.median(latencies),
        "batch_latency_ms_p95": percentile(latencies, .95),
    }


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--fixture", type=Path, default=Path(__file__).with_name("results") / "real-states.npz")
    parser.add_argument("--rows", type=int, default=8192)
    parser.add_argument("--batches", default="256,2048,8192")
    parser.add_argument("--seconds", type=float, default=.2)
    parser.add_argument("--repeats", type=int, default=3)
    parser.add_argument("--max-total-seconds", type=float, default=45)
    parser.add_argument("--seed", type=int, default=20260925)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    batches = [int(value) for value in args.batches.split(",")]
    if not 1 <= args.rows <= 32768 or not batches or min(batches) < 1 or max(batches) > 8192:
        parser.error("Bounds: rows1..32768, batches1..8192")
    if not 0 < args.seconds <= 1 or not 1 <= args.repeats <= 5 or not 0 < args.max_total_seconds <= 60:
        parser.error("Bounds: seconds/repeat<=1, repeats<=5, total<=60s; all positive")
    torch.set_num_threads(1)
    torch.set_num_interop_threads(1)
    started = time.perf_counter()
    deadline = started + args.max_total_seconds
    fixture_hash = hashlib.sha256(args.fixture.read_bytes()).hexdigest()
    host, action_tensor, fixture_metadata = load_recorded_fixture({
        "fixture_path": str(args.fixture.resolve()), "fixture_sha256": fixture_hash,
        "obs_dim": 2048, "actions": 64, "action_dim": 32, "batch": args.rows,
    })
    obs, candidates, mask = (tensor.numpy() for tensor in host)
    actions = action_tensor.numpy() if action_tensor is not None else mask.argmax(1).astype(np.int64)
    store = build_store("dense_fp32", obs, candidates, mask, actions)
    # Full-store checks explicitly exercise BOTH modes before measurements.
    checks = {
        mode: {**source_checks("dense_fp32", store, obs, candidates, mask, actions, take_mode=mode),
               **verify_indices_rejected(store, mode)}
        for mode in ("raise", "clip")
    }
    manifest = {
        "schema": "shards-paired-take-mode-v1", "training_campaign_started": False,
        "scope": "Single-thread CPU dense replay gather; no inference, GPU or game simulation",
        "torch": torch.__version__, "numpy": np.__version__, "python": platform.python_version(),
        "platform": platform.platform(), "command": sys.argv, "fixture": fixture_metadata,
        "source_sha256": {name: hashlib.sha256(Path(__file__).with_name(name).read_bytes()).hexdigest()
                          for name in ("take_mode_bench.py", "rollout_bench.py", "gpu_bench.py")},
        "official_api_reference": "https://numpy.org/doc/stable/reference/generated/numpy.take.html",
        "storage_bytes": sum(value.nbytes for value in store.values()), "correctness": checks,
        "pairing": "Same store/destination addresses, same seeded 16-batch cyclic index stream starting at zero each phase; alternating phase order",
        "results": [],
    }
    rng = np.random.default_rng(args.seed)
    try:
        for batch in batches:
            if time.perf_counter() >= deadline:
                raise TimeoutError("Comparison deadline reached")
            destination = BatchBuffer(batch)
            stream = [rng.integers(0, args.rows, size=batch, dtype=np.int64) for _ in range(16)]
            verify_batch(store, stream[0], destination)
            result = {"batch": batch, "index_stream_sha256": hashlib.sha256(np.stack(stream).tobytes()).hexdigest(),
                      "exact_reconstructed_arrays_and_recorded_action_check": True,
                      "destination_reused_across_modes": True, "repeats": []}
            print(f"Paired take modes: batch={batch}", file=sys.stderr, flush=True)
            for repeat in range(args.repeats):
                order = ("raise", "clip") if repeat % 2 == 0 else ("clip", "raise")
                paired = {"order": list(order)}
                for mode in order:
                    destination.take_mode = mode
                    # Both modes receive the same warmup requests and then
                    # restart the same index stream for their measured phase.
                    for indices in stream[:4]:
                        decode("dense_fp32", store, indices, destination)
                    paired[mode] = timed_repeat(store, destination, stream, args.seconds, deadline)
                paired["clip_speedup"] = paired["clip"]["rows_per_second"] / paired["raise"]["rows_per_second"]
                result["repeats"].append(paired)
            for mode in ("raise", "clip"):
                values = [repeat[mode] for repeat in result["repeats"]]
                result[mode] = {
                    "rows_per_second_median": statistics.median(value["rows_per_second"] for value in values),
                    "batch_latency_ms_mean_median": statistics.median(value["batch_latency_ms_mean"] for value in values),
                    "rows_per_second_repeats": [value["rows_per_second"] for value in values],
                }
            result["paired_clip_speedup_median"] = statistics.median(repeat["clip_speedup"] for repeat in result["repeats"])
            manifest["results"].append(result)
            print(f"  paired clip speedup={result['paired_clip_speedup_median']:.3f}", file=sys.stderr, flush=True)
    except TimeoutError:
        manifest["stopped_at_time_limit"] = True
    manifest["elapsed_seconds"] = time.perf_counter() - started
    args.output.parent.mkdir(parents=True, exist_ok=True)
    temporary = args.output.with_suffix(args.output.suffix + ".tmp")
    temporary.write_text(json.dumps(manifest, indent=2, allow_nan=False) + "\n")
    temporary.replace(args.output)


if __name__ == "__main__":
    main()
