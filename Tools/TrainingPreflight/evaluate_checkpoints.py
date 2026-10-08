"""Compare frozen campaign checkpoints without updates or training-budget access.

Example: evaluate_checkpoints.py --a RUN/latest.soicp --b RUN/latest.soicp
         --a-role learner --b-role initial --games 256 --output evaluation.json

Each checkpoint requires its run's adjacent identity.json. Current rules,
catalog, host binary and learner sources must still match that saved identity.
The two runs may use different supported policy widths/configurations. Only
explicitly requested, seat-swapped matches run; no optimizer or ledger is opened.
"""
from __future__ import annotations

import argparse
from dataclasses import dataclass
import hashlib
import json
import os
from pathlib import Path
import time

import torch

from bench_common import save_json
from campaign_state import load_checkpoint
from learning_eval import evaluate_match
from learning_model import LearningPolicy, PolicyConfig
from train_campaign import TrainConfig, identity


ROLES = ("learner", "champion", "initial")
SCHEMA = "shards-frozen-evaluation-v1"


@dataclass
class FrozenSelection:
    config: PolicyConfig
    weights: dict
    metadata: dict
    catalog: dict


def load_selection(checkpoint, role="learner"):
    """Read and validate a frozen selection entirely on CPU; never opens a ledger."""
    if role not in ROLES:
        raise ValueError("Unknown frozen policy role")
    checkpoint = Path(checkpoint).expanduser().resolve()
    identity_path = checkpoint.parent / "identity.json"
    saved_identity = json.loads(identity_path.read_text())
    if not isinstance(saved_identity, dict) or not isinstance(saved_identity.get("configuration"), dict):
        raise ValueError("Run identity is missing its pinned training configuration")
    config = TrainConfig(**saved_identity["configuration"])
    current_identity, catalog = identity(config)
    if current_identity != saved_identity:
        differing = sorted(key for key in set(current_identity) | set(saved_identity)
                           if current_identity.get(key) != saved_identity.get(key))
        raise ValueError("Current source/rules/catalog/binary/config differs from saved run identity: " + ", ".join(differing))
    # budget=None is intentional: this is a read-only load, not a new campaign
    # session or authorization to update a model. The loader verifies checksum,
    # identity, finite tensors and the episode-free checkpoint boundary.
    payload = load_checkpoint(checkpoint, expected_identity=current_identity, budget=None)
    state = payload["state"]
    if state.get("configuration") != saved_identity["configuration"]:
        raise ValueError("Checkpoint state and pinned training configuration disagree")
    policy_config = PolicyConfig(**state["policy_config"])
    if policy_config.width != config.width:
        raise ValueError("Checkpoint policy width differs from pinned training width")
    generation = state["generations"]
    if isinstance(generation, bool) or not isinstance(generation, int) or generation < 0:
        raise ValueError("Invalid checkpoint generation")
    if role == "learner":
        learner = state["learner"]
        if PolicyConfig(**learner["policy_config"]) != policy_config:
            raise ValueError("Learner and checkpoint policy configurations disagree")
        weights, version = learner["policy"], generation
    elif role == "champion":
        weights, version = state["champion"]["policy"], state["champion"]["version"]
    else:
        weights, version = state["initial_policy"], 0
    if isinstance(version, bool) or not isinstance(version, int) or not 0 <= version <= generation:
        raise ValueError("Invalid selected policy version")
    if not isinstance(weights, dict) or not weights:
        raise ValueError("Selected role has no policy state")
    metadata = {
        "checkpoint": str(checkpoint), "run_dir": str(checkpoint.parent), "role": role,
        "version": version, "width": policy_config.width, "checkpoint_generation": generation,
        "checkpoint_payload_sha256": payload["file_sha256"],
        "checkpoint_hash_scope": "SHA-256 of the exact checksummed serialized payload loaded; excludes wrapper header",
        "checkpoint_created_wall": payload["created_wall"],
        "campaign_id": payload["budget"]["campaign_id"],
        "checkpoint_training_seconds": payload["budget"]["charged_seconds"],
        "run_identity": saved_identity, "policy_configuration": policy_config.to_dict(),
    }
    return FrozenSelection(policy_config, weights, metadata, catalog)


def materialize_policy(selection, device="cpu"):
    """Own model storage separately from deserialized checkpoint tensors."""
    policy = LearningPolicy(selection.config)
    policy.load_state_dict(selection.weights, strict=True)
    return policy.eval().requires_grad_(False).to(device)


def policy_hash(policy):
    digest = hashlib.sha256()
    for name, value in sorted(policy.state_dict().items()):
        tensor = value.detach().cpu().contiguous()
        digest.update(json.dumps([name, str(tensor.dtype), list(tensor.shape)], separators=(",", ":")).encode())
        digest.update(tensor.numpy().tobytes())
    return digest.hexdigest()


def utc():
    return time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime())


def integer(value):
    return int(value, 0)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--a", type=Path, required=True, help="Policy A campaign .soicp checkpoint")
    parser.add_argument("--b", type=Path, required=True, help="Policy B campaign .soicp checkpoint")
    parser.add_argument("--a-role", choices=ROLES, default="learner")
    parser.add_argument("--b-role", choices=ROLES, default="learner")
    parser.add_argument("--games", type=int, default=256, help="Total games, including both seats of each paired seed")
    parser.add_argument("--batch", type=int, default=128)
    parser.add_argument("--workers", type=int, default=4)
    parser.add_argument("--seed", type=integer, default=0x2000000000000000, help="First paired engine seed; decimal or 0x hex")
    parser.add_argument("--sampling-seed", type=integer, default=60926, help="Independent frozen-policy action-sampling seed")
    parser.add_argument("--telemetry", action="store_true", help="Record policy A candidate opportunities/selections; not causal balance estimates")
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    if not 2 <= args.games <= 1000000 or args.games % 2:
        parser.error("Require an even total game count in [2,1000000]")
    if not 1 <= args.batch <= 256 or not 1 <= args.workers <= 16:
        parser.error("Require batch1..256 and workers1..16")
    if not 0 <= args.seed <= 2**64 - args.games//2 or not 0 <= args.sampling_seed < 2**63:
        parser.error("Paired seeds must fit uint64; sampling seed must be in [0,2**63)")
    # Refuse accidental replacement of any source checkpoint or its identity.
    output = args.output.expanduser().resolve()
    protected = {path.expanduser().resolve() for path in (args.a, args.b)}
    protected |= {path.parent/"identity.json" for path in protected}
    if output in protected:
        parser.error("Output must not overwrite a checkpoint or its identity")
    started = time.monotonic()
    report = {"schema": SCHEMA, "event": "external_frozen_evaluation", "state": "running",
        "phase": "loading", "complete": False, "pid": os.getpid(), "started_utc": utc(),
        "evaluation_only": True, "training_budget_seconds_charged": 0, "optimizer_updates": 0,
        "configuration": {key: getattr(args, key) for key in ("games", "batch", "workers", "seed", "sampling_seed", "telemetry")},
        "policy_a": {"checkpoint": str(args.a.expanduser().resolve()), "role": args.a_role},
        "policy_b": {"checkpoint": str(args.b.expanduser().resolve()), "role": args.b_role}}
    save_json(output, report)
    try:
        selection_a = load_selection(args.a, args.a_role)
        selection_b = load_selection(args.b, args.b_role)
        report.update(policy_a=selection_a.metadata, policy_b=selection_b.metadata, catalog=selection_a.catalog)
        if selection_a.catalog != selection_b.catalog:
            raise ValueError("Cannot compare policies with different observation/card catalogs")
        torch.set_num_threads(1)
        torch.set_num_interop_threads(1)
        torch.backends.fp32_precision = "ieee"
        torch.backends.cuda.matmul.fp32_precision = "tf32"
        if not torch.cuda.is_available():
            raise RuntimeError("Frozen graph evaluation requires the CUDA training environment")
        policy_a = materialize_policy(selection_a, "cuda")
        policy_b = materialize_policy(selection_b, "cuda")
        hashes = (policy_hash(policy_a), policy_hash(policy_b))
        report["policy_a"]["policy_sha256"] = hashes[0]
        report["policy_b"]["policy_sha256"] = hashes[1]
        report.update(phase="matches", runtime={"torch": torch.__version__, "cuda": torch.version.cuda,
                      "device": torch.cuda.get_device_name(), "matmul_precision": "tf32"})
        save_json(output, report)
        # The saved training RNG is deliberately untouched. Evaluation gets its
        # own reproducible sampling stream, independent of paired engine seeds.
        torch.manual_seed(args.sampling_seed)
        evaluation = evaluate_match(policy_a, policy_b, games=args.games, seed=args.seed,
                                    batch=args.batch, workers=args.workers, telemetry=args.telemetry,
                                    censor_truncated=True, debug_dir=output.parent/(output.stem+"-censored"))
        if (policy_hash(policy_a), policy_hash(policy_b)) != hashes:
            raise RuntimeError("Frozen evaluation unexpectedly mutated policy state")
        report.update(evaluation)
        report.update(state="completed" if evaluation["complete"] else "failed", phase="finished",
                      finished_utc=utc(), total_seconds=time.monotonic()-started, frozen_weights_unchanged=True)
        save_json(output, report)
        print(json.dumps({key: report.get(key) for key in ("state", "games", "score_a", "score_bound_95", "wins_a", "draws", "losses_a", "seconds")}))
    except BaseException as error:
        report.update(state="failed", phase="finished", complete=False, finished_utc=utc(),
                      total_seconds=time.monotonic()-started,
                      error={"type": type(error).__name__, "message": str(error)})
        save_json(output, report)
        raise


if __name__ == "__main__":
    main()
