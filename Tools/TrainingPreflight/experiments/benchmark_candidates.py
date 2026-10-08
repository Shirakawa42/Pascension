"""Frozen, paired real-episode tests. Refuses to compete with active training.

No optimizer is constructed and no ledger is opened for writing. Each candidate
uses identical frozen policies, engine seeds, role assignment and restored RNG.
The complete owned rollout must match the baseline exactly, including credit.
"""
import argparse
import hashlib
import json
from pathlib import Path
import sys
import time
from types import MethodType

import numpy as np
import torch

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE.parent))
from adaptive_actor import AdaptiveLearningActor
from bench_common import save_json
from evaluate_checkpoints import load_selection, materialize_policy, policy_hash
from learning_rollout import EpisodeStore, LearningHost, collect_episodes
from train_campaign import verify_behavior
from queued_actor import collect_queued
from rollout_fastpath import ContiguousEpisodeStore, validate_host_fast


VARIANTS = {
    "baseline": (False, False, False),
    "copy": (True, False, False),
    "validation": (False, True, False),
    "queued": (False, False, True),
    "copy-validation": (True, True, False),
    "all": (True, True, True),
}


class TracedHost(LearningHost):
    def __init__(self, *args, **kwargs):
        self.action_trace = hashlib.sha256()
        super().__init__(*args, **kwargs)

    def advance_active(self, indices, active):
        self.action_trace.update(np.where(active, indices, -1).astype("<i4").tobytes())
        return super().advance_active(indices, active)


def require_idle_ledger(path):
    ledger = json.loads(Path(path).read_text())
    if ledger.get("active") is not None:
        raise RuntimeError("Training owns the GPU: finish a graceful checkpointed handoff first")
    return ledger


def exact_rollout(left, right):
    if left.rows != right.rows or left.sealed != right.sealed:
        raise AssertionError("Candidate changed rollout length/sealing")
    for name in ("obs", "candidates", "mask", "actions", "old_logp", "old_values", "returns", "advantages"):
        if not torch.equal(getattr(left, name)[:left.rows], getattr(right, name)[:right.rows]):
            raise AssertionError("Candidate changed retained " + name)
    for name in ("episode_ids", "seats"):
        if not np.array_equal(getattr(left, name)[:left.rows], getattr(right, name)[:right.rows]):
            raise AssertionError("Candidate changed ownership " + name)


def benchmark(args):
    before_ledger = require_idle_ledger(args.ledger)
    torch.set_num_threads(1)
    torch.set_num_interop_threads(1)
    torch.backends.fp32_precision = "ieee"
    torch.backends.cuda.matmul.fp32_precision = args.precision
    selection_a = load_selection(args.checkpoint, "learner")
    selection_b = load_selection(args.checkpoint, "champion")
    a, b = (materialize_policy(selection, "cuda") for selection in (selection_a, selection_b))
    hashes = (policy_hash(a), policy_hash(b))
    actors = [AdaptiveLearningActor(policy, args.batch, graph=True) for policy in (a, b)]
    reference = EpisodeStore(args.capacity)
    scratch_base = EpisodeStore(args.capacity)
    # Reuse scratch storage for the fast subclass to bound VRAM; no unresolved
    # trajectories cross a trial and only the reference must survive comparison.
    scratch_fast = object.__new__(ContiguousEpisodeStore)
    scratch_fast.__dict__.update(scratch_base.__dict__)
    scratch_fast._owned_storage_pointers = frozenset(getattr(scratch_fast, name).untyped_storage().data_ptr()
        for name in ("obs", "candidates", "mask", "actions", "old_logp", "old_values"))
    report = {"schema": "shards-frozen-candidate-benchmark-v1", "state": "running",
        "evaluation_only": True, "optimizer_updates": 0, "policy_a": selection_a.metadata,
        "policy_b": selection_b.metadata, "configuration": vars(args).copy(), "trials": [],
        "scope": "Frozen whole-episode collection plus behavior verification; no PPO timing or strength claim"}
    report["configuration"] = {key: str(value) if isinstance(value, Path) else value
                               for key, value in report["configuration"].items()}
    save_json(args.output, report)
    try:
        for repeat in range(args.repeats):
            flags = np.random.default_rng(27100+repeat).random(args.batch) < .25
            seats = (np.arange(args.batch)+repeat) % 2
            torch.manual_seed(61713+repeat)
            cpu_rng, gpu_rng = torch.get_rng_state(), torch.cuda.get_rng_state()
            variants = list(args.variants)
            variants = variants[repeat % len(variants):] + variants[:repeat % len(variants)]
            order = ["baseline"] + [v for v in variants if v != "baseline"] + ["baseline-post"]
            reference_metrics = None
            for name in order:
                require_idle_ledger(args.ledger)
                contiguous, validation, queued = VARIANTS["baseline" if name == "baseline-post" else name]
                store = reference if name == "baseline" else scratch_fast if contiguous else scratch_base
                for adaptive in actors:
                    for bucket in adaptive.actors.values():
                        if validation:
                            bucket._validate_host = MethodType(validate_host_fast, bucket)
                        else:
                            bucket.__dict__.pop("_validate_host", None)
                host = TracedHost(args.batch, args.workers, args.seed+repeat*args.batch,
                                    pinned=True, transport="shared", split_branches=8)
                try:
                    torch.set_rng_state(cpu_rng)
                    torch.cuda.set_rng_state(gpu_rng)
                    torch.cuda.synchronize()
                    start = time.perf_counter()
                    collection = (collect_queued if queued else collect_episodes)(
                        host, actors[0], store, version=selection_a.metadata["version"],
                        opponent=actors[1], opponent_lanes=flags, learner_seats=seats,
                        censor_truncated=True, seed_base=args.seed+repeat*args.batch)
                    parity = verify_behavior(a, store) if store.rows else {}
                    torch.cuda.synchronize()
                    seconds = time.perf_counter()-start
                    if not collection.completed or not store.sealed:
                        raise AssertionError("Benchmark requires a completed, update-ready cohort")
                    lifecycle = {key: collection.metrics[key] for key in
                        ("completed_games", "censored_games", "wrapper_decisions", "learning_rows",
                         "action_kind_counts", "seat0_wins", "draws", "censored_learning_rows")}
                    lifecycle["all_actions_sha256"] = host.action_trace.hexdigest()
                    if name == "baseline":
                        reference_metrics = lifecycle
                    else:
                        exact_rollout(reference, store)
                        if lifecycle != reference_metrics:
                            raise AssertionError("Candidate changed episode outcomes or action counts")
                    trial = {"repeat": repeat, "variant": name, "seconds": seconds,
                        "retained_rows_per_second": store.rows/seconds,
                        "exact_reference_match": True, "collection": collection.metrics,
                        "all_actions_sha256": host.action_trace.hexdigest(),
                        "behavior_parity": parity, "gpu_allocated_bytes": torch.cuda.memory_allocated()}
                    report["trials"].append(trial)
                    save_json(args.output, report)
                    print(json.dumps({key: trial[key] for key in
                        ("repeat", "variant", "seconds", "retained_rows_per_second", "exact_reference_match")}), flush=True)
                finally:
                    host.close()
        if (policy_hash(a), policy_hash(b)) != hashes:
            raise AssertionError("Frozen policy mutated")
        after_ledger = require_idle_ledger(args.ledger)
        if after_ledger != before_ledger:
            raise AssertionError("Benchmark observed a changed campaign ledger")
        report.update(state="completed", frozen_weights_unchanged=True, ledger_unchanged=True)
    except BaseException as error:
        report.update(state="failed", error={"type": type(error).__name__, "message": str(error)})
        raise
    finally:
        save_json(args.output, report)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--checkpoint", type=Path, required=True)
    parser.add_argument("--ledger", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--repeats", type=int, default=3)
    parser.add_argument("--batch", type=int, default=256)
    parser.add_argument("--workers", type=int, default=8)
    parser.add_argument("--capacity", type=int, default=524288)
    parser.add_argument("--precision", choices=("ieee", "tf32"), default="ieee")
    parser.add_argument("--seed", type=lambda x: int(x, 0), default=0x5000000000000000)
    parser.add_argument("--variants", nargs="+", choices=tuple(VARIANTS), default=list(VARIANTS))
    args = parser.parse_args()
    if not 1 <= args.repeats <= 20 or not 1 <= args.batch <= 256 or not 1 <= args.workers <= 16:
        parser.error("Use 1..20 repeats, 1..256 lanes and 1..16 workers")
    benchmark(args)


if __name__ == "__main__":
    main()
