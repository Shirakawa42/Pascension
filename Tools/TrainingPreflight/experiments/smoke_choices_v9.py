"""One bounded frozen V8 GPU cohort, without optimizer or budget mutation.

Root must schedule exclusive GPU use. The default entry supervises a fresh
worker process group; importing this module or --help never initializes CUDA.
Statistics/censor evidence lives in a new temporary sibling of the output.
"""
from __future__ import annotations

import argparse
from collections import Counter
import hashlib
import json
import math
import os
from pathlib import Path
import signal
import subprocess
import sys
import tempfile
import time

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE.parent))
HEROES = ("decima", "tetra", "volos", "kosynwu", "rez")
BATCH = 256
DEFAULT_SEED = 0x8700000000000000


def file_sha256(path):
    digest = hashlib.sha256()
    with Path(path).open("rb") as stream:
        for block in iter(lambda: stream.read(1 << 20), b""):
            digest.update(block)
    return digest.hexdigest()


def context_bytes(value):
    import numpy as np
    hashed = 2166136261
    for character in value:
        hashed = ((hashed ^ ord(character)) * 16777619) & 0xffffffff
    return np.asarray([((hashed >> (8*i)) & 255)/255 for i in range(4)], np.float32)


def initial_setup(obs, seats, metrics):
    """Validate observed setup only; do not infer a mode from a seed namespace."""
    import numpy as np
    if obs.shape != (BATCH, 2816) or seats.shape != (BATCH,):
        raise ValueError("Smoke requires the complete fixed256-lane initial observation")
    if not np.isfinite(obs).all() or not np.array_equal(metrics[2:6], np.zeros(4)):
        raise RuntimeError("Initial inputs must be finite and exclude setup from all policy counters")
    natural = ((obs[:, 22] == 0) & (obs[:, 70] == 0) & (seats == 1) & (obs[:, 4] == 1)
               & np.all(obs[:, 112:116] == context_bytes("soi.herodraft"), axis=1))
    forced = ((obs[:, 22] > 0) & (obs[:, 70] > 0) & (obs[:, 22] != obs[:, 70]) & (seats == 0)
              & (obs[:, 4] == 0) & np.all(obs[:, 112:116] == context_bytes(""), axis=1))
    if not np.all(natural ^ forced) or not natural.any() or not forced.any():
        raise RuntimeError("Expected separate natural and fully assigned starting states")
    pairs = Counter()
    for own, other in obs[forced][:, (22, 70)]:
        a, b = int(round(float(own)*5)), int(round(float(other)*5))
        if (a not in range(1, 6) or b not in range(1, 6) or a == b or
                own != np.float32(a/5) or other != np.float32(b/5)):
            raise RuntimeError("Malformed public forced hero code")
        pairs[(HEROES[a-1], HEROES[b-1])] += 1
    return forced, {"forced_games": int(forced.sum()), "natural_games": int(natural.sum()),
        "forced_pair_counts": [{"seat0_hero": a, "seat1_hero": b, "games": count}
                               for (a, b), count in sorted(pairs.items())],
        "forced_ordered_pair_coverage": len(pairs), "wire_setup_counters_zero": True}


def validate_statistics(directory, forced, collection):
    import numpy as np
    directory = Path(directory)
    errors = list(directory.glob("*/session-*.error.json")) + list(directory.glob("*/session-*.history-error.json"))
    if errors:
        raise RuntimeError("Passive statistics reported an error: " + str(errors))
    paths = [path for path in directory.glob("*/session-*.json") if not path.name.endswith("error.json")]
    if len(paths) != 2:
        raise RuntimeError("One host must finish exactly two independent statistics snapshots")
    capped = np.zeros(BATCH, bool)
    capped[collection["censored_lanes"]] = True
    summaries = {}
    all_pairs = Counter()
    for path in paths:
        report = json.loads(path.read_text())
        cohort = report.get("hero_setup", {}).get("cohort")
        if (cohort not in ("forced-random", "natural-draft") or cohort in summaries or
                path.parent.name != cohort or report.get("schema") != "shards-training-pool-stats-v1" or
                not report.get("final") or report.get("purpose") != "training_pool" or
                report["hero_setup"].get("requested_mode") != "curriculum-75-25"):
            raise RuntimeError("Incorrect setup cohort provenance")
        selected = forced if cohort == "forced-random" else ~forced
        totals = report["totals"]
        if (totals["completed_games"] != int((selected & ~capped).sum()) or
                totals["censored_games"] != int((selected & capped).sum()) or
                totals["unfinished_discarded_games"] != 0):
            raise RuntimeError("Statistics disagree with the collector's exact original lane outcomes")
        pairs = Counter()
        for row in report["matchup_rows"]:
            pair = row["seat0_hero_id"], row["seat1_hero_id"]
            if pair[0] not in HEROES or pair[1] not in HEROES or pair[0] == pair[1]:
                raise RuntimeError("Invalid terminal hero pairing")
            pairs[pair] += row["games"] + row["censored_games"]
        if sum(pairs.values()) != int(selected.sum()):
            raise RuntimeError("Pairing histogram does not cover exactly its population")
        all_pairs.update(pairs)
        summaries[cohort] = {"path": str(path), "totals": totals,
            "hero_setup": report["hero_setup"], "matchup_rows": report["matchup_rows"],
            "ordered_pair_coverage": len(pairs), "pair_counts": dict(pairs)}
    if sum(sum(value["pair_counts"].values()) for value in summaries.values()) != BATCH:
        raise RuntimeError("Exactly256 attempted games must be accounted for, never pooled twice")
    return summaries, all_pairs


def _actor_hashes(actor, policy_hash):
    return {str(size): policy_hash(bucket.policy) for size, bucket in actor.actors.items()}


def worker(args):
    import numpy as np
    import torch
    import campaign_state
    import train_campaign
    import evaluate_checkpoints
    import variant_v9_runtime as runtime
    from migrate_checkpoint import _inactive_ledger
    from bench_common import save_json

    output = args.output.resolve()
    report = {"schema": "shards-v9-frozen-collector-smoke-v1", "state": "running", "passed": False,
        "started_wall": time.time(), "pid": os.getpid(), "optimizer_updates": 0,
        "training_budget_seconds_charged": 0, "batch": BATCH, "seed": args.seed,
        "sampling_seed": args.sampling_seed, "scope": "Frozen real-engine collector/likelihood/lifecycle check, not training or strength evaluation"}
    save_json(output, report)
    host = None
    assets = None
    policies = actors = before_hashes = before_actor_hashes = None
    ledger_before = file_sha256(args.ledger)
    checkpoint_before = file_sha256(args.checkpoint)
    started = time.monotonic()
    try:
        # Hold the existing campaign lock for the whole smoke; never instantiate
        # a budget session, recover an active ledger or publish a checkpoint.
        with _inactive_ledger(args.ledger) as (ledger, locked_hash):
            if locked_hash != ledger_before:
                raise RuntimeError("Ledger changed before acquiring its inactive lock")
            identity = json.loads((args.checkpoint.parent / "identity.json").read_text())
            config = train_campaign.TrainConfig(**identity["configuration"])
            current, catalog = runtime.variant_identity(config)
            if identity != current or identity.get("schema") != runtime.SCHEMA:
                raise RuntimeError("Smoke requires the exact frozen V9 checkpoint identity")
            assets = Path(tempfile.mkdtemp(prefix=output.stem + "-assets-", dir=output.parent))
            statistics_dir = assets / "training-statistics"
            torch.set_num_threads(1)
            if torch.get_num_interop_threads() != 1:
                torch.set_num_interop_threads(1)
            runtime.install(statistics_directory=statistics_dir)
            # Resolve classes after the explicit installation. No stale imported
            # model or Host aliases may bypass IEEE precision/training routing.
            import learning_model
            import adaptive_actor
            import learning_rollout
            from variant_v9_runtime import EpisodeStore as ContiguousEpisodeStore
            evaluate_checkpoints.LearningPolicy = learning_model.LearningPolicy
            evaluate_checkpoints.identity = runtime.variant_identity
            payload = campaign_state.load_checkpoint(args.checkpoint, expected_identity=current)
            provenance = payload.get("readiness_prior_migration", {})
            if (provenance.get("schema") != "shards-v8-v9-public-deck-migration-v1" or
                    not provenance.get("old_parameters_and_optimizer_verified_unchanged")):
                raise RuntimeError("Smoke requires a V8→V9 migrated checkpoint with choice-learning provenance")
            if (payload["budget"]["campaign_id"] != ledger["campaign_id"] or
                    payload["budget"]["limit_seconds"] != ledger["limit_seconds"] or
                    payload["budget"]["charged_seconds"] > ledger["charged_seconds"] + 1e-6):
                raise RuntimeError("Checkpoint must belong to the unchanged authoritative campaign allocation")
            selections = [evaluate_checkpoints.load_selection(args.checkpoint, role) for role in ("learner", "champion")]
            if not torch.cuda.is_available():
                raise RuntimeError("This explicit smoke requires the scheduled CUDA device")
            policies = [evaluate_checkpoints.materialize_policy(selection, "cuda") for selection in selections]
            policy_hash = evaluate_checkpoints.policy_hash
            before_hashes = [policy_hash(policy) for policy in policies]
            actors = [adaptive_actor.AdaptiveLearningActor(policy, max_batch=BATCH, graph=True) for policy in policies]
            for actor, policy, selection in zip(actors, policies, selections):
                actor.refresh(policy, selection.metadata["version"])
            before_actor_hashes = [_actor_hashes(actor, policy_hash) for actor in actors]
            capacity = config.capacity if args.capacity is None else args.capacity
            store = ContiguousEpisodeStore(capacity, device="cuda")
            torch.manual_seed(args.sampling_seed)  # Explicit independent smoke stream, after graph warmups.
            rng = np.random.default_rng(args.sampling_seed)
            opponent_lanes = rng.random(BATCH) < config.archive_fraction
            learner_seats = (np.arange(BATCH) + selections[0].metadata["version"]) % 2
            host = train_campaign.LearningHost(BATCH, args.workers or config.workers, args.seed,
                pinned=True, transport="shared", split_branches=8)
            forced, setup = initial_setup(host.obs, host.seats, host.metrics)
            report.update(identity=current, checkpoint=str(args.checkpoint), checkpoint_sha256_before=checkpoint_before,
                ledger_sha256_before=ledger_before, assets_directory=str(assets), initial_setup=setup,
                policies=[selection.metadata for selection in selections], policy_sha256_before=before_hashes,
                capacity=capacity, opponent_lanes=int(opponent_lanes.sum()),
                execution={"device": "cuda", "matmul_precision": torch.backends.cuda.matmul.fp32_precision,
                    "host_binary_sha256": current["host_binary_sha256"], "host_setup": "curriculum-75-25",
                    "actor": "AdaptiveLearningActor", "store": "ContiguousEpisodeStore"})
            save_json(output, report)
            collected = learning_rollout.collect_episodes(host, actors[0], store,
                version=selections[0].metadata["version"], opponent=actors[1], opponent_lanes=opponent_lanes,
                learner_seats=learner_seats, stop_check=lambda: time.monotonic()-started >= args.max_seconds,
                censor_truncated=True, seed_base=args.seed, debug_dir=assets/"censored-episodes")
            report["collection"] = collected.metrics
            host.close()
            host = None
            if not collected.completed or not collected.metrics["update_ready"]:
                raise RuntimeError("Smoke did not complete a usable full256-attempt cohort")
            verify_started = time.monotonic()
            report["behavior"] = train_campaign.verify_behavior(policies[0], store, config.minibatch)
            report["behavior_verification_seconds"] = time.monotonic()-verify_started
            # Every retained forced trajectory must start after its external
            # prefix; a hero-draft context anywhere in those rows is a failure.
            draft = torch.as_tensor(context_bytes("soi.herodraft"), device=store.obs.device)
            leaked = 0
            for start in range(0, store.rows, config.minibatch):
                end = min(store.rows, start+config.minibatch)
                assigned = torch.as_tensor(forced[store.episode_ids[start:end]], device=store.obs.device)
                leaked += int((assigned & (store.obs[start:end, 112:116] == draft).all(1)).sum().item())
            if leaked:
                raise RuntimeError("A forced hero-draft choice leaked into retained experience")
            report["forced_setup_rows_in_experience"] = leaked
            stats, pairs = validate_statistics(statistics_dir, forced, collected.metrics)
            actual_forced = stats["forced-random"].pop("pair_counts")
            stats["natural-draft"].pop("pair_counts")
            expected_forced = Counter({(row["seat0_hero"], row["seat1_hero"]): row["games"] for row in setup["forced_pair_counts"]})
            if actual_forced != expected_forced:
                raise RuntimeError("Forced terminal pairing histogram differs from the initial actual assignments")
            report.update(statistics=stats, actual_ordered_pair_coverage=len(pairs),
                all_twenty_forced_pairs_observed=len(actual_forced) == 20,
                hero_coverage=sorted(set(hero for pair in pairs for hero in pair)))
            after_hashes = [policy_hash(policy) for policy in policies]
            after_actor_hashes = [_actor_hashes(actor, policy_hash) for actor in actors]
            if before_hashes != after_hashes or before_actor_hashes != after_actor_hashes:
                raise RuntimeError("Frozen policy or actor clone weights changed during the smoke")
            report.update(policy_sha256_after=after_hashes, actor_bucket_sha256=after_actor_hashes,
                frozen_weights_unchanged=True, state="completed", passed=True)
    except BaseException as error:
        report.update(state="failed", passed=False, error={"type": type(error).__name__, "message": str(error)})
    finally:
        if host is not None:
            host.close()
        if before_hashes is not None:
            try:
                after = [evaluate_checkpoints.policy_hash(policy) for policy in policies]
                buckets = [_actor_hashes(actor, evaluate_checkpoints.policy_hash) for actor in actors] if before_actor_hashes is not None else None
                unchanged = before_hashes == after and (buckets is None or before_actor_hashes == buckets)
                report.update(policy_sha256_after=after, actor_bucket_sha256=buckets, frozen_weights_unchanged=unchanged)
                if not unchanged:
                    report.update(state="failed", passed=False, weights_preservation_error="Frozen weights changed")
            except BaseException as error:
                report.update(state="failed", passed=False, frozen_weights_unchanged=None,
                    weights_verification_error={"type": type(error).__name__, "message": str(error)})
        report.update(ledger_sha256_after=file_sha256(args.ledger), checkpoint_sha256_after=file_sha256(args.checkpoint),
            seconds=time.monotonic()-started, finished_wall=time.time(),
            assets_directory=str(assets) if assets is not None else None)
        report["ledger_unchanged"] = report["ledger_sha256_after"] == ledger_before
        report["checkpoint_unchanged"] = report["checkpoint_sha256_after"] == checkpoint_before
        if not report["ledger_unchanged"] or not report["checkpoint_unchanged"]:
            report.update(state="failed", passed=False, preservation_error="Checkpoint or ledger bytes changed")
        save_json(output, report)
    return 0 if report["passed"] else 2


def terminate_group(process):
    try:
        os.killpg(process.pid, signal.SIGTERM)
    except ProcessLookupError:
        return
    try:
        process.wait(timeout=5)
    except subprocess.TimeoutExpired:
        pass
    try:
        os.killpg(process.pid, signal.SIGKILL)
    except ProcessLookupError:
        pass
    process.wait(timeout=5)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--checkpoint", type=Path, required=True)
    parser.add_argument("--ledger", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--seed", type=lambda x: int(x, 0), default=DEFAULT_SEED)
    parser.add_argument("--sampling-seed", type=int, default=880926)
    parser.add_argument("--workers", type=int)
    parser.add_argument("--capacity", type=int)
    parser.add_argument("--max-seconds", type=float, default=180)
    parser.add_argument("--worker", action="store_true", help=argparse.SUPPRESS)
    args = parser.parse_args()
    args.checkpoint = args.checkpoint.expanduser().resolve(strict=True)
    args.ledger = args.ledger.expanduser().resolve(strict=True)
    args.output = args.output.expanduser().resolve()
    if (not 0 <= args.seed <= 2**64-BATCH or not 0 <= args.sampling_seed < 2**63 or
            not math.isfinite(args.max_seconds) or not 10 <= args.max_seconds <= 300 or
            args.workers is not None and not 1 <= args.workers <= 16 or
            args.capacity is not None and not 32768 <= args.capacity <= 1048576):
        parser.error("Require bounded seeds, workers1..16, capacity32768..1048576, max-seconds10..300")
    if args.output in {args.checkpoint, args.ledger, args.checkpoint.parent/"identity.json"} or args.output.exists():
        parser.error("Output must be new and separate from checkpoint, identity and ledger")
    if args.output.is_relative_to(args.ledger.parent):
        parser.error("Smoke output/statistics must stay outside the live campaign directory")
    args.output.parent.mkdir(parents=True, exist_ok=True)
    if args.worker:
        return worker(args)
    command = [sys.executable, str(Path(__file__).resolve()), *sys.argv[1:], "--worker"]
    env = dict(os.environ, OMP_NUM_THREADS="1", OPENBLAS_NUM_THREADS="1", MKL_NUM_THREADS="1")
    def interrupted(signum, frame):
        raise KeyboardInterrupt("Smoke supervisor received signal " + str(signum))
    signal.signal(signal.SIGTERM, interrupted)
    process = subprocess.Popen(command, env=env, start_new_session=True)
    try:
        return process.wait(timeout=args.max_seconds+60)
    except BaseException as error:
        terminate_group(process)
        report = json.loads(args.output.read_text()) if args.output.exists() else {}
        report.update(schema="shards-v9-frozen-collector-smoke-v1", state="failed", passed=False,
            supervisor_error={"type": type(error).__name__, "message": str(error)}, optimizer_updates=0,
            training_budget_seconds_charged=0, finished_wall=time.time())
        from bench_common import save_json
        save_json(args.output, report)
        return 2
    finally:
        # Kill any orphan host still in the child's group even after worker exit.
        terminate_group(process)


if __name__ == "__main__":
    raise SystemExit(main())
