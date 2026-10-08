#!/usr/bin/env python3
"""Bounded immutable rollout-storage experiment; no training or checkpoint.

Layouts: dense FP32 with current float32 wire mask; dense FP16 with bool mask;
exact sparse FP32 observations plus ragged FP16 legal candidates. FP16 layouts
retain exact uint8 card IDs. Ragged rows retain ORIGINAL candidate slot indices,
so recorded actions are never renumbered or truncated. Numeric quantization is
measured and checked, not described as lossless. Other trajectory fields are
explicitly fabricated metadata to account for their storage cost.

Packing and random gather/decode are measured separately. Optional CPU/CUDA
actor service includes gather/decode and, for CUDA, reusable pinned upload plus
batched output download. CUDA inference uses a captured frozen actor. Memory
figures for 131072/1000000 rows are extrapolations, never large allocations.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import os
from pathlib import Path
import platform
import resource
import signal
import statistics
import subprocess
import sys
import time
import traceback

import numpy as np
import torch

from gpu_bench import capture, load_recorded_fixture, percentile
from model import CandidatePolicy, sample_actions
from bench_common import gpu_status


METADATA = np.dtype([
    ("action", "<i2"), ("seat", "u1"), ("done", "u1"),
    ("policy_version", "<u4"), ("episode_id", "<u4"), ("decision_id", "<u4"),
    ("old_logp", "<f4"), ("old_value", "<f4"), ("reward", "<f4"),
    ("return", "<f4"), ("advantage", "<f4"),
])
LAYOUTS = ("dense_fp32", "dense_fp16", "ragged_fp16_sparse_obs_fp32")


def checked_half(values):
    """Reject overflow/underflow instead of silently losing unsupported fields."""
    if not np.isfinite(values).all() or np.any(np.abs(values) > np.finfo(np.float16).max):
        raise ValueError("FP16 layout rejected: nonfinite or out-of-range feature")
    result = values.astype(np.float16)
    if not np.isfinite(result).all() or np.any((values != 0) & (result == 0)):
        raise ValueError("FP16 layout rejected: nonfinite conversion or nonzero feature underflow")
    return result


def offsets_for(rows, count):
    offsets = np.zeros(count + 1, dtype=np.int64)
    np.cumsum(np.bincount(rows, minlength=count), out=offsets[1:])
    return offsets


def build_store(layout, obs, candidates, mask, actions):
    """All arrays become immutable after packing; no references to mutable input."""
    rows = obs.shape[0]
    metadata = np.zeros(rows, dtype=METADATA)
    metadata["action"] = actions
    metadata["decision_id"] = np.arange(rows, dtype=np.uint32)
    arrays = {"metadata": metadata}
    if layout == "dense_fp32":
        arrays.update(obs=obs.copy(), candidates=candidates.copy(), mask=mask.astype(np.float32))
    elif layout == "dense_fp16":
        arrays.update(obs=checked_half(obs), candidates=checked_half(candidates), mask=mask.copy(),
                      card_ids=np.rint(candidates[..., 16] * 192).astype(np.uint8))
    elif layout == "ragged_fp16_sparse_obs_fp32":
        # The current adapter has zero padding. Reject a future schema that
        # assigns meaning to masked-out descriptors rather than discarding it.
        if np.any(candidates[~mask] != 0):
            raise ValueError("Ragged layout requires zero-filled invalid candidate descriptors")
        obs_rows, obs_features = np.nonzero(obs)
        candidate_rows, slots = np.nonzero(mask)
        arrays.update(
            obs_offsets=offsets_for(obs_rows, rows), obs_features=obs_features.astype(np.uint16),
            obs_values=obs[obs_rows, obs_features].copy(),
            candidate_offsets=offsets_for(candidate_rows, rows), candidate_slots=slots.astype(np.uint8),
            candidate_values=checked_half(candidates[candidate_rows, slots]),
            card_ids=np.rint(candidates[candidate_rows, slots, 16] * 192).astype(np.uint8),
        )
    else:
        raise ValueError(layout)
    for value in arrays.values():
        value.flags.writeable = False
    return arrays


class BatchBuffer:
    def __init__(self, batch, pinned=False, take_mode="clip"):
        self.batch = batch
        self.take_mode = take_mode
        self.tensor = torch.empty(batch * (2048 + 64 * 32 + 64), dtype=torch.float32, pin_memory=pinned)
        flat = self.tensor.numpy()
        self.obs = flat[:batch * 2048].reshape(batch, 2048)
        self.candidates = flat[batch * 2048:batch * (2048 + 2048)].reshape(batch, 64, 32)
        self.mask = flat[batch * (2048 + 2048):].reshape(batch, 64)
        self.metadata = np.empty(batch, dtype=METADATA)


def gathered_segments(offsets, indices):
    """Vectorized ragged gather, preserving each selected row's stored order."""
    lengths = offsets[indices + 1] - offsets[indices]
    starts = np.cumsum(lengths) - lengths
    positions = np.repeat(offsets[indices] - starts, lengths) + np.arange(int(lengths.sum()))
    rows = np.repeat(np.arange(indices.size), lengths)
    return rows, positions


def decode(layout, store, indices, destination):
    # Explicitly reject invalid rows before using clip mode to avoid NumPy's
    # always-buffered mode='raise' output. No index is silently clipped.
    if indices.shape!=(destination.batch,) or not np.issubdtype(indices.dtype,np.integer) or np.any(indices<0) or np.any(indices>=len(store["metadata"])):
        raise ValueError("Invalid replay row indices")
    np.take(store["metadata"], indices, out=destination.metadata, mode=destination.take_mode)
    if layout == "dense_fp32":
        np.take(store["obs"], indices, axis=0, out=destination.obs, mode=destination.take_mode)
        np.take(store["candidates"], indices, axis=0, out=destination.candidates, mode=destination.take_mode)
        np.take(store["mask"], indices, axis=0, out=destination.mask, mode=destination.take_mode)
    elif layout == "dense_fp16":
        destination.obs[:] = store["obs"][indices]
        destination.candidates[:] = store["candidates"][indices]
        destination.mask[:] = store["mask"][indices]
        destination.candidates[..., 16] = store["card_ids"][indices].astype(np.float32) / 192
    else:
        destination.obs.fill(0)
        destination.candidates.fill(0)
        destination.mask.fill(0)
        rows, positions = gathered_segments(store["obs_offsets"], indices)
        destination.obs[rows, store["obs_features"][positions]] = store["obs_values"][positions]
        rows, positions = gathered_segments(store["candidate_offsets"], indices)
        slots = store["candidate_slots"][positions]
        destination.candidates[rows, slots] = store["candidate_values"][positions]
        destination.candidates[rows, slots, 16] = store["card_ids"][positions].astype(np.float32) / 192
        destination.mask[rows, slots] = 1


def measure(step, seconds, repeats, examples, synchronize=lambda: None):
    latencies, rates, iterations = [], [], []
    for _ in range(repeats):
        synchronize()
        start = time.perf_counter()
        count = 0
        while time.perf_counter() - start < seconds or count == 0:
            tick = time.perf_counter()
            step()
            synchronize()
            latencies.append((time.perf_counter() - tick) * 1000)
            count += 1
        elapsed = time.perf_counter() - start
        rates.append(examples * count / elapsed)
        iterations.append(count)
    return {
        "rows_per_second_median": statistics.median(rates), "rows_per_second_repeats": rates,
        "batch_latency_ms_median": statistics.median(latencies),
        "batch_latency_ms_p95": percentile(latencies, .95), "iterations_per_repeat": iterations,
    }


def source_checks(layout, store, obs, candidates, mask, actions, take_mode="clip"):
    """Reconstruct ALL stored rows in bounded chunks, including original slots."""
    obs_error = candidate_error = 0.0
    for start in range(0, obs.shape[0], 256):
        indices = np.arange(start, min(start + 256, obs.shape[0]))
        target = BatchBuffer(indices.size, take_mode=take_mode)
        decode(layout, store, indices, target)
        reference_obs, reference_candidates, reference_mask = obs[indices], candidates[indices], mask[indices]
        if not np.isfinite(target.tensor.numpy()).all():
            raise AssertionError("Nonfinite reconstruction")
        if not np.array_equal(target.mask.astype(bool), reference_mask):
            raise AssertionError("Legal mask changed")
        if not np.array_equal(target.metadata["action"], actions[indices]):
            raise AssertionError("Recorded action slot changed")
        if not reference_mask[np.arange(indices.size), target.metadata["action"]].all():
            raise AssertionError("Reconstructed recorded action is illegal")
        if not np.array_equal(np.rint(target.candidates[..., 16] * 192), np.rint(reference_candidates[..., 16] * 192)):
            raise AssertionError("Categorical card identity changed")
        obs_error = max(obs_error, float(np.max(np.abs(target.obs - reference_obs))))
        candidate_error = max(candidate_error, float(np.max(np.abs(target.candidates - reference_candidates))))
        if layout != "dense_fp16" and not np.array_equal(target.obs, reference_obs):
            raise AssertionError("Lossless observation layout changed a value")
        if not np.allclose(target.obs, reference_obs, rtol=5e-4, atol=1e-7):
            raise AssertionError("Observation quantization exceeds FP16 tolerance")
        if not np.allclose(target.candidates, reference_candidates, rtol=5e-4, atol=1e-7):
            raise AssertionError("Candidate quantization exceeds FP16 tolerance")
    return {"all_stored_rows_checked": obs.shape[0], "take_mode_checked": take_mode, "observation_max_abs_error": obs_error,
            "candidate_max_abs_error": candidate_error, "mask_action_slots_and_card_ids_exact": True,
            "finite_reconstruction": True, "immutable_storage": all(not array.flags.writeable for array in store.values())}


@torch.inference_mode()
def policy_checks(model, obs, candidates, mask, actions, decoded):
    source = tuple(torch.from_numpy(np.ascontiguousarray(value)) for value in (obs, candidates, mask))
    target = (torch.from_numpy(decoded.obs), torch.from_numpy(decoded.candidates), torch.from_numpy(decoded.mask).bool())
    reference_logits, reference_values = model(*source)
    logits, values = model(*target)
    chosen = torch.from_numpy(actions.astype(np.int64))
    reference_logp = reference_logits.log_softmax(-1).gather(1, chosen[:, None])
    logp = logits.log_softmax(-1).gather(1, chosen[:, None])
    result = {
        "policy_checked_rows": obs.shape[0],
        "policy_max_legal_logit_abs_error": (logits[source[2]] - reference_logits[source[2]]).abs().max().item(),
        "policy_max_probability_abs_error": (logits.softmax(-1) - reference_logits.softmax(-1)).abs().max().item(),
        "policy_max_value_abs_error": (values - reference_values).abs().max().item(),
        "recorded_action_logp_max_abs_error": (logp - reference_logp).abs().max().item(),
        "greedy_action_matches": int((logits.argmax(-1) == reference_logits.argmax(-1)).sum().item()),
        "greedy_action_agreement": (logits.argmax(-1) == reference_logits.argmax(-1)).float().mean().item(),
    }
    for values_to_check in (logits, values, logp):
        if not bool(torch.isfinite(values_to_check).all()):
            raise AssertionError("Nonfinite policy output after decode")
    if result["policy_max_probability_abs_error"] > .003 or result["policy_max_value_abs_error"] > .01:
        raise AssertionError(f"Decoded policy parity failed: {result}")
    packet = sample_actions(logits, values)
    if not bool(target[2].gather(1, packet[:, :1].long()).all()):
        raise AssertionError("Decoded policy sampled an illegal action")
    return result


class Inference:
    def __init__(self, host, model, device):
        self.host, self.device = host, device
        self.model = model.to(device).eval()
        self.inputs = torch.empty_like(host.tensor, device=device)
        batch = host.batch
        self.obs = self.inputs[:batch * 2048].view(batch, 2048)
        self.candidates = self.inputs[batch * 2048:batch * 4096].view(batch, 64, 32)
        self.mask = self.inputs[batch * 4096:].view(batch, 64)
        self.output = torch.empty(batch, 3, pin_memory=device == "cuda")
        self.inputs.copy_(host.tensor)
        self.graph_owner = None
        with torch.inference_mode():
            if device == "cuda":
                self.forward, self.graph_owner = capture(self.compute)
            else:
                self.forward = self.compute

    def compute(self):
        return sample_actions(*self.model(self.obs, self.candidates, self.mask.bool()))

    @torch.inference_mode()
    def step(self):
        self.inputs.copy_(self.host.tensor, non_blocking=self.device == "cuda")
        self.output.copy_(self.forward(), non_blocking=self.device == "cuda")
        if self.device == "cuda":
            torch.cuda.synchronize()


    @torch.inference_mode()
    def validate(self):
        """Check the actual staged/captured packet against fresh eager inputs."""
        self.step()
        if not bool(torch.isfinite(self.output).all()):
            raise AssertionError("Nonfinite staged inference packet")
        packet_actions = self.output[:, 0]
        if not torch.equal(packet_actions, packet_actions.round()) or not bool(((packet_actions >= 0) & (packet_actions < 64)).all()):
            raise AssertionError("Staged inference returned a noninteger or out-of-range action")
        actions = packet_actions.long()
        decoded_mask = torch.from_numpy(self.host.mask).bool()
        if not bool(decoded_mask.gather(1, actions[:, None]).all()):
            raise AssertionError("Staged inference selected an illegal decoded action")
        # Use a separate upload from the current decoded batch for the eager
        # reference, rather than accidentally validating stale device inputs.
        fresh = self.host.tensor.to(self.device)
        batch = self.host.batch
        reference_obs = fresh[:batch * 2048].view(batch, 2048)
        reference_candidates = fresh[batch * 2048:batch * 4096].view(batch, 64, 32)
        reference_mask = fresh[batch * 4096:].view(batch, 64).bool()
        logits, values = self.model(reference_obs, reference_candidates, reference_mask)
        expected_logp = logits.log_softmax(-1).gather(1, actions.to(self.device)[:, None]).squeeze(1).cpu()
        expected_values = values.cpu()
        torch.testing.assert_close(self.output[:, 1], expected_logp, rtol=1e-5, atol=1e-5)
        torch.testing.assert_close(self.output[:, 2], expected_values, rtol=1e-5, atol=1e-5)
        return {
            "finite_packet": True, "selected_action_integer_in_range_and_legal": True,
            "selected_logp_eager_max_abs_error": (self.output[:, 1] - expected_logp).abs().max().item(),
            "value_eager_max_abs_error": (self.output[:, 2] - expected_values).abs().max().item(),
            "fresh_decoded_batch_eager_reference": True,
        }


def run_case(config):
    before=gpu_status()
    torch.set_num_threads(config["threads"])
    torch.set_num_interop_threads(1)
    torch.manual_seed(config["seed"])
    torch.backends.fp32_precision = "ieee"
    # Deliberately FP32 for the policy-equivalence check and service measurement.
    fixture_config = dict(fixture_path=config["fixture_path"], fixture_sha256=config["fixture_sha256"],
                          obs_dim=2048, actions=64, action_dim=32, batch=config["rows"])
    host, action_tensor, fixture_metadata = load_recorded_fixture(fixture_config)
    obs, candidates, mask = (tensor.numpy() for tensor in host)
    actions = action_tensor.numpy() if action_tensor is not None else mask.argmax(1).astype(np.int64)
    packing_latencies = []
    store = None
    for _ in range(config["repeats"]):
        store = None
        started = time.perf_counter()
        store = build_store(config["layout"], obs, candidates, mask, actions)
        packing_latencies.append(time.perf_counter() - started)
    array_bytes = sum(array.nbytes for array in store.values())
    checks = source_checks(config["layout"], store, obs, candidates, mask, actions, take_mode=config.get("take_mode", "clip"))
    model = CandidatePolicy(width=config["width"], scorer="embedded").eval().requires_grad_(False)
    # Check every distinct corpus row used by this store, in manageable chunks.
    policy_chunks = []
    for start in range(0, min(config["rows"], fixture_metadata["corpus_rows"]), 256):
        indices = np.arange(start, min(start + 256, config["rows"], fixture_metadata["corpus_rows"]))
        reconstructed = BatchBuffer(indices.size, take_mode=config.get("take_mode", "clip"))
        decode(config["layout"], store, indices, reconstructed)
        policy_chunks.append(policy_checks(model, obs[indices], candidates[indices], mask[indices], actions[indices], reconstructed))
    checks["policy_checked_unique_rows"] = sum(chunk["policy_checked_rows"] for chunk in policy_chunks)
    checks["greedy_action_matches"] = sum(chunk["greedy_action_matches"] for chunk in policy_chunks)
    checks["greedy_action_agreement"] = checks["greedy_action_matches"] / checks["policy_checked_unique_rows"]
    for key in policy_chunks[0]:
        if key not in ("policy_checked_rows", "greedy_action_matches", "greedy_action_agreement"):
            checks[key] = max(chunk[key] for chunk in policy_chunks)
    rng = np.random.default_rng(config["seed"])
    batch_results = []
    for batch in config["batches"]:
        destination = BatchBuffer(batch, pinned=config["device"] == "cuda",take_mode=config.get("take_mode","clip"))
        index_batches = [rng.integers(0, config["rows"], size=batch) for _ in range(16)]
        index_cursor = 0

        def gather():
            nonlocal index_cursor
            indices = index_batches[index_cursor % len(index_batches)]
            index_cursor += 1
            decode(config["layout"], store, indices, destination)

        gather()
        timings = {"batch": batch, "gather_decode": measure(gather, config["seconds"], config["repeats"], batch)}
        if config["device"] != "none":
            service = Inference(destination, model, config["device"])
            # Change the source after capture, proving that replay/transfer
            # consumes a fresh decoded batch rather than the warmup fixture.
            gather()
            timings["inference_correctness"] = service.validate()

            def actor_service():
                gather()
                service.step()

            timings["gather_decode_and_actor"] = measure(actor_service, config["seconds"], config["repeats"], batch)
            timings["inference_scope"] = "gather/decode + upload + captured FP32 model/mask/sample/logp + output download" if config["device"] == "cuda" else "gather/decode + CPU input copy + FP32 model/mask/sample/logp + output copy"
            del service
        batch_results.append(timings)
    bytes_per_row = array_bytes / config["rows"]
    return {
        "status": "ok", "config": config, "fixture": fixture_metadata, "correctness": checks,
        "gpu_before":before,"gpu_after":gpu_status(),
        "storage_array_bytes": array_bytes, "bytes_per_stored_decision": bytes_per_row,
        "storage_array_breakdown": {name: array.nbytes for name, array in store.items()},
        "bookkeeping_bytes_per_row": METADATA.itemsize,
        "bookkeeping_scope": "Recorded action plus fabricated seat/done/policy/episode/decision/logp/value/reward/return/advantage fields; no trained rollout",
        "packing_ms_repeats": [elapsed * 1000 for elapsed in packing_latencies],
        "packing_rows_per_second_median": statistics.median(config["rows"] / elapsed for elapsed in packing_latencies),
        "memory_estimates": {str(rows): {"rows": rows, "estimated_array_bytes": round(bytes_per_row * rows),
                                       "estimated_GiB": bytes_per_row * rows / 2**30} for rows in (131072, 1000000)},
        "estimate_scope": "Linear extrapolation of this corpus occupancy; excludes allocator/objects/source arrays/staging and does not allocate estimated row counts",
        "process_peak_rss_bytes": resource.getrusage(resource.RUSAGE_SELF).ru_maxrss * 1024,
        "batches": batch_results,
    }


def stop_child_group(child):
    """Own-session cleanup, including descendants, tolerant of an exit race."""
    try:
        os.killpg(child.pid, signal.SIGKILL)
    except ProcessLookupError:
        pass
    # Reap the owned child and drain closed pipes after the entire group stops.
    try:
        child.communicate(timeout=3)
    except subprocess.TimeoutExpired:
        # A descendant that unexpectedly detached could retain a pipe even
        # after the owned process group exited; never hang the coordinator.
        if child.poll() is None:
            child.kill()
        child.wait(timeout=3)
        for pipe in (child.stdout, child.stderr):
            if pipe is not None:
                pipe.close()


def exit_on_termination(signum, _frame):
    # Converting SIGTERM into an exception lets the parent's finally/except
    # cleanup reap the active child's process group before this process exits.
    raise SystemExit(128 + signum)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--fixture", type=Path, default=Path(__file__).with_name("results") / "real-states.npz")
    parser.add_argument("--layouts", default=",".join(LAYOUTS))
    parser.add_argument("--rows", type=int, default=8192, help="Actual allocated rows; estimates use larger counts without allocating them")
    parser.add_argument("--batches", default="256,2048,8192")
    parser.add_argument("--device", choices=("none", "cpu", "cuda"), default="none")
    parser.add_argument("--take-mode",choices=("raise","clip"),default="clip")
    parser.add_argument("--width", type=int, default=256)
    parser.add_argument("--threads", type=int, default=1)
    parser.add_argument("--seconds", type=float, default=.1)
    parser.add_argument("--repeats", type=int, default=3)
    parser.add_argument("--seed", type=int, default=20260925)
    parser.add_argument("--case-timeout", type=float, default=45)
    parser.add_argument("--max-total-seconds", type=float, default=120)
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
    batches = [int(value) for value in args.batches.split(",")]
    layouts = args.layouts.split(",")
    if not 1 <= args.rows <= 32768 or not batches or min(batches) < 1 or max(batches) > 8192:
        parser.error("Bounds: rows1..32768, minibatches1..8192")
    if not 0 < args.seconds <= 1 or not 1 <= args.repeats <= 5 or not 0 < args.max_total_seconds <= 180 or not 0 < args.case_timeout <= 60:
        parser.error("Bounds: seconds/repeat<=1, repeats<=5, total<=180s, case-timeout<=60s")
    if any(layout not in LAYOUTS for layout in layouts) or not 1 <= args.threads <= 8 or args.width not in (128, 256, 512):
        parser.error("Invalid layout, thread count or model width")
    if not args.fixture.is_file():
        parser.error(f"Fixture missing: {args.fixture}")
    fixture_hash = hashlib.sha256(args.fixture.read_bytes()).hexdigest()
    manifest = {
        "schema": "shards-rollout-storage-preflight-v1", "training_campaign_started": False,
        "scope": "Immutable replay storage/gather/optional frozen inference; no learner or strength claim",
        "torch": torch.__version__, "numpy": np.__version__, "python": platform.python_version(),
        "platform": platform.platform(), "command": sys.argv,
        "source_sha256": {name: hashlib.sha256(Path(__file__).with_name(name).read_bytes()).hexdigest()
                          for name in ("rollout_bench.py", "gpu_bench.py", "model.py")}, "results": [],
    }
    started = time.perf_counter()
    for layout in layouts:
        remaining = args.max_total_seconds - (time.perf_counter() - started)
        if remaining <= 1:
            manifest["stopped_at_time_limit"] = True
            break
        config = dict(layout=layout, rows=args.rows, batches=batches, device=args.device, width=args.width,
                      take_mode=args.take_mode,
                      threads=args.threads, seconds=args.seconds, repeats=args.repeats, seed=args.seed,
                      fixture_path=str(args.fixture.resolve()), fixture_sha256=fixture_hash)
        print(f"Replay storage case: {layout}, device={args.device}", file=sys.stderr, flush=True)
        child = None
        try:
            child = subprocess.Popen([sys.executable, str(Path(__file__).resolve()), "--single-case", json.dumps(config)],
                                     stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True, start_new_session=True)
            output, errors = child.communicate(timeout=min(args.case_timeout, remaining))
            result = json.loads(output.strip().splitlines()[-1])
            if errors:
                result["stderr_tail"] = errors[-4000:]
        except subprocess.TimeoutExpired:
            stop_child_group(child)
            result = {"status": "timeout", "config": config}
        except Exception as error:
            result = {"status": "error", "config": config, "error": str(error)}
        except BaseException:
            if child is not None:
                stop_child_group(child)
            raise
        finally:
            if child is not None and child.poll() is None:
                stop_child_group(child)
        manifest["results"].append(result)
        manifest["elapsed_seconds"] = time.perf_counter() - started
        if args.output:
            args.output.parent.mkdir(parents=True, exist_ok=True)
            temporary = args.output.with_suffix(args.output.suffix + ".tmp")
            temporary.write_text(json.dumps(manifest, indent=2, allow_nan=False) + "\n")
            temporary.replace(args.output)
        print("  " + result["status"] + (f", {result['bytes_per_stored_decision']:.1f} bytes/decision" if result["status"] == "ok" else " " + result.get("error", "")), file=sys.stderr, flush=True)
    if not args.output:
        print(json.dumps(manifest, indent=2, allow_nan=False))
    if any(result["status"] != "ok" for result in manifest["results"]):
        raise SystemExit(1)


if __name__ == "__main__":
    main()
