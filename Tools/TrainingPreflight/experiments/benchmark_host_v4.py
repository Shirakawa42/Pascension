"""Frozen warmed whole-episode V3/V4-off/V4-statistics comparison; no updates."""
import argparse
import hashlib
import json
from pathlib import Path
import sys
import time

HERE = Path(__file__).resolve().parent
sys.path[:0] = [str(HERE), str(HERE.parent)]

import numpy as np
import torch
import pipeline_bench
import variant_runtime as v3
import variant_v4_runtime as v4
from bench_common import save_json
from benchmark_candidates import exact_rollout, require_idle_ledger
from learning_rollout import LearningHost, collect_episodes
from rollout_fastpath import ContiguousEpisodeStore
from train_campaign import TrainConfig, verify_behavior


class TracedHost(LearningHost):
    def __init__(self, *args, **kwargs):
        self.trace = hashlib.sha256()
        super().__init__(*args, **kwargs)

    def advance_active(self, indices, active):
        self.trace.update(np.where(active, indices, -1).astype("<i4").tobytes())
        return super().advance_active(indices, active)


class Factory:
    def __init__(self, binary, *, statistics=None, seed=None, batch=None):
        self.binary, self.statistics, self.seed, self.batch = binary, statistics, seed, batch

    def __getattr__(self, name):
        return getattr(v3._original_subprocess, name)

    def Popen(self, command, *args, **kwargs):
        original = str(pipeline_bench.ROOT/"Tools/TrainingPreflight/Host/bin/Release/net8.0/TrainingHost.dll")
        if command != [pipeline_bench.DOTNET, original, "serve"]:
            raise ValueError("Unexpected host request")
        env = {key: value for key, value in kwargs["env"].items() if not key.startswith("SHARDS_STATS_")}
        if self.statistics is not None:
            env.update(SHARDS_STATS_DIRECTORY=str(self.statistics), SHARDS_STATS_PURPOSE="training_pool",
                       SHARDS_STATS_EXPECTED_SEED=str(self.seed), SHARDS_STATS_EXPECTED_BATCH=str(self.batch))
        kwargs["env"] = env
        return v3._original_subprocess.Popen([command[0], str(self.binary), "serve"], *args, **kwargs)


def validate_statistics(folder, warm_metrics, measured_metrics, identity, *, returncode, diagnostics=""):
    """Check final publication against the independently observed game outcomes."""
    if returncode != 0:
        raise AssertionError("Statistics host did not exit cleanly: " + str(returncode))
    paths = list(Path(folder).glob("session-*.json"))
    if any(path.name.endswith((".error.json", ".history-error.json")) for path in paths) or "statistics_error" in diagnostics:
        raise AssertionError("Statistics host reported a counter/publication failure")
    if len(paths) != 1:
        raise AssertionError("Expected exactly one final statistics session")
    content = paths[0].read_bytes()
    snapshot = json.loads(content)
    if (snapshot.get("schema") != "shards-training-pool-stats-v1" or
            snapshot.get("purpose") != "training_pool" or snapshot.get("final") is not True or
            snapshot.get("cumulative") is not True or
            snapshot.get("host_binary_sha256") != identity["host_binary_sha256"] or
            snapshot.get("observation_schema") != identity["observation_schema"]):
        raise AssertionError("Final statistics schema, routing, or pinned host identity differs")
    expected = {key: warm_metrics[key] + measured_metrics[key]
                for key in ("completed_games", "censored_games", "draws", "seat0_wins")}
    expected["seat1_wins"] = expected["completed_games"]-expected["draws"]-expected["seat0_wins"]
    for key, count in expected.items():
        value = snapshot.get("totals", {}).get(key)
        if type(value) is not int or value != count:
            raise AssertionError("Statistics outcome count differs from observed terminals: " + key)
    return {"path": str(paths[0]), "file_sha256": hashlib.sha256(content).hexdigest(),
            "session_id": snapshot["session_id"], "final": True, "clean_exit": True,
            "verified_totals": expected,
            "publication_sequence": snapshot.get("publication_sequence")}


def run(args):
    budget = require_idle_ledger(args.ledger)
    v3.install()
    # The shared parity helper may have imported this module before installation.
    # Bind its captured names explicitly, as in the CPU frozen evaluator.
    import evaluate_checkpoints as checkpoints
    import learning_model
    checkpoints.identity = v3.variant_identity
    checkpoints.LearningPolicy = learning_model.LearningPolicy
    from evaluate_checkpoints import load_selection, materialize_policy, policy_hash
    from adaptive_actor import AdaptiveLearningActor
    selected = [load_selection(args.checkpoint, role) for role in ("learner", "champion")]
    identity4, _ = v4.variant_identity(TrainConfig(**selected[0].metadata["run_identity"]["configuration"]))
    torch.set_num_threads(1)
    torch.set_num_interop_threads(1)
    policies = [materialize_policy(selection, "cuda") for selection in selected]
    hashes = [policy_hash(policy) for policy in policies]
    actors = [AdaptiveLearningActor(policy, args.batch, graph=True) for policy in policies]
    reference, scratch = (ContiguousEpisodeStore(args.capacity) for _ in range(2))
    publication_source = HERE/"HostV4/statistics-request-benchmark.json"
    report = {"schema": "shards-frozen-host-v4-benchmark-v1", "state": "running", "optimizer_updates": 0,
        "source": selected[0].metadata, "candidate_identity": identity4, "trials": [],
        "scope": "One warmup cohort per host into its measured store, then identical complete-episode collection and behavior verification. Learning and startup are excluded; final flush/close is measured separately. No strength claim.",
        "periodic_publication": {"included_in_gpu_timings": False,
            "maximum_finished_games_per_host": 2*args.batch, "publication_cadence_completed_games": 10000,
            "reason": "Each host finishes fewer games than the periodic publication threshold; GPU timings measure passive counters, not periodic snapshot publication.",
            "separate_cpu_report": {"path": str(publication_source),
                "sha256": hashlib.sha256(publication_source.read_bytes()).hexdigest(),
                "scope": "Contended one-worker CPU request test with10,438 completed games and a10k publication; acquisition-prefix/concession workload, not a trained-policy GPU population."}}}
    save_json(args.output, report)
    for repeat in range(args.repeats):
        warm_seed = args.seed+repeat*2*args.batch
        flags = np.random.default_rng(47191+repeat).random(args.batch) < .25
        seats = (np.arange(args.batch)+repeat) % 2
        order = ["v3", *( ["v4-off", "v4-statistics"] if repeat % 2 == 0 else ["v4-statistics", "v4-off"] ), "v3-post"]
        reference_counts = None
        for name in order:
            require_idle_ledger(args.ledger)
            folder = args.output.parent/(args.output.stem+"-statistics")/f"repeat-{repeat}"
            factory = Factory(v3.BINARY if name.startswith("v3") else v4.BINARY,
                statistics=folder if name == "v4-statistics" else None, seed=warm_seed, batch=args.batch)
            pipeline_bench.subprocess = factory
            host = TracedHost(args.batch, args.workers, warm_seed, pinned=True, transport="shared", split_branches=8)
            try:
                store = reference if name == "v3" else scratch
                torch.manual_seed(77311+repeat)
                warm = collect_episodes(host, actors[0], store, version=selected[0].metadata["version"],
                    opponent=actors[1], opponent_lanes=flags, learner_seats=seats,
                    censor_truncated=True, seed_base=warm_seed)
                if not warm.completed: raise AssertionError("Warmup failed")
                host.trace = hashlib.sha256()
                torch.manual_seed(88317+repeat)
                torch.cuda.synchronize()
                start = time.perf_counter()
                result = collect_episodes(host, actors[0], store, version=selected[0].metadata["version"],
                    opponent=actors[1], opponent_lanes=flags, learner_seats=seats,
                    censor_truncated=True, seed_base=warm_seed+args.batch)
                parity = verify_behavior(policies[0], store)
                torch.cuda.synchronize()
                seconds = time.perf_counter()-start
                if not result.completed or not store.sealed: raise AssertionError("Incomplete frozen cohort")
                counts = {key: result.metrics[key] for key in ("completed_games", "censored_games", "wrapper_decisions",
                    "learning_rows", "action_kind_counts", "seat0_wins", "draws", "censored_learning_rows")}
                counts["action_trace_sha256"] = host.trace.hexdigest()
                if name == "v3": reference_counts = counts
                else:
                    exact_rollout(reference, store)
                    if counts != reference_counts: raise AssertionError("Host changed frozen game trajectories")
                before_close = time.perf_counter()
                host.close()
                close_seconds = time.perf_counter()-before_close
                if host.process.returncode != 0:
                    raise AssertionError("Benchmark host did not exit cleanly: " + str(host.process.returncode))
                trial = {"repeat": repeat, "variant": name, "seconds": seconds,
                    "rows_per_second": store.rows/seconds, "collection": result.metrics,
                    "exact_reference_match": True, "behavior_parity": parity, "counts": counts,
                    "final_flush_close_seconds": close_seconds, "host_returncode": host.process.returncode}
                if name == "v4-statistics":
                    validation = validate_statistics(folder, warm.metrics, result.metrics, identity4,
                        returncode=host.process.returncode, diagnostics=host.diagnostics)
                    trial["statistics_validation"] = validation
                    trial["statistics_completed_games"] = validation["verified_totals"]["completed_games"]
                report["trials"].append(trial)
                save_json(args.output, report)
                print(json.dumps({key: trial[key] for key in ("repeat", "variant", "seconds", "rows_per_second")}), flush=True)
            finally:
                host.close()
    if [policy_hash(policy) for policy in policies] != hashes or require_idle_ledger(args.ledger) != budget:
        raise AssertionError("Frozen test changed policy or budget")
    report.update(state="completed", frozen_weights_unchanged=True, ledger_unchanged=True)
    save_json(args.output, report)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--checkpoint", type=Path, required=True)
    parser.add_argument("--ledger", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--repeats", type=int, default=3)
    parser.add_argument("--batch", type=int, default=256)
    parser.add_argument("--workers", type=int, default=8)
    parser.add_argument("--capacity", type=int, default=524288)
    parser.add_argument("--seed", type=lambda value: int(value, 0), default=0x5700000000000000)
    args = parser.parse_args()
    if not 1 <= args.repeats <= 10 or not 1 <= args.batch <= 256 or not 1 <= args.workers <= 16 or args.output.exists():
        parser.error("Require fresh output, repeats1..10, batch1..256 and workers1..16")
    try:
        run(args)
    except BaseException as error:
        if args.output.exists():
            failed = json.loads(args.output.read_text())
            failed.update(state="failed", error={"type": type(error).__name__, "message": str(error)})
            save_json(args.output, failed)
        raise
