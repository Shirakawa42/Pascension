"""Read-only, CPU-only snapshot of live training metrics/checkpoint diagnostics.

Writes derived JSON and an immutable checkpoint copy only to --output's folder.
Never opens the budget lock, creates an actor/optimizer, or initializes CUDA.
"""
import argparse
from collections import Counter, defaultdict
import datetime as dt
import hashlib
import json
import math
from pathlib import Path
import statistics
import time


def mean(values):
    values = [value for value in values if value is not None]
    return statistics.fmean(values) if values else None


def utc(row):
    return dt.datetime.fromisoformat(row["utc"].replace("Z", "+00:00")).timestamp()


def analyze(campaign, output):
    source = campaign / "main" / "metrics.jsonl"
    raw = source.read_bytes()
    complete = raw[:raw.rfind(b"\n") + 1]
    events = [json.loads(line) for line in complete.splitlines() if line.strip()]
    generations = [row for row in events if row.get("event") == "generation"]
    start = next(row for row in events if row.get("event") == "session_start")
    config = start["configuration"]

    def window(lo, hi):
        rows = generations[lo:hi]
        before = generations[lo-1]["charged_seconds"] if lo else start["charged_seconds"]
        elapsed = rows[-1]["charged_seconds"]-before
        counts = [sum(row["action_kind_counts"][i] for row in rows) for i in range(16)]
        opponents = defaultdict(lambda: {"generations": 0, "games": 0, "score_sum": 0.})
        for row in rows:
            item = opponents[row["opponent_version"]]
            item["generations"] += 1
            n = row.get("archive_completed_games", row["archive_games"])
            item["games"] += n
            item["score_sum"] += n * (row["archive_score"] or 0)
        for item in opponents.values():
            item["mean_score"] = item.pop("score_sum")/item["games"] if item["games"] else None
        attempted = sum(row["attempted_games"] for row in rows)
        completed = sum(row["completed_games"] for row in rows)
        return {"generation_range": [rows[0]["generation"], rows[-1]["generation"]],
            "utc_range": [rows[0]["utc"], rows[-1]["utc"]], "generations": len(rows),
            "charged_seconds": elapsed, "fresh_learning_rows": sum(row["learning_rows"] for row in rows),
            "rows_per_charged_second": sum(row["learning_rows"] for row in rows)/elapsed,
            "completed_games": completed, "attempted_games": attempted,
            "censored_games": sum(row["censored_games"] for row in rows),
            "censored_learning_rows": sum(row["censored_learning_rows"] for row in rows),
            "seat0_win_fraction": sum(row["seat0_wins"] for row in rows)/completed,
            "draw_fraction": sum(row["draws"] for row in rows)/completed,
            "mean_episode_decisions": sum(row["mean_episode_decisions"]*row["attempted_games"] for row in rows)/attempted,
            "maximum_episode_decisions": max(row["max_episode_decisions"] for row in rows),
            "mean_lane_occupancy": mean(row["lane_occupancy"] for row in rows),
            "mean_sample_reuse": mean(row["sample_reuse"] for row in rows),
            "rejected_minibatches": sum(row["rejected_minibatches"] for row in rows),
            "accepted_optimizer_steps": sum(row["accepted_optimizer_steps"] for row in rows),
            "metrics_mean": {key: mean(row["metrics"].get(key) for row in rows) for key in
                ("loss", "policy_loss", "value_loss", "entropy", "approx_kl", "clip_fraction", "grad_norm")},
            "max_behavior_errors": {key: max(row["behavior_parity"][key] for row in rows) for key in
                ("max_abs_behavior_logp_error", "max_abs_behavior_value_error")},
            "finite_failures": sum(any(value is False for value in row["finite"].values()) for row in rows),
            "action_counts": counts, "action_fractions": [count/sum(counts) for count in counts],
            "component_seconds": {key: sum(row[key] for row in rows) for key in
                ("collection_seconds", "verification_seconds", "learning_seconds", "actor_seconds", "host_seconds", "storage_seconds")},
            "opponents": dict(sorted(opponents.items()))}

    result = {"captured_wall": time.time(), "metrics_path": str(source),
        "metrics_bytes": len(complete), "metrics_sha256": hashlib.sha256(complete).hexdigest(),
        "metrics_incomplete_tail_bytes_ignored": len(raw)-len(complete), "configuration": config,
        "all": window(0, len(generations)), "windows": {},
        "evaluations": [row for row in events if row.get("event") == "champion_evaluation"]}
    count = min(100, len(generations))
    for name, lo, hi in (("first100", 0, count), ("previous100", max(0, len(generations)-2*count), len(generations)-count),
                         ("last100", len(generations)-count, len(generations))):
        if hi > lo:
            result["windows"][name] = window(lo, hi)
    for minutes in (15, 30, 60):
        selected = next((i for i, row in enumerate(generations) if utc(row) >= utc(generations[-1])-minutes*60), 0)
        result["windows"][f"last{minutes}min"] = window(selected, len(generations))
    result["external_evaluations"] = [json.loads(path.read_text()) for path in sorted((campaign/"evaluations").glob("*.json"))]

    import torch
    torch.set_num_threads(1)
    torch.set_num_interop_threads(1)
    from campaign_state import load_checkpoint
    checkpoint_bytes = (campaign/"main/latest.soicp").read_bytes()
    output.parent.mkdir(parents=True, exist_ok=True)
    copied = output.with_suffix(".soicp")
    copied.write_bytes(checkpoint_bytes)
    expected = json.loads((campaign/"main/identity.json").read_text())
    payload = load_checkpoint(copied, expected_identity=expected)
    state = payload["state"]

    def policy_stats(policy):
        digest = hashlib.sha256()
        norms, maximum, elements = [], 0., 0
        for name, value in sorted(policy.items()):
            digest.update(name.encode()); digest.update(value.numpy().tobytes())
            if value.is_floating_point():
                norms.append(float(value.double().square().sum()))
                maximum = max(maximum, float(value.abs().max()))
            elements += value.numel()
        return {"sha256": digest.hexdigest(), "l2_norm": math.sqrt(sum(norms)),
                "max_abs": maximum, "elements_including_buffers": elements}

    policies = {"learner": policy_stats(state["learner"]["policy"]),
                "initial": policy_stats(state["initial_policy"]),
                "champion": {"version": state["champion"]["version"], **policy_stats(state["champion"]["policy"])},
                "archive": [{"version": row["version"], **policy_stats(row["policy"])} for row in state["archive"]]}
    result["checkpoint"] = {"copy": str(copied), "file_sha256": hashlib.sha256(checkpoint_bytes).hexdigest(),
        "payload_sha256": payload["file_sha256"], "identity": payload["identity"],
        "generation": state["generations"], "games": state["games"], "decisions": state["decisions"],
        "optimizer_passes": state["optimizer_passes"], "eval_index": state["eval_index"],
        "charged_seconds_at_checkpoint": payload["budget"]["charged_seconds"],
        "learner_metadata": {key: value for key, value in state["learner"].items()
                             if key in ("format", "policy_config", "ppo_config", "updates", "rejected")},
        "policies": policies}
    result["cuda_initialized"] = torch.cuda.is_initialized()
    if result["cuda_initialized"]:
        raise RuntimeError("Audit unexpectedly initialized CUDA")
    output.write_text(json.dumps(result, indent=2, allow_nan=False)+"\n")
    return result


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--campaign", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    result = analyze(args.campaign, args.output)
    print(json.dumps({"output": str(args.output), "last_generation": result["all"]["generation_range"][-1],
                      "checkpoint_generation": result["checkpoint"]["generation"],
                      "cuda_initialized": result["cuda_initialized"]}))
