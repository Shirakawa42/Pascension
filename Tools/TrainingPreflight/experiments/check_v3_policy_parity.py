"""Prove a migrated policy preserves sampled real games before its first update."""
import argparse
import json
from pathlib import Path
import sys
import time

import numpy as np
import torch

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE.parent))
import evaluate_checkpoints as evaluation
from adaptive_actor import AdaptiveLearningActor
from bench_common import save_json
from learning_rollout import EpisodeStore, collect_episodes
from train_campaign import verify_behavior
from benchmark_candidates import TracedHost, require_idle_ledger
from rollout_fastpath import ContiguousEpisodeStore
from variant_runtime import install, variant_identity, IeeeLearningPolicy, SLOTS


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--source", type=Path, required=True)
    parser.add_argument("--migrated", type=Path, required=True)
    parser.add_argument("--ledger", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    ledger = require_idle_ledger(args.ledger)
    torch.set_num_threads(1)
    torch.set_num_interop_threads(1)
    torch.backends.fp32_precision = "ieee"
    torch.backends.cuda.matmul.fp32_precision = "ieee"
    source = [evaluation.load_selection(args.source, role) for role in ("learner", "champion")]
    old_policies = [evaluation.materialize_policy(s, "cuda") for s in source]
    old_actors = [AdaptiveLearningActor(p, 256, graph=True) for p in old_policies]
    torch.manual_seed(47631)
    cpu_rng, gpu_rng = torch.get_rng_state(), torch.cuda.get_rng_state()
    flags = np.random.default_rng(971).random(256)<.25
    seats = np.arange(256)%2
    old_store = EpisodeStore()
    host = TracedHost(256, 8, 0x5200000000000000, pinned=True, transport="shared", split_branches=8)
    try:
        before = collect_episodes(host, old_actors[0], old_store, version=source[0].metadata["version"],
                                  opponent=old_actors[1], opponent_lanes=flags, learner_seats=seats, censor_truncated=True)
        old_trace = host.action_trace.hexdigest()
        old_parity = verify_behavior(old_policies[0], old_store)
    finally:
        host.close()
    if bool(old_store.obs[:old_store.rows, list(SLOTS)].any()):
        raise AssertionError("V2 rollout violates reserved-zero-column proof")
    install()
    # The loader was imported before installation to read the source revision.
    # Rebind its documented identity/model dependencies for the target load.
    evaluation.identity = variant_identity
    evaluation.LearningPolicy = IeeeLearningPolicy
    target = [evaluation.load_selection(args.migrated, role) for role in ("learner", "champion")]
    new_policies = [evaluation.materialize_policy(s, "cuda") for s in target]
    new_actors = [AdaptiveLearningActor(p, 256, graph=True) for p in new_policies]
    new_store = ContiguousEpisodeStore()
    torch.set_rng_state(cpu_rng)
    torch.cuda.set_rng_state(gpu_rng)
    host = TracedHost(256, 8, 0x5200000000000000, pinned=True, transport="shared", split_branches=8)
    try:
        after = collect_episodes(host, new_actors[0], new_store, version=target[0].metadata["version"],
                                 opponent=new_actors[1], opponent_lanes=flags, learner_seats=seats, censor_truncated=True)
        new_trace = host.action_trace.hexdigest()
        new_parity = verify_behavior(new_policies[0], new_store)
    finally:
        host.close()
    if old_trace != new_trace or old_store.rows != new_store.rows:
        raise AssertionError("Migration changed sampled games before learning")
    n = old_store.rows
    legacy_columns = [i for i in range(2048) if i not in SLOTS]
    if not torch.equal(old_store.obs[:n, legacy_columns], new_store.obs[:n, legacy_columns]):
        raise AssertionError("Migration changed legacy observations")
    for name in ("candidates", "mask", "actions", "old_logp", "old_values", "returns", "advantages"):
        if not torch.equal(getattr(old_store, name)[:n], getattr(new_store, name)[:n]):
            raise AssertionError("Migration changed retained "+name)
    for name in ("episode_ids", "seats"):
        if not np.array_equal(getattr(old_store, name)[:n], getattr(new_store, name)[:n]):
            raise AssertionError("Migration changed "+name)
    if not bool(new_store.obs[:n, list(SLOTS)].any()):
        raise AssertionError("V3 Allegiance features were never populated")
    if require_idle_ledger(args.ledger) != ledger:
        raise AssertionError("Parity test changed campaign ledger")
    report = {"passed": True, "source": source[0].metadata, "target": target[0].metadata,
              "all_actions_sha256": old_trace, "retained_rows": n, "before": before.metrics,
              "after": after.metrics, "source_behavior_parity": old_parity,
              "target_behavior_parity": new_parity, "reserved_columns": list(SLOTS),
              "all_legacy_features_actions_packets_credit_exact": True,
              "new_features_populated": True, "optimizer_updates": 0, "ledger_unchanged": True}
    save_json(args.output, report)
    print(json.dumps({key: report[key] for key in ("passed", "retained_rows", "all_actions_sha256", "target_behavior_parity")}))


if __name__ == "__main__":
    main()
