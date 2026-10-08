"""Frozen incumbent evaluation and conservative paired strength reporting.

Every engine seed is played twice with learner seats swapped. Capped games are
unknown outcomes, not draws. The strength gate uses the complete predeclared
set of seed pairs, so inspecting intermediate results cannot promote a model.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import math
import os
import shutil
import tempfile
import time
from dataclasses import asdict, dataclass
from datetime import datetime, timezone
from pathlib import Path
from typing import Iterable


REPO_ROOT = Path(__file__).resolve().parents[2]
BUNDLE_SCHEMA = "shards-zero-depth-incumbent-v1"
ARTIFACT_NAMES = (
    "shards-policy.bytes", "shards-search-settings.json",
    "shards-policy-metadata.json", "shards-inference.json",
)
HELD_OUT_SEED_BASE = 1 << 63


def sha256_file(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1 << 20), b""):
            digest.update(chunk)
    return digest.hexdigest()


def runtime_sources(repo_root: Path) -> list[Path]:
    sources = []
    for directory in ("Assets/Scripts/Core", "Assets/Scripts/Shards/Engine",
                      "Assets/Scripts/Shards/Content", "Assets/Scripts/Shards/AI"):
        sources.extend((repo_root / directory).rglob("*.cs"))
    sources.append(repo_root / "Assets/Scripts/Game/Soi/SoiSoloMatch.cs")
    return sorted(sources)


def verify_bundle(directory: Path, *, repo_root: Path | None = None) -> dict:
    directory = Path(directory)
    manifest = json.loads((directory / "manifest.json").read_text(encoding="utf-8"))
    if manifest.get("schema") != BUNDLE_SCHEMA:
        raise ValueError("Unsupported incumbent bundle schema")
    for name in ARTIFACT_NAMES:
        path = directory / name
        record = manifest["files"][name]
        if path.stat().st_size != record["bytes"] or sha256_file(path) != record["sha256"]:
            raise ValueError(f"Frozen incumbent artifact changed: {name}")
    metadata = json.loads((directory / "shards-policy-metadata.json").read_text(encoding="utf-8"))
    inference = json.loads((directory / "shards-inference.json").read_text(encoding="utf-8"))
    for metadata_hash, artifact in ((metadata.get("policy_sha256"), "shards-policy.bytes"),
                                   (inference.get("settings_sha256"), "shards-search-settings.json")):
        if metadata_hash is not None and metadata_hash != manifest["files"][artifact]["sha256"]:
            raise ValueError(f"Packaged metadata does not match {artifact}")
    if manifest.get("tactical_search") is not True:
        raise ValueError("Incumbent evaluation requires its deployed tactical search")
    if repo_root is not None:
        actual_paths = {str(p.relative_to(repo_root)).replace(os.sep, "/")
                        for p in runtime_sources(repo_root)}
        expected = manifest["runtime_sources"]
        if actual_paths != set(expected):
            raise ValueError("Production runtime source inventory changed after incumbent freeze")
        for relative, expected_hash in expected.items():
            if sha256_file(repo_root / relative) != expected_hash:
                raise ValueError(f"Production runtime source changed after incumbent freeze: {relative}")
    return manifest


def freeze_incumbent(directory: Path, *, repo_root: Path = REPO_ROOT) -> dict:
    """Make an immutable copy, or verify an existing copy without replacing it."""
    directory = Path(directory)
    if directory.exists():
        return verify_bundle(directory, repo_root=repo_root)
    directory.parent.mkdir(parents=True, exist_ok=True)
    temporary = Path(tempfile.mkdtemp(prefix=f".{directory.name}-", dir=directory.parent))
    try:
        files = {}
        for name in ARTIFACT_NAMES:
            source = repo_root / "Assets/Resources/AI" / name
            target = temporary / name
            shutil.copyfile(source, target)
            files[name] = {"sha256": sha256_file(target), "bytes": target.stat().st_size}
        manifest = {
            "schema": BUNDLE_SCHEMA,
            "created_utc": datetime.now(timezone.utc).isoformat(),
            "files": files,
            "runtime_sources": {
                str(path.relative_to(repo_root)).replace(os.sep, "/"): sha256_file(path)
                for path in runtime_sources(repo_root)
            },
            "engine": "unmodified production Shards.AI.PolicyEngine",
            "tactical_search": True,
            "settings": json.loads((temporary / "shards-search-settings.json").read_text(encoding="utf-8")),
            "game_configuration": "ShardsDlc.Duel, two players, normal hero draft",
        }
        (temporary / "manifest.json").write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8")
        verify_bundle(temporary, repo_root=repo_root)
        # Rename publishes only a fully verified bundle. An existing destination
        # remains authoritative, including when two evaluations start together.
        temporary.rename(directory)
        return manifest
    finally:
        if temporary.exists():
            shutil.rmtree(temporary)


@dataclass(frozen=True)
class GameResult:
    seed: int
    learner_seat: int
    winner: int | None  # None is an unresolved capped game; -1 is a terminal draw.
    censored: bool = False

    def score_bounds(self) -> tuple[float, float]:
        if self.learner_seat not in (0, 1):
            raise ValueError("Learner seat must be 0 or 1")
        if not 0 <= self.seed < 1 << 64:
            raise ValueError("Engine seed must fit uint64")
        if self.censored:
            if self.winner is not None:
                raise ValueError("Censored games cannot have a recorded winner")
            return 0.0, 1.0
        if self.winner not in (-1, 0, 1):
            raise ValueError("Resolved game must record winner -1, 0, or 1")
        score = 0.5 if self.winner == -1 else float(self.winner == self.learner_seat)
        return score, score


def summarize_pairs(results: Iterable[GameResult], *, planned_pairs: int, alpha: float = 0.05) -> dict:
    if planned_pairs < 1 or not 0 < alpha < 1:
        raise ValueError("Positive planned pair count and alpha between zero and one required")
    pairs: dict[int, dict[int, tuple[float, float]]] = {}
    records = list(results)
    wins = losses = draws = censored = 0
    seats = [{"wins": 0, "losses": 0, "draws": 0, "censored": 0} for _ in range(2)]
    for game in records:
        bounds = game.score_bounds()
        pair = pairs.setdefault(game.seed, {})
        if game.learner_seat in pair:
            raise ValueError("Duplicate engine seed and learner seat")
        pair[game.learner_seat] = bounds
        if game.censored:
            censored += 1
            seats[game.learner_seat]["censored"] += 1
        elif game.winner == -1:
            draws += 1
            seats[game.learner_seat]["draws"] += 1
        elif game.winner == game.learner_seat:
            wins += 1
            seats[game.learner_seat]["wins"] += 1
        else:
            losses += 1
            seats[game.learner_seat]["losses"] += 1
    if len(pairs) > planned_pairs or len(records) > 2 * planned_pairs:
        raise ValueError("More games than the predeclared evaluation plan")
    complete_pairs = sum(len(pair) == 2 for pair in pairs.values())
    # Missing games are unknown outcomes too. Fixed planned_pairs is essential:
    # stopping after a lucky prefix never increases the lower confidence bound.
    lower_sum = sum(bound[0] for pair in pairs.values() for bound in pair.values())
    upper_sum = sum(bound[1] for pair in pairs.values() for bound in pair.values())
    missing = 2 * planned_pairs - len(records)
    lower_mean = lower_sum / (2 * planned_pairs)
    upper_mean = (upper_sum + missing) / (2 * planned_pairs)
    # Each seat-swapped seed pair averages to a bounded [0, 1] sample.
    # Two Hoeffding tails with alpha/2 each cover both censor-bound endpoints.
    radius = math.sqrt(math.log(2 / alpha) / (2 * planned_pairs))
    lower, upper = max(0.0, lower_mean - radius), min(1.0, upper_mean + radius)
    finished = complete_pairs == planned_pairs and len(records) == 2 * planned_pairs
    resolved = wins + losses + draws
    stronger = finished and lower > 0.5
    return {
        "planned_pairs": planned_pairs, "recorded_games": len(records),
        "complete_pairs": complete_pairs, "evaluation_finished": finished,
        "wins": wins, "losses": losses, "draws": draws, "censored": censored,
        "missing_games": missing, "by_learner_seat": seats,
        "resolved_game_score": (wins + 0.5 * draws) / resolved if resolved else None,
        "score_bounds_including_censors": [lower_mean, upper_mean],
        "confidence": 1 - alpha,
        "paired_hoeffding_score_interval": [lower, upper],
        "statistical_unit": "independent engine-seed pairs; both seats averaged per seed",
        "interval_assumptions": "Independent seed pairs, one fixed checkpoint and one predeclared evaluation; no repeated selection using this gate",
        "stronger_than_current": stronger,
        "verdict": "stronger" if stronger else ("incomplete" if not finished else "not_demonstrated"),
        "gate": "Finished predeclared evaluation and censor-conservative confidence lower bound above 0.5",
    }


def save_report(path: Path, report: dict) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    temporary = path.with_name(path.name + ".tmp")
    temporary.write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
    temporary.replace(path)


def run_evaluation(checkpoint: Path, *, bundle_directory: Path, output: Path,
                   pairs: int = 500, batch: int = 16, workers: int = 8,
                   seed_base: int = HELD_OUT_SEED_BASE, policy_seed: int = 716203,
                   device: str = "cuda", graph: bool = True, alpha: float = 0.05,
                   binary: Path | None = None, timeout: float = 60,
                   progress_seconds: float = 30, validation_only: bool = False) -> dict:
    """Evaluate one fixed checkpoint on a predeclared held-out seat-swapped set."""
    import numpy as np
    import torch
    from host import BINARY, Host, catalog
    from model import Actor, load_checkpoint

    torch.set_num_threads(1)
    torch.backends.fp32_precision = "ieee"
    torch.backends.cuda.matmul.fp32_precision = "ieee"

    checkpoint, output = Path(checkpoint), Path(output)
    binary = Path(binary or BINARY)
    if pairs < 1 or batch < 2 or batch % 2:
        raise ValueError("Evaluation needs a positive pair count and an even batch of at least two")
    if validation_only and pairs > 2:
        raise ValueError("Pipeline validation is limited to one or two seed pairs")
    if not 0 < alpha < 1:
        raise ValueError("Alpha must be between zero and one")
    if not HELD_OUT_SEED_BASE <= seed_base or seed_base + pairs > 1 << 64:
        raise ValueError("Evaluation seeds must use the reserved uint64 high-bit namespace")
    if output.exists() or output.with_suffix(".plan.json").exists():
        raise FileExistsError("Evaluation output already exists; each fixed evaluation has a separate predeclared report")
    manifest = verify_bundle(bundle_directory, repo_root=REPO_ROOT)
    incumbent_manifest_hash = sha256_file(Path(bundle_directory) / "manifest.json")
    policy, payload = load_checkpoint(checkpoint, device=device)
    current_catalog = catalog(binary)
    canonical = lambda value: json.dumps(value, sort_keys=True, separators=(",", ":"))
    if canonical(policy.catalog) != canonical(current_catalog):
        raise ValueError("Checkpoint observation/card/action catalog differs from the evaluation host")
    identity = payload["identity"]
    if identity.get("schema") != "shards-zero-depth-training-v1":
        raise ValueError("Evaluation requires a zero-depth training checkpoint")
    from train import source_fingerprint
    if source_fingerprint() != identity["source_fingerprint"]:
        raise ValueError("Training runtime sources changed after checkpoint creation")
    binary_hash = sha256_file(binary)
    if binary_hash != identity["host_sha256"]:
        raise ValueError("Evaluation host differs from the pinned training binary")
    training_start = int(identity["configuration"]["engine_seed"])
    training_next = int(payload["state"]["next_engine_seed"])
    if not 0 <= training_start < HELD_OUT_SEED_BASE or not training_start <= training_next < HELD_OUT_SEED_BASE:
        raise ValueError("Training seeds overlap the reserved held-out evaluation namespace")
    compiled = identity["configuration"].get("compiled_actor", False)
    packed = identity["configuration"].get("packed_inputs", False)
    plan = {
        "schema": "shards-zero-depth-evaluation-v1",
        "purpose": "pipeline_validation" if validation_only else "final_strength_evaluation",
        "checkpoint": str(checkpoint.resolve()), "checkpoint_sha256": sha256_file(checkpoint),
        "checkpoint_identity": identity,
        "observation_schema": current_catalog["observation_schema"],
        "observation_limitations": current_catalog["limitations"],
        "host_binary_sha256": binary_hash,
        "incumbent_bundle": str(Path(bundle_directory).resolve()),
        "incumbent_manifest_sha256": incumbent_manifest_hash,
        "incumbent_files": manifest["files"],
        "incumbent_tactical_search": True, "learner_search_depth": 0,
        "pairs": pairs, "games": pairs * 2, "batch": batch, "workers": workers,
        "seed_base": seed_base, "policy_seed": policy_seed,
        "paired_assignment": "engine_seed=seed_base+pair; learner_seat=lane%2",
        "held_out_namespace": "Training uses uint64 seeds below 2^63; evaluation sets the high bit",
        "alpha": alpha, "device": device, "cuda_graph": graph, "precision": "ieee-fp32",
        "compiled_actor": compiled, "packed_inputs": packed,
        "created_utc": datetime.now(timezone.utc).isoformat(),
    }
    save_report(output.with_suffix(".plan.json"), plan)
    torch.manual_seed(policy_seed)
    results: list[GameResult] = []
    started = time.monotonic()
    last_progress = started
    actor_cache = {}

    def report(error: str | None = None):
        summary = summarize_pairs(results, planned_pairs=pairs, alpha=alpha)
        if validation_only:
            summary["stronger_than_current"] = False
            summary["strength_evidence"] = False
            summary["verdict"] = "pipeline_validated" if summary["evaluation_finished"] else "validation_incomplete"
        value = {
            **plan, "elapsed_seconds": time.monotonic() - started,
            "summary": summary,
            "results": [asdict(result) for result in results],
        }
        if error is not None:
            value["error"] = error
        save_report(output, value)
        return value

    report()
    try:
        for first_pair in range(0, pairs, batch // 2):
            lanes = 2 * min(batch // 2, pairs - first_pair)
            actor = actor_cache.setdefault(lanes, None)
            if actor is None:
                actor = actor_cache[lanes] = Actor(policy, lanes, graph=graph, device=device,
                                                  compiled=compiled, packed=packed)
            with Host(binary=binary, batch=lanes, workers=workers,
                      seed=seed_base + first_pair, automation=True,
                      opponent_bundle=bundle_directory, paired=True, timeout=timeout) as host:
                seen = np.zeros(lanes, dtype=bool)
                while not seen.all():
                    new_done = np.flatnonzero((host.done != 0) & ~seen)
                    for lane in new_done:
                        capped = int(host.done[lane]) == 2
                        rewards = host.rewards[lane]
                        if capped:
                            winner = None
                        elif np.array_equal(rewards, np.zeros(2)):
                            winner = -1
                        elif np.array_equal(rewards, np.array([1, -1])):
                            winner = 0
                        elif np.array_equal(rewards, np.array([-1, 1])):
                            winner = 1
                        else:
                            raise RuntimeError(f"Invalid terminal rewards: {rewards.tolist()}")
                        results.append(GameResult(seed_base + first_pair + int(lane) // 2,
                                                  int(lane) % 2, winner, capped))
                        seen[lane] = True
                    now = time.monotonic()
                    if new_done.size or now - last_progress >= progress_seconds:
                        value = report()
                        if now - last_progress >= progress_seconds:
                            print(json.dumps({"evaluation_progress": True,
                                              "recorded_games": len(results), "planned_games": pairs * 2,
                                              "elapsed_seconds": value["elapsed_seconds"]}), flush=True)
                            last_progress = now
                    if seen.all():
                        break
                    live = ~seen
                    if not np.all(host.actors[live] == np.arange(lanes)[live] % 2):
                        raise RuntimeError("Evaluation host returned an incumbent decision to the learner")
                    if not host.mask[live].any(axis=1).all():
                        raise RuntimeError("Live learner game has no legal candidate")
                    actions, _ = actor.act(host)
                    actions[seen] = -1
                    host.advance(actions)
        return report()
    except BaseException as error:
        report(f"{type(error).__name__}: {error}")
        raise


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    commands = parser.add_subparsers(dest="command", required=True)
    freeze = commands.add_parser("freeze", help="Freeze current packaged policy, metadata and deployed search profile")
    freeze.add_argument("--output", type=Path, required=True)
    summarize = commands.add_parser("summarize", help="Score recorded held-out paired games")
    summarize.add_argument("--results", type=Path, required=True)
    summarize.add_argument("--pairs", type=int, required=True)
    summarize.add_argument("--alpha", type=float, default=0.05)
    summarize.add_argument("--output", type=Path)
    run = commands.add_parser("run", help="Run the final fixed-checkpoint challenge against the frozen current AI")
    run.add_argument("--checkpoint", type=Path, required=True)
    run.add_argument("--incumbent", type=Path, required=True)
    run.add_argument("--output", type=Path, required=True)
    run.add_argument("--pairs", type=int, default=500)
    run.add_argument("--batch", type=int, default=16)
    run.add_argument("--workers", type=int, default=8)
    run.add_argument("--seed-base", type=int, default=HELD_OUT_SEED_BASE)
    run.add_argument("--policy-seed", type=int, default=716203)
    run.add_argument("--device", default="cuda")
    run.add_argument("--no-graph", action="store_true")
    run.add_argument("--alpha", type=float, default=0.05)
    run.add_argument("--binary", type=Path)
    run.add_argument("--timeout", type=float, default=60)
    run.add_argument("--validation-only", action="store_true")
    args = parser.parse_args()
    if args.command == "freeze":
        manifest = freeze_incumbent(args.output)
        print(json.dumps({"bundle": str(args.output), "files": manifest["files"], "tactical_search": True}, indent=2))
    elif args.command == "summarize":
        values = json.loads(args.results.read_text(encoding="utf-8"))
        if isinstance(values, dict):
            values = values["results"]
        results = [GameResult(**value) for value in values]
        report = summarize_pairs(results, planned_pairs=args.pairs, alpha=args.alpha)
        if args.output:
            save_report(args.output, report)
        print(json.dumps(report, indent=2))
    else:
        report = run_evaluation(args.checkpoint, bundle_directory=args.incumbent, output=args.output,
                                pairs=args.pairs, batch=args.batch, workers=args.workers,
                                seed_base=args.seed_base, policy_seed=args.policy_seed,
                                device=args.device, graph=not args.no_graph, alpha=args.alpha,
                                binary=args.binary, timeout=args.timeout, validation_only=args.validation_only)
        print(json.dumps(report["summary"], indent=2))


if __name__ == "__main__":
    main()
