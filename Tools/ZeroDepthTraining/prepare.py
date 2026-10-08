"""Build and pin a new campaign without starting outcome learning."""
from __future__ import annotations

import argparse
from dataclasses import asdict
import json
from pathlib import Path
import subprocess
import sys

from evaluate import freeze_incumbent
from host import BINARY, DOTNET, HERE, catalog
from train import Rollout, TrainConfig, identity


def prepare(directory, config=None, *, build=True):
    directory = Path(directory)
    config = config or TrainConfig(hero_mode="balanced_random")
    config.validate()
    if (directory / "budget.json").exists() or (directory / "latest.soicp").exists():
        raise RuntimeError("Preparation must target an unused campaign directory")
    if build:
        subprocess.run([str(DOTNET), "build", str(HERE / "Host/ZeroDepthHost.csproj"), "-c", "Release", "-t:Rebuild", "--nologo"], check=True)
    subprocess.run([str(DOTNET), str(BINARY), "selftest"], check=True)
    directory.mkdir(parents=True, exist_ok=True)
    frozen = freeze_incumbent(directory / "incumbent")
    host_catalog = catalog()
    import torch
    if torch.cuda.is_available() and Rollout.estimate_bytes(config.capacity, host_catalog) > torch.cuda.mem_get_info()[0] * .7:
        raise RuntimeError("Prepared rollout exceeds this GPU's memory limit; reduce capacity or batch")
    pinned = identity(config, host_catalog)
    for name, value in (("config.json", asdict(config)), ("identity.json", pinned), ("catalog.json", host_catalog),
                        ("prepared.json", {"schema": "shards-zero-depth-prepared-v1", "training_started": False,
                         "lookahead_depth": 0, "incumbent_policy_sha256": frozen["files"]["shards-policy.bytes"]["sha256"],
                         "incumbent_tactical_search": True, "post_training_pairs": 2048})):
        destination = directory / name
        content = json.dumps(value, indent=2, sort_keys=True, allow_nan=False) + "\n"
        if destination.exists() and destination.read_text() != content:
            raise RuntimeError(f"Prepared campaign differs; use a fresh destination: {destination}")
        destination.write_text(content)
    return directory


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--config", type=Path)
    parser.add_argument("--no-build", action="store_true")
    args = parser.parse_args()
    config = TrainConfig(**json.loads(args.config.read_text())) if args.config else None
    print(prepare(args.output, config, build=not args.no_build))


if __name__ == "__main__":
    main()
