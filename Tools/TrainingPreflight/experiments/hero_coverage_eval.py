"""Read-only CPU draft coverage and explicitly interventional hero evaluation.

The panel submits two actual legal initial hero choices before normal frozen
policy play. It does not estimate the policies' unmodified game strength. No
optimizer, trajectory store or campaign ledger is opened. Run in an isolated
process with an outer process-group timeout; max-seconds is cooperative between
host replies, and cannot bound a blocked engine pipe.
"""
from __future__ import annotations

import argparse
import hashlib
import itertools
import math
from pathlib import Path
import time

import cpu_shadow_eval as shadow  # hides CUDA before importing torch
import numpy as np
import torch

from bench_common import save_json
from learning_rollout import LearningHost

HEROES = shadow.HEROES
PAIRINGS = tuple(itertools.permutations(HEROES, 2))


def context_bytes(value):
    hashed = 2166136261
    for character in value:
        hashed = ((hashed ^ ord(character)) * 16777619) & 0xffffffff
    return np.asarray([((hashed >> (8*i)) & 255)/255 for i in range(4)], np.float32)


DRAFT_CONTEXT = context_bytes("soi.herodraft")


def draft_slots(host, hero, expected_seat):
    """Fail closed on request/seat/menu changes; preserve original action slots."""
    if hero not in HEROES or expected_seat not in (0, 1):
        raise ValueError("Unknown hero or draft seat")
    if not np.all(host.seats == expected_seat) or np.any(host.done):
        raise ValueError("Expected unfinished initial draft owned by the requested seat")
    if not np.all(host.obs[:, 4] == 1) or not np.all(host.obs[:, 112:116] == DRAFT_CONTEXT):
        raise ValueError("Request is not the public hero draft")
    if not np.all(host.obs[:, 22] == 0):
        raise ValueError("Deciding player already has a hero")
    code = (HEROES.index(hero)+1)/5
    legal = host.mask == 1
    kinds = host.candidates[..., :16].argmax(-1)
    matches = legal & (kinds == 12) & (host.candidates[..., 31] == np.float32(code))
    if not np.all(matches.sum(-1) == 1):
        raise ValueError("Requested hero must have exactly one legal original candidate")
    return matches.argmax(-1).astype(np.int64)


def force_initial_draft(host, heroes_by_seat):
    """Intervene only through the engine's first two normal decision submissions."""
    if len(heroes_by_seat) != 2 or heroes_by_seat[0] == heroes_by_seat[1]:
        raise ValueError("Require two distinct legal heroes")
    active = np.ones(host.batch, bool)
    for seat in (1, 0):
        actions = draft_slots(host, heroes_by_seat[seat], seat)
        host.advance_active(actions, active)
        if np.any(host.done):
            raise RuntimeError("Game ended during initial hero selection")
    if np.any(host.obs[:, 112:116] != context_bytes("")) or np.any(host.seats != 0):
        raise RuntimeError("Expected seat0 priority immediately after draft")
    for seat in (0, 1):
        # At seat0 priority, own/opponent fields map to absolute seats0/1.
        expected = np.float32((HEROES.index(heroes_by_seat[seat])+1)/5)
        if not np.all(host.obs[:, 22+48*seat] == expected):
            raise RuntimeError("Engine did not assign the requested public hero")


@torch.inference_mode()
def probabilities(policy, host):
    logits, values = policy(torch.from_numpy(host.obs), torch.from_numpy(host.candidates),
                            torch.from_numpy(host.mask).bool())
    result = logits.softmax(-1).numpy()
    if not np.isfinite(result).all() or not np.isfinite(values.numpy()).all():
        raise RuntimeError("Nonfinite draft policy output")
    return result


def draft_probe(policy, *, seed, boards=32, host_factory=LearningHost):
    """Measure natural seat1 draft and seat0 conditional on each legal first pick."""
    if not 1 <= boards <= 128 or not 0 <= seed <= 2**64-boards:
        raise ValueError("Require boards1..128 and uint64 engine seeds")
    shadow.configure_cpu()
    before = shadow.checkpoints.policy_hash(policy)
    rows = []
    for opponent in HEROES:
        host = host_factory(boards, 1, seed, pinned=False, transport="shared", split_branches=8)
        try:
            for phase in (0, 1):
                if phase == 0 and opponent != HEROES[0]:
                    pass  # all five hosts have identical pre-draft boards
                else:
                    probability = probabilities(policy, host)
                    choices = HEROES if phase == 0 else tuple(h for h in HEROES if h != opponent)
                    for lane in range(boards):
                        record = {"engine_seed": seed+lane, "deciding_seat": 1-phase,
                                  "opponent_hero": None if phase == 0 else opponent,
                                  "probabilities": {}}
                        for hero in choices:
                            slot = draft_slots(host, hero, 1-phase)[lane]
                            record["probabilities"][hero] = float(probability[lane, slot])
                        rows.append(record)
                if phase == 0:
                    host.advance_active(draft_slots(host, opponent, 1), np.ones(boards, bool))
        finally:
            host.close()
    groups = []
    for seat, opponent in [(1, None), *((0, hero) for hero in HEROES)]:
        selected = [row for row in rows if (row["deciding_seat"], row["opponent_hero"]) == (seat, opponent)]
        groups.append({"deciding_seat": seat, "opponent_hero": opponent,
            "heroes": {hero: {"mean": float(np.mean([row["probabilities"][hero] for row in selected])),
                "min": min(row["probabilities"][hero] for row in selected),
                "max": max(row["probabilities"][hero] for row in selected)}
                for hero in selected[0]["probabilities"]}})
    if shadow.checkpoints.policy_hash(policy) != before or torch.cuda.is_initialized():
        raise RuntimeError("Probe mutated policy or initialized CUDA")
    return {"boards": boards, "seed_start": seed, "groups": groups, "rows": rows,
            "policy_sha256": before, "cuda_initialized": False,
            "scope": "CPU FP32 exact frozen-policy probabilities on sampled initial boards; seat0 conditional first picks are interventions; not hero strength"}


def run_forced_panel(policy_a, policy_b, *, seed, seeds_per_pair=16, batch=16,
                     sampling_seed=80927, max_seconds=600, host_factory=LearningHost,
                     pairings=PAIRINGS):
    if not 1 <= seeds_per_pair <= 100000 or not 1 <= batch <= 128:
        raise ValueError("Require seeds-per-pair1..100000 and batch1..128")
    if not 0 <= seed <= 2**64-seeds_per_pair or not 0 <= sampling_seed < 2**63:
        raise ValueError("Engine/sampling seeds outside supported ranges")
    if not math.isfinite(max_seconds) or not 0 < max_seconds <= 7200:
        raise ValueError("Require max-seconds in (0,7200]")
    pairings = tuple(pairings)
    if not pairings or len(set(pairings)) != len(pairings) or any(pair not in PAIRINGS for pair in pairings):
        raise ValueError("Require unique ordered distinct-hero pairings")
    started = time.monotonic()
    cells = []
    for hero_a, hero_b in pairings:
        remaining = max_seconds-(time.monotonic()-started)
        if remaining <= 0:
            break
        count = 0
        def build(n, workers, engine_seed, **kwargs):
            nonlocal count
            seat_a = count % 2
            count += 1
            host = host_factory(n, workers, engine_seed, **kwargs)
            try:
                force_initial_draft(host, (hero_a, hero_b) if seat_a == 0 else (hero_b, hero_a))
                return host
            except BaseException:
                host.close()
                raise
        match = shadow.run_cpu_match(policy_a, policy_b, games=2*seeds_per_pair,
            seed=seed, batch=batch, sampling_seed=sampling_seed, telemetry=True,
            max_seconds=remaining, host_factory=build)
        match.update(policy_a_hero=hero_a, policy_b_hero=hero_b,
            behavior="Forced legal initial draft, then actual frozen policy sampling; interventional evaluation",
            draft_policy_actions_excluded=True,
            length_scope="Episode lengths and entropy omit the two imposed drafts; engine wrapper/submission counters include them")
        if match["complete"]:
            low, high = match["score_identification_interval"]
            margin = math.sqrt(math.log(40*len(pairings))/(2*seeds_per_pair))
            match["simultaneous_score_bound_95"] = [max(0., low-margin), min(1., high+margin)]
            for episode in match["shadow_diagnostics"]["episodes"]:
                if (episode["policy_a_hero"], episode["policy_b_hero"]) != (hero_a, hero_b):
                    raise RuntimeError("Recorded hero assignment differs from intervention")
        cells.append(match)
        if not match["complete"]:
            break
    return {"complete": len(cells) == len(pairings) and all(cell["complete"] for cell in cells),
        "cells": cells, "planned_pairings": [list(pair) for pair in pairings],
        "seed_start": seed, "seeds_per_pair": seeds_per_pair,
        "common_engine_seed_blocks": True, "seconds": time.monotonic()-started,
        "scope": "20 ordered policy-hero assignments, each seat-swapped. Conditional policy competence/counterplay only; not natural-draft strength or optimal hero balance. No aggregate independent-game claim across common seeds.",
        "bound_scope": "Per-cell paired-seed bounds plus Bonferroni simultaneous95% bounds over all planned cells; censored outcomes unknown"}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("mode", choices=("draft-probe", "forced-panel"))
    parser.add_argument("--a", type=Path, required=True)
    parser.add_argument("--b", type=Path)
    parser.add_argument("--a-role", choices=shadow.checkpoints.ROLES, default="learner")
    parser.add_argument("--b-role", choices=shadow.checkpoints.ROLES, default="learner")
    parser.add_argument("--seed", type=shadow.checkpoints.integer, default=0x6100000000000000)
    parser.add_argument("--sampling-seed", type=int, default=80927)
    parser.add_argument("--boards", type=int, default=32)
    parser.add_argument("--seeds-per-pair", type=int, default=16)
    parser.add_argument("--batch", type=int, default=16)
    parser.add_argument("--max-seconds", type=float, default=600)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    if args.mode == "forced-panel" and args.b is None:
        parser.error("forced-panel requires an explicit --b frozen checkpoint")
    args.b = args.b or args.a
    output = args.output.resolve()
    protected = {args.a.resolve(), args.b.resolve()}
    protected |= {path.parent/"identity.json" for path in protected}
    if output in protected or output.exists():
        parser.error("Output must be a new path distinct from checkpoints/identities")
    shadow.configure_cpu()
    shadow.configure_variant("v3")
    selections = shadow.frozen_selections(args.a, args.a_role, args.b, args.b_role)
    policies = [shadow.checkpoints.materialize_policy(selection, "cpu") for selection in selections]
    report = {"schema": "shards-interventional-hero-coverage-v1", "mode": args.mode,
        "evaluation_only": True, "optimizer_updates": 0, "training_budget_seconds_charged": 0,
        "helper_sha256": hashlib.sha256(Path(__file__).read_bytes()).hexdigest(),
        "policy_a": selections[0].metadata, "policy_b": selections[1].metadata,
        "execution": {"device": "cpu", "dtype": "float32", "matmul_precision": "ieee",
                      "torch_threads": 1, "host_workers": 1}}
    if args.mode == "draft-probe":
        report.update(draft_probe(policies[0], seed=args.seed, boards=args.boards))
    else:
        report.update(run_forced_panel(*policies, seed=args.seed, seeds_per_pair=args.seeds_per_pair,
            batch=args.batch, sampling_seed=args.sampling_seed, max_seconds=args.max_seconds))
    if torch.cuda.is_initialized():
        raise RuntimeError("Unexpected CUDA initialization")
    save_json(output, report)


if __name__ == "__main__":
    main()
