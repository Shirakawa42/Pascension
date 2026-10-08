"""Audited, CPU-only v2 -> v3 zero-column checkpoint migration candidate.

No live training module imports this file. No budget session is opened or
recovered. Actual publication requires an existing, exclusively locked, inactive
ledger and a manifest digest explicitly added to APPROVED_MANIFEST_SHA256 after
review. The single approved digest below pins the reviewed 2026-09-26 target.

prepare_migration is an in-memory preview; migrate_checkpoint additionally
publishes a new checksummed copy without overwriting any existing destination.
Neither recaptures RNG nor resets Adam, counters, seeds or charged time. Numerical
execution metadata may change only as pinned in the approved target identity;
TrainConfig and saved learner state otherwise remain unchanged.
"""
from __future__ import annotations

from collections.abc import Mapping
import contextlib
import copy
import fcntl
import hashlib
import io
import json
import os
from pathlib import Path
import re
import tempfile
import time

import torch

import campaign_state as persistence
from learning_model import LearningPolicy, PolicyConfig, PPOConfig, PPOLearner


SOURCE_TRAINING_SHA256 = "03454a86354ccd5c320604511df055e3043a9c56f298c2c2087fc3972070cd42"
SOURCE_SCHEMA, TARGET_SCHEMA = "shards-real-selfplay-v2", "shards-real-selfplay-v3"
SOURCE_OBSERVATION, TARGET_OBSERVATION = "shards-observation-v2", "shards-observation-v3"
SLOTS = (317, 318, 319, 509, 510, 511, 701)
WEIGHT_KEY = "core.trunk.0.weight"
FACTIONS = ("None", "Homodeus", "Undergrowth", "Order", "Wraethe", "Aion", "Monster")
APPROVAL_SCHEMA = "shards-v2-v3-migration-approval-v1"
PROVENANCE_SCHEMA = "shards-v2-v3-migration-provenance-v1"
# Reviewed by the coordinator after freezing variant runtime/entry/adoption and
# HostV3. There is no CLI/env/argument switch that trusts an arbitrary manifest.
APPROVED_MANIFEST_SHA256: frozenset[str] = frozenset({
    "49748084470e30b7a41d5c737217dde40d29a6bdcd07db79ba6bd9d6585bf61b",
})
_IDENTITY_KEYS = {"schema", "observation_schema", "rules_sha256", "host_binary_sha256",
                  "catalog_sha256", "training_source_sha256", "configuration"}
_IDENTITY_CHANGE_KEYS = {"schema", "observation_schema", "host_binary_sha256", "catalog_sha256",
                         "training_source_sha256", "execution", "execution_precision",
                         "base_training_source_sha256", "new_observation_slots"}


class MigrationError(persistence.CheckpointError):
    pass


def canonical_sha256(value):
    return hashlib.sha256(persistence._canonical(value)).hexdigest()


def catalog_sha256(value):
    # Matches train_campaign.identity, including its default JSON separators.
    return hashlib.sha256(json.dumps(value, sort_keys=True, allow_nan=False).encode()).hexdigest()


def expected_features():
    return [{"slot": slot, "name": "own_allegiance_"+faction.lower(), "faction": faction,
             "normalization": 20, "source": "AllegianceEffect.OwnedCount(current deciding player)"}
            for slot, faction in zip(SLOTS, FACTIONS)]


def _validate_transition(source_identity, target_identity, slots, source_catalog, target_catalog, reviewed_manifest):
    if not isinstance(reviewed_manifest, Mapping):
        raise MigrationError("A reviewed migration manifest is required")
    manifest = dict(reviewed_manifest)
    digest = canonical_sha256(manifest)
    if digest not in APPROVED_MANIFEST_SHA256:
        raise MigrationError("Target migration manifest has not been explicitly approved")
    required = {"schema", "source_identity_sha256", "target_identity_sha256", "slots",
                "weight_key", "allowed_identity_changes"}
    if set(manifest) != required or manifest["schema"] != APPROVAL_SCHEMA:
        raise MigrationError("Unknown migration manifest schema/fields")
    if not isinstance(source_identity, dict) or not isinstance(target_identity, dict):
        raise MigrationError("Source and target identities must be dictionaries")
    if set(source_identity) != _IDENTITY_KEYS or not _IDENTITY_KEYS <= set(target_identity):
        raise MigrationError("Unexpected source/target identity fields")
    if set(target_identity)-_IDENTITY_KEYS-{"execution", "execution_precision", "base_training_source_sha256", "new_observation_slots"}:
        raise MigrationError("Unreviewed target identity field")
    if source_identity["training_source_sha256"] != SOURCE_TRAINING_SHA256:
        raise MigrationError("Source is not the pinned revision-2 trainer")
    if target_identity.get("base_training_source_sha256") != SOURCE_TRAINING_SHA256 or target_identity.get("new_observation_slots") != list(SLOTS):
        raise MigrationError("Target identity must declare the pinned base source and exactly seven new slots")
    if (source_identity["schema"], target_identity["schema"], source_identity["observation_schema"],
            target_identity["observation_schema"]) != (SOURCE_SCHEMA, TARGET_SCHEMA, SOURCE_OBSERVATION, TARGET_OBSERVATION):
        raise MigrationError("Only the explicit observation-v2 to observation-v3 transition is supported")
    for identity in (source_identity, target_identity):
        if not isinstance(identity["configuration"], dict):
            raise MigrationError("Identity configuration is missing")
        for field in ("rules_sha256", "host_binary_sha256", "catalog_sha256", "training_source_sha256"):
            if not isinstance(identity[field], str) or not re.fullmatch("[0-9a-f]{64}", identity[field]):
                raise MigrationError("Identity contains an invalid SHA-256")
    if source_identity["configuration"] != target_identity["configuration"] or source_identity["rules_sha256"] != target_identity["rules_sha256"]:
        raise MigrationError("Migration cannot change rules or TrainConfig")
    changes = sorted(key for key in set(source_identity) | set(target_identity)
                     if source_identity.get(key) != target_identity.get(key))
    if not set(changes) <= _IDENTITY_CHANGE_KEYS or manifest["allowed_identity_changes"] != changes:
        raise MigrationError("Identity changes differ from the reviewed allowlist")
    if (manifest["source_identity_sha256"] != canonical_sha256(source_identity) or
            manifest["target_identity_sha256"] != canonical_sha256(target_identity)):
        raise MigrationError("Source/target identity does not match the reviewed manifest")
    if not isinstance(slots, (list, tuple)) or any(type(slot) is not int for slot in slots) or tuple(slots) != SLOTS:
        raise MigrationError("Migration columns must be the seven proven-zero v2 slots")
    if manifest["slots"] != list(SLOTS) or manifest["weight_key"] != WEIGHT_KEY:
        raise MigrationError("Manifest changes unapproved parameter columns")
    if not isinstance(source_catalog, dict) or not isinstance(target_catalog, dict):
        raise MigrationError("Source and target catalogs are required")
    if (source_catalog.get("ObsDim"), source_catalog.get("MaxActions"), source_catalog.get("ActionDim"),
            source_catalog.get("observationSchema")) != (2048, 64, 32, SOURCE_OBSERVATION):
        raise MigrationError("Source catalog schema/dimensions differ")
    cards = source_catalog.get("cards")
    if not isinstance(cards, list) or len(cards) != 189 or any(not isinstance(card, str) for card in cards) or len(set(cards)) != 189:
        raise MigrationError("The zero-column proof requires the exact 189-definition catalog")
    expected = copy.deepcopy(source_catalog)
    expected.update(observationSchema=TARGET_OBSERVATION,
                    extraObservationFeatures=expected_features(), maxCardDefinitions=189)
    if target_catalog != expected:
        raise MigrationError("Catalog changes extend beyond the seven declared own-Allegiance features")
    if catalog_sha256(source_catalog) != source_identity["catalog_sha256"] or catalog_sha256(target_catalog) != target_identity["catalog_sha256"]:
        raise MigrationError("Catalog bytes do not match the pinned identities")
    return digest, changes


def _tree_digest(value, zero_paths=frozenset(), path=()):
    digest = hashlib.sha256()

    def visit(item, current):
        if torch.is_tensor(item):
            tensor = item.detach().cpu().contiguous()
            if current in zero_paths:
                tensor = tensor.clone()
                tensor[:, list(SLOTS)] = 0
            digest.update(b"tensor"+str(tensor.dtype).encode()+repr(tuple(tensor.shape)).encode())
            digest.update(tensor.reshape(-1).view(torch.uint8).numpy().tobytes())
        elif isinstance(item, dict):
            digest.update(b"dict"+str(len(item)).encode())
            for key in sorted(item, key=lambda key: (type(key).__name__, repr(key))):
                visit(key, current+("<key>",))
                visit(item[key], current+(key,))
        elif isinstance(item, (list, tuple)):
            digest.update(type(item).__name__.encode()+str(len(item)).encode())
            for index, child in enumerate(item):
                visit(child, current+(index,))
        elif isinstance(item, bytes):
            digest.update(b"bytes"+str(len(item)).encode()+b":"+item)
        else:
            digest.update(type(item).__name__.encode()+b":"+persistence._canonical(item)+b";")

    visit(value, path)
    return digest.hexdigest()


def _policies_and_optimizer(state, source_identity):
    if state.get("configuration") != source_identity["configuration"]:
        raise MigrationError("Checkpoint TrainConfig differs from its source identity")
    config = PolicyConfig(**state["policy_config"])
    if config.width != source_identity["configuration"].get("width"):
        raise MigrationError("Policy width differs from TrainConfig")
    learner = state["learner"]
    if learner.get("format") != PPOLearner.FORMAT or PolicyConfig(**learner["policy_config"]) != config:
        raise MigrationError("Learner format/configuration mismatch")
    PPOConfig(**learner["ppo_config"])
    for name in ("generations", "decisions", "games", "optimizer_passes", "next_engine_seed", "eval_index"):
        if type(state.get(name)) is not int or state[name] < 0:
            raise MigrationError("Invalid saved training counter: "+name)
    if type(learner.get("updates")) is not int or learner["updates"] < 0:
        raise MigrationError("Invalid learner update count")
    policies = [(('learner', 'policy', WEIGHT_KEY), learner["policy"]),
                (('champion', 'policy', WEIGHT_KEY), state["champion"]["policy"]),
                (('initial_policy', WEIGHT_KEY), state["initial_policy"])]
    if not isinstance(state["archive"], list) or not state["archive"]:
        raise MigrationError("Frozen archive is missing")
    for index, entry in enumerate(state["archive"]):
        policies.append((("archive", index, "policy", WEIGHT_KEY), entry["policy"]))
    for entry in [state["champion"], *state["archive"]]:
        if type(entry.get("version")) is not int or not 0 <= entry["version"] <= state["generations"]:
            raise MigrationError("Invalid archive/champion version")
    # Model construction stays on CPU and must not change caller RNG state.
    with torch.random.fork_rng(devices=[]):
        template = LearningPolicy(config)
    parameter_shapes = [(name, tuple(value.shape)) for name, value in template.named_parameters()]
    for _, policy in policies:
        if not isinstance(policy, dict) or any(not torch.is_tensor(value) or value.dtype != torch.float32 for value in policy.values()):
            raise MigrationError("All policy parameters/buffers must remain FP32 tensors")
        template.load_state_dict(policy, strict=True)
        if policy[WEIGHT_KEY].shape != (config.width, 2048):
            raise MigrationError("First-layer schema differs from the approved migration")
    optimizer = learner["optimizer"]
    groups = optimizer.get("param_groups")
    expected_ids = list(range(len(parameter_shapes)))
    if not isinstance(groups, list) or len(groups) != 1 or groups[0].get("params") != expected_ids:
        raise MigrationError("Optimizer parameter mapping is not the pinned single-group ordering")
    states = optimizer.get("state")
    if not isinstance(states, dict) or any(type(key) is not int or key not in expected_ids for key in states):
        raise MigrationError("Invalid optimizer state keys")
    if learner["updates"] and set(states) != set(expected_ids):
        raise MigrationError("An updated learner is missing Adam state")
    first_id = next(index for index, (name, _) in enumerate(parameter_shapes) if name == WEIGHT_KEY)
    for parameter_id, entry in states.items():
        if not isinstance(entry, dict) or not {"step", "exp_avg", "exp_avg_sq"} <= set(entry) or set(entry)-{"step", "exp_avg", "exp_avg_sq", "max_exp_avg_sq"}:
            raise MigrationError("Unexpected Adam state fields")
        for key in set(entry)-{"step"}:
            moment = entry[key]
            if not torch.is_tensor(moment) or tuple(moment.shape) != parameter_shapes[parameter_id][1] or moment.dtype != torch.float32:
                raise MigrationError("Adam moment shape/dtype differs from its parameter")
            if parameter_id == first_id and bool(torch.count_nonzero(moment[:, list(SLOTS)])):
                raise MigrationError("Formerly-zero feature columns have nonzero Adam moments; refusing migration")
        step = entry["step"]
        if not torch.is_tensor(step) or step.numel() != 1 or float(step) < 0 or float(step) != int(float(step)):
            raise MigrationError("Invalid Adam step")
    return policies, first_id


def prepare_migration(source_checkpoint, *, source_identity, target_identity, slots,
                      source_catalog, target_catalog, reviewed_manifest):
    """Return an owned migrated payload/report without writes, RNG capture or CUDA.

Even previews require an approved manifest. Tests use synthetic approved
manifests in their own process; no unapproved-mode flag exists in this API.
"""
    if torch.cuda.is_initialized():
        raise MigrationError("Migration must run in an independent CPU-only process")
    approval, identity_changes = _validate_transition(source_identity, target_identity, slots,
        source_catalog, target_catalog, reviewed_manifest)
    source_checkpoint = Path(source_checkpoint).resolve(strict=True)
    # Keep one inode open across hashing and strict loading, even if a producer
    # atomically replaces the public path during a read-only preview.
    with source_checkpoint.open("rb") as source:
        payload = persistence.load_checkpoint(f"/proc/self/fd/{source.fileno()}", expected_identity=source_identity)
        source.seek(0)
        source_file_sha256 = hashlib.sha256(source.read()).hexdigest()
    if "migration" in payload:
        raise MigrationError("Refusing to migrate a checkpoint that already carries migration provenance")
    policies, first_id = _policies_and_optimizer(payload["state"], source_identity)
    paths = frozenset(path for path, _ in policies)
    preserved_state_sha256 = _tree_digest(payload["state"], zero_paths=paths)
    rng_sha256 = _tree_digest(payload["rng"])
    budget_sha256 = _tree_digest(payload["budget"])
    source_payload_sha256 = payload.pop("file_sha256")  # Loader metadata, not serialized source state.
    for _, policy in policies:
        policy[WEIGHT_KEY][:, list(SLOTS)] = 0
    if _tree_digest(payload["state"]) != preserved_state_sha256:
        raise MigrationError("Migration changed state outside approved zero columns")
    payload["identity"] = copy.deepcopy(target_identity)
    payload["identity_sha256"] = canonical_sha256(target_identity)
    provenance = {
        "schema": PROVENANCE_SCHEMA, "migrated_wall": time.time(),
        "source_file_sha256": source_file_sha256, "source_payload_sha256": source_payload_sha256,
        "source_identity_sha256": canonical_sha256(source_identity),
        "target_identity_sha256": canonical_sha256(target_identity), "approval_manifest_sha256": approval,
        "identity_changes": identity_changes, "slots": list(SLOTS), "weight_key": WEIGHT_KEY,
        "changed_policy_paths": [list(path) for path, _ in policies], "optimizer_first_weight_id": first_id,
        "unchanged_state_except_columns_sha256": preserved_state_sha256,
        "preserved_rng_sha256": rng_sha256, "preserved_budget_sha256": budget_sha256,
        "adam_moments_already_zero": True, "adam_step_reset": False,
        "state_mutation": "Zero only the seven formerly-unused first-layer columns in every stored policy",
        "numerical_scope": "CPU serialization/column migration; isolated GPU parity and resume validation still required",
    }
    payload["migration"] = provenance
    return payload, copy.deepcopy(provenance)


@contextlib.contextmanager
def _inactive_ledger(path):
    path = Path(path).resolve(strict=True)
    try:
        fd = os.open(str(path)+".lock", os.O_RDWR | os.O_NOFOLLOW)
    except OSError as error:
        raise MigrationError("Existing campaign lock file is required; no ledger is created") from error
    try:
        try:
            fcntl.flock(fd, fcntl.LOCK_EX | fcntl.LOCK_NB)
        except BlockingIOError as error:
            raise persistence.CampaignLocked("Training or another operation owns the campaign ledger") from error
        original = path.read_bytes()
        data = json.loads(original)
        if not isinstance(data, dict) or "active" not in data or data["active"] is not None:
            raise MigrationError("Active or unclean campaign session; migration never recovers or charges a ledger")
        # Reuse validation only. Do not __enter__, recover, persist or open a
        # session. Validation may normalize an old in-memory copy, never disk.
        check = persistence.CampaignBudget(path, limit_seconds=data["limit_seconds"])
        check._data = copy.deepcopy(data)
        check._validate()
        yield data, hashlib.sha256(original).hexdigest()
        if path.read_bytes() != original:
            raise MigrationError("Campaign ledger changed during the locked migration")
    finally:
        os.close(fd)


def _publish_new(path, payload, target_identity):
    path = Path(path)
    if path.exists() or path.is_symlink():
        raise MigrationError("Migration destination already exists; never overwrite a checkpoint")
    path.parent.mkdir(parents=True, exist_ok=True)
    serialized = io.BytesIO()
    torch.save(payload, serialized)
    content = serialized.getvalue()
    digest = hashlib.sha256(content).digest()
    fd, temporary = tempfile.mkstemp(prefix="."+path.name+".", suffix=".tmp", dir=path.parent)
    try:
        with os.fdopen(fd, "wb") as output:
            output.write(persistence._HEADER.pack(persistence._MAGIC, len(content), digest)+content)
            output.flush()
            os.fsync(output.fileno())
        persistence.load_checkpoint(temporary, expected_identity=target_identity)
        # Atomic no-replace publication on the same filesystem. Unlike replace,
        # link refuses even a destination created after the earlier exists check.
        try:
            os.link(temporary, path)
        except FileExistsError as error:
            raise MigrationError("Migration destination appeared concurrently; nothing was overwritten") from error
        directory = os.open(path.parent, os.O_RDONLY | os.O_DIRECTORY)
        try:
            os.fsync(directory)
        finally:
            os.close(directory)
    finally:
        with contextlib.suppress(FileNotFoundError):
            os.unlink(temporary)


def migrate_checkpoint(source_checkpoint, destination, *, ledger_path, source_identity, target_identity,
                       slots, source_catalog, target_catalog, reviewed_manifest):
    """Publish a new target-identity copy while leaving source and ledger intact."""
    source_checkpoint = Path(source_checkpoint).resolve(strict=True)
    destination = Path(destination).absolute()
    if source_checkpoint == destination.resolve():
        raise MigrationError("Migration must create a different checkpoint path")
    with _inactive_ledger(ledger_path) as (ledger, ledger_sha256):
        payload, report = prepare_migration(source_checkpoint, source_identity=source_identity,
            target_identity=target_identity, slots=slots, source_catalog=source_catalog,
            target_catalog=target_catalog, reviewed_manifest=reviewed_manifest)
        saved = payload["budget"]
        if saved.get("campaign_id") != ledger["campaign_id"] or saved.get("limit_seconds") != ledger["limit_seconds"]:
            raise MigrationError("Source checkpoint belongs to a different campaign allocation")
        if saved["charged_seconds"] > ledger["charged_seconds"]+1e-6:
            raise MigrationError("Source checkpoint time is ahead of the authoritative ledger")
        payload["migration"]["ledger_sha256_at_migration"] = ledger_sha256
        _publish_new(destination, payload, target_identity)
        restored = persistence.load_checkpoint(destination, expected_identity=target_identity)
        if _tree_digest(restored["state"]) != report["unchanged_state_except_columns_sha256"]:
            raise MigrationError("Published migration state differs from the audited payload")
        if _tree_digest(restored["rng"]) != report["preserved_rng_sha256"] or _tree_digest(restored["budget"]) != report["preserved_budget_sha256"]:
            raise MigrationError("Published migration changed saved RNG or budget")
        return {**report, "path": str(destination), "payload_sha256": restored["file_sha256"],
                "file_sha256": hashlib.sha256(destination.read_bytes()).hexdigest(),
                "ledger_sha256_at_migration": ledger_sha256, "strict_target_reload_passed": True,
                "cuda_initialized": torch.cuda.is_initialized()}
