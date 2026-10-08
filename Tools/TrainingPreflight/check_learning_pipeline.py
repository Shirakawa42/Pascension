"""Real complete-episode collection/likelihood checks, with no learning updates."""
import argparse
import time

import numpy as np
import torch

from bench_common import Monitor, metadata, save_json
from learning_model import LearningActor, LearningPolicy, PolicyConfig
from learning_rollout import EpisodeStore, LearningHost, collect_episodes
from train_campaign import verify_behavior


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", required=True)
    parser.add_argument("--adaptive", action="store_true")
    args = parser.parse_args()
    torch.set_num_threads(1)
    torch.backends.fp32_precision = "ieee"
    torch.backends.cuda.matmul.fp32_precision = "tf32"
    torch.manual_seed(60926)
    store = EpisodeStore(524288)
    result = {"metadata": metadata(), "outcome_learning_performed": False, "adaptive": args.adaptive, "cases": []}
    for batch, workers, width in ((128, 4, 128), (256, 4, 128), (256, 8, 128), (256, 8, 256)):
        policy = LearningPolicy(PolicyConfig(width=width)).cuda()
        if args.adaptive:
            from adaptive_actor import AdaptiveLearningActor
            actor = AdaptiveLearningActor(policy, batch, graph=True)
            opponent = AdaptiveLearningActor(policy, batch, graph=True)
        else:
            actor = LearningActor(policy, batch, graph=True)
            opponent = LearningActor(policy, batch, graph=True)
        host = LearningHost(batch, workers, 0x3000000000000000, pinned=True, transport="shared", split_branches=8)
        try:
            with Monitor() as monitor:
                started = time.monotonic()
                collected = collect_episodes(host, actor, store, version=0, opponent=opponent,
                    opponent_lanes=np.arange(batch)%4 == 0, learner_seats=np.arange(batch)%2)
                parity = verify_behavior(policy, store)
                elapsed = time.monotonic()-started
            if not collected.completed:
                raise AssertionError("Complete episode probe stopped early")
            result["cases"].append({"batch": batch, "workers": workers, "width": width,
                **collected.metrics, "parity": parity, "seconds_with_verification": elapsed,
                "learning_rows_per_second": store.rows/elapsed, "gpu_monitor": monitor.summary()})
            print({"batch": batch, "workers": workers, "width": width,
                   "rows": store.rows, "seconds": elapsed, "parity": parity}, flush=True)
        finally:
            host.close()
    save_json(args.output, result)


if __name__ == "__main__":
    main()
