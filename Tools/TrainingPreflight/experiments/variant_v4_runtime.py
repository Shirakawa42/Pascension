"""Independent process composition: frozen v3 learner plus exact HostV4 counts.

Observation schema, model, precision, optimizer and rollout semantics stay v3.
Only a separately built host and its explicit source identity change. Importing
this module installs nothing and initializes no CUDA context.
"""
import hashlib
import json
import contextvars
import os
from pathlib import Path
import subprocess

import pipeline_bench
import train_campaign
from learning_rollout import LearningHost as _BaseLearningHost
import variant_runtime as v3

HERE = Path(__file__).resolve().parent
BINARY = HERE / "HostV4/bin/Release/net8.0/TrainingHostV4.dll"
BASE_V3_SOURCE = "4d7b826d5d11b7ab825faf170115e4bbc75adf115250706244dcdb6ecb9f72f6"
BASE_V3_HOST = "472f94ef69e265b1ab03249413275ccc088063dfa5f0fc2d9b57e806025a8520"
PINNED_V4_HOST = "cdda3e733d416f5e32b7af93b232ef2de898be7adcfdc0a09e34a9e447ca6315"
SCHEMA = "shards-real-selfplay-v4"
_installed = False
_statistics_directory = None
_statistics_launch = contextvars.ContextVar("shards_v4_statistics_launch", default=None)


def variant_identity(config):
    base, catalog = v3.variant_identity(config)
    if (base.get("schema") != "shards-real-selfplay-v3" or
            base.get("training_source_sha256") != BASE_V3_SOURCE or
            base.get("host_binary_sha256") != BASE_V3_HOST):
        raise RuntimeError("HostV4 requires the exact reviewed v3 source and binary")
    binary_hash = hashlib.sha256(BINARY.read_bytes()).hexdigest()
    if binary_hash != PINNED_V4_HOST:
        raise RuntimeError("HostV4 binary differs from the independently verified candidate")
    new_catalog = json.loads(subprocess.check_output(
        [pipeline_bench.DOTNET, str(BINARY), "catalog"], text=True, timeout=20))
    if new_catalog != catalog or catalog.get("observationSchema") != "shards-observation-v3":
        raise RuntimeError("Counter optimization must preserve the complete v3 catalog/schema")
    # The frozen v3 source digest covers the learner, transport, shared engine,
    # v3 composition and execution profile. Include every new executable source
    # and the offline migration helpers that can publish this new identity.
    sources = [HERE / name for name in ("variant_v4_runtime.py", "variant_v4_entry.py",
               "migrate_runtime_v4.py", "migrate_checkpoint.py")]
    sources += sorted((HERE / "HostV4").glob("*.cs"))
    sources += sorted((HERE / "HostV4").glob("*.csproj"))
    extra = train_campaign.file_hash(sources)
    identity = {**base, "schema": SCHEMA, "host_binary_sha256": binary_hash,
        "training_source_sha256": hashlib.sha256((BASE_V3_SOURCE + extra).encode()).hexdigest(),
        "base_v3_training_source_sha256": BASE_V3_SOURCE}
    return identity, new_catalog


class _HostProcessFactory:
    """Accept only the frozen transport's exact base-host launch signature."""
    def __getattr__(self, name):
        return getattr(v3._original_subprocess, name)

    def Popen(self, args, *positional, **kwargs):
        expected = str(pipeline_bench.ROOT / "Tools/TrainingPreflight/Host/bin/Release/net8.0/TrainingHost.dll")
        if (not isinstance(args, list) or len(args) != 3 or args[0] != pipeline_bench.DOTNET or
                args[1] != expected or args[2] != "serve"):
            raise RuntimeError("Unexpected Host process request in v4 execution")
        # Evaluation Host constructors never enter this explicit training-only
        # context. Strip inherited statistics settings, even if an outer shell
        # or another process supplied them; no seed-namespace inference occurs.
        env = dict(kwargs.get("env") or os.environ)
        for key in list(env):
            if key.startswith("SHARDS_STATS_"):
                del env[key]
        requested = _statistics_launch.get()
        if requested is not None:
            env.update(SHARDS_STATS_DIRECTORY=requested["directory"], SHARDS_STATS_PURPOSE="training_pool",
                SHARDS_STATS_EXPECTED_SEED=str(requested["seed"]), SHARDS_STATS_EXPECTED_BATCH=str(requested["batch"]))
        kwargs["env"] = env
        return v3._original_subprocess.Popen([args[0], str(BINARY), args[2]], *positional, **kwargs)


class TrainingStatisticsHost(_BaseLearningHost):
    def __init__(self, *args, **kwargs):
        requested = None
        if _statistics_directory is not None:
            requested = {"directory": str(_statistics_directory),
                "batch": kwargs.get("batch", args[0] if args else None),
                "seed": kwargs.get("seed", args[2] if len(args) > 2 else 17)}
        token = _statistics_launch.set(requested)
        try:
            super().__init__(*args, **kwargs)
        finally:
            _statistics_launch.reset(token)


def install(*, statistics_directory=None):
    global _installed, _statistics_directory
    requested = Path(statistics_directory).resolve() if statistics_directory is not None else None
    if _installed:
        if requested is not None and requested != _statistics_directory:
            raise RuntimeError("V4 runtime already installed with different statistics routing")
        return
    # Validate all frozen dependencies before composing the tested v3 overrides.
    # The pinned v3 lineage includes adaptive_actor.py in its source identity.
    # Actual checkpoint configuration is verified separately by the trainer.
    variant_identity(train_campaign.TrainConfig(adaptive_actors=True))
    v3.install()
    _statistics_directory = requested
    pipeline_bench.subprocess = _HostProcessFactory()
    train_campaign.identity = variant_identity
    train_campaign.LearningHost = TrainingStatisticsHost
    _installed = True
