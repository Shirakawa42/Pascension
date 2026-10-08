"""Strict frozen V4/V5 comparison on natural or balanced hero populations.

No optimizer or campaign ledger is opened. The same explicit natural HostV5
serves both policies. Balanced mode imposes two normal legal draft choices,
then evaluates the frozen policies; those choices are outside policy sampling.
CPU/GPU results use different numeric kernels and sampling streams.
"""
from __future__ import annotations

import argparse
import hashlib
import itertools
import json
import math
import os
from pathlib import Path
import shutil
import sys
import tempfile
import time
from unittest.mock import patch

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE.parent))
import numpy as np
import torch
import evaluate_checkpoints as checkpoints
import learning_eval
import learning_model
from bench_common import save_json
from learning_rollout import LearningHost
from train_campaign import TrainConfig
import variant_v4_runtime as v4
import variant_v5_runtime as v5

# Keep the complete GPU evaluation allocation inside this identity-hashed file.
# CPU diagnostic helpers are independently fingerprinted in reports; changing
# a monitor helper must not silently change this predeclared matrix.
HEROES = ("decima", "tetra", "volos", "kosynwu", "rez")
PRIMARY_SEED, NATURAL_SEED = 0x6300000000000000, 0x6400000000000000


def evaluation_plan(*, pairs_per_cell=100, seed=PRIMARY_SEED, sampling_seed=98311):
    if type(pairs_per_cell) is not int or not 1 <= pairs_per_cell <= 100000:
        raise ValueError("Require paired seeds per cell1..100000")
    if type(seed) is not int or not 0 <= seed <= 2**64-20*pairs_per_cell:
        raise ValueError("Disjoint matrix seed blocks must fit uint64")
    if type(sampling_seed) is not int or not 0 <= sampling_seed < 2**63-20:
        raise ValueError("Sampling seed must leave room for20 cells")
    return {"schema": "shards-balanced-hero-plan-v1", "planned_games": 40*pairs_per_cell,
        "paired_seeds_per_cell": pairs_per_cell, "ordered_hero_pairings": 20,
        "policy_seat_assignments_per_pair": 2, "disjoint_seed_blocks": True,
        "cells": [{"policy_a_hero": a, "policy_b_hero": b,
            "seed_start": seed+index*pairs_per_cell, "paired_seed_count": pairs_per_cell,
            "sampling_seed": sampling_seed+index} for index, (a, b) in enumerate(itertools.permutations(HEROES, 2))],
        "intervention": {"kind": "forced_legal_initial_hero_draft", "setup_actions_per_game": 2,
            "subsequent_behavior": "sample actual frozen policy", "training_data": False},
        "scope": "Equal-weight ordered hero-pair competence/counterplay; not unmodified natural-draft policy strength or optimal hero balance"}


def helper_hashes(device, mode):
    files = [Path(__file__), HERE.parent / "evaluate_checkpoints.py", HERE.parent / "learning_eval.py", HERE.parent / "learning_model.py",
             HERE.parent / "adaptive_actor.py"]
    if device == "cpu":
        files += [HERE / "cpu_shadow_eval.py", HERE / "balance_telemetry.py"]
        if mode == "balanced":
            files += [HERE / "hero_coverage_eval.py", HERE / "hero_curriculum/balanced_evaluation.py"]
    return {str(path.relative_to(HERE.parent)): hashlib.sha256(path.read_bytes()).hexdigest() for path in files}

_base_load_selection = checkpoints.load_selection
_natural_host = LearningHost


def load_selection(checkpoint, role="learner"):
    path = Path(checkpoint).resolve()
    saved = json.loads((path.parent / "identity.json").read_text())
    version = saved.get("schema")
    if version == v4.SCHEMA:
        resolver = v4.variant_identity
    elif version == v5.SCHEMA:
        resolver = v5.variant_identity
    else:
        raise ValueError("Cross-variant evaluation accepts only pinned V4/V5 checkpoints")
    # Existing loader checks the complete source-specific identity and payload.
    # The temporary override is scoped to this synchronous CPU loading call.
    with patch.object(checkpoints, "identity", resolver):
        result = _base_load_selection(path, role)
    original = result.catalog
    result.catalog = v5.observation_catalog(original)
    result.metadata.update(training_variant=version, training_catalog=original,
        evaluation_setup="natural HostV5; optional explicit balanced draft intervention",
        observation_catalog_comparison="Full V4/V5 catalogs must match exactly; setup descriptor is separate")
    return result


def frozen_selections(path_a, role_a, path_b, role_b):
    paths = [Path(path).expanduser().resolve() for path in (path_a, path_b)]
    with tempfile.TemporaryDirectory(prefix="shards-variant-evaluation-") as directory:
        copies = {}
        for original in dict.fromkeys(paths):
            target = Path(directory) / str(len(copies)) / "snapshot.soicp"
            target.parent.mkdir()
            with original.open("rb") as source, target.open("wb") as destination:
                shutil.copyfileobj(source, destination)
            shutil.copyfile(original.parent / "identity.json", target.parent / "identity.json")
            copies[original] = target
        results = []
        for original, role in zip(paths, (role_a, role_b)):
            selected = load_selection(copies[original], role)
            selected.metadata.update(checkpoint=str(original), run_dir=str(original.parent),
                snapshot_scope="One immutable copy per distinct resolved input path")
            results.append(selected)
    if results[0].catalog != results[1].catalog:
        raise ValueError("Observation or card catalogs differ")
    if results[0].metadata["run_identity"]["rules_sha256"] != results[1].metadata["run_identity"]["rules_sha256"]:
        raise ValueError("Rule sources differ")
    return results


def _context_bytes(value):
    hashed = 2166136261
    for character in value:
        hashed = ((hashed ^ ord(character)) * 16777619) & 0xffffffff
    return np.asarray([((hashed >> (8*i)) & 255) / 255 for i in range(4)], np.float32)


def force_initial_draft(host, heroes_by_seat):
    """GPU-safe helper: only NumPy host arrays and exact legal draft actions."""
    if len(heroes_by_seat) != 2 or len(set(heroes_by_seat)) != 2 or any(h not in HEROES for h in heroes_by_seat):
        raise ValueError("Require two distinct reviewed heroes")
    for seat in (1, 0):
        if (np.any(host.done) or np.any(host.seats != seat) or np.any(host.obs[:, 4] != 1) or
                np.any(host.obs[:, 112:116] != _context_bytes("soi.herodraft")) or np.any(host.obs[:, 22] != 0)):
            raise RuntimeError("Balanced intervention requires the unmodified initial hero draft")
        code = np.float32((HEROES.index(heroes_by_seat[seat]) + 1) / 5)
        matches = (host.mask == 1) & (host.candidates[..., :16].argmax(-1) == 12) & (host.candidates[..., 31] == code)
        if np.any(matches.sum(-1) != 1):
            raise RuntimeError("Requested hero lacks exactly one legal original action slot")
        host.advance_active(matches.argmax(-1), np.ones(host.batch, bool))
        if np.any(host.done):
            raise RuntimeError("Initial draft unexpectedly terminated")
    expected = [np.float32((HEROES.index(hero) + 1) / 5) for hero in heroes_by_seat]
    if (np.any(host.seats != 0) or np.any(host.obs[:, 112:116] != _context_bytes("")) or
            np.any(host.obs[:, 22] != expected[0]) or np.any(host.obs[:, 70] != expected[1])):
        raise RuntimeError("Actual engine hero assignments differ from the balanced plan")


def run_balanced_cuda(policy_a, policy_b, *, pairs_per_cell=100, seed=PRIMARY_SEED,
                      sampling_seed=98311, batch=128, workers=4, max_seconds=3600,
                      telemetry=False, host_factory=None, evaluate_fn=None, debug_dir=None):
    plan = evaluation_plan(pairs_per_cell=pairs_per_cell, seed=seed, sampling_seed=sampling_seed)
    if not math.isfinite(max_seconds) or not 0 < max_seconds <= 7200:
        raise ValueError("Require max_seconds in (0,7200]")
    factory = _natural_host if host_factory is None else host_factory
    evaluate = learning_eval.evaluate_match if evaluate_fn is None else evaluate_fn
    started = time.monotonic()
    cells, scores = [], []
    censored = 0
    for specification in plan["cells"]:
        if time.monotonic() - started >= max_seconds:
            break
        launches = 0
        hero_a, hero_b = specification["policy_a_hero"], specification["policy_b_hero"]
        def build(*args, **kwargs):
            nonlocal launches
            seat_a = launches % 2
            launches += 1
            host = factory(*args, **kwargs)
            try:
                force_initial_draft(host, (hero_a, hero_b) if seat_a == 0 else (hero_b, hero_a))
                return host
            except BaseException:
                host.close()
                raise
        torch.manual_seed(specification["sampling_seed"])
        cell_debug = prepare_cell_replay(debug_dir, specification)
        with patch.object(learning_eval, "LearningHost", build):
            cell = evaluate(policy_a, policy_b, games=2*pairs_per_cell,
                seed=specification["seed_start"], batch=batch, workers=workers, telemetry=telemetry,
                censor_truncated=True, stop_check=lambda: time.monotonic()-started >= max_seconds,
                debug_dir=cell_debug)
        cell.update(policy_a_hero=hero_a, policy_b_hero=hero_b, planned_configuration=specification,
            draft_policy_actions_excluded=True,
            length_scope="Policy episode lengths omit two imposed draft actions; wire engine counters include them")
        if cell["complete"]:
            if cell["games"] != 2*pairs_per_cell or launches != 2*math.ceil(pairs_per_cell / batch):
                raise RuntimeError("Balanced GPU cell did not execute the exact paired allocation")
            censored += cell["censored_games"]
            scores.extend([1.] * cell["wins_a"] + [.5] * cell["draws"] + [0.] * cell["losses_a"])
            low, high = cell["score_identification_interval"]
            margin = math.sqrt(math.log(40*20)/(2*pairs_per_cell))
            cell["simultaneous_score_bound_95"] = [max(0., low-margin), min(1., high+margin)]
        cells.append(cell)
        if not cell["complete"]:
            break
    complete = len(cells) == 20 and all(cell["complete"] for cell in cells)
    result = {"schema": "shards-balanced-hero-evaluation-v1", "complete": complete,
        "plan": plan, "intervention": plan["intervention"], "cells": cells,
        "seconds": time.monotonic()-started, "scope": plan["scope"],
        "bound_scope": "Equal-allocation disjoint paired seeds; simultaneous cell bounds use a union bound across20 cells; caps unknown"}
    if complete:
        result.update(games=plan["planned_games"], **learning_eval.score_summary(scores, plan["planned_games"], censored))
        result["balanced_macro_score"] = result["score_a"]
    else:
        result["reason"] = "cooperative_deadline_or_incomplete_cell"
    return result


def prepare_cell_replay(directory, specification):
    if directory is None:
        return None
    target = Path(directory) / ("cell-" + str(specification["seed_start"]))
    save_json(target / "setup.json", {
        "schema": "shards-balanced-cell-replay-v1", "host_binary_sha256": v5.PINNED_V5_HOST,
        "host_setup": "natural", "planned_configuration": specification,
        "prefix": "Force actual initial legal hero choices in seat1 then seat0 order. Policy A/B heroes are in planned_configuration; derive heroes_by_seat from each trace policy_a_seat.",
        "suffix": "After both draft submissions, replay trace actions exactly. Trace length excludes the two imposed choices.",
        "cap_handling": "Unknown outcome; never a fabricated draw"})
    return target


def run_balanced_cpu(policy_a, policy_b, *, pairs_per_cell=100, seed=PRIMARY_SEED,
                     sampling_seed=98311, batch=16, max_seconds=3600, debug_dir=None):
    import cpu_shadow_eval as shadow
    from hero_curriculum.balanced_evaluation import run_balanced_matrix, evaluation_plan as cpu_plan
    plan = evaluation_plan(pairs_per_cell=pairs_per_cell, seed=seed, sampling_seed=sampling_seed)
    if cpu_plan(pairs_per_cell=pairs_per_cell, seed=seed, sampling_seed=sampling_seed) != plan:
        raise RuntimeError("CPU balanced helper changed the identity-pinned evaluation plan")
    specifications = {cell["seed_start"]: cell for cell in plan["cells"]}
    original = shadow.run_cpu_match
    def run_with_replay(*args, **kwargs):
        specification = specifications[kwargs["seed"]]
        kwargs["debug_dir"] = prepare_cell_replay(debug_dir, specification)
        return original(*args, **kwargs)
    with patch.object(shadow, "run_cpu_match", run_with_replay):
        return run_balanced_matrix(policy_a, policy_b, pairs_per_cell=pairs_per_cell, seed=seed,
            sampling_seed=sampling_seed, batch=batch, max_seconds=max_seconds, host_factory=_natural_host)


def main(*, runtime_installed=False):
    parser = argparse.ArgumentParser(description=__doc__)
    for side in ("a", "b"):
        parser.add_argument("--"+side, type=Path, required=True)
        parser.add_argument("--"+side+"-role", choices=checkpoints.ROLES, default="learner")
    parser.add_argument("--mode", choices=("natural", "balanced"), default="natural")
    parser.add_argument("--device", choices=("cuda", "cpu"), default="cuda")
    parser.add_argument("--games", type=int, default=4096)
    parser.add_argument("--pairs-per-cell", type=int, default=100)
    parser.add_argument("--batch", type=int, default=128)
    parser.add_argument("--workers", type=int, default=4)
    parser.add_argument("--seed", type=lambda x: int(x, 0))
    parser.add_argument("--sampling-seed", type=lambda x: int(x, 0))
    parser.add_argument("--max-seconds", type=float, default=3600)
    parser.add_argument("--telemetry", action="store_true")
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    if (not 2 <= args.games <= 1000000 or args.games % 2 or not 1 <= args.batch <= 128 or
            not 1 <= args.workers <= 16 or not math.isfinite(args.max_seconds) or not 0 < args.max_seconds <= 7200):
        parser.error("Require even games2..1e6, batch1..128, workers1..16, finite max-seconds in (0,7200]")
    seed = args.seed if args.seed is not None else (NATURAL_SEED if args.mode == "natural" else PRIMARY_SEED)
    sampling_seed = args.sampling_seed if args.sampling_seed is not None else (98391 if args.mode == "natural" else 98311)
    if args.mode == "balanced":
        evaluation_plan(pairs_per_cell=args.pairs_per_cell, seed=seed, sampling_seed=sampling_seed)
    elif not 0 <= seed <= 2**64-args.games//2 or not 0 <= sampling_seed < 2**63:
        parser.error("Evaluation seeds outside supported ranges")
    output = args.output.expanduser().resolve()
    protected = {args.a.expanduser().resolve(), args.b.expanduser().resolve()}
    protected |= {path.parent / "identity.json" for path in protected}
    if output in protected or output.exists():
        parser.error("Output must be new and distinct from source checkpoints/identities")
    torch.set_num_threads(1)
    if torch.get_num_interop_threads() != 1:
        torch.set_num_interop_threads(1)
    if not runtime_installed:
        v5.install()
    checkpoints.LearningPolicy = learning_model.LearningPolicy
    started = time.monotonic()
    report = {"schema": "shards-cross-variant-frozen-evaluation-v1", "event": "external_frozen_evaluation",
        "state": "running", "complete": False, "pid": os.getpid(), "started_utc": checkpoints.utc(),
        "evaluation_only": True, "optimizer_updates": 0, "training_budget_seconds_charged": 0,
        "configuration": {**vars(args), "seed": seed, "sampling_seed": sampling_seed},
        "execution": {"device": args.device, "host_setup": "natural", "host_binary_sha256": v5.PINNED_V5_HOST,
            "matmul_precision": "ieee", "torch_threads": 1, "host_workers": args.workers if args.device == "cuda" else 1,
            "helper_sha256": helper_hashes(args.device, args.mode),
            "helper_identity_scope": "CUDA matrix plan/prefix are part of V5 identity. CPU diagnostic helpers are separate fingerprinted provenance; their matrix plan must match exactly."}}
    report["configuration"] = {key: str(value) if isinstance(value, Path) else value for key, value in report["configuration"].items()}
    save_json(output, report)
    try:
        selections = frozen_selections(args.a, args.a_role, args.b, args.b_role)
        if args.device == "cpu":
            import cpu_shadow_eval as shadow
            shadow.configure_cpu()
        elif not torch.cuda.is_available():
            raise RuntimeError("CUDA evaluation requested without an available CUDA device")
        policies = [checkpoints.materialize_policy(selection, args.device) for selection in selections]
        hashes = [checkpoints.policy_hash(policy) for policy in policies]
        for selected, value in zip(selections, hashes):
            selected.metadata["policy_sha256"] = value
        report.update(policy_a=selections[0].metadata, policy_b=selections[1].metadata, catalog=selections[0].catalog)
        save_json(output, report)
        if args.mode == "balanced":
            if args.device == "cpu":
                result = run_balanced_cpu(*policies, pairs_per_cell=args.pairs_per_cell, seed=seed,
                    sampling_seed=sampling_seed, batch=args.batch, max_seconds=args.max_seconds,
                    debug_dir=output.parent/(output.stem+"-censored"))
            else:
                result = run_balanced_cuda(*policies, pairs_per_cell=args.pairs_per_cell, seed=seed,
                    sampling_seed=sampling_seed, batch=args.batch, workers=args.workers,
                    max_seconds=args.max_seconds, telemetry=args.telemetry,
                    debug_dir=output.parent/(output.stem+"-censored"))
        elif args.device == "cpu":
            result = shadow.run_cpu_match(*policies, games=args.games, seed=seed, batch=args.batch,
                sampling_seed=sampling_seed, telemetry=args.telemetry, max_seconds=args.max_seconds, host_factory=_natural_host,
                debug_dir=output.parent/(output.stem+"-censored"))
        else:
            torch.manual_seed(sampling_seed)
            result = learning_eval.evaluate_match(*policies, games=args.games, seed=seed, batch=args.batch,
                workers=args.workers, telemetry=args.telemetry, censor_truncated=True,
                stop_check=lambda: time.monotonic()-started >= args.max_seconds,
                debug_dir=output.parent/(output.stem+"-censored"))
        if [checkpoints.policy_hash(policy) for policy in policies] != hashes:
            raise RuntimeError("Frozen evaluation mutated a policy")
        actual_execution = {**report["execution"], **result.get("execution", {})}
        report.update(result)
        report["execution"] = actual_execution
        report.update(schema="shards-cross-variant-frozen-evaluation-v1", state="completed" if result["complete"] else "failed",
            finished_utc=checkpoints.utc(), total_seconds=time.monotonic()-started, frozen_weights_unchanged=True)
        save_json(output, report)
        print(json.dumps({key: report.get(key) for key in ("state", "games", "score_a", "score_bound_95", "censored_games")}))
        return 0 if result["complete"] else 2
    except BaseException as error:
        report.update(state="failed", complete=False, finished_utc=checkpoints.utc(), error={"type": type(error).__name__, "message": str(error)})
        save_json(output, report)
        raise


if __name__ == "__main__":
    raise SystemExit(main())
