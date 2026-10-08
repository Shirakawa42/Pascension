"""Frozen actor/learner batch-shape differential; never updates a model."""
import argparse
import json
from pathlib import Path
import sys
import time

import numpy as np
import torch

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE.parent))
from adaptive_actor import AdaptiveLearningActor
from bench_common import save_json
from evaluate_checkpoints import load_selection, materialize_policy
from learning_rollout import EpisodeStore, LearningHost, collect_episodes
from benchmark_candidates import require_idle_ledger


@torch.inference_mode()
def compare_store(policy, store):
    worst = {"max_logp_error": 0., "max_value_error": 0., "row": 0}
    for start in range(0, store.rows, 2048):
        end = min(start+2048, store.rows)
        logits, values = policy(store.obs[start:end], store.candidates[start:end], store.mask[start:end])
        logp = logits.log_softmax(-1).gather(1, store.actions[start:end, None]).squeeze(1)
        errors = (logp-store.old_logp[start:end]).abs()
        value_error = float((values-store.old_values[start:end]).abs().max())
        error, index = errors.max(dim=0)
        if float(error) > worst["max_logp_error"]:
            worst.update(max_logp_error=float(error), row=start+int(index),
                         stored_logp=float(store.old_logp[start+int(index)]),
                         recomputed_logp=float(logp[index]))
        worst["max_value_error"] = max(worst["max_value_error"], value_error)
    return worst


@torch.inference_mode()
def shape_probe(policy, obs, candidates, mask):
    result = []
    for precision in ("tf32", "ieee"):
        torch.backends.cuda.matmul.fp32_precision = precision
        outputs = {}
        for size in (1, 32, 64, 128, 256, 2048):
            o = obs.expand(size, -1).contiguous()
            c = candidates.expand(size, -1, -1).contiguous()
            m = mask.expand(size, -1).contiguous()
            logits, value = policy(o, c, m)
            outputs[size] = (logits[0].log_softmax(-1), value[0])
        base_logp, base_value = outputs[2048]
        for size, (logp, value) in outputs.items():
            result.append({"precision": precision, "batch": size,
                           "max_legal_logp_difference_vs_2048": float((logp-base_logp)[mask[0]].abs().max()),
                           "value_difference_vs_2048": float((value-base_value).abs())})
    return result


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--checkpoint", type=Path, required=True)
    parser.add_argument("--ledger", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--cohorts", type=int, default=3)
    args = parser.parse_args()
    ledger = require_idle_ledger(args.ledger)
    torch.set_num_threads(1)
    torch.set_num_interop_threads(1)
    torch.backends.fp32_precision = "ieee"
    selection = load_selection(args.checkpoint)
    other = load_selection(args.checkpoint, "champion")
    policy, opposing = (materialize_policy(x, "cuda") for x in (selection, other))
    report = {"schema": "shards-behavior-precision-diagnosis-v1", "state": "running",
              "checkpoint": selection.metadata, "optimizer_updates": 0, "trials": []}
    store = EpisodeStore()
    for precision in ("tf32", "ieee"):
        torch.backends.cuda.matmul.fp32_precision = precision
        actors = [AdaptiveLearningActor(p, 256, graph=True) for p in (policy, opposing)]
        for cohort in range(args.cohorts):
            require_idle_ledger(args.ledger)
            torch.manual_seed(8301+cohort)
            host = LearningHost(256, 8, 0x5100000000000000+cohort*256, pinned=True, transport="shared", split_branches=8)
            try:
                start = time.perf_counter()
                collection = collect_episodes(host, actors[0], store, version=selection.metadata["version"],
                    opponent=actors[1], opponent_lanes=np.random.default_rng(82+cohort).random(256)<.25,
                    learner_seats=(np.arange(256)+cohort)%2, censor_truncated=True)
                worst = compare_store(policy, store)
                seconds = time.perf_counter()-start
                row = slice(worst["row"], worst["row"]+1)
                artifact = args.output.with_name(args.output.stem+f"-{precision}-{cohort}.npz")
                np.savez(artifact, obs=store.obs[row].cpu().numpy(), candidates=store.candidates[row].cpu().numpy(),
                         mask=store.mask[row].cpu().numpy(), action=store.actions[row].cpu().numpy(),
                         stored_logp=store.old_logp[row].cpu().numpy())
                shapes = shape_probe(policy, store.obs[row], store.candidates[row], store.mask[row])
                # shape_probe changes the global setting, so restore before next cohort.
                torch.backends.cuda.matmul.fp32_precision = precision
                trial = {"precision": precision, "cohort": cohort, "rows": store.rows,
                         "seconds": seconds, "collection": collection.metrics, "worst": worst,
                         "shape_probe": shapes, "worst_input_artifact": str(artifact)}
                report["trials"].append(trial)
                save_json(args.output, report)
                print(json.dumps({"precision": precision, "cohort": cohort, "rows": store.rows,
                                  "seconds": seconds, **worst}), flush=True)
            finally:
                host.close()
        del actors
        torch.cuda.empty_cache()
    if require_idle_ledger(args.ledger) != ledger:
        raise RuntimeError("Frozen diagnostic changed ledger")
    report.update(state="completed", ledger_unchanged=True)
    save_json(args.output, report)


if __name__ == "__main__":
    main()
