"""Larger progress checks composed from independently verified, bounded arenas.

The ordinary arena deliberately caps each invocation at 4,000 games and ten
minutes. Keep those guards intact: freeze both policies once, run disjoint
blocks, and publish a larger result only when every block passes validation.
This is an exploratory progress check, never a champion-promotion shortcut.
"""
from pathlib import Path
import hashlib
import json
import math
import sys

TOOLS = Path(__file__).resolve().parents[1]
sys.path[:0] = [str(TOOLS / "ZeroDepthTraining"), str(TOOLS / "TrainingPreflight")]
from league import ArenaGame, HEROES, PLAN_SCHEMA, run_neural_arena, summarize_arena, validate_plan


def compare_heroes(results, *, alpha=.05):
    """Compare each model playing the SAME hero on mirrored engine seeds.

    Hero win rates alone confound policy quality with hero/matchup difficulty.
    Differences are paired within each seed. Bonferroni-adjusted Hoeffding
    intervals cover the five hero differences jointly for this single check.
    """
    if isinstance(alpha, bool) or not isinstance(alpha, (int, float)) or not 0 < alpha < 1:
        raise ValueError("Invalid hero comparison confidence")
    records = [ArenaGame(**game) for game in results]
    if not records or len(records) % 40:
        raise ValueError("Hero comparisons require complete balanced paired games")
    summary, _, coverage = summarize_arena(records, planned_pairs=len(records) // 2)
    if not summary["evaluation_finished"] or summary["censored"]:
        raise ValueError("Hero comparisons require natural complete pairs")
    if any(row["candidate_seat0"] != len(records) // 40
           or row["candidate_seat1"] != len(records) // 40 for row in coverage.values()):
        raise ValueError("Hero matchups must have equal coverage in both seats")
    pairs = {}
    for game in records:
        pair = pairs.setdefault(game.seed, {})
        if game.learner_seat in pair:
            raise ValueError("Duplicate hero pair seat")
        pair[game.learner_seat] = game
    scores = {hero: [0., 0., 0] for hero in HEROES}
    for pair in pairs.values():
        if set(pair) != {0, 1}:
            raise ValueError("Missing mirrored hero game")
        for seat, hero in enumerate((pair[0].hero0, pair[0].hero1)):
            def score(game):
                return .5 if game.winner == -1 else float(game.winner == seat)
            scores[hero][0] += score(pair[seat])
            scores[hero][1] += score(pair[1 - seat])
            scores[hero][2] += 1
    comparisons = {}
    for hero, (candidate, baseline, count) in scores.items():
        delta = (candidate - baseline) / count
        radius = math.sqrt(2 * math.log(2 * len(HEROES) / alpha) / count)
        comparisons[hero] = {
            "games_per_model": count, "candidate_score": candidate / count,
            "baseline_score": baseline / count, "paired_score_difference": delta,
            "simultaneous_difference_interval": [max(-1., delta - radius), min(1., delta + radius)]}
    return {"family_confidence": 1 - alpha, "heroes": comparisons,
            "method": "Paired Hoeffding differences, range [-1,1], Bonferroni over five heroes. "
                      "Single exploratory comparison; no repeated-check promotion guarantee."}


def merge_blocks(blocks):
    """Recompute paired statistics from complete, nonoverlapping raw outcomes."""
    if not 2 <= len(blocks) <= 8:
        raise ValueError("Two through eight complete 4000-game blocks required")
    first = blocks[0]
    results, mirrors = [], []
    stable = ("candidate", "baseline", "hero_mode", "hero_assignment_version",
              "pairing", "action_selection", "search_depth", "device",
              "precision", "batch", "workers", "elapsed_clock")
    for index, result in enumerate(blocks):
        if (not result.get("integrity_verified")
                or not result.get("initial_publication_mirrors_verified")
                or not result["summary"]["evaluation_finished"]
                or result["summary"]["censored"]
                or result["planned_games"] != 4000):
            raise ValueError("Incomplete or invalid arena block")
        if result.get("promotion_allowed") is not False or result["search_depth"] != 0:
            raise ValueError("Only exploratory zero-depth arenas may be composed")
        if any(result[key] != first[key] for key in stable):
            raise ValueError("Policies or arena protocol differ between blocks")
        if result["seed_base"] != first["seed_base"] + index * 2000:
            raise ValueError("Seed blocks overlap or have gaps")
        if result["policy_seed"] != first["policy_seed"] + index * 100000:
            raise ValueError("Policy sampling seeds differ from the declared schedule")
        if len(result["results"]) != 4000:
            raise ValueError("Wrong block game count")
        seen = set()
        for game in result["results"]:
            key = game["seed"], game["learner_seat"]
            if key in seen or game["censored"]:
                raise ValueError("Duplicate or censored game")
            seen.add(key)
        expected = {(seed, seat)
                    for seed in range(result["seed_base"], result["seed_base"] + 2000)
                    for seat in (0, 1)}
        if seen != expected:
            raise ValueError("Missing paired seed or seat")
        checked, _, _ = summarize_arena(
            [ArenaGame(**game) for game in result["results"]], planned_pairs=2000)
        for key in ("wins", "losses", "draws", "recorded_games", "evaluation_finished", "censored"):
            if checked[key] != result["summary"][key]:
                raise ValueError("Block summary differs from raw outcomes")
        expected_mirrors = [(result["seed_base"] + offset,
                             min(result["batch"], 2000 - offset))
                            for offset in range(0, 2000, result["batch"])]
        observed = [(mirror["seed_start"], mirror["lanes"])
                    for mirror in result["mirror_initial_publications"]]
        if observed != expected_mirrors or any(
                len(mirror["initial_publication_sha256"]) != 64
                for mirror in result["mirror_initial_publications"]):
            raise ValueError("Missing initial-state mirror verification")
        results.extend(result["results"])
        mirrors.extend(result["mirror_initial_publications"])
    summary, rounds, coverage = summarize_arena(
        [ArenaGame(**game) for game in results], planned_pairs=len(blocks) * 2000)
    return {**first, "planned_games": len(blocks) * 4000, "planned_pairs": len(blocks) * 2000,
            "max_seconds": sum(block["max_seconds"] for block in blocks),
            "elapsed_seconds": sum(block["elapsed_seconds"] for block in blocks),
            "summary": summary, "rounds": rounds, "hero_coverage": coverage,
            "results": results, "mirror_initial_publications": mirrors,
            "hero_comparisons": compare_heroes(results),
            "integrity_verified": True, "initial_publication_mirrors_verified": True,
            "composition": "Bounded 4000-game arenas; identical frozen policy manifests, "
                           "contiguous disjoint engine seeds, both seats in every pair. "
                           "Each block independently verifies policies, Host and source. "
                           "No optional stopping based on partial score.",
            "block_policy_seeds": [block["policy_seed"] for block in blocks]}


def run(candidate, baseline, output, *, seed_base, policy_seed,
        baseline_manifest_sha256, stop_check, deadline_monotonic, games=8000,
        batch=40, workers=2):
    """Run within the caller's owner/resource/deadline guards; outputs are exclusive."""
    output = Path(output)
    if type(games) is not int or not 8000 <= games <= 32000 or games % 4000:
        raise ValueError("Declare 8000 through 32000 games in complete 4000-game blocks")
    if output.exists():
        raise FileExistsError(output)
    paths = [output.with_name(output.stem + f"-block-{index + 1}.json")
             for index in range(games // 4000)]
    for index, path in enumerate(paths):
        if path.exists() or path.with_suffix(".plan.json").exists():
            raise FileExistsError(path)
        validate_plan({"schema": PLAN_SCHEMA, "baseline": str(Path(baseline).resolve()),
                       "games": 4000, "batch": batch, "workers": workers, "max_seconds": 600,
                       "seed_base": seed_base + index * 2000,
                       "policy_seed": policy_seed + index * 100000,
                       "baseline_manifest_sha256": baseline_manifest_sha256})
    blocks, provenance = [], []
    for index, path in enumerate(paths):
        result = run_neural_arena(
            candidate, baseline, path, games=4000, batch=batch, workers=workers,
            seed_base=seed_base + index * 2000, policy_seed=policy_seed + index * 100000,
            max_seconds=600, stop_check=stop_check,
            baseline_manifest_sha256=baseline_manifest_sha256,
            deadline_monotonic=deadline_monotonic)
        if not result["integrity_verified"] or not result["summary"]["evaluation_finished"]:
            raise RuntimeError("Block incomplete; cannot infer aggregate strength")
        if result["batch"] != batch or result["workers"] != workers:
            raise RuntimeError("Arena execution differs from the declared batching plan")
        blocks.append(result)
        provenance.append({"path": str(path),
                           "sha256": hashlib.sha256(path.read_bytes()).hexdigest()})
    result = merge_blocks(blocks)
    result["verified_blocks"] = provenance
    with output.open("x") as stream:
        json.dump(result, stream)
    return result
