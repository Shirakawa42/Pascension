#!/usr/bin/env python3
"""Bounded exact sparse-first-layer CPU policy experiment on recorded states.

Dense Linear(x, W, b) is replaced by a weighted sum of rows of contiguous W.T
for x's nonzero feature indices. All downstream policy operations are unchanged.
The complete sparse path includes extraction/packing; a separate prepacked path
is an upper bound if an encoder eventually emits sparse features directly.
Optional --ragged-legal also scores only legal candidate descriptors while
retaining/scattering their original slots in the full 64-action output.

This is frozen inference, not learning or a strength test. It does not prove a
C# actor would achieve these Python/PyTorch rates. CPU threads also compete with
simulation. No GPU is initialized. Work is bounded to at most 60 seconds.

Operation reference:
https://docs.pytorch.org/docs/2.14/generated/torch.nn.functional.embedding_bag.html
"""

from __future__ import annotations

import argparse
import copy
import hashlib
import itertools
import json
from pathlib import Path
import platform
import statistics
import sys
import time

SCRIPT_STARTED = time.perf_counter()

import torch
from torch import nn
from torch.nn import functional as F

from gpu_bench import load_recorded_fixture, percentile
from model import CandidatePolicy, sample_actions


def pack_observations(obs):
    """Return row-major feature IDs, B+1 offsets and original FP32 values.

    No thresholding or quantization: every nonzero feature, including negative
    and fractional values, survives. Empty rows become empty bags plus bias.
    """
    rows, features = obs.nonzero(as_tuple=True)
    features = features.contiguous()
    values = obs[rows, features].contiguous()
    counts = torch.bincount(rows, minlength=obs.shape[0])
    offsets = torch.zeros(obs.shape[0] + 1, dtype=torch.int64)
    torch.cumsum(counts, dim=0, out=offsets[1:])
    return features, offsets, values


def pack_legal_candidates(candidates, mask):
    """Every legal descriptor plus its original row/slot; no truncation."""
    rows, slots = mask.nonzero(as_tuple=True)
    return rows.contiguous(), slots.contiguous(), candidates[rows, slots].contiguous()


class SparseFirstLayerPolicy(nn.Module):
    """Frozen equivalent of CandidatePolicy with a weighted embedding bag."""

    def __init__(self, dense):
        super().__init__()
        self.policy = copy.deepcopy(dense).eval().requires_grad_(False)
        # A feature row must be contiguous for efficient weighted accumulation.
        self.register_buffer("first_weight", self.policy.trunk[0].weight.detach().t().contiguous())
        self.register_buffer("first_bias", self.policy.trunk[0].bias.detach().clone())
        self.remaining_trunk = self.policy.trunk[1:]

    def first_layer(self, packed):
        features, offsets, values = packed
        return F.embedding_bag(
            features, self.first_weight, offsets,
            mode="sum", per_sample_weights=values,
            include_last_offset=True,
        ) + self.first_bias

    def forward(self, packed, candidates, mask):
        state = self.remaining_trunk(self.first_layer(packed))
        # Mirror the current shared candidate head exactly; parity checks below
        # fail if this experiment diverges from the source policy in the future.
        query = self.policy.query(state)
        keys = self.policy.candidate(candidates)
        if self.policy.card_embedding is not None:
            identity = (candidates[..., 16] * 192).round().long().clamp(0, 192)
            keys = keys + self.policy.card_embedding(identity)
        scores = torch.bmm(keys, query.unsqueeze(-1)).squeeze(-1) * self.policy.scale
        scores = scores + self.policy.candidate_bias(candidates).squeeze(-1)
        logits = scores.float().masked_fill(~mask, -1.0e9)
        return logits, self.policy.value(state).squeeze(-1).float()


    def forward_ragged(self, packed, legal_candidates, batch, actions):
        state = self.remaining_trunk(self.first_layer(packed))
        rows, slots, descriptors = legal_candidates
        query = self.policy.query(state)
        keys = self.policy.candidate(descriptors)
        if self.policy.card_embedding is not None:
            identity = (descriptors[:, 16] * 192).round().long().clamp(0, 192)
            keys = keys + self.policy.card_embedding(identity)
        scores = (keys * query[rows]).sum(dim=-1) * self.policy.scale
        scores = scores + self.policy.candidate_bias(descriptors).squeeze(-1)
        logits = torch.full((batch, actions), -1.0e9, dtype=torch.float32)
        logits[rows, slots] = scores.float()
        return logits, self.policy.value(state).squeeze(-1).float()


@torch.inference_mode()
def verify(dense, sparse, obs, candidates, mask, packed):
    first_reference = dense.trunk[0](obs)
    first_result = sparse.first_layer(packed)
    logits_reference, values_reference = dense(obs, candidates, mask)
    logits_result, values_result = sparse(packed, candidates, mask)
    errors = {
        "first_layer_max_abs_error": (first_reference - first_result).abs().max().item(),
        "legal_logit_max_abs_error": (logits_reference[mask] - logits_result[mask]).abs().max().item(),
        "probability_max_abs_error": (logits_reference.softmax(-1) - logits_result.softmax(-1)).abs().max().item(),
        "value_max_abs_error": (values_reference - values_result).abs().max().item(),
    }
    torch.testing.assert_close(first_result, first_reference, rtol=2e-5, atol=2e-5)
    torch.testing.assert_close(logits_result[mask], logits_reference[mask], rtol=2e-5, atol=2e-5)
    torch.testing.assert_close(values_result, values_reference, rtol=2e-5, atol=2e-5)
    torch.testing.assert_close(logits_result.softmax(-1), logits_reference.softmax(-1), rtol=2e-5, atol=2e-5)
    for logits, values in ((logits_reference, values_reference), (logits_result, values_result)):
        if not bool(torch.isfinite(logits).all() and torch.isfinite(values).all()):
            raise AssertionError("Nonfinite policy output")
        for _ in range(3):
            packet = sample_actions(logits, values)
            if not bool(torch.isfinite(packet).all()):
                raise AssertionError("Nonfinite sampled packet")
            if not bool(mask.gather(1, packet[:, :1].long()).all()):
                raise AssertionError("Sampled an illegal action")
    # This boundary fixture tests an empty bag and weighted signed features.
    boundary = torch.zeros(3, obs.shape[1])
    boundary[1, 5], boundary[1, 7], boundary[2, -1] = -0.5, 1.25, -2.0
    torch.testing.assert_close(
        sparse.first_layer(pack_observations(boundary)), dense.trunk[0](boundary),
        rtol=2e-5, atol=2e-5,
    )
    # The sparse transformation touches only observations, never candidate IDs.
    errors.update(finite_outputs=True, sampled_actions_legal=True,
                  empty_and_signed_rows_equivalent=True, candidate_ids_unchanged=True)
    return errors


@torch.inference_mode()
def verify_ragged(dense, sparse, obs, candidates, mask, packed, legal_candidates):
    rows, slots, descriptors = legal_candidates
    reconstructed_mask = torch.zeros_like(mask)
    reconstructed_mask[rows, slots] = True
    if not torch.equal(reconstructed_mask, mask) or rows.numel() != int(mask.sum()):
        raise AssertionError("Ragged packing dropped, duplicated or renumbered a legal slot")
    if not torch.equal(descriptors, candidates[rows, slots]):
        raise AssertionError("Legal descriptor/card identity changed during packing")
    reference_logits, reference_values = dense(obs, candidates, mask)
    logits, values = sparse.forward_ragged(packed, legal_candidates, *mask.shape)
    torch.testing.assert_close(logits, reference_logits, rtol=2e-5, atol=2e-5)
    torch.testing.assert_close(values, reference_values, rtol=2e-5, atol=2e-5)
    torch.testing.assert_close(logits.softmax(-1), reference_logits.softmax(-1), rtol=2e-5, atol=2e-5)
    packet = sample_actions(logits, values)
    if not bool(torch.isfinite(packet).all()) or not bool(mask.gather(1, packet[:, :1].long()).all()):
        raise AssertionError("Ragged actor produced a nonfinite or illegal packet")
    # A dedicated holey mask guards against accidentally treating legal actions
    # as a compact prefix or returning packed positions instead of real slots.
    count = min(3, obs.shape[0])
    holey_mask = torch.zeros_like(mask[:count])
    holey_mask[:, (0, 5, 63)] = True
    holey_candidates = candidates[:count]
    expected, expected_values = dense(obs[:count], holey_candidates, holey_mask)
    actual, actual_values = sparse.forward_ragged(
        pack_observations(obs[:count]), pack_legal_candidates(holey_candidates, holey_mask), count, 64,
    )
    torch.testing.assert_close(actual, expected, rtol=2e-5, atol=2e-5)
    torch.testing.assert_close(actual_values, expected_values, rtol=2e-5, atol=2e-5)
    return {
        "all_original_legal_slots_and_descriptors_preserved": True,
        "nonprefix_slots_0_5_63_parity": True,
        "legal_logit_max_abs_error": (logits[mask] - reference_logits[mask]).abs().max().item(),
        "probability_max_abs_error": (logits.softmax(-1) - reference_logits.softmax(-1)).abs().max().item(),
        "value_max_abs_error": (values - reference_values).abs().max().item(),
        "greedy_action_agreement": (logits.argmax(-1) == reference_logits.argmax(-1)).float().mean().item(),
        "sampled_actions_legal_and_finite": True,
    }


def time_repeat(step, seconds, deadline):
    latencies = []
    start = time.perf_counter()
    stop = min(start + seconds, deadline)
    while time.perf_counter() < stop:
        tick = time.perf_counter()
        step()
        latencies.append((time.perf_counter() - tick) * 1e3)
    elapsed = time.perf_counter() - start
    if not latencies:
        raise TimeoutError("Benchmark deadline reached")
    return latencies, elapsed


def summarize(repeats, batch):
    latencies = [sample for values, _ in repeats for sample in values]
    rates = [batch * len(values) / elapsed for values, elapsed in repeats]
    return {
        "decisions_per_second_median": statistics.median(rates),
        "decisions_per_second_repeats": rates,
        "batch_latency_ms_median": statistics.median(latencies),
        "batch_latency_ms_p95": percentile(latencies, .95),
        "batch_latency_ms_p99": percentile(latencies, .99),
        "iterations_per_repeat": [len(values) for values, _ in repeats],
        "elapsed_seconds_per_repeat": [elapsed for _, elapsed in repeats],
    }


@torch.inference_mode()
def benchmark_case(config, deadline):
    torch.set_num_threads(config["threads"])
    torch.manual_seed(config["seed"] + config["width"])
    (obs, candidates, mask), _, fixture_metadata = load_recorded_fixture({
        "fixture_path": config["fixture_path"], "fixture_sha256": config["fixture_sha256"],
        "obs_dim": 2048, "actions": 64, "action_dim": 32, "batch": config["batch"],
    })
    dense = CandidatePolicy(width=config["width"], scorer="embedded").eval().requires_grad_(False)
    sparse = SparseFirstLayerPolicy(dense)
    packed = pack_observations(obs)
    checks = verify(dense, sparse, obs, candidates, mask, packed)

    def dense_step():
        return sample_actions(*dense(obs, candidates, mask))

    def sparse_step():
        return sample_actions(*sparse(pack_observations(obs), candidates, mask))

    def prepacked_step():
        return sample_actions(*sparse(packed, candidates, mask))

    def packing_step():
        return pack_observations(obs)

    steps = {"dense": dense_step, "sparse_including_packing": sparse_step,
             "sparse_prepacked_ceiling": prepacked_step, "packing_only": packing_step}
    ragged_checks = None
    if config.get("ragged_legal"):
        legal_candidates = pack_legal_candidates(candidates, mask)
        ragged_checks = verify_ragged(dense, sparse, obs, candidates, mask, packed, legal_candidates)

        def ragged_step():
            return sample_actions(*sparse.forward_ragged(
                pack_observations(obs), pack_legal_candidates(candidates, mask), *mask.shape,
            ))

        def ragged_prepacked_step():
            return sample_actions(*sparse.forward_ragged(packed, legal_candidates, *mask.shape))

        def ragged_packing_step():
            return pack_observations(obs), pack_legal_candidates(candidates, mask)

        steps.update(sparse_ragged_including_packing=ragged_step,
                     sparse_ragged_prepacked_ceiling=ragged_prepacked_step,
                     observation_and_legal_packing_only=ragged_packing_step)
    for step in steps.values():
        for _ in range(4):
            step()
    samples = {name: [] for name in steps}
    names = list(steps)
    # Rotate the comparison order between repeats to reduce ordering bias.
    for repeat in range(config["repeats"]):
        order = names[repeat % len(names):] + names[:repeat % len(names)]
        for name in order:
            samples[name].append(time_repeat(steps[name], config["seconds"], deadline))
    results = {name: summarize(values, config["batch"]) for name, values in samples.items()}
    dense_rate = results["dense"]["decisions_per_second_median"]
    result = {
        "status": "ok", "config": config, "fixture": fixture_metadata,
        "parameters": sum(parameter.numel() for parameter in dense.parameters()),
        "nonzero_observation_features": packed[0].numel(),
        "nonzero_features_per_row_mean": packed[0].numel() / obs.shape[0],
        "observation_density": packed[0].numel() / obs.numel(),
        "packed_bytes": sum(tensor.numel() * tensor.element_size() for tensor in packed),
        "dense_observation_bytes": obs.numel() * obs.element_size(),
        "correctness": checks, "measurements": results,
        "sparse_with_packing_speedup": results["sparse_including_packing"]["decisions_per_second_median"] / dense_rate,
        "prepacked_ceiling_speedup": results["sparse_prepacked_ceiling"]["decisions_per_second_median"] / dense_rate,
    }
    if ragged_checks is not None:
        result["ragged_correctness"] = ragged_checks
        result["legal_candidates_per_row_mean"] = legal_candidates[0].numel() / obs.shape[0]
        result["ragged_with_packing_speedup"] = results["sparse_ragged_including_packing"]["decisions_per_second_median"] / dense_rate
        result["ragged_prepacked_ceiling_speedup"] = results["sparse_ragged_prepacked_ceiling"]["decisions_per_second_median"] / dense_rate
    return result


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--fixture", type=Path, default=Path(__file__).with_name("results") / "real-states.npz")
    parser.add_argument("--batches", default="32,128,256")
    parser.add_argument("--widths", default="128,256")
    parser.add_argument("--threads", default="1,4")
    parser.add_argument("--ragged-legal", action="store_true", help="Also pack and score every legal candidate at its original slot")
    parser.add_argument("--seconds", type=float, default=.1, help="Seconds per path and repeat")
    parser.add_argument("--repeats", type=int, default=3)
    parser.add_argument("--max-total-seconds", type=float, default=60)
    parser.add_argument("--seed", type=int, default=20260925)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    if not 0 < args.max_total_seconds <= 60 or not 0 < args.seconds <= 2 or not 1 <= args.repeats <= 10:
        parser.error("Bounds: total <=60 seconds, seconds/repeat <=2, repeats <=10; all positive")
    batches, widths, threads = ([int(value) for value in text.split(",")] for text in (args.batches, args.widths, args.threads))
    if not batches or not widths or not threads or min(batches + widths + threads) < 1:
        parser.error("At least one positive batch, width and thread count is required")
    if max(batches) > 2048 or max(widths) > 512 or max(threads) > 8:
        parser.error("Bounds: batch <=2048, width <=512, threads <=8")
    if not args.fixture.is_file():
        parser.error(f"Fixture not found: {args.fixture}")
    torch.set_num_threads(1)
    torch.set_num_interop_threads(1)
    torch.backends.fp32_precision = "ieee"
    fixture_hash = hashlib.sha256(args.fixture.read_bytes()).hexdigest()
    deadline = SCRIPT_STARTED + args.max_total_seconds
    manifest = {
        "schema": "shards-exact-sparse-cpu-preflight-v1", "training_campaign_started": False,
        "scope": "Frozen real-observation policy systems experiment; no strength/C# throughput claim",
        "torch": torch.__version__, "python": platform.python_version(), "platform": platform.platform(),
        "command": sys.argv, "source_sha256": {
            name: hashlib.sha256(Path(__file__).with_name(name).read_bytes()).hexdigest()
            for name in ("sparse_cpu_bench.py", "model.py", "gpu_bench.py")
        },
        "fixture_path": str(args.fixture.resolve()), "fixture_sha256": fixture_hash,
        "thread_caveat": "Intra-op threads compete with the C# simulation workers",
        "paths": "All actor paths include candidate scoring/masking/Gumbel sampling/logprob/value; no IPC included",
        "results": [],
    }
    for thread_count, width, batch in itertools.product(threads, widths, batches):
        if time.perf_counter() >= deadline:
            manifest["stopped_at_time_limit"] = True
            break
        config = dict(threads=thread_count, width=width, batch=batch, seconds=args.seconds,
                      repeats=args.repeats, seed=args.seed, fixture_path=str(args.fixture.resolve()), fixture_sha256=fixture_hash,
                      ragged_legal=args.ragged_legal)
        print(f"CPU sparse case: threads={thread_count} width={width} batch={batch}", file=sys.stderr, flush=True)
        try:
            result = benchmark_case(config, deadline)
        except TimeoutError:
            manifest["stopped_at_time_limit"] = True
            break
        except Exception as error:
            result = {"status": "error", "config": config, "error": str(error)}
        manifest["results"].append(result)
        manifest["elapsed_seconds_including_imports"] = time.perf_counter() - SCRIPT_STARTED
        args.output.parent.mkdir(parents=True, exist_ok=True)
        temporary = args.output.with_suffix(args.output.suffix + ".tmp")
        temporary.write_text(json.dumps(manifest, indent=2, allow_nan=False) + "\n")
        temporary.replace(args.output)
        if result["status"] == "ok":
            print(f"  packing-inclusive speedup={result['sparse_with_packing_speedup']:.3f}; prepacked={result['prepacked_ceiling_speedup']:.3f}", file=sys.stderr, flush=True)
            if args.ragged_legal:
                print(f"  ragged packing-inclusive={result['ragged_with_packing_speedup']:.3f}; ragged prepacked={result['ragged_prepacked_ceiling_speedup']:.3f}", file=sys.stderr, flush=True)
        else:
            print("  " + result["error"], file=sys.stderr, flush=True)
    manifest["elapsed_seconds_including_imports"] = time.perf_counter() - SCRIPT_STARTED
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(manifest, indent=2, allow_nan=False) + "\n")
    if any(result["status"] != "ok" for result in manifest["results"]):
        raise SystemExit(1)


if __name__ == "__main__":
    main()
