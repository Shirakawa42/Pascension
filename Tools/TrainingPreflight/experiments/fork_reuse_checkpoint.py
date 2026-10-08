"""Prepare one bounded, unlaunched epochs3 -> epochs6 quality experiment.

This helper is not imported by training and exposes no arbitrary override. The
runtime, host, observation schema, execution profile, policy, Adam state, RNG,
budget and engine seeds remain unchanged. A preview performs only CPU reads.
Publication is a separate explicit call requiring the existing inactive ledger
lock and a new destination; it neither starts nor authorizes a training branch.
Every later branch still consumes the original shared training allocation.
"""
from __future__ import annotations

import copy
from dataclasses import asdict
import hashlib
from pathlib import Path
import time

import torch

import campaign_state as persistence
from learning_model import PolicyConfig, PPOLearner
from migrate_checkpoint import (_inactive_ledger, _publish_new, _tree_digest,
                                canonical_sha256)
from train_campaign import TrainConfig
import variant_runtime


PINNED_RUNTIME_SHA256 = "4d7b826d5d11b7ab825faf170115e4bbc75adf115250706244dcdb6ecb9f72f6"
FORK_SCHEMA = "shards-epochs3-to6-checkpoint-fork-v1"


class ForkError(persistence.CheckpointError):
    pass


def _identities(source_identity):
    if not isinstance(source_identity, dict) or source_identity.get("schema") != "shards-real-selfplay-v3" or source_identity.get("observation_schema") != "shards-observation-v3":
        raise ForkError("Reuse fork requires the current strict schema-v3 identity")
    if source_identity.get("training_source_sha256") != PINNED_RUNTIME_SHA256:
        raise ForkError("Reuse fork runtime source is not the pinned reviewed revision")
    raw_config = source_identity.get("configuration")
    if not isinstance(raw_config, dict) or type(raw_config.get("epochs")) is not int or raw_config["epochs"] != 3:
        raise ForkError("Only an epochs3 checkpoint can be forked to epochs6")
    config = TrainConfig(**raw_config)
    if asdict(config) != raw_config:
        raise ForkError("Source identity must contain the complete exact TrainConfig")
    current, source_catalog = variant_runtime.variant_identity(config)
    if current != source_identity:
        raise ForkError("Source identity differs from the current frozen runtime/host/configuration")
    target_config = copy.deepcopy(raw_config)
    target_config["epochs"] = 6
    target, target_catalog = variant_runtime.variant_identity(TrainConfig(**target_config))
    expected = copy.deepcopy(source_identity)
    expected["configuration"]["epochs"] = 6
    if target != expected or target_catalog != source_catalog:
        raise ForkError("Derived target changes more than the single approved epochs3-to6 setting")
    return target


def _protected_payload_digest(payload):
    """Normalize only permitted deltas; hash everything else, including history."""
    normalized = dict(payload)
    normalized.pop("file_sha256", None)  # Loader metadata, not saved source state.
    normalized.pop("configuration_fork", None)
    normalized["identity"] = copy.deepcopy(payload["identity"])
    normalized["identity"]["configuration"]["epochs"] = 3
    normalized["identity_sha256"] = canonical_sha256(normalized["identity"])
    normalized["state"] = dict(payload["state"])
    normalized["state"]["configuration"] = copy.deepcopy(payload["state"]["configuration"])
    normalized["state"]["configuration"]["epochs"] = 3
    return _tree_digest(normalized)


def prepare_reuse_fork(source_checkpoint, *, source_identity):
    """Return an owned CPU payload/report; no file or ledger write occurs.

Target identity is recomputed from the same frozen variant runtime. There is no
target-config argument, learning-rate override, fresh optimizer or RNG capture.
"""
    if torch.cuda.is_initialized() or variant_runtime._installed:
        raise ForkError("Reuse fork requires an independent CPU-only process without runtime installation")
    target_identity = _identities(source_identity)
    source_checkpoint = Path(source_checkpoint).resolve(strict=True)
    with source_checkpoint.open("rb") as source:
        payload = persistence.load_checkpoint(f"/proc/self/fd/{source.fileno()}", expected_identity=source_identity)
        source.seek(0)
        source_file_sha256 = hashlib.sha256(source.read()).hexdigest()
    if "configuration_fork" in payload:
        raise ForkError("Checkpoint already carries configuration-fork provenance")
    state = payload["state"]
    if state.get("configuration") != source_identity["configuration"]:
        raise ForkError("Saved TrainConfig differs from its strict source identity")
    if state.get("learner", {}).get("format") != PPOLearner.FORMAT:
        raise ForkError("Checkpoint does not contain the expected trained PPO learner")
    policy_config = PolicyConfig(**state["policy_config"])
    if (policy_config.width != source_identity["configuration"]["width"] or
            PolicyConfig(**state["learner"]["policy_config"]) != policy_config):
        raise ForkError("Saved policy configuration differs from the source identity")
    for name in ("optimizer", "policy", "torch_rng_cpu"):
        if name not in state["learner"]:
            raise ForkError("Saved learner state is missing: "+name)
    for name in ("champion", "initial_policy", "archive", "generations", "decisions", "games",
                 "optimizer_passes", "next_engine_seed", "eval_index"):
        if name not in state:
            raise ForkError("Checkpoint continuation state is missing: "+name)
    protected = _protected_payload_digest(payload)
    source_payload_sha256 = payload.pop("file_sha256")
    payload["state"]["configuration"] = copy.deepcopy(target_identity["configuration"])
    payload["identity"] = copy.deepcopy(target_identity)
    payload["identity_sha256"] = canonical_sha256(target_identity)
    if _protected_payload_digest(payload) != protected:
        raise ForkError("Fork changed checkpoint content outside the approved epochs fields")
    provenance = {"schema": FORK_SCHEMA, "prepared_wall": time.time(),
        "source_file_sha256": source_file_sha256, "source_payload_sha256": source_payload_sha256,
        "source_identity_sha256": canonical_sha256(source_identity),
        "target_identity_sha256": canonical_sha256(target_identity),
        "runtime_source_sha256": PINNED_RUNTIME_SHA256,
        "helper_sha256": hashlib.sha256(Path(__file__).read_bytes()).hexdigest(),
        "changes": {"state.configuration.epochs": {"from": 3, "to": 6},
                    "identity.configuration.epochs": {"from": 3, "to": 6}},
        "protected_payload_sha256": protected,
        "learner_and_optimizer_sha256": _tree_digest(state["learner"]),
        "preserved_rng_sha256": _tree_digest(payload["rng"]),
        "preserved_budget_sha256": _tree_digest(payload["budget"]),
        "generation": state["generations"], "training_started": False,
        "budget_scope": "Fork copies no allocation; any later training uses the original campaign ledger"}
    payload["configuration_fork"] = provenance
    return payload, copy.deepcopy(provenance)


def fork_reuse_checkpoint(source_checkpoint, destination, *, ledger_path, source_identity):
    """Explicit publication only; never overwrite, recover a ledger or launch."""
    source_checkpoint = Path(source_checkpoint).resolve(strict=True)
    destination = Path(destination).absolute()
    if source_checkpoint == destination.resolve():
        raise ForkError("Reuse fork must create a different checkpoint path")
    with _inactive_ledger(ledger_path) as (ledger, ledger_sha256):
        payload, report = prepare_reuse_fork(source_checkpoint, source_identity=source_identity)
        saved = payload["budget"]
        if saved.get("campaign_id") != ledger["campaign_id"] or saved.get("limit_seconds") != ledger["limit_seconds"]:
            raise ForkError("Source checkpoint belongs to a different shared campaign allocation")
        if saved["charged_seconds"] > ledger["charged_seconds"]+1e-6:
            raise ForkError("Source checkpoint time is ahead of the authoritative ledger")
        payload["configuration_fork"]["ledger_sha256_at_fork"] = ledger_sha256
        _publish_new(destination, payload, payload["identity"])
        restored = persistence.load_checkpoint(destination, expected_identity=payload["identity"])
        if _protected_payload_digest(restored) != report["protected_payload_sha256"]:
            raise ForkError("Published fork changed protected checkpoint content")
        return {**report, "target_identity": copy.deepcopy(payload["identity"]),
                "path": str(destination), "payload_sha256": restored["file_sha256"],
                "file_sha256": hashlib.sha256(destination.read_bytes()).hexdigest(),
                "ledger_sha256_at_fork": ledger_sha256, "strict_target_reload_passed": True,
                "cuda_initialized": torch.cuda.is_initialized()}
