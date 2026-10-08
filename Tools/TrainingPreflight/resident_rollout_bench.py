#!/usr/bin/env python3
"""Bounded GPU-resident FP32 rollout storage probe; no optimizer or training.

Run under an outer timeout, for example ``timeout --kill-after=5s 120s ...``.
The coordinator also bounds its owned child process group. Actual storage is
small (8192 rows by default); larger capacity figures are byte estimates only.
Append copies already-device-resident actor-like inputs into an owned ring.
After that experiment the recorded corpus is restored and the ring is sealed;
random minibatch gather and frozen inference never modify stored experience.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import os
from pathlib import Path
import platform
import signal
import statistics
import subprocess
import sys
import time
import traceback

PROCESS_STARTED = time.monotonic()

import numpy as np
import torch

from bench_common import Monitor, gpu_status
from gpu_bench import capture, load_recorded_fixture, percentile
from model import CandidatePolicy, sample_actions
from rollout_bench import METADATA, exit_on_termination, stop_child_group


FIELDS = ("obs", "candidates", "mask", "metadata")


def memory_sample():
    return {
        "allocated_bytes": torch.cuda.memory_allocated(),
        "reserved_bytes": torch.cuda.memory_reserved(),
        "peak_allocated_bytes": torch.cuda.max_memory_allocated(),
        "peak_reserved_bytes": torch.cuda.max_memory_reserved(),
        "cuda_free_total_bytes": list(torch.cuda.mem_get_info()),
    }


class OwnedRing:
    """Single-stream ring with an explicit collection-to-read-only phase change."""

    def __init__(self, source):
        self.rows = source["obs"].shape[0]
        self.arrays = {name: torch.empty_like(source[name]) for name in FIELDS}
        self.position = 0
        self.sealed = False
        self._captured_append = None
        self.restore(source)

    def restore(self, source):
        if self.sealed:
            raise RuntimeError("A sealed rollout cannot be overwritten")
        for name in FIELDS:
            self.arrays[name].copy_(source[name])
        self.position = 0
        if self._captured_append is not None:
            self._captured_append.reset_cursor(0)

    def append(self, actor_inputs):
        if self.sealed:
            raise RuntimeError("A sealed rollout cannot be overwritten")
        if self._captured_append is not None:
            raise RuntimeError("Close the captured append before switching to eager append")
        count = actor_inputs["obs"].shape[0]
        if not 1 <= count <= self.rows or any(actor_inputs[name].shape[0] != count for name in FIELDS):
            raise ValueError("Append needs one bounded, equally sized batch")
        first = min(count, self.rows - self.position)
        for name in FIELDS:
            self.arrays[name][self.position:self.position + first].copy_(actor_inputs[name][:first])
            if first < count:
                self.arrays[name][:count - first].copy_(actor_inputs[name][first:])
        self.position = (self.position + count) % self.rows

    def seal(self):
        self.sealed = True


class CapturedAppend:
    """Optional moving append; cursor, modulo indices and writes replay on GPU.

    This owns the append phase until close; it is not a concurrent replay buffer
    or a trajectory-lifetime manager. A public replay checks the ring's seal.
    """

    def __init__(self, ring, actor_inputs):
        if ring.sealed or ring._captured_append is not None:
            raise RuntimeError("Captured append requires an unsealed, unowned append phase")
        self.ring, self.actor_inputs = ring, actor_inputs
        self.count = actor_inputs["obs"].shape[0]
        if not 1 <= self.count <= ring.rows or any(actor_inputs[name].shape[0] != self.count for name in FIELDS):
            raise ValueError("Captured append needs one bounded, equally sized batch")
        self.cursor = torch.tensor(ring.position, dtype=torch.int64, device="cuda")
        self.row_offsets = torch.arange(self.count, dtype=torch.int64, device="cuda")
        self.row_indices = torch.empty_like(self.row_offsets)
        self._replay = self._owner = None
        self.closed = False
        ring._captured_append = self
        self._replay, self._owner = capture(self._copy_and_advance)
        # Capture warmup performed real writes. The caller restores the corpus;
        # reset the device cursor so warmup does not change measured semantics.
        self.reset_cursor(ring.position)

    def _copy_and_advance(self):
        torch.add(self.row_offsets, self.cursor, out=self.row_indices)
        self.row_indices.remainder_(self.ring.rows)
        for name in FIELDS:
            self.ring.arrays[name].index_copy_(0, self.row_indices, self.actor_inputs[name])
        self.cursor.add_(self.count).remainder_(self.ring.rows)

    def reset_cursor(self, position):
        if self.closed or self.ring.sealed:
            raise RuntimeError("Captured append is closed or its rollout is sealed")
        if not 0 <= position < self.ring.rows:
            raise ValueError("Cursor is outside the ring")
        self.cursor.fill_(position)

    def step(self):
        if self.closed or self.ring.sealed:
            raise RuntimeError("Captured append is closed or its rollout is sealed")
        self._replay()

    def close(self):
        if self.closed:
            return
        self.ring.position = int(self.cursor.item())
        self.ring._captured_append = None
        self._replay = self._owner = self.actor_inputs = None
        self.closed = True


def verify_owned_append(ring, actor_inputs):
    count = actor_inputs["obs"].shape[0]
    expected = {name: value.clone() for name, value in actor_inputs.items()}
    ring.position = ring.rows - max(1, count // 2)
    positions = (torch.arange(count, device="cuda") + ring.position) % ring.rows
    ring.append(actor_inputs)
    for name in FIELDS:
        if ring.arrays[name].untyped_storage().data_ptr() == actor_inputs[name].untyped_storage().data_ptr():
            raise AssertionError(f"Ring aliases actor inputs: {name}")
        if not torch.equal(ring.arrays[name].index_select(0, positions), expected[name]):
            raise AssertionError(f"Wrapped append changed a field: {name}")
        actor_inputs[name].add_(1)
        if not torch.equal(ring.arrays[name].index_select(0, positions), expected[name]):
            raise AssertionError(f"Actor buffer reuse overwrote stored experience: {name}")
        actor_inputs[name].copy_(expected[name])
    torch.cuda.synchronize()
    return {"wrapped_append_exact": True, "separate_storage_addresses": True,
            "actor_buffer_mutation_does_not_change_ring": True}


def verify_captured_append(append, original):
    ring, actor_inputs, count = append.ring, append.actor_inputs, append.count
    expected = {name: value.clone() for name, value in actor_inputs.items()}
    first_start = ring.rows - max(1, count // 2)
    first_indices = (torch.arange(count) + first_start) % ring.rows
    second_start = (first_start + count) % ring.rows
    second_indices = (torch.arange(count) + second_start) % ring.rows
    append.reset_cursor(first_start)
    append.step()
    if int(append.cursor.item()) != second_start:
        raise AssertionError("Captured cursor did not advance on first replay")
    for name in FIELDS:
        actual = ring.arrays[name].index_select(0, first_indices.to("cuda"))
        if not torch.equal(actual, expected[name]):
            raise AssertionError(f"Captured wrapped append differs: {name}")
        actor_inputs[name].add_(1)
        if not torch.equal(ring.arrays[name].index_select(0, first_indices.to("cuda")), expected[name]):
            raise AssertionError(f"Captured append aliases actor source: {name}")
    append.step()
    if int(append.cursor.item()) != (second_start + count) % ring.rows:
        raise AssertionError("Captured cursor did not advance on second replay")
    # Independent CPU scatter reference checks both writes, their overlap and
    # every untouched row. Metadata bytes are compared without interpretation.
    for name in FIELDS:
        reference = original[name].clone()
        reference[first_indices] = expected[name].cpu()
        reference[second_indices] = actor_inputs[name].cpu()
        if not torch.equal(ring.arrays[name].cpu(), reference):
            raise AssertionError(f"Captured replay ignored changed source/cursor or changed untouched rows: {name}")
        actor_inputs[name].copy_(expected[name])
    torch.cuda.synchronize()
    return {"gpu_cursor_advances_every_replay": True, "wrapped_and_next_append_exact": True,
            "changed_source_consumed_after_capture": True, "actor_buffer_mutation_does_not_change_prior_copy": True,
            "all_rows_and_metadata_match_independent_cpu_scatter": True}


class Timer:
    """Completed request latency, with a common wall deadline for measured work."""

    def __init__(self, seconds, repeats, max_seconds):
        self.seconds, self.repeats = seconds, repeats
        self.deadline = time.monotonic() + max_seconds
        self.measured_seconds = 0.0

    def measure(self, step, rows):
        rates, latencies, counts, elapsed_repeats = [], [], [], []
        for _ in range(self.repeats):
            torch.cuda.synchronize()
            started = time.monotonic()
            count = 0
            while time.monotonic() - started < self.seconds or count == 0:
                if time.monotonic() >= self.deadline:
                    raise TimeoutError("Resident storage measured-work deadline reached")
                tick = time.monotonic()
                step()
                torch.cuda.synchronize()
                latencies.append((time.monotonic() - tick) * 1000)
                count += 1
            elapsed = time.monotonic() - started
            self.measured_seconds += elapsed
            rates.append(rows * count / elapsed)
            counts.append(count)
            elapsed_repeats.append(elapsed)
        return {
            "rows_per_second_median": statistics.median(rates),
            "rows_per_second_repeats": rates,
            "batch_latency_ms_median": statistics.median(latencies),
            "batch_latency_ms_p95": percentile(latencies, .95),
            "iterations_per_repeat": counts, "elapsed_seconds_repeats": elapsed_repeats,
        }


class ResidentBatch:
    def __init__(self, ring, model, indices, mode):
        if not ring.sealed:
            raise AssertionError("Gather requires sealed storage")
        self.ring, self.model, self.mode = ring, model, mode
        self.index_bank = torch.from_numpy(indices).to("cuda")
        self.indices = self.index_bank[0].clone()
        self.cursor = 0
        count = indices.shape[1]
        self.destination = {name: torch.empty((count, *value.shape[1:]), dtype=value.dtype, device="cuda")
                            for name, value in ring.arrays.items()}
        self.output_host = torch.empty(count, 3, dtype=torch.float32, pin_memory=True)
        self.gather_owner = self.actor_owner = None
        if mode == "graph":
            self.gather_call, self.gather_owner = capture(self.gather)
            self.actor_call, self.actor_owner = capture(self.gather_and_actor)
        else:
            self.gather_call, self.actor_call = self.gather, self.gather_and_actor

    def gather(self):
        for name in FIELDS:
            torch.index_select(self.ring.arrays[name], 0, self.indices, out=self.destination[name])

    def gather_and_actor(self):
        self.gather()
        return sample_actions(*self.model(self.destination["obs"], self.destination["candidates"],
                                          self.destination["mask"].bool()))

    def select_next(self):
        self.indices.copy_(self.index_bank[self.cursor % self.index_bank.shape[0]])
        self.cursor += 1

    def step_gather(self):
        self.select_next()
        self.gather_call()

    def step_actor(self):
        self.select_next()
        self.output_host.copy_(self.actor_call(), non_blocking=True)

    def close(self):
        # Eager callables are bound methods and otherwise retain a self-cycle.
        self.gather_call = self.actor_call = None
        self.gather_owner = self.actor_owner = None

    def validate(self, original, cpu_indices):
        # A changed post-capture batch proves that replays consume new indices.
        checks = []
        for bank_index in (1, 2):
            self.cursor = bank_index
            self.step_actor()
            torch.cuda.synchronize()
            selected = cpu_indices[bank_index]
            fresh = {}
            for name in FIELDS:
                expected_cpu = original[name].index_select(0, torch.from_numpy(selected))
                if not torch.equal(self.destination[name].cpu(), expected_cpu):
                    raise AssertionError(f"GPU gather did not preserve {name} or original row/slot order")
                fresh[name] = expected_cpu.to("cuda")
            packet = self.output_host
            if not bool(torch.isfinite(packet).all()):
                raise AssertionError("Nonfinite resident inference packet")
            actions = packet[:, 0]
            if not torch.equal(actions, actions.round()) or not bool(((actions >= 0) & (actions < 64)).all()):
                raise AssertionError("Resident actor returned an invalid slot")
            chosen = actions.long().to("cuda")
            if not bool(fresh["mask"].bool().gather(1, chosen[:, None]).all()):
                raise AssertionError("Resident actor selected an illegal original slot")
            logits, values = self.model(fresh["obs"], fresh["candidates"], fresh["mask"].bool())
            expected_logp = logits.log_softmax(-1).gather(1, chosen[:, None]).squeeze(1).cpu()
            expected_value = values.cpu()
            torch.testing.assert_close(packet[:, 1], expected_logp, rtol=1e-5, atol=1e-5)
            torch.testing.assert_close(packet[:, 2], expected_value, rtol=1e-5, atol=1e-5)
            checks.append({"logp": (packet[:, 1] - expected_logp).abs().max().item(),
                           "value": (packet[:, 2] - expected_value).abs().max().item()})
        self.cursor = 0
        return {
            "post_capture_changed_batches_checked": 2, "gathered_fields_bit_exact": True,
            "mask_and_original_candidate_slots_exact": True, "finite_legal_packet": True,
            "selected_logp_eager_max_abs_error": max(item["logp"] for item in checks),
            "value_eager_max_abs_error": max(item["value"] for item in checks),
            "eager_reference_independently_uploaded_from_cpu_fixture": True,
        }


@torch.inference_mode()
def run_case(config):
    torch.set_num_threads(1)
    torch.set_num_interop_threads(1)
    torch.manual_seed(config["seed"])
    torch.backends.fp32_precision = "ieee"
    if not torch.cuda.is_available():
        raise RuntimeError("CUDA is required for GPU-resident storage")
    torch.cuda.reset_peak_memory_stats()
    before = memory_sample()
    telemetry_before = gpu_status()
    host, actions, fixture = load_recorded_fixture({
        "fixture_path": config["fixture_path"], "fixture_sha256": config["fixture_sha256"],
        "obs_dim": 2048, "actions": 64, "action_dim": 32, "batch": config["rows"],
    })
    metadata = np.zeros(config["rows"], dtype=METADATA)
    metadata["action"] = actions.numpy() if actions is not None else host[2].numpy().argmax(1)
    metadata["decision_id"] = np.arange(config["rows"], dtype=np.uint32)
    original = dict(obs=host[0], candidates=host[1], mask=host[2].float(),
                    metadata=torch.from_numpy(metadata.view(np.uint8).reshape(config["rows"], METADATA.itemsize)))
    tick = time.monotonic()
    source = {name: value.to("cuda") for name, value in original.items()}
    torch.cuda.synchronize()
    initial_upload_seconds = time.monotonic() - tick
    ring = OwnedRing(source)
    actor_inputs = {name: value[:config["append_batch"]].clone() for name, value in source.items()}
    ownership = verify_owned_append(ring, actor_inputs)
    ring.restore(source)
    torch.cuda.synchronize()
    store_bytes = sum(value.numel() * value.element_size() for value in ring.arrays.values())
    resident_memory = memory_sample()
    model = CandidatePolicy(width=config["width"], scorer="embedded").eval().requires_grad_(False).to("cuda")
    # Equivalent model initialization and index-bank order to rollout_bench.
    rng = np.random.default_rng(config["seed"])
    results = []
    with Monitor() as monitor:
        timer = Timer(config["seconds"], config["repeats"], config["max_timed_seconds"])
        append_results, captured_append = [], None
        for append_mode in config["append_modes"]:
            tick = time.monotonic()
            if captured_append is not None and not captured_append.closed:
                captured_append.close()
            ring.restore(source)
            if append_mode == "graph":
                captured_append = CapturedAppend(ring, actor_inputs)
                ring.restore(source)
                append_checks = verify_captured_append(captured_append, original)
                ring.restore(source)
                append_step = captured_append.step
            else:
                append_checks = dict(ownership)
                append_step = lambda: ring.append(actor_inputs)
            torch.cuda.synchronize()
            append_setup_seconds = time.monotonic() - tick
            append_timing = timer.measure(append_step, config["append_batch"])
            expected_cursor = sum(append_timing["iterations_per_repeat"]) * config["append_batch"] % ring.rows
            actual_cursor = int(captured_append.cursor.item()) if append_mode == "graph" else ring.position
            if actual_cursor != expected_cursor:
                raise AssertionError("Measured append cursor does not reconcile with completed batches")
            append_checks["measured_cursor_matches_completed_rows"] = True
            append_results.append({"mode": append_mode, "batch": config["append_batch"],
                                   "setup_and_parity_seconds": append_setup_seconds,
                                   "correctness": append_checks, "timing": append_timing,
                                   "memory": memory_sample()})
        del append_step
        # If the caller requested graph then eager, recreate a live captured
        # handle so the final seal check exercises the sealed (not closed) path.
        if captured_append is not None and captured_append.closed:
            captured_append = CapturedAppend(ring, actor_inputs)
        # The copy probe intentionally overwrites its own rows. Restore every
        # original row before any immutable minibatch/inference measurement.
        tick = time.monotonic()
        ring.restore(source)
        torch.cuda.synchronize()
        restore_seconds = time.monotonic() - tick
        ring.seal()
        for name in FIELDS:
            if not torch.equal(ring.arrays[name], source[name]):
                raise AssertionError(f"Restored sealed corpus differs: {name}")
        try:
            ring.append(actor_inputs)
        except RuntimeError:
            ownership["sealed_ring_rejects_append"] = True
        else:
            raise AssertionError("Sealed corpus accepted an overwrite")
        if captured_append is not None:
            try:
                captured_append.step()
            except RuntimeError:
                ownership["sealed_ring_rejects_captured_append"] = True
            else:
                raise AssertionError("Captured public API overwrote a sealed corpus")
            captured_append.close()
            del captured_append
        # No second device copy of the entire corpus survives into gather tests.
        del source, actor_inputs
        for batch in config["batches"]:
            indices = np.stack([rng.integers(0, config["rows"], size=batch) for _ in range(16)])
            for mode in config["modes"]:
                tick = time.monotonic()
                service = ResidentBatch(ring, model, indices, mode)
                checks = service.validate(original, indices)
                setup_seconds = time.monotonic() - tick
                gather = timer.measure(service.step_gather, batch)
                service.cursor = 0
                actor = timer.measure(service.step_actor, batch)
                results.append({
                    "batch": batch, "mode": mode, "setup_and_parity_seconds": setup_seconds,
                    "correctness": checks, "gather_only": gather, "gather_and_actor": actor,
                    "memory": memory_sample(),
                })
                service.close()
                del service
        # Compare against CPU originals after every measured gather and actor
        # call; no mutation, lossy format conversion, or slot compaction allowed.
        for name in FIELDS:
            if not torch.equal(ring.arrays[name].cpu(), original[name]):
                raise AssertionError(f"Measured workloads mutated sealed rollout: {name}")
        ownership["all_storage_fields_unchanged_after_measurements"] = True
        final_memory = memory_sample()
    per_row = store_bytes / config["rows"]
    return {
        "status": "ok", "config": config, "fixture": fixture,
        "gpu": torch.cuda.get_device_name(), "capability": list(torch.cuda.get_device_capability()),
        "gpu_before": telemetry_before, "gpu_after": gpu_status(), "gpu_monitor": monitor.summary(),
        "telemetry_scope": "Driver-wide NVML and CUDA free/total estimates differ under WSL; allocator fields belong only to this process",
        "ownership_correctness": ownership, "storage_array_bytes": store_bytes,
        "storage_array_breakdown": {name: value.numel() * value.element_size() for name, value in ring.arrays.items()},
        "bytes_per_stored_decision": per_row, "bookkeeping_bytes_per_row": METADATA.itemsize,
        "bookkeeping_scope": "Same 36-byte metadata as CPU rollout probe; selected actions are "
                             + ("recorded" if actions is not None else "legal argmax fallbacks")
                             + ", decision IDs are local row ordinals and remaining trajectory fields are fabricated",
        "initial_fixture_upload_seconds": initial_upload_seconds,
        "restore_after_append_seconds": restore_seconds,
        "append": append_results[0]["timing"], "append_primary_mode": append_results[0]["mode"],
        "append_modes": append_results,
        "append_scope": "Moving/wrapping eager slice copies or optional captured GPU-cursor/modulo/index_copy_ from already-device-resident actor-like fields into independent owned storage; source production and initial H2D excluded; this is not complete trajectory-lifetime or concurrent replay-buffer semantics",
        "gather_scope": "Resident index-bank selection/copy plus GPU index_select of observation/candidates/FP32 mask/metadata into reusable buffers; every call waits for completion",
        "actor_scope": "Gather plus strict-FP32 embedded model, legal mask, sampling/logp/value, batched pinned packet D2H and completion wait; no minibatch observation H2D",
        "index_scope": "16 seeded index banks prepared before timing, matching the CPU benchmark's precomputed-index convention; index generation excluded",
        "memory_before": before, "memory_after_ring_initialization": resident_memory, "memory_final": final_memory,
        "memory_scope": "Allocator peaks are cumulative for this case and include fixture upload, correctness references and graph pools; storage_array_bytes alone is the owned rollout payload",
        "memory_estimates": {str(rows): {"rows": rows, "estimated_array_bytes": round(per_row * rows),
                                        "estimated_GiB": per_row * rows / 2**30} for rows in (131072, 1000000)},
        "estimate_scope": "Exact dense payload scaling only; larger rollouts were not allocated or timed; excludes model/optimizer/graph/staging/allocator memory and does not establish available VRAM",
        "measured_seconds": timer.measured_seconds, "batches": results,
    }


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--fixture", type=Path, default=Path(__file__).with_name("results") / "real-states.npz")
    parser.add_argument("--rows", type=int, default=8192)
    parser.add_argument("--batches", default="256,2048,8192")
    parser.add_argument("--append-batch", type=int, default=256)
    parser.add_argument("--append-modes", default="eager", help="eager, graph, or eager,graph; eager remains the simple default")
    parser.add_argument("--modes", default="graph", help="graph, eager, or eager,graph")
    parser.add_argument("--width", type=int, default=256)
    parser.add_argument("--seconds", type=float, default=.2)
    parser.add_argument("--repeats", type=int, default=3)
    parser.add_argument("--seed", type=int, default=20260925)
    parser.add_argument("--max-timed-seconds", type=float, default=60)
    parser.add_argument("--max-total-seconds", type=float, default=110)
    parser.add_argument("--output", type=Path)
    parser.add_argument("--single-case", help=argparse.SUPPRESS)
    args = parser.parse_args()
    if args.single_case:
        config = json.loads(args.single_case)
        try:
            result = run_case(config)
        except Exception as error:
            result = {"status": "error", "config": config, "error": str(error), "traceback": traceback.format_exc()}
        print(json.dumps(result, allow_nan=False), flush=True)
        return
    signal.signal(signal.SIGTERM, exit_on_termination)
    try:
        batches = [int(value) for value in args.batches.split(",")]
    except ValueError:
        parser.error("Batches must be comma-separated integers")
    modes = args.modes.split(",")
    append_modes = args.append_modes.split(",")
    if not 256 <= args.rows <= 32768 or not batches or len(batches) > 3 or min(batches) < 1 or max(batches) > 8192:
        parser.error("Bounds: stored rows 256..32768; one to three minibatches in 1..8192")
    if not 1 <= args.append_batch <= min(8192, args.rows) or args.width not in (128, 256, 512):
        parser.error("Invalid append batch or width")
    if not modes or len(modes) > 2 or len(set(modes)) != len(modes) or any(mode not in ("eager", "graph") for mode in modes):
        parser.error("Use distinct eager/graph modes")
    if not append_modes or len(append_modes) > 2 or len(set(append_modes)) != len(append_modes) or any(mode not in ("eager", "graph") for mode in append_modes):
        parser.error("Use distinct eager/graph append modes")
    if not 0 < args.seconds <= 1 or not 1 <= args.repeats <= 5 or not 0 < args.max_timed_seconds <= 60 or not 10 <= args.max_total_seconds <= 120:
        parser.error("Bounds: seconds <=1, repeats <=5, measured work <=60s, total 10..120s")
    if args.seconds * args.repeats * (len(append_modes) + 2 * len(batches) * len(modes)) > args.max_timed_seconds:
        parser.error("Requested measurement windows exceed the measured-work budget")
    if not args.fixture.is_file():
        parser.error(f"Fixture missing: {args.fixture}")
    config = dict(rows=args.rows, batches=batches, append_batch=args.append_batch, append_modes=append_modes, modes=modes, width=args.width,
                  seconds=args.seconds, repeats=args.repeats, seed=args.seed, max_timed_seconds=args.max_timed_seconds,
                  fixture_path=str(args.fixture.resolve()), fixture_sha256=hashlib.sha256(args.fixture.read_bytes()).hexdigest())
    manifest = {
        "schema": "shards-resident-rollout-preflight-v1", "training_campaign_started": False,
        "scope": "Owned GPU rollout copy/gather plus frozen FP32 inference; no optimizer, learning, or strength claim",
        "torch": torch.__version__, "cuda": torch.version.cuda, "numpy": np.__version__,
        "python": platform.python_version(), "platform": platform.platform(), "command": sys.argv,
        "source_sha256": {name: hashlib.sha256(Path(__file__).with_name(name).read_bytes()).hexdigest()
                          for name in ("resident_rollout_bench.py", "rollout_bench.py", "gpu_bench.py", "model.py", "bench_common.py")},
    }
    child = None
    try:
        remaining = args.max_total_seconds - (time.monotonic() - PROCESS_STARTED) - 8
        if remaining <= 0:
            raise TimeoutError("No child runtime remains inside the total budget")
        child = subprocess.Popen([sys.executable, str(Path(__file__).resolve()), "--single-case", json.dumps(config)],
                                 stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True, start_new_session=True)
        output, errors = child.communicate(timeout=remaining)
        result = json.loads(output.strip().splitlines()[-1])
        if errors:
            result["stderr_tail"] = errors[-4000:]
        if child.returncode != 0:
            result["status"], result["returncode"] = "error", child.returncode
    except subprocess.TimeoutExpired:
        result = {"status": "timeout", "config": config, "error": "Owned child exceeded total wall budget"}
    except Exception as error:
        result = {"status": "error", "config": config, "error": str(error)}
    finally:
        if child is not None:
            stop_child_group(child)
    manifest["result"] = result
    manifest["elapsed_seconds"] = time.monotonic() - PROCESS_STARTED
    if args.output:
        args.output.parent.mkdir(parents=True, exist_ok=True)
        temporary = args.output.with_suffix(args.output.suffix + ".tmp")
        temporary.write_text(json.dumps(manifest, indent=2, allow_nan=False) + "\n")
        temporary.replace(args.output)
    else:
        print(json.dumps(manifest, indent=2, allow_nan=False))
    print(f"Resident rollout probe: {result['status']}", file=sys.stderr)
    if result["status"] != "ok":
        raise SystemExit(1)


if __name__ == "__main__":
    main()
