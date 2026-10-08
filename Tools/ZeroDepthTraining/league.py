"""Bounded exploratory neural arena; never a training or incumbent promotion gate.

A fixed public hero curriculum is played twice on identical engine seeds, with
candidate seats swapped. All search-free models are immutable policy exports.
The optional watcher is owned by the launcher and stops when its training lease
ends. It never owns a training budget, optimizer, rollout store or checkpoint.
"""
from __future__ import annotations

import argparse
from dataclasses import asdict, dataclass
from datetime import datetime, timezone
from functools import wraps
import hashlib
import json
import math
import os
from pathlib import Path
import shutil
import signal
import sys
import tempfile
import time

import numpy as np

HERE = Path(__file__).resolve().parent
POLICY_SCHEMA = "shards-zero-depth-frozen-neural-policy-v1"
ARENA_SCHEMA = "shards-zero-depth-neural-arena-v1"
PLAN_SCHEMA = "shards-zero-depth-league-plan-v1"
SEED_BASE = 0xA000000000000000
SEED_LIMIT = 0xB000000000000000
HERO_VERSION = "shards-balanced-heroes-v1-splitmix64"
HEROES = ("decima", "tetra", "volos", "kosynwu", "rez")
UINT64 = (1 << 64) - 1


def sha256_file(path):
    digest = hashlib.sha256()
    with Path(path).open("rb") as stream:
        for part in iter(lambda: stream.read(1 << 20), b""):
            digest.update(part)
    return digest.hexdigest()


def save_json(path, value):
    path = Path(path)
    path.parent.mkdir(parents=True, exist_ok=True)
    temporary = path.with_name(path.name + "." + os.urandom(8).hex() + ".tmp")
    try:
        with temporary.open("x", encoding="utf-8") as stream:
            json.dump(value, stream, indent=2, allow_nan=False)
            stream.write("\n"); stream.flush(); os.fsync(stream.fileno())
        temporary.replace(path)
        descriptor = os.open(path.parent, os.O_RDONLY | os.O_DIRECTORY)
        try: os.fsync(descriptor)
        finally: os.close(descriptor)
    finally:
        temporary.unlink(missing_ok=True)


def raw_clock():
    try: return time.clock_gettime(time.CLOCK_MONOTONIC_RAW)
    except (AttributeError, OSError): return time.monotonic()


def _integer(value, name, low, high):
    if type(value) is not int or not low <= value <= high:
        raise ValueError(f"Invalid {name}")
    return value


def validate_plan(value):
    allowed = {"schema", "baseline", "baseline_manifest_sha256", "every_games", "games", "batch",
               "workers", "max_seconds", "seed_base", "device", "policy_seed"}
    if not isinstance(value, dict) or set(value) - allowed or value.get("schema") != PLAN_SCHEMA:
        raise ValueError("Invalid league plan schema/fields")
    plan = dict(value)
    if not isinstance(plan.get("baseline"), str) or not Path(plan["baseline"]).is_absolute():
        raise ValueError("League baseline must be an absolute frozen-policy directory")
    for name, default, low, high in (("every_games", 100000, 1000, 10000000), ("games", 400, 40, 4000),
                                    ("batch", 20, 1, 128), ("workers", 2, 1, 8),
                                    ("policy_seed", 716204, 0, (1 << 32) - 1)):
        plan[name] = _integer(plan.get(name, default), name, low, high)
    if plan["games"] % 40:
        raise ValueError("Arena games must be a multiple of 40: 20 ordered hero pairs in both seats")
    plan["seed_base"] = _integer(plan.get("seed_base", SEED_BASE), "seed base", SEED_BASE, SEED_LIMIT - plan["games"] // 2)
    if plan["seed_base"] % 20:
        raise ValueError("Arena seed base must align to a 20-seed hero cycle")
    seconds = plan.get("max_seconds", 120.)
    if isinstance(seconds, bool) or not isinstance(seconds, (int, float)) or not math.isfinite(seconds) or not 1 <= seconds <= 600:
        raise ValueError("Arena cap must be finite and no greater than 600 seconds")
    plan["max_seconds"] = float(seconds)
    plan["device"] = plan.get("device", "cuda")
    if plan["device"] not in ("cpu", "cuda", "cuda:0"):
        raise ValueError("League device must be CPU or the single CUDA device")
    pin = plan.get("baseline_manifest_sha256")
    if pin is not None and (not isinstance(pin, str) or len(pin) != 64 or any(c not in "0123456789abcdef" for c in pin)):
        raise ValueError("Invalid baseline manifest SHA256")
    return plan


def heroes_for_seed(seed):
    """Exact port of Host/HeroAssignments.cs, including rejection sampling."""
    _integer(seed, "engine seed", 0, UINT64)
    state = (seed // 20) ^ 0xD1B54A32D192ED03
    def next_value():
        nonlocal state
        state = (state + 0x9E3779B97F4A7C15) & UINT64
        value = state
        value = ((value ^ (value >> 30)) * 0xBF58476D1CE4E5B9) & UINT64
        value = ((value ^ (value >> 27)) * 0x94D049BB133111EB) & UINT64
        return value ^ (value >> 31)
    permutation = list(range(20))
    for last in range(19, 0, -1):
        bound = last + 1; threshold = ((-bound) & UINT64) % bound
        value = next_value()
        while value < threshold: value = next_value()
        chosen = value % bound
        permutation[last], permutation[chosen] = permutation[chosen], permutation[last]
    matchup = permutation[seed % 20]
    first, second = divmod(matchup, 4)
    if second >= first: second += 1
    return HEROES[first], HEROES[second]


def verify_frozen_policy(directory, *, manifest_sha256=None):
    directory = Path(directory)
    manifest_path = directory / "manifest.json"
    if manifest_sha256 is not None and sha256_file(manifest_path) != manifest_sha256:
        raise ValueError("Frozen-policy manifest changed")
    manifest = json.loads(manifest_path.read_text())
    if manifest.get("schema") != POLICY_SCHEMA or manifest.get("file") != "policy.pt":
        raise ValueError("Invalid frozen neural policy schema")
    file = directory / "policy.pt"
    if file.stat().st_size != manifest.get("bytes") or sha256_file(file) != manifest.get("policy_file_sha256"):
        raise ValueError("Frozen neural policy bytes changed")
    return manifest


def export_frozen_policy(checkpoint, directory, *, identity_path=None, expected_identity=None, stop_check=None):
    """Read-only checked full checkpoint -> create-only policy-only bundle.

    Capture an immutable inode first; a live atomic replacement cannot mix model
    weights and counters. No RNG restore, budget object or learning is involved.
    An interrupted incomplete destination is rejected, never silently replaced.
    """
    import torch
    sys.path.append(str(HERE.parent / "TrainingPreflight"))
    from campaign_state import load_checkpoint
    checkpoint, directory = Path(checkpoint), Path(directory)
    if directory.exists():
        raise FileExistsError("Frozen policy destination already exists")
    if stop_check and stop_check(): raise TimeoutError("Arena stopped before checkpoint export")
    if expected_identity is None:
        expected_identity = json.loads(Path(identity_path or checkpoint.parent / "identity.json").read_text())
    directory.parent.mkdir(parents=True, exist_ok=True)
    temporary = Path(tempfile.mkdtemp(prefix=".league-export-", dir=directory.parent))
    try:
        snapshot = temporary / "snapshot.soicp"
        try: os.link(checkpoint, snapshot)
        except OSError:
            # The source descriptor owns one atomic inode even across devices.
            with checkpoint.open("rb") as source, snapshot.open("xb") as target:
                shutil.copyfileobj(source, target, 1 << 20)
                target.flush(); os.fsync(target.fileno())
        payload = load_checkpoint(snapshot, expected_identity=expected_identity)
        if stop_check and stop_check(): raise TimeoutError("Arena stopped during checkpoint export")
        state = payload["state"]
        if payload["identity"].get("schema") != "shards-zero-depth-training-v1":
            raise ValueError("Arena requires a checked zero-depth checkpoint")
        metadata = {key: _integer(state.get(key, 0), key, 0, 1 << 63)
                    for key in ("games", "generations", "optimizer_steps", "decisions")}
        manifest = {"schema": POLICY_SCHEMA, "file": "policy.pt", "training": metadata,
                    "identity": payload["identity"], "checkpoint": str(checkpoint.resolve()),
                    "checkpoint_sha256": sha256_file(snapshot),
                    "checkpoint_payload_sha256": payload["file_sha256"],
                    "created_utc": datetime.now(timezone.utc).isoformat()}
        compact = {"schema": POLICY_SCHEMA, "identity": payload["identity"], "training": metadata,
                   "catalog": state["catalog"], "policy_config": state["policy_config"], "policy": state["policy"]}
        # Retain only policy tensors and small provenance; free optimizer/archive.
        del state, payload
        with (temporary / "policy.pt").open("xb") as stream:
            torch.save(compact, stream); stream.flush(); os.fsync(stream.fileno())
        del compact
        manifest["bytes"] = (temporary / "policy.pt").stat().st_size
        manifest["policy_file_sha256"] = sha256_file(temporary / "policy.pt")
        if stop_check and stop_check(): raise TimeoutError("Arena stopped before frozen-policy publication")
        directory.mkdir()  # Exclusive create: never overwrite a foreign directory.
        (temporary / "policy.pt").replace(directory / "policy.pt")
        save_json(directory / "manifest.json", manifest)  # Publish completeness last.
        return verify_frozen_policy(directory)
    finally:
        shutil.rmtree(temporary)


def load_frozen_policy(directory, *, device="cpu", manifest_sha256=None, allow_external_calibration=False):
    import torch
    from model import Policy, PolicyConfig
    directory = Path(directory)
    manifest = verify_frozen_policy(directory, manifest_sha256=manifest_sha256)
    if manifest.get("required_inference_calibration") is not None and not allow_external_calibration:
        raise ValueError("This policy requires external inference calibration; use a calibration-aware evaluator")
    # Hash and deserialize the same open inode: atomic replacement after the
    # initial manifest verification must not substitute different model bytes.
    with (directory / "policy.pt").open("rb") as stream:
        digest = hashlib.sha256()
        for part in iter(lambda: stream.read(1 << 20), b""): digest.update(part)
        if stream.tell() != manifest["bytes"] or digest.hexdigest() != manifest["policy_file_sha256"]:
            raise ValueError("Frozen neural policy bytes changed before loading")
        stream.seek(0)
        payload = torch.load(stream, map_location="cpu", weights_only=True)
    if payload.get("schema") != POLICY_SCHEMA or payload.get("identity") != manifest["identity"] or payload.get("training") != manifest["training"]:
        raise ValueError("Frozen-policy payload provenance changed")
    for tensor in payload["policy"].values():
        if not isinstance(tensor, torch.Tensor) or tensor.dtype != torch.float32 or not bool(torch.isfinite(tensor).all()):
            raise ValueError("Frozen-policy tensors must be finite FP32")
    policy = Policy(payload["catalog"], PolicyConfig(**payload["policy_config"]))
    policy.load_state_dict(payload["policy"], strict=True)
    policy.eval().requires_grad_(False)
    return policy.to(device), manifest


@dataclass(frozen=True)
class ArenaGame:
    seed: int
    learner_seat: int
    winner: int | None
    censored: bool
    rounds: int | None
    hero0: str
    hero1: str


def summarize_arena(results, *, planned_pairs, alpha=.05):
    from evaluate import GameResult, summarize_pairs
    records = list(results)
    for game in records:
        if (game.hero0, game.hero1) != heroes_for_seed(game.seed):
            raise ValueError("Recorded heroes disagree with fixed seed assignment")
        if game.rounds is not None: _integer(game.rounds, "terminal rounds", 0, 1000000)
    summary = summarize_pairs((GameResult(x.seed, x.learner_seat, x.winner, x.censored) for x in records),
                              planned_pairs=planned_pairs, alpha=alpha)
    # Repeated exploratory reports cannot replace the predeclared incumbent gate.
    ahead = summary.pop("stronger_than_current")
    summary["ahead_on_this_fixed_benchmark"] = ahead
    summary["promotion_allowed"] = False
    summary["verdict"] = "ahead_on_fixed_benchmark" if ahead else "incomplete" if not summary["evaluation_finished"] else "not_demonstrated"
    summary["interval_assumptions"] = "Independent engine-seed pairs; same engine seed and hero matchup with models swapped. Per-checkpoint exploratory interval, not a repeated-testing promotion guarantee."
    summary["gate"] = "Exploratory neural baseline only; final 4096-game search-incumbent gate remains separate"
    coverage = {f"{left}/{right}": {"games": 0, "candidate_seat0": 0, "candidate_seat1": 0}
                for left in HEROES for right in HEROES if left != right}
    for game in records:
        row = coverage[f"{game.hero0}/{game.hero1}"]
        row["games"] += 1; row[f"candidate_seat{game.learner_seat}"] += 1
    natural_rounds = [x.rounds for x in records if not x.censored and x.rounds is not None]
    rounds = {"natural_games_with_rounds": len(natural_rounds),
              "mean_natural_rounds": sum(natural_rounds) / len(natural_rounds) if natural_rounds else None,
              "by_outcome": {}}
    for name, selected in (("wins", [x for x in records if not x.censored and x.winner == x.learner_seat]),
                           ("losses", [x for x in records if not x.censored and x.winner in (0, 1) and x.winner != x.learner_seat]),
                           ("draws", [x for x in records if not x.censored and x.winner == -1])):
        values = [x.rounds for x in selected if x.rounds is not None]
        rounds["by_outcome"][name] = {"games_with_rounds": len(values), "mean_rounds": sum(values) / len(values) if values else None}
    return summary, rounds, coverage


def _terminal(host, lane, seed, seat, catalog):
    done = int(host.done[lane])
    if done not in (1, 2): raise ValueError("Arena terminal state must be natural or censored")
    hero0, hero1 = heroes_for_seed(seed)
    row = host.obs[lane]; actor = int(host.actors[lane])
    if actor not in (0, 1): raise ValueError("Terminal deciding seat invalid")
    if catalog.get("observation_schema") != "shards-zero-depth-observation-v2":
        raise ValueError("Arena needs reviewed terminal round/hero scalar schema")
    observed = []
    for player in (0, 1):
        value = float(row[22 if player == actor else 86]) * 5
        if not math.isfinite(value) or abs(value - round(value)) > .001:
            raise ValueError("Terminal hero scalar malformed")
        index = round(value) - 1
        if not 0 <= index < len(HEROES): raise ValueError("Terminal hero index invalid")
        observed.append(HEROES[index])
    if tuple(observed) != (hero0, hero1): raise ValueError("Host hero assignments differ from planned curriculum")
    if done == 2: return ArenaGame(seed, seat, None, True, None, hero0, hero1)
    rewards = np.asarray(host.rewards[lane])
    winner = -1 if np.array_equal(rewards, [0, 0]) else 0 if np.array_equal(rewards, [1, -1]) else 1 if np.array_equal(rewards, [-1, 1]) else None
    if winner is None: raise ValueError("Malformed natural terminal outcome")
    value = float(row[2]) * 100
    if not math.isfinite(value) or value < 0 or abs(value - round(value)) > .001:
        raise ValueError("Malformed terminal round scalar")
    return ArenaGame(seed, seat, winner, False, round(value), hero0, hero1)


def _close_host(host):
    try: host.close()
    except TimeoutError:
        # Only the exact Popen object created by this arena is eligible for cleanup.
        process = getattr(host, "process", None)
        if process is None: raise
        if process.poll() is None: process.kill(); process.wait(timeout=3)
        host.close()


def run_neural_arena(candidate, baseline, output, *, games=400, batch=20, workers=2,
                     seed_base=SEED_BASE, policy_seed=716204, max_seconds=120., device="cuda",
                     graph=True, binary=None, alpha=.05, stop_check=None,
                     baseline_manifest_sha256=None, deadline_monotonic=None, latest_output=None,
                     _host_factory=None, _actor_factory=None, _loader=None, _catalog=None):
    import torch
    from adaptive import AdaptiveActor
    from host import BINARY, Host, catalog as get_catalog
    from train import source_fingerprint
    plan_config = validate_plan({"schema": PLAN_SCHEMA, "baseline": str(Path(baseline).resolve()),
        "games": games, "batch": batch, "workers": workers, "seed_base": seed_base,
        "max_seconds": max_seconds, "device": device, "policy_seed": policy_seed})
    started = time.monotonic(); raw_started = raw_clock()
    deadline = min(started + plan_config["max_seconds"], deadline_monotonic or math.inf)
    def stopped(): return time.monotonic() >= deadline or bool(stop_check and stop_check())
    output = Path(output)
    if output.exists() or output.with_suffix(".plan.json").exists(): raise FileExistsError("Arena outputs are create-only")
    if stopped(): raise TimeoutError("Arena stopped before setup")
    torch.set_num_threads(1)
    torch.backends.fp32_precision = "ieee"
    torch.backends.cuda.matmul.fp32_precision = "ieee"
    loader = _loader or load_frozen_policy
    # Retain both policies and all actors until their graphs have finished.
    left, left_meta = loader(candidate, device=device)
    right, right_meta = loader(baseline, device=device, manifest_sha256=baseline_manifest_sha256)
    binary = Path(binary or BINARY)
    host_catalog = _catalog if _catalog is not None else get_catalog(binary)
    canonical = lambda value: json.dumps(value, sort_keys=True, separators=(",", ":"))
    if canonical(left.catalog) != canonical(right.catalog) or canonical(left.catalog) != canonical(host_catalog):
        raise ValueError("Neural arena models/Host observation catalogs differ")
    if tuple(x["id"] for x in host_catalog.get("heroes", [])) != HEROES:
        raise ValueError("Arena requires the reviewed five-hero order")
    if _loader is None:
        host_hash = sha256_file(binary)
        for metadata in (left_meta, right_meta):
            if metadata["identity"].get("host_sha256") != host_hash:
                raise ValueError("Frozen model rules/Host binary differ")
        if left_meta["identity"].get("source_fingerprint") != source_fingerprint():
            raise ValueError("Candidate source pin differs from arena runtime")
    plan = {"schema": ARENA_SCHEMA, "purpose": "exploratory_fixed_neural_arena", "promotion_allowed": False,
            "candidate": left_meta, "baseline": right_meta, "planned_games": games, "planned_pairs": games // 2,
            "seed_base": seed_base, "seed_namespace": "[0xA000000000000000,0xB000000000000000); separate from final incumbent0x8000 namespace",
            "hero_mode": "balanced_random", "hero_assignment_version": HERO_VERSION,
            "pairing": "same engine seed and exact initial heroes; candidate seat0 then seat1 in each cohort",
            "policy_seed": policy_seed, "action_selection": "sampled categorical policy", "search_depth": 0,
            "max_seconds": max_seconds, "batch": batch, "workers": workers, "precision": "ieee-fp32",
            "created_utc": datetime.now(timezone.utc).isoformat(), "device": device,
            "final_incumbent_evaluation_unchanged": True}
    save_json(output.with_suffix(".plan.json"), plan)
    results = []
    mirrors = []
    def publish(reason=None, *, verified=False, invalid=False):
        summary, rounds, coverage = summarize_arena(results, planned_pairs=games // 2, alpha=alpha)
        if not verified or invalid:
            summary["ahead_on_this_fixed_benchmark"] = False
            summary["verdict"] = "invalid" if invalid else "incomplete"
        result = {**plan, "elapsed_seconds": raw_clock() - raw_started, "elapsed_clock": "monotonic_raw" if hasattr(time, "CLOCK_MONOTONIC_RAW") else "monotonic",
                  "summary": summary, "rounds": rounds, "hero_coverage": coverage, "results": [asdict(x) for x in results],
                  "integrity_verified": verified and not invalid,
                  "mirror_initial_publications": mirrors, "initial_publication_mirrors_verified": verified and not invalid and summary["evaluation_finished"] and len(mirrors) == math.ceil((games // 2) / batch)}
        if reason: result["stop_reason"] = reason
        save_json(output, result)
        if latest_output is not None: save_json(latest_output, result)
        return result
    actor_factory = _actor_factory or AdaptiveActor
    host_factory = _host_factory or Host
    actors = {}
    publish()
    try:
        for offset in range(0, games // 2, batch):
            lanes = min(batch, games // 2 - offset)
            if lanes not in actors:
                if stopped(): return publish("deadline_or_owner_stop")
                actors[lanes] = (actor_factory(left, lanes, graph=graph and device != "cpu", device=device, packed=device != "cpu"),
                                 actor_factory(right, lanes, graph=graph and device != "cpu", device=device, packed=device != "cpu"))
            if offset == 0: torch.manual_seed(policy_seed)  # Capture warmups do not determine arena RNG.
            initial_digest = None
            for seat in (0, 1):
                if stopped(): return publish("deadline_or_owner_stop")
                remaining = deadline - time.monotonic()
                host = host_factory(binary=binary, batch=lanes, workers=workers, seed=seed_base + offset,
                                    automation=True, paired=False, hero_mode="balanced_random", timeout=max(.01, min(10., remaining)))
                try:
                    publication = hashlib.sha256()
                    for name in ("obs", "candidates", "mask", "rewards", "done", "actors"):
                        publication.update(np.asarray(getattr(host, name)).tobytes())
                    if initial_digest is None: initial_digest = publication.hexdigest()
                    elif publication.hexdigest() != initial_digest: raise ValueError("Mirrored games did not reset to identical public initial states")
                    else: mirrors.append({"seed_start": seed_base + offset, "lanes": lanes, "initial_publication_sha256": initial_digest})
                    seen = np.zeros(lanes, dtype=bool)
                    frames = 0
                    while not seen.all():
                        for lane in np.flatnonzero((host.done != 0) & ~seen):
                            results.append(_terminal(host, int(lane), seed_base + offset + int(lane), seat, host_catalog)); seen[lane] = True
                        if seen.all(): break
                        if stopped() or frames >= 10000: return publish("deadline_or_owner_stop" if stopped() else "frame_cap")
                        live = ~seen
                        actor, opponent = actors[lanes]
                        candidate_lanes = live & (host.actors == seat)
                        baseline_lanes = live & ~candidate_lanes
                        actions = np.full(lanes, -1, np.int32)
                        for brain, selected in ((actor, candidate_lanes), (opponent, baseline_lanes)):
                            if not selected.any(): continue
                            choices, packet = brain.act(host, selected)
                            if not np.isfinite(packet[selected]).all(): raise ValueError("Nonfinite frozen actor packet")
                            actions[selected] = choices[selected]
                        active_lanes = np.flatnonzero(live)
                        if np.any(actions[active_lanes] < 0) or np.any(actions[active_lanes] >= host.mask.shape[1]) or not np.all(host.mask[active_lanes, actions[active_lanes]] == 1):
                            raise ValueError("Frozen actor selected illegal action")
                        host.timeout = max(.01, min(10., deadline - time.monotonic()))
                        host.advance(actions); frames += 1
                    publish()
                finally: _close_host(host)
        if stopped(): return publish("deadline_or_owner_stop")
        if _loader is None:
            verify_frozen_policy(candidate); verify_frozen_policy(baseline, manifest_sha256=baseline_manifest_sha256)
            if sha256_file(binary) != host_hash or left_meta["identity"].get("source_fingerprint") != source_fingerprint():
                raise ValueError("Arena runtime/Host changed during evaluation")
        return publish(verified=True)
    except BaseException as error:
        publish(f"{type(error).__name__}: {error}", invalid=True)
        raise


def process_identity(pid):
    try:
        stat = Path(f"/proc/{pid}/stat").read_text()
        rest = stat[stat.rfind(")") + 2:].split()
        if rest[0] == "Z": return None
        command = Path(f"/proc/{pid}/cmdline").read_bytes().split(b"\0")
        return {"pid": int(pid), "startticks": int(rest[19]), "command": [x.decode() for x in command if x]}
    except (OSError, ValueError, UnicodeError): return None


CHAMPION_SCHEMA = "shards-zero-depth-champion-v1"


def champion_seed(trial):
    _integer(trial, "champion trial", 1, 1000000)
    return SEED_BASE + 2000000 + (trial - 1) * 4000


def champion_decision(result, trial, expected_games=400):
    """Fresh seed pairs and alpha spending; this never promotes a deployed AI."""
    if expected_games not in (400,3200):raise ValueError('Unreviewed champion sample size')
    pairs=expected_games//2
    seed = champion_seed(trial)
    if result.get("seed_base") != seed or result.get("planned_games") != expected_games:
        raise ValueError("Champion evidence differs from its predeclared trial")
    records = [ArenaGame(**row) for row in result["results"]]
    if any(not seed <= row.seed < seed + pairs for row in records):
        raise ValueError("Champion evidence uses foreign or repeated trial seeds")
    alpha = .05 / (trial * (trial + 1))
    summary, rounds, coverage = summarize_arena(records, planned_pairs=pairs, alpha=alpha)
    complete = (summary["evaluation_finished"] and not summary["censored"]
                and all(row["candidate_seat0"] == row["candidate_seat1"] == expected_games//40 for row in coverage.values()))
    promote = bool(complete and result.get("integrity_verified") is True
                   and result.get("initial_publication_mirrors_verified") is True
                   and summary["paired_hoeffding_score_interval"][0] > .5)
    return {"trial": trial, "alpha": alpha, "promote": promote, "summary": summary,
            "rounds": rounds, "hero_coverage": coverage,
            "scope": "Retained neural evaluation champion only; final incumbent promotion remains separate",
            "error_control": "Alpha_n = 0.05/(n*(n+1)); distinct engine-seed pairs for every attempted trial"}


def read_champion(campaign):
    root = Path(campaign) / "champion"
    state = json.loads((root / "state.json").read_text())
    if state.get("schema") != CHAMPION_SCHEMA: raise ValueError("Invalid champion state schema")
    sha = state.get("checkpoint_sha256")
    if not isinstance(sha, str) or len(sha) != 64 or any(c not in "0123456789abcdef" for c in sha):
        raise ValueError("Invalid champion checkpoint identity")
    if state.get("directory") != "policies/" + sha: raise ValueError("Champion path differs from pinned identity")
    _integer(state.get("trials"), "champion trials", 0, 1000000)
    _integer(state.get("promotions"), "champion promotions", 0, state["trials"])
    metadata = verify_frozen_policy(root / state["directory"], manifest_sha256=state["manifest_sha256"])
    if metadata["checkpoint_sha256"] != sha or metadata["training"] != state.get("training"):
        raise ValueError("Champion provenance differs from retained weights")
    return state


def _retain_champion(campaign, candidate, *, trials, promotions):
    root = Path(campaign) / "champion"
    metadata = verify_frozen_policy(candidate)
    relative = "policies/" + metadata["checkpoint_sha256"]
    destination = root / relative
    destination.parent.mkdir(parents=True, exist_ok=True)
    if not destination.exists():
        temporary = Path(tempfile.mkdtemp(prefix=".retain-", dir=destination.parent))
        try:
            for name in ("policy.pt", "manifest.json"):
                shutil.copy2(Path(candidate) / name, temporary / name)
                with (temporary / name).open("rb") as durable: os.fsync(durable.fileno())
            verify_frozen_policy(temporary, manifest_sha256=sha256_file(Path(candidate) / "manifest.json"))
            temporary.rename(destination)
            descriptor = os.open(destination.parent, os.O_RDONLY | os.O_DIRECTORY)
            try: os.fsync(descriptor)
            finally: os.close(descriptor)
        finally:
            if temporary.exists(): shutil.rmtree(temporary)
    verify_frozen_policy(destination, manifest_sha256=sha256_file(Path(candidate) / "manifest.json"))
    state = {"schema": CHAMPION_SCHEMA, "directory": relative,
             "manifest_sha256": sha256_file(destination / "manifest.json"),
             "checkpoint_sha256": metadata["checkpoint_sha256"], "training": metadata["training"],
             "trials": trials, "promotions": promotions, "updated_utc": datetime.now(timezone.utc).isoformat()}
    save_json(root / "state.json", state)
    return state


def ensure_champion(campaign, candidate):
    if (Path(campaign) / "champion/state.json").exists(): return read_champion(campaign)
    return _retain_champion(campaign, candidate, trials=0, promotions=0)


def challenge_champion(campaign, candidate, *, stop_check, deadline_monotonic, workers=2, device="cuda"):
    root = Path(campaign) / "champion"
    state = ensure_champion(campaign, candidate)
    metadata = verify_frozen_policy(candidate)
    if metadata["training"]["games"] <= state["training"]["games"]: return None
    trial = state["trials"] + 1
    # Spend/reserve the trial BEFORE any GPU work, including an interrupted attempt.
    state = {**state, "trials": trial}
    save_json(root / "state.json", state)
    output = root / f"trial-{trial:06d}.json"
    result = run_neural_arena(candidate, root / state["directory"], output,
        games=3200, batch=64, max_seconds=600., workers=workers, device=device, seed_base=champion_seed(trial),
        policy_seed=916204 + trial, stop_check=stop_check, deadline_monotonic=deadline_monotonic,
        baseline_manifest_sha256=state["manifest_sha256"], alpha=.05 / (trial * (trial + 1)))
    decision = champion_decision(result, trial, expected_games=3200)
    if decision["promote"]:
        state = _retain_champion(campaign, candidate, trials=trial, promotions=state["promotions"] + 1)
    report = {"schema": CHAMPION_SCHEMA, "candidate_training": metadata["training"],
              "opponent_training": result["baseline"]["training"], "champion": state,
              "created_utc": datetime.now(timezone.utc).isoformat(), **decision}
    save_json(root / f"decision-{trial:06d}.json", report)
    save_json(root / "latest.json", report)
    return report


class ArenaOwner:
    """Read-only parent + active trainer identity lease; never signals either."""
    def __init__(self, campaign, parent_pid, *, reader=process_identity):
        self.campaign = Path(campaign); self.reader = reader
        self.parent = reader(parent_pid)
        if self.parent is None: raise ValueError("Arena launcher is absent")
        self.boot = Path("/proc/sys/kernel/random/boot_id").read_text().strip()
        self.trainer = None
        self.lease = None

    def remaining(self):
        if self.reader(self.parent["pid"]) != self.parent: return None
        try: budget = json.loads((self.campaign / "budget.json").read_text())
        except (OSError, ValueError): return None
        active = budget.get("active")
        if not isinstance(active, dict) or active.get("boot_id") != self.boot or type(active.get("pid")) is not int: return None
        trainer = self.reader(active["pid"])
        if trainer is None: return None
        command = trainer["command"]
        if not any(Path(x).name == "train.py" for x in command) or "--run-dir" not in command: return None
        position = command.index("--run-dir")
        if position + 1 >= len(command) or Path(command[position + 1]).resolve() != self.campaign.resolve(): return None
        values = (active.get("hard_deadline_monotonic"), active.get("hard_deadline_wall"), active.get("last_heartbeat_monotonic"))
        if any(isinstance(x, bool) or not isinstance(x, (float, int)) or not math.isfinite(x) for x in values): return None
        session = active.get("session_id")
        if not isinstance(session, str) or not session: return None
        now = time.monotonic()
        if not -1 <= now - values[2] <= 60: return None
        lease = (session, values[0], values[1])
        if self.lease is None:
            self.lease, self.trainer = lease, trainer
        if lease != self.lease or trainer != self.trainer: return None
        return min(self.lease[1] - now, self.lease[2] - time.time())


def _restore_signal_handlers(function):
    @wraps(function)
    def wrapped(*args, **kwargs):
        previous = {number: signal.getsignal(number) for number in (signal.SIGTERM, signal.SIGINT)}
        try: return function(*args, **kwargs)
        finally:
            for number, handler in previous.items(): signal.signal(number, handler)
    return wrapped


@_restore_signal_handlers
def watch(campaign, baseline, *, parent_pid, every_games=100000, games=400, batch=20,
          workers=2, max_seconds=120., seed_base=SEED_BASE, device="cuda", policy_seed=716204,
          baseline_manifest_sha256=None, poll_seconds=5.):
    import torch
    campaign = Path(campaign).resolve()
    config = validate_plan({"schema": PLAN_SCHEMA, "baseline": str(Path(baseline).resolve()),
        "every_games": every_games, "games": games, "batch": batch, "workers": workers,
        "max_seconds": max_seconds, "seed_base": seed_base, "device": device, "policy_seed": policy_seed})
    owner = ArenaOwner(campaign, parent_pid)
    plan_path = campaign / "league-plan.json"
    plan_hash = None
    if plan_path.exists():
        plan_bytes = plan_path.read_bytes()
        plan_hash = hashlib.sha256(plan_bytes).hexdigest()
        plan = validate_plan(json.loads(plan_bytes))
        for key in config:
            left = str(Path(plan[key]).resolve()) if key == "baseline" else plan[key]
            if left != config[key]: raise ValueError(f"Watcher {key} differs from launch plan")
        pin = plan.get("baseline_manifest_sha256")
        if baseline_manifest_sha256 is not None and pin is not None and pin != baseline_manifest_sha256:
            raise ValueError("Watcher baseline manifest pin differs from launch plan")
        baseline_manifest_sha256 = pin or baseline_manifest_sha256
    baseline_meta = verify_frozen_policy(baseline, manifest_sha256=baseline_manifest_sha256)
    last_games = baseline_meta["training"]["games"]
    try:
        prior = json.loads((campaign / "league" / "latest.json").read_text())
        if prior.get("schema") == ARENA_SCHEMA and prior.get("baseline", {}).get("checkpoint_sha256") == baseline_meta["checkpoint_sha256"]:
            last_games = max(last_games, _integer(prior["candidate"]["training"]["games"], "prior arena games", 0, 1 << 63))
    except (OSError, ValueError, KeyError, TypeError): pass
    next_games = last_games + every_games
    # Enable the new curriculum only for the explicitly migrated configuration.
    campaign_config = campaign / "config.json"
    champion_enabled = (campaign_config.exists() and
                        json.loads(campaign_config.read_text()).get("archive_strategy") == "prioritized")
    stopped = False
    def signal_stop(*_):
        nonlocal stopped
        stopped = True
    for number in (signal.SIGTERM, signal.SIGINT): signal.signal(number, signal_stop)
    def should_stop():
        if plan_hash is not None and sha256_file(plan_path) != plan_hash:
            raise ValueError("League launch plan changed while watcher was alive")
        remaining = owner.remaining()
        return stopped or remaining is None or remaining <= 20
    # A launcher may start this process just before the trainer publishes its lease.
    startup_until = time.monotonic() + 180
    while owner.remaining() is None and time.monotonic() < startup_until and not stopped:
        if process_identity(parent_pid) != owner.parent: return
        time.sleep(min(poll_seconds, 1.))
    while not should_stop():
        remaining = owner.remaining()
        if remaining is None or remaining <= max_seconds + 20: return
        try:
            status = json.loads((campaign / "status.json").read_text())
            # status is published after the atomic checkpoint: heartbeat-only
            # counters could repeatedly export a stale 839MB checkpoint.
            current_games = _integer(status.get("games", 0), "checkpoint games", 0, 1 << 63)
        except (OSError, ValueError, TypeError, KeyError): time.sleep(poll_seconds); continue
        if current_games < next_games: time.sleep(poll_seconds); continue
        started = time.monotonic(); raw_started = raw_clock()
        deadline = min(started + max_seconds, started + remaining - 20)
        stop = lambda: should_stop() or time.monotonic() >= deadline
        work = Path(tempfile.mkdtemp(prefix="shards-neural-arena-"))
        try:
            torch.set_num_threads(1)
            candidate = work / "candidate"
            metadata = export_frozen_policy(campaign / "latest.soicp", candidate, stop_check=stop)
            if metadata["training"]["games"] < next_games:
                time.sleep(poll_seconds); continue
            output = campaign / "league" / f"games-{metadata['training']['games']:012d}-{metadata['checkpoint_sha256'][:16]}.json"
            result = run_neural_arena(candidate, baseline, output, games=games, batch=batch, workers=workers,
                       seed_base=seed_base, policy_seed=policy_seed, max_seconds=max_seconds, device=device,
                       stop_check=stop, baseline_manifest_sha256=baseline_manifest_sha256, deadline_monotonic=deadline,
                       latest_output=campaign / "league" / "latest.json")
            result["export_and_setup_included_in_cap"] = True
            result["sidecar_elapsed_seconds"] = raw_clock() - raw_started
            result["sidecar_elapsed_clock"] = "monotonic_raw" if hasattr(time, "CLOCK_MONOTONIC_RAW") else "monotonic"
            save_json(output, result)
            save_json(campaign / "league" / "latest.json", result)
            if champion_enabled and not stop():
                challenge_champion(campaign, candidate, stop_check=stop, deadline_monotonic=deadline,
                                   workers=workers, device=device)
            next_games = metadata["training"]["games"] + every_games
        except TimeoutError:
            if should_stop(): return
            next_games = current_games + every_games
        finally: shutil.rmtree(work)
        time.sleep(poll_seconds)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    sub = parser.add_subparsers(dest="command", required=True)
    export = sub.add_parser("export")
    export.add_argument("--checkpoint", type=Path, required=True); export.add_argument("--output", type=Path, required=True)
    export.add_argument("--identity", type=Path)
    run = sub.add_parser("run")
    run.add_argument("--candidate", type=Path, required=True); run.add_argument("--baseline", type=Path, required=True)
    run.add_argument("--output", type=Path, required=True)
    monitor = sub.add_parser("watch")
    monitor.add_argument("--campaign", type=Path, required=True); monitor.add_argument("--baseline", type=Path, required=True)
    monitor.add_argument("--parent-pid", type=int, required=True); monitor.add_argument("--every-games", type=int, default=100000)
    for command in (run, monitor):
        command.add_argument("--games", type=int, default=400); command.add_argument("--batch", type=int, default=20)
        command.add_argument("--workers", type=int, default=2); command.add_argument("--max-seconds", type=float, default=120.)
        command.add_argument("--seed-base", type=int, default=SEED_BASE); command.add_argument("--policy-seed", type=int, default=716204)
        command.add_argument("--device", default="cuda")
    args = vars(parser.parse_args()); command = args.pop("command")
    if command == "export":
        args["directory"] = args.pop("output"); args["identity_path"] = args.pop("identity")
        print(json.dumps(export_frozen_policy(**args), indent=2))
    elif command == "run": print(json.dumps(run_neural_arena(**args)["summary"], indent=2))
    else: watch(**args)


if __name__ == "__main__": main()
