"""Explicit forced-hero CPU evaluation; default prints a plan without execution.

All20 ordered (policy A hero, policy B hero) assignments receive the same
number of seat-swapped seed pairs. Seed blocks are DISJOINT across cells. The
two initial hero choices use actual legal actions; all later choices sample
the ordinary frozen policy. These are interventions, not natural-draft scores.
Use an outer process-group timeout for any real --run invocation.
"""
from __future__ import annotations

import argparse
import hashlib
import itertools
import json
import math
from pathlib import Path
import sys
import time

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE.parent))
HEROES = ("decima", "tetra", "volos", "kosynwu", "rez")
PAIRINGS = tuple(itertools.permutations(HEROES, 2))
PRIMARY_SEED = 0x6300000000000000
NATURAL_SEED = 0x6400000000000000


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
            "sampling_seed": sampling_seed+index} for index, (a, b) in enumerate(PAIRINGS)],
        "intervention": {"kind": "forced_legal_initial_hero_draft", "setup_actions_per_game": 2,
            "subsequent_behavior": "sample actual frozen policy", "training_data": False},
        "scope": "Equal-weight ordered hero-pair competence/counterplay; not unmodified natural-draft policy strength or optimal hero balance"}


def run_balanced_matrix(policy_a, policy_b, *, pairs_per_cell=100, seed=PRIMARY_SEED,
                        sampling_seed=98311, batch=16, max_seconds=1200, host_factory=None):
    import hero_coverage_eval as helpers
    from learning_eval import score_summary
    plan = evaluation_plan(pairs_per_cell=pairs_per_cell, seed=seed, sampling_seed=sampling_seed)
    if not math.isfinite(max_seconds) or not 0 < max_seconds <= 7200:
        raise ValueError("Require a finite max_seconds in (0,7200]")
    started = time.monotonic()
    cells, scores = [], []
    censored = 0
    for specification in plan["cells"]:
        remaining = max_seconds-(time.monotonic()-started)
        if remaining <= 0:
            break
        options = {} if host_factory is None else {"host_factory": host_factory}
        cell_result = helpers.run_forced_panel(policy_a, policy_b,
            seed=specification["seed_start"], seeds_per_pair=pairs_per_cell, batch=batch,
            sampling_seed=specification["sampling_seed"], max_seconds=remaining,
            pairings=((specification["policy_a_hero"], specification["policy_b_hero"]),), **options)
        if not cell_result["cells"]:
            break
        cell = cell_result["cells"][0]
        cell["planned_configuration"] = specification
        if cell["complete"]:
            lower, upper = cell["score_identification_interval"]
            margin = math.sqrt(math.log(40*20)/(2*pairs_per_cell))
            cell["simultaneous_score_bound_95"] = [max(0., lower-margin), min(1., upper+margin)]
            episodes = cell["shadow_diagnostics"]["episodes"]
            actual = {(record["seed"], record["policy_a_seat"]) for record in episodes}
            expected = {(engine_seed, seat) for engine_seed in range(specification["seed_start"],
                        specification["seed_start"]+pairs_per_cell) for seat in (0, 1)}
            if actual != expected or len(episodes) != len(expected):
                raise RuntimeError("Balanced cell did not produce exactly its predeclared paired games")
            for episode in episodes:
                if episode["censored"]:
                    censored += 1
                else:
                    scores.append(episode["score_a"])
        cells.append(cell)
        if not cell["complete"]:
            break
    complete = len(cells) == 20 and all(cell["complete"] for cell in cells)
    result = {"schema": "shards-balanced-hero-evaluation-v1", "complete": complete,
        "plan": plan, "intervention": plan["intervention"], "cells": cells,
        "seconds": time.monotonic()-started, "optimizer_updates": 0,
        "training_budget_seconds_charged": 0, "evaluation_only": True,
        "scope": plan["scope"],
        "bound_scope": "Overall score uses the predeclared equal allocation and disjoint paired seeds. Cell simultaneous95% bounds use a union bound across20 planned cells. Censors remain unknown."}
    if complete:
        result.update(games=plan["planned_games"],
            **score_summary(scores, plan["planned_games"], censored))
        result["balanced_macro_score"] = result["score_a"]
    else:
        result["reason"] = "cooperative_deadline_or_incomplete_cell"
    return result


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--run", action="store_true", help="Explicitly execute the frozen CPU panel; default only prints its plan")
    parser.add_argument("--a", type=Path)
    parser.add_argument("--b", type=Path)
    parser.add_argument("--a-role", choices=("learner", "champion", "initial"), default="learner")
    parser.add_argument("--b-role", choices=("learner", "champion", "initial"), default="learner")
    parser.add_argument("--variant", choices=("v3", "v4", "v5", "v6", "v7"), default="v4")
    parser.add_argument("--pairs-per-cell", type=int, default=100)
    parser.add_argument("--seed", type=lambda value: int(value, 0), default=PRIMARY_SEED)
    parser.add_argument("--sampling-seed", type=int, default=98311)
    parser.add_argument("--batch", type=int, default=16)
    parser.add_argument("--max-seconds", type=float, default=1200)
    parser.add_argument("--output", type=Path)
    args = parser.parse_args()
    plan = evaluation_plan(pairs_per_cell=args.pairs_per_cell, seed=args.seed, sampling_seed=args.sampling_seed)
    if not args.run:
        print(json.dumps(plan, indent=2))
        return 0
    if args.a is None or args.b is None or args.output is None:
        parser.error("--run requires two explicit frozen checkpoints and a new --output path")
    output = args.output.resolve()
    protected = {args.a.resolve(), args.b.resolve()}
    protected |= {path.parent/"identity.json" for path in protected}
    if output.exists() or output in protected:
        parser.error("Output must be new and distinct from checkpoint/identity sources")
    import cpu_shadow_eval as shadow
    import torch
    from bench_common import save_json
    shadow.configure_cpu()
    shadow.configure_variant(args.variant)
    selections = shadow.frozen_selections(args.a, args.a_role, args.b, args.b_role)
    policies = [shadow.checkpoints.materialize_policy(selection, "cpu") for selection in selections]
    for selection, policy in zip(selections, policies):
        selection.metadata["policy_sha256"] = shadow.checkpoints.policy_hash(policy)
    report = run_balanced_matrix(*policies, pairs_per_cell=args.pairs_per_cell, seed=args.seed,
        sampling_seed=args.sampling_seed, batch=args.batch, max_seconds=args.max_seconds)
    report.update(policy_a=selections[0].metadata, policy_b=selections[1].metadata,
        catalog=selections[0].catalog, helper_sha256=hashlib.sha256(Path(__file__).read_bytes()).hexdigest(),
        execution={"device": "cpu", "dtype": "float32", "matmul_precision": "ieee",
                   "torch_threads": 1, "host_workers": 1}, cuda_initialized=torch.cuda.is_initialized())
    if torch.cuda.is_initialized():
        raise RuntimeError("Unexpected CUDA initialization")
    save_json(output, report)
    print(json.dumps({key: report.get(key) for key in ("complete", "games", "balanced_macro_score", "score_bound_95", "censored_games")}))
    return 0 if report["complete"] else 2


if __name__ == "__main__":
    raise SystemExit(main())
