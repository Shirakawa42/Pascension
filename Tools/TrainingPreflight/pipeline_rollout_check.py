"""Bounded correctness checks for the pipeline's optional owned CUDA ring.

No game campaign or strength measurement runs here. Real-state fixture batches
exercise captured append, non-divisible wraparound and immutable ownership.
--with-learner adds a few disposable optimizer updates with fabricated targets.
Run only inside the parent benchmark coordinator's allocated timing window.
"""
from __future__ import annotations

import argparse
import hashlib
from pathlib import Path
import sys

import numpy as np
import torch

from bench_common import metadata, save_json
from gpu_bench import load_recorded_fixture
from pipeline_bench import Actor, LearnerLoad
from soak_bench import learner_final_finite_checks


class FixtureHost:
    def __init__(self, batch, corpus):
        self.batch, self.pinned, self.corpus = batch, True, corpus
        self.upload = torch.empty(batch * (2048 + 64 * 32 + 64), pin_memory=True)
        self.obs = self.upload[:batch * 2048].view(batch, 2048)
        self.candidates = self.upload[batch * 2048:batch * (2048 + 64 * 32)].view(batch, 64, 32)
        self.mask = self.upload[batch * (2048 + 64 * 32):].view(batch, 64)
        self.set_rows(0)

    def set_rows(self, offset):
        indices = (torch.arange(self.batch) + offset) % len(self.corpus[0])
        for target, source in zip((self.obs, self.candidates, self.mask), self.corpus):
            target.copy_(source[indices])


def assert_ring(ring, expected, total):
    count = min(total, ring.rows)
    actual_counters = (ring.cursor.item(), ring.count.item(), ring.total.item(), ring.completed_rows)
    if actual_counters != (total % ring.rows, count, total, total):
        raise AssertionError(f"Ring counters mismatch: {actual_counters}")
    for name in ("obs", "candidates", "mask", "packet"):
        np.testing.assert_array_equal(getattr(ring, name)[:count].cpu().numpy(), expected[name][:count])


def run_case(mode, corpus, *, batch, capacity, steps, with_learner):
    torch.manual_seed(1901)
    host = FixtureHost(batch, corpus)
    actor = Actor(host, "cuda", 128, "embedded", "fp32", mode, rollout_rows=capacity)
    ring = actor.rollout
    if (ring.cursor.item(), ring.count.item(), ring.total.item(), ring.completed_rows) != (0, 0, 0, 0):
        raise AssertionError("Capture/warmup did not reset all counters")
    expected = {name: np.empty(tuple(getattr(ring, name).shape), np.float32)
                for name in ("obs", "candidates", "mask", "packet")}
    learner = None
    private_snapshot = None
    changed_batches = 0
    previous_inputs = None
    logp_error = value_error = 0.
    sampled_stored_rows = set()
    learner_step_checks = 0
    fixed_returns = None
    for iteration in range(steps):
        host.set_rows(iteration * batch)
        current_inputs = host.upload.clone()
        if previous_inputs is not None:
            changed_batches += int(not torch.equal(previous_inputs, current_inputs))
        previous_inputs = current_inputs
        action, _ = actor.act()
        if private_snapshot is not None:
            for name, reference in private_snapshot.items():
                if not torch.equal(getattr(learner, name), reference):
                    raise AssertionError("A later actor append mutated private learner storage")
        packet = actor.output_host.numpy().copy()
        if not np.isfinite(packet).all() or not np.array_equal(packet[:, 0], action):
            raise AssertionError("Invalid sampled actor packet")
        if not bool(host.mask.gather(1, torch.from_numpy(action).long()[:, None]).bool().all()):
            raise AssertionError("Actor sampled an illegal original slot")
        with torch.inference_mode():
            logits, values = actor.model(actor.obs, actor.candidates, actor.mask.bool())
            logits = logits * .02 + (actor.candidates[:, :, :16] @ actor.bias)
            selected = torch.from_numpy(action).to("cuda").long()
            logp = logits.log_softmax(-1).gather(1, selected[:, None]).squeeze(1)
            observed = actor.output_host.to("cuda")
            torch.testing.assert_close(observed[:, 1], logp, rtol=2e-5, atol=2e-5)
            torch.testing.assert_close(observed[:, 2], values, rtol=2e-5, atol=2e-5)
            logp_error = max(logp_error, (observed[:, 1] - logp).abs().max().item())
            value_error = max(value_error, (observed[:, 2] - values).abs().max().item())
        positions = (np.arange(batch) + iteration * batch) % capacity
        for name in ("obs", "candidates", "mask"):
            expected[name][positions] = getattr(host, name).numpy()
        expected["packet"][positions] = packet
        assert_ring(ring, expected, (iteration + 1) * batch)
        # Reuse the entire actor input allocation. An owned ring must retain
        # its values even when the producer destroys its staging buffer.
        with torch.inference_mode():
            actor.input.fill_(float("nan"))
            assert_ring(ring, expected, (iteration + 1) * batch)
            actor.input.copy_(host.upload)
        if with_learner:
            if learner is None:
                # Larger than the first valid row count: replacement sampling
                # must work without reading uninitialized ring capacity.
                learner = LearnerLoad(actor, batch + 1, mode)
                fixed_returns = learner.advantages.clone()
                original_step = learner.step

                def checked_step():
                    nonlocal learner_step_checks
                    # Observe the prepared minibatch before its sole update:
                    # after an update, the learner argmax/logp can change.
                    with torch.no_grad(), torch.autocast("cuda", dtype=torch.bfloat16,
                                                       enabled=actor.precision == "bf16"):
                        learner_logits, _ = learner.model(learner.obs, learner.candidates, learner.mask)
                        expected_selected = learner_logits.masked_fill(~learner.mask, -torch.inf).argmax(-1)
                        if not torch.equal(learner.selected, expected_selected):
                            raise AssertionError("Synthetic workload did not choose its own learner's legal argmax")
                        expected_logp = learner_logits.log_softmax(-1).gather(
                            1, expected_selected[:, None]).squeeze(1)
                        torch.testing.assert_close(learner.old_logp, expected_logp, rtol=2e-5, atol=2e-5)
                        if not torch.equal(learner.returns, fixed_returns):
                            raise AssertionError("Fabricated returns changed with learner values")
                        if not bool(torch.isfinite(learner.returns).all()) or not bool((learner.returns.abs() <= 1).all()):
                            raise AssertionError("Fabricated returns are not finite and bounded to [-1,1]")
                    learner_step_checks += 1
                    return original_step()

                learner.step = checked_step
            learner.run(1)
            if not bool(((learner.index >= 0) & (learner.index < ring.valid_rows)).all()):
                raise AssertionError("Learner sampled an unwritten ring row")
            for name in ("obs", "candidates", "mask"):
                source = getattr(ring, name).index_select(0, learner.index)
                target = getattr(learner, name)
                if name == "mask":
                    source = source.bool()
                if not torch.equal(source, target):
                    raise AssertionError(f"Learner gather changed stored {name}")
                if target.untyped_storage().data_ptr() == getattr(ring, name).untyped_storage().data_ptr():
                    raise AssertionError("Private learner storage aliases the owned ring")
            expected_packet = ring.packet.index_select(0, learner.index)
            if not torch.equal(learner.behavior_packet, expected_packet):
                raise AssertionError("Learner lost the stored actor packet")
            stored_actions = expected_packet[:, 0]
            if not bool(torch.isfinite(expected_packet).all()) or not torch.equal(stored_actions, stored_actions.round()):
                raise AssertionError("Gathered behavior packet is nonfinite or lost integral action slots")
            if not bool(((stored_actions >= 0) & (stored_actions < 64)).all()):
                raise AssertionError("Gathered behavior action is outside original slots")
            if not bool(learner.mask.gather(1, stored_actions.long()[:, None]).all()):
                raise AssertionError("Gathered behavior action is illegal in its stored mask")
            if not bool(learner.mask.gather(1, learner.selected[:, None]).all()):
                raise AssertionError("Synthetic learner argmax is illegal in its gathered mask")
            sampled_stored_rows.update(learner.index.cpu().tolist())
            private_snapshot = {name: getattr(learner, name).clone()
                                for name in ("obs", "candidates", "mask", "selected", "behavior_packet")}
    if changed_batches == 0:
        raise AssertionError("Fixture failed to exercise changed input after capture")
    result = actor.validate_rollout()
    if learner is not None:
        result["learner_numerical"] = learner_final_finite_checks(learner)
    result.update(mode=mode, actor_calls=steps, wraps=(batch * steps) // capacity,
                  changed_input_batches=changed_batches, complete_ring_contents_exact_after_every_append=True,
                  actor_buffer_reuse_does_not_mutate_ring=True, sampled_packet_eager_logp_max_error=logp_error,
                  sampled_packet_eager_value_max_error=value_error, learner_checked=with_learner,
                  learner_private_ownership_checked=with_learner, gathered_behavior_original_slots_checked=with_learner,
                  learner_own_legal_argmax_and_logp_checks=learner_step_checks,
                  learner_fixed_bounded_fabricated_returns_checked=with_learner,
                  learner_action_semantics="Learner's own legal argmax drives synthetic ratios; stored actor packet is independently preserved and validated",
                  unique_sampled_storage_rows=len(sampled_stored_rows))
    del learner, actor, ring
    torch.cuda.synchronize()
    torch.cuda.empty_cache()
    return result


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--fixture", type=Path, default=Path(__file__).with_name("results") / "real-states.npz")
    parser.add_argument("--output", required=True)
    parser.add_argument("--modes", default="eager,graph")
    parser.add_argument("--batch", type=int, default=3)
    parser.add_argument("--capacity", type=int, default=5)
    parser.add_argument("--steps", type=int, default=6)
    parser.add_argument("--with-learner", action="store_true")
    args = parser.parse_args()
    modes = args.modes.split(",")
    if not modes or len(modes) > 2 or any(mode not in ("eager", "graph") for mode in modes):
        parser.error("Use one or both modes: eager,graph")
    if not 1 <= args.batch <= args.capacity <= 64 or not 2 <= args.steps <= 16 or args.steps * args.batch < 2 * args.capacity:
        parser.error("Require batch<=capacity<=64, steps2..16, and at least two complete wraps")
    torch.set_num_threads(1)
    torch.set_num_interop_threads(1)
    torch.backends.fp32_precision = "ieee"
    torch.backends.cuda.matmul.fp32_precision = "ieee"
    corpus, _, fixture_metadata = load_recorded_fixture({
        "fixture_path": str(args.fixture), "batch": args.batch * args.steps,
        "obs_dim": 2048, "actions": 64, "action_dim": 32,
    })
    result = {"metadata": metadata(), "kind": "owned_pipeline_rollout_correctness_only",
              "command": sys.argv, "fixture": fixture_metadata,
              "source_sha256": {name: hashlib.sha256(Path(__file__).with_name(name).read_bytes()).hexdigest()
                                 for name in ("pipeline_bench.py", "pipeline_rollout_check.py", "soak_bench.py", "model.py")},
              "configuration": {**vars(args), "fixture": str(args.fixture)},
              "learner_semantic_version": LearnerLoad.SEMANTIC_VERSION,
              "training_claim": "No campaign; optional optimizer steps use fabricated targets",
              "cases": []}
    for mode in modes:
        result["cases"].append(run_case(mode, corpus, batch=args.batch, capacity=args.capacity,
                                        steps=args.steps, with_learner=args.with_learner))
        save_json(args.output, result)
    print({"passed": len(result["cases"]), "modes": modes, "with_learner": args.with_learner})


if __name__ == "__main__":
    main()
