"""CPU-only exact-state v3 -> v4 runtime migration; never launches training.

The target identity is derived from the reviewed local v4 composition, with no
arbitrary target/config override. Preview only reads. Explicit publication needs
the existing inactive ledger lock and a new checkpoint destination. All source
payload fields except identity and added runtime provenance remain byte-valued
equivalent, including every tensor, Adam state, RNG, archive and budget counter.
"""
from __future__ import annotations

import copy
from dataclasses import asdict
import hashlib
from pathlib import Path
import time

import torch

import campaign_state as persistence
from learning_model import LearningPolicy, PolicyConfig, PPOConfig, PPOLearner
from migrate_checkpoint import _inactive_ledger, _publish_new, _tree_digest, canonical_sha256
from train_campaign import TrainConfig
import variant_runtime as v3
import variant_v4_runtime as v4

PROVENANCE_SCHEMA = "shards-v3-v4-runtime-migration-v1"
ALLOWED_IDENTITY_CHANGES = frozenset({"schema", "host_binary_sha256", "training_source_sha256",
                                     "base_v3_training_source_sha256"})


class RuntimeMigrationError(persistence.CheckpointError):
    pass


def _target_identity(source_identity):
    if (not isinstance(source_identity, dict) or source_identity.get("schema") != "shards-real-selfplay-v3" or
            source_identity.get("observation_schema") != "shards-observation-v3" or
            source_identity.get("training_source_sha256") != v4.BASE_V3_SOURCE):
        raise RuntimeMigrationError("Runtime migration requires the pinned reviewed v3 identity")
    raw_config = source_identity.get("configuration")
    if not isinstance(raw_config, dict):
        raise RuntimeMigrationError("Source TrainConfig is missing")
    config = TrainConfig(**raw_config)
    if asdict(config) != raw_config:
        raise RuntimeMigrationError("Source identity must contain the complete exact TrainConfig")
    current, old_catalog = v3.variant_identity(config)
    if source_identity != current:
        raise RuntimeMigrationError("Source identity differs from the current frozen v3 runtime")
    target, catalog = v4.variant_identity(config)
    changed = {key for key in set(source_identity) | set(target) if source_identity.get(key) != target.get(key)}
    if changed != ALLOWED_IDENTITY_CHANGES or catalog != old_catalog:
        raise RuntimeMigrationError("Runtime migration changes more than the reviewed host/source identity")
    if target.get("schema") != v4.SCHEMA or target.get("base_v3_training_source_sha256") != v4.BASE_V3_SOURCE:
        raise RuntimeMigrationError("Unexpected target runtime lineage")
    return target


def protected_payload_digest(payload, source_identity):
    """Canonical typed hash of every original field, normalizing only identity."""
    normalized = dict(payload)
    normalized.pop("file_sha256", None)  # Loader metadata, not source serialized data.
    normalized.pop("runtime_migration", None)
    normalized["identity"] = source_identity
    normalized["identity_sha256"] = canonical_sha256(source_identity)
    return _tree_digest(normalized)


def _validate_state(state, source_identity):
    if state.get("configuration") != source_identity["configuration"]:
        raise RuntimeMigrationError("Saved TrainConfig differs from its strict source identity")
    learner = state.get("learner", {})
    if learner.get("format") != PPOLearner.FORMAT:
        raise RuntimeMigrationError("Missing expected PPO learner state")
    config = PolicyConfig(**state["policy_config"])
    if config.width != source_identity["configuration"]["width"] or PolicyConfig(**learner["policy_config"]) != config:
        raise RuntimeMigrationError("Saved policy configuration differs from the source identity")
    PPOConfig(**learner["ppo_config"])
    for key in ("generations", "decisions", "games", "optimizer_passes", "next_engine_seed", "eval_index"):
        if type(state.get(key)) is not int or state[key] < 0:
            raise RuntimeMigrationError("Invalid saved continuation counter: " + key)
    for key in ("updates", "rejected"):
        if type(learner.get(key)) is not int or learner[key] < 0:
            raise RuntimeMigrationError("Invalid saved learner counter: " + key)
    if "torch_rng_cpu" not in learner or "torch_rng_device" not in learner:
        raise RuntimeMigrationError("Saved learner RNG state is missing")
    archives = state.get("archive")
    if not isinstance(archives, list) or not archives:
        raise RuntimeMigrationError("Frozen archive is missing")
    entries = [state["champion"], *archives]
    for entry in entries:
        if type(entry.get("version")) is not int or not 0 <= entry["version"] <= state["generations"]:
            raise RuntimeMigrationError("Invalid stored policy version")
    with torch.random.fork_rng(devices=[]):
        template = LearningPolicy(config)
    shapes = [tuple(parameter.shape) for parameter in template.parameters()]
    for policy in [learner["policy"], state["initial_policy"], *[entry["policy"] for entry in entries]]:
        if any(not torch.is_tensor(tensor) or tensor.dtype != torch.float32 for tensor in policy.values()):
            raise RuntimeMigrationError("Stored policy tensors must remain FP32")
        template.load_state_dict(policy, strict=True)
    optimizer = learner.get("optimizer", {})
    groups, states = optimizer.get("param_groups"), optimizer.get("state")
    ids = list(range(len(shapes)))
    if not isinstance(groups, list) or len(groups) != 1 or groups[0].get("params") != ids:
        raise RuntimeMigrationError("Optimizer parameter mapping differs from the saved policy")
    if not isinstance(states, dict) or any(type(index) is not int or index not in ids for index in states):
        raise RuntimeMigrationError("Invalid optimizer state keys")
    if learner["updates"] and set(states) != set(ids):
        raise RuntimeMigrationError("Updated learner is missing Adam state")
    for index, entry in states.items():
        if not isinstance(entry, dict) or set(entry) != {"step", "exp_avg", "exp_avg_sq"}:
            raise RuntimeMigrationError("Unexpected Adam state fields")
        for key in ("exp_avg", "exp_avg_sq"):
            value = entry[key]
            if not torch.is_tensor(value) or value.dtype != torch.float32 or tuple(value.shape) != shapes[index]:
                raise RuntimeMigrationError("Adam moment shape/dtype differs from the saved parameter")
        value = entry["step"]
        if not torch.is_tensor(value) or value.numel() != 1 or float(value) < 0 or float(value) != int(float(value)):
            raise RuntimeMigrationError("Invalid Adam step")


def prepare_runtime_migration(source_checkpoint, *, source_identity):
    """Owned CPU preview; no publication, RNG capture, or ledger mutation."""
    if torch.cuda.is_initialized() or v3._installed or v4._installed:
        raise RuntimeMigrationError("Migration requires a fresh CPU-only process without runtime installation")
    target_identity = _target_identity(source_identity)
    source_checkpoint = Path(source_checkpoint).resolve(strict=True)
    # One inode across checksum/loading even when a trainer atomically replaces latest.
    with source_checkpoint.open("rb") as source:
        payload = persistence.load_checkpoint(f"/proc/self/fd/{source.fileno()}", expected_identity=source_identity)
        source.seek(0)
        source_file_sha256 = hashlib.sha256(source.read()).hexdigest()
    if "runtime_migration" in payload:
        raise RuntimeMigrationError("Checkpoint already carries runtime-migration provenance")
    _validate_state(payload["state"], source_identity)
    protected = protected_payload_digest(payload, source_identity)
    source_payload_sha256 = payload.pop("file_sha256")
    payload["identity"] = copy.deepcopy(target_identity)
    payload["identity_sha256"] = canonical_sha256(target_identity)
    if protected_payload_digest(payload, source_identity) != protected:
        raise RuntimeMigrationError("Runtime migration changed protected checkpoint content")
    provenance = {"schema": PROVENANCE_SCHEMA, "prepared_wall": time.time(),
        "source_file_sha256": source_file_sha256, "source_payload_sha256": source_payload_sha256,
        "source_identity_sha256": canonical_sha256(source_identity),
        "target_identity_sha256": canonical_sha256(target_identity),
        "source_runtime_sha256": v4.BASE_V3_SOURCE,
        "helper_sha256": hashlib.sha256(Path(__file__).read_bytes()).hexdigest(),
        "changed_identity_fields": sorted(ALLOWED_IDENTITY_CHANGES),
        "protected_payload_sha256": protected,
        "preserved_state_sha256": _tree_digest(payload["state"]),
        "preserved_rng_sha256": _tree_digest(payload["rng"]),
        "preserved_budget_sha256": _tree_digest(payload["budget"]),
        "generation": payload["state"]["generations"], "state_mutation": "none",
        "observation_schema_unchanged": True, "training_started": False,
        "budget_scope": "No new allocation; future continuation requires the original shared ledger"}
    payload["runtime_migration"] = provenance
    return payload, copy.deepcopy(provenance)


def migrate_runtime_checkpoint(source_checkpoint, destination, *, ledger_path, source_identity):
    """Explicit publication of a new copy while the existing ledger is inactive."""
    source_checkpoint = Path(source_checkpoint).resolve(strict=True)
    destination = Path(destination).absolute()
    if source_checkpoint == destination.resolve():
        raise RuntimeMigrationError("Runtime migration must create a different checkpoint path")
    with _inactive_ledger(ledger_path) as (ledger, ledger_sha256):
        payload, report = prepare_runtime_migration(source_checkpoint, source_identity=source_identity)
        saved = payload["budget"]
        if saved.get("campaign_id") != ledger["campaign_id"] or saved.get("limit_seconds") != ledger["limit_seconds"]:
            raise RuntimeMigrationError("Checkpoint belongs to a different campaign allocation")
        if saved["charged_seconds"] > ledger["charged_seconds"] + 1e-6:
            raise RuntimeMigrationError("Checkpoint time exceeds the authoritative ledger")
        payload["runtime_migration"]["ledger_sha256_at_migration"] = ledger_sha256
        _publish_new(destination, payload, payload["identity"])
        restored = persistence.load_checkpoint(destination, expected_identity=payload["identity"])
        if protected_payload_digest(restored, source_identity) != report["protected_payload_sha256"]:
            raise RuntimeMigrationError("Published migration changed protected checkpoint content")
        return {**report, "target_identity": copy.deepcopy(payload["identity"]),
            "path": str(destination), "payload_sha256": restored["file_sha256"],
            "file_sha256": hashlib.sha256(destination.read_bytes()).hexdigest(),
            "ledger_sha256_at_migration": ledger_sha256, "strict_target_reload_passed": True,
            "cuda_initialized": torch.cuda.is_initialized()}
