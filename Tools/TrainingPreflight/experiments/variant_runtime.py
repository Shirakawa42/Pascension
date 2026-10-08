"""Explicit new-process composition of the reviewed schema-v3 trainer.

Original v2 sources/binary stay intact and readable for old checkpoints. Every
runtime override below participates in the new identity; no running process is
patched. State migration is a separate fail-closed operation.
"""
import hashlib
import json
from pathlib import Path
import subprocess

import torch

import adaptive_actor
import learning_model
import pipeline_bench
import train_campaign
from rollout_fastpath import ContiguousEpisodeStore, FastValidationLearningActor
from queued_actor import collect_queued

HERE = Path(__file__).resolve().parent
BINARY = HERE / "HostV3/bin/Release/net8.0/TrainingHostV3.dll"
BASE_SOURCE = "03454a86354ccd5c320604511df055e3043a9c56f298c2c2087fc3972070cd42"
SLOTS = (317, 318, 319, 509, 510, 511, 701)
_base_identity = train_campaign.identity
_base_policy = learning_model.LearningPolicy
_original_subprocess = pipeline_bench.subprocess
_installed = False


def execution_profile():
    result = json.loads((HERE/"adoption.json").read_text())
    if set(result) != {"matmul_precision", "contiguous_store", "fast_validation", "queued_actors"}:
        raise ValueError("Unknown execution profile fields")
    if result["matmul_precision"] != "ieee" or any(type(result[key]) is not bool for key in result if key != "matmul_precision"):
        raise ValueError("Require IEEE FP32 and explicit Boolean optimization switches")
    return result


def variant_identity(config):
    base, old_catalog = _base_identity(config)
    if base["training_source_sha256"] != BASE_SOURCE:
        raise RuntimeError("Reviewed v2 source dependency changed")
    catalog = json.loads(subprocess.check_output([pipeline_bench.DOTNET, str(BINARY), "catalog"], text=True, timeout=20))
    if catalog.get("observationSchema") != "shards-observation-v3" or catalog.get("cards") != old_catalog.get("cards"):
        raise RuntimeError("Variant schema/card ordering differs from reviewed migration")
    if len(catalog["cards"]) != 189:
        raise RuntimeError("Reserved zero-column migration requires the exact189-card catalog")
    sources = [HERE/name for name in ("variant_runtime.py", "variant_entry.py", "adoption.json",
                                      "rollout_fastpath.py", "queued_actor.py")]
    sources += sorted((HERE/"HostV3").glob("*.cs"))
    sources += sorted((HERE/"HostV3").glob("*.csproj"))
    sources += [HERE.parent/"run_campaign_with_audit.py", HERE.parent/"supervise_training.py"]
    helper_hash = train_campaign.file_hash(sources)
    identity = {**base, "schema": "shards-real-selfplay-v3", "observation_schema": "shards-observation-v3",
        "host_binary_sha256": hashlib.sha256(BINARY.read_bytes()).hexdigest(),
        "catalog_sha256": hashlib.sha256(json.dumps(catalog, sort_keys=True).encode()).hexdigest(),
        "training_source_sha256": hashlib.sha256((BASE_SOURCE+helper_hash).encode()).hexdigest(),
        "base_training_source_sha256": BASE_SOURCE,
        "execution": execution_profile(), "new_observation_slots": list(SLOTS)}
    return identity, catalog


class _HostProcessFactory:
    """Inject only the binary argument into the existing, tested Host transport."""
    def __getattr__(self, name):
        return getattr(_original_subprocess, name)

    def Popen(self, args, *positional, **kwargs):
        expected = str(pipeline_bench.ROOT/"Tools/TrainingPreflight/Host/bin/Release/net8.0/TrainingHost.dll")
        if not isinstance(args, list) or len(args) != 3 or args[0] != pipeline_bench.DOTNET or args[1] != expected or args[2] != "serve":
            raise RuntimeError("Unexpected Host process request in schema-v3 execution")
        return _original_subprocess.Popen([args[0], str(BINARY), args[2]], *positional, **kwargs)


class IeeeLearningPolicy(_base_policy):
    def __init__(self, *args, **kwargs):
        # The legacy trainer/evaluation entry sets TF32 before constructing its
        # first policy. Set the pinned mode here, before any graph is captured.
        torch.backends.fp32_precision = "ieee"
        torch.backends.cuda.matmul.fp32_precision = "ieee"
        super().__init__(*args, **kwargs)


def install():
    global _installed
    if _installed:
        return
    profile = execution_profile()
    pipeline_bench.subprocess = _HostProcessFactory()
    learning_model.LearningPolicy = IeeeLearningPolicy
    train_campaign.LearningPolicy = IeeeLearningPolicy
    train_campaign.identity = variant_identity
    if profile["fast_validation"]:
        adaptive_actor.LearningActor = FastValidationLearningActor
        learning_model.LearningActor = FastValidationLearningActor
        train_campaign.LearningActor = FastValidationLearningActor
    if profile["contiguous_store"]:
        train_campaign.EpisodeStore = ContiguousEpisodeStore
    if profile["queued_actors"]:
        train_campaign.collect_episodes = collect_queued
    _installed = True
