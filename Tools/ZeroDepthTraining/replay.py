"""Replay a saved action trace on CPU without inference or learning."""
from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path

import numpy as np

from host import BINARY, Host


def replay(path, *, binary=BINARY, allow_drift=False, max_steps=None):
    path, binary = Path(path), Path(binary)
    with np.load(path, allow_pickle=False) as archive:
        actions = archive["actions"].copy()
        if "metadata" in archive:
            metadata = json.loads(str(archive["metadata"].item()))
            seed = metadata["cohort_seed"]
        else:
            metadata = json.loads(path.with_suffix(".json").read_text())
            seed = int(archive["engine_seed"][0])
    if actions.ndim != 2 or actions.dtype.kind != "i" or not len(actions):
        raise ValueError("Expected a nonempty signed integer action matrix")
    if metadata["batch"] != actions.shape[1]:
        raise ValueError("Trace batch differs from its metadata")
    binary_hash = hashlib.sha256(binary.read_bytes()).hexdigest()
    if not allow_drift and binary_hash != metadata["host_binary_sha256"]:
        raise RuntimeError("Host binary changed; --allow-drift is required to validate a fix")
    if max_steps is not None and max_steps < 1:
        raise ValueError("max_steps must be positive")
    steps = min(len(actions), max_steps) if max_steps is not None else len(actions)
    with Host(actions.shape[1], metadata["workers"], seed, binary=binary,
              automation=metadata["automation"] == "singleton",
              hero_mode=metadata.get("hero_mode", "policy")) as host:
        catalog_hash = hashlib.sha256(json.dumps(host.catalog, sort_keys=True, separators=(",", ":")).encode()).hexdigest()
        if not allow_drift and catalog_hash != metadata["catalog_sha256"]:
            raise RuntimeError("Observation catalog changed; --allow-drift is required to validate a fix")
        for index in range(steps):
            try:
                host.advance(actions[index])
            except Exception as error:
                raise RuntimeError(f"Replay failed at vector {index + 1}/{steps}: {error}") from error
        return {"trace": str(path.resolve()), "seed": seed, "vectors_replayed": steps,
                "recorded_vectors": len(actions), "completed": int((host.done == 1).sum()),
                "censored": int((host.done == 2).sum()), "unresolved": int((host.done == 0).sum()),
                "host_binary_sha256": binary_hash, "catalog_sha256": catalog_hash,
                "original_binary_matched": binary_hash == metadata["host_binary_sha256"],
                "hero_mode": metadata.get("hero_mode", "policy"), "learning_updates": 0}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("trace", type=Path)
    parser.add_argument("--binary", type=Path, default=BINARY)
    parser.add_argument("--allow-drift", action="store_true", help="Explicitly replay against changed code to validate a fix")
    parser.add_argument("--max-steps", type=int)
    args = parser.parse_args()
    print(json.dumps(replay(args.trace, binary=args.binary, allow_drift=args.allow_drift,
                            max_steps=args.max_steps), indent=2))


if __name__ == "__main__":
    main()
