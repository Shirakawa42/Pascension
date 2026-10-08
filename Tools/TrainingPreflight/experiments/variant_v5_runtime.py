"""Explicit mixed-hero training population; frozen V4 learner and natural eval.

Importing installs nothing. Only TrainingCurriculumHost enables the 75/25
curriculum, before the first published observation. Evaluation always requests
natural setup and never receives pooled-statistics settings.
"""
import contextvars
import hashlib
import json
import os
from pathlib import Path
import subprocess

import pipeline_bench
import train_campaign
import variant_v4_runtime as v4

HERE = Path(__file__).resolve().parent
BINARY = HERE / "HostV5/bin/Release/net8.0/TrainingHostV5.dll"
BASE_V4_SOURCE = "3b8cf5b37bc032081d46c944b570ec64c3e231240f2e57ec82d8f3b43aad862c"
BASE_V4_HOST = "cdda3e733d416f5e32b7af93b232ef2de898be7adcfdc0a09e34a9e447ca6315"
PINNED_V5_HOST = "0b00ef87c842c320f368367be73349076902b74fd49355dcfd3824eccdfee797"
SCHEMA = "shards-real-selfplay-v5"
HERO_SETUP = {
    "schema": "shards-hero-curriculum-75-25-v1", "defaultMode": "natural",
    "trainingMode": "curriculum-75-25", "forcedProbability": .75,
    "orderedPairCount": 20, "heroOrder": ["decima", "tetra", "volos", "kosynwu", "rez"],
    "selection": "separate-seed-derived-splitmix64-rejection-v1",
    "setupPolicyActions": False, "adapterCapCountersIncludeSetup": True,
}
TRAINING_POPULATION = {
    "schema": HERO_SETUP["schema"], "mode": "curriculum-75-25",
    "forced_probability": .75, "natural_probability": .25,
    "forced_ordered_hero_pairs": 20, "setup_actions_in_ppo": False,
    "evaluation_mode": "natural", "setup_descriptor": HERO_SETUP,
}
_installed = False
_training_setup = contextvars.ContextVar("shards_v5_training_setup", default=False)


def observation_catalog(catalog):
    """V5 preserves the entire exact V4 catalog, including all metadata."""
    return dict(catalog)


def variant_identity(config):
    base, catalog = v4.variant_identity(config)
    if (base.get("schema") != "shards-real-selfplay-v4" or
            base.get("training_source_sha256") != BASE_V4_SOURCE or
            base.get("host_binary_sha256") != BASE_V4_HOST):
        raise RuntimeError("HostV5 requires the exact reviewed V4 source and binary")
    binary_hash = hashlib.sha256(BINARY.read_bytes()).hexdigest()
    if binary_hash != PINNED_V5_HOST:
        raise RuntimeError("HostV5 binary is not the reviewed mixed-hero candidate")
    new_catalog = json.loads(subprocess.check_output(
        [pipeline_bench.DOTNET, str(BINARY), "catalog"], text=True, timeout=20))
    descriptor = json.loads(subprocess.check_output(
        [pipeline_bench.DOTNET, str(BINARY), "hero-setup-descriptor"], text=True, timeout=20))
    if (descriptor != HERO_SETUP or new_catalog != catalog or
            catalog.get("observationSchema") != "shards-observation-v3"):
        raise RuntimeError("V5 must preserve the exact V4 catalog and reviewed separate setup descriptor")
    sources = [HERE / name for name in ("variant_v5_runtime.py", "variant_v5_entry.py",
               "migrate_runtime_v5.py", "evaluate_variants.py")]
    sources += sorted((HERE / "HostV5").glob("*.cs"))
    sources += sorted((HERE / "HostV5").glob("*.csproj"))
    extra = train_campaign.file_hash(sources)
    return {**base, "schema": SCHEMA, "host_binary_sha256": binary_hash,
        "training_source_sha256": hashlib.sha256((BASE_V4_SOURCE + extra).encode()).hexdigest(),
        "base_v4_training_source_sha256": BASE_V4_SOURCE,
        "training_population": dict(TRAINING_POPULATION)}, new_catalog


class _HostProcessFactory:
    def __getattr__(self, name):
        return getattr(v4.v3._original_subprocess, name)

    def Popen(self, args, *positional, **kwargs):
        expected = str(pipeline_bench.ROOT / "Tools/TrainingPreflight/Host/bin/Release/net8.0/TrainingHost.dll")
        if (not isinstance(args, list) or len(args) != 3 or args[0] != pipeline_bench.DOTNET or
                args[1] != expected or args[2] != "serve"):
            raise RuntimeError("Unexpected Host process request in V5 execution")
        env = dict(kwargs.get("env") or os.environ)
        for key in list(env):
            if key.startswith("SHARDS_STATS_"):
                del env[key]
        training = _training_setup.get()
        requested = v4._statistics_launch.get()
        if requested is not None:
            if not training:
                raise RuntimeError("Statistics requested outside the explicit V5 training context")
            env.update(SHARDS_STATS_DIRECTORY=requested["directory"], SHARDS_STATS_PURPOSE="training_pool",
                SHARDS_STATS_EXPECTED_SEED=str(requested["seed"]), SHARDS_STATS_EXPECTED_BATCH=str(requested["batch"]))
        kwargs["env"] = env
        mode = "curriculum-75-25" if training else "natural"
        return v4.v3._original_subprocess.Popen(
            [args[0], str(BINARY), "serve", "--hero-setup", mode], *positional, **kwargs)


class TrainingCurriculumHost(v4.TrainingStatisticsHost):
    def __init__(self, *args, **kwargs):
        token = _training_setup.set(True)
        try:
            super().__init__(*args, **kwargs)
        finally:
            _training_setup.reset(token)


def install(*, statistics_directory=None):
    global _installed
    if _installed:
        v4.install(statistics_directory=statistics_directory)
        return
    variant_identity(train_campaign.TrainConfig(adaptive_actors=True))
    v4.install(statistics_directory=statistics_directory)
    pipeline_bench.subprocess = _HostProcessFactory()
    train_campaign.identity = variant_identity
    train_campaign.LearningHost = TrainingCurriculumHost
    # learning_eval.LearningHost retains its original constructor; no curriculum
    # ContextVar is entered for inline or external evaluation.
    _installed = True
