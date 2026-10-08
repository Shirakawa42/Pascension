"""Check seat-swapped search comparisons and summarize matched self-play timing.

Confidence bounds resample seed pairs, not the two correlated games separately.
No files are published to the balance dashboard.
"""
import argparse
import collections
import json
from pathlib import Path
import random


def read(path):
    data = json.loads(path.read_text())
    assert data["games"] == data["total"] == len(data["rows"])
    assert len({r["index"] for r in data["rows"]}) == data["games"]
    assert data["draws"] == sum(r["winner"] < 0 for r in data["rows"])
    return data


def paired(data, control=False):
    groups = collections.defaultdict(list)
    for row in data["rows"]:
        groups[row["seed"]].append(row)
    scores = []
    for rows in groups.values():
        assert len(rows) == 2 and {r["treatment"] for r in rows} == {0, 1}
        assert len({(r["hero0"], r["hero1"]) for r in rows}) == 1
        if control:
            for key in ("winner", "rounds", "steps", "overrides"):
                assert rows[0][key] == rows[1][key], (key, rows)
        scores.append(sum(.5 if r["winner"] < 0 else float(r["winner"] == r["treatment"]) for r in rows) / 2)
    rng = random.Random(927691)
    samples = sorted(sum(rng.choices(scores, k=len(scores))) / len(scores) for _ in range(20000))
    return {
        "games": data["games"], "independent_seed_pairs": len(groups),
        "wins": data["wins"], "losses": data["losses"], "draws": data["draws"],
        "score": sum(scores) / len(scores),
        "paired_bootstrap_95_percent": [samples[500], samples[19499]],
        "pair_scores": dict(collections.Counter(scores)),
        "seats": [{"seat": seat, "games": sum(r["treatment"] == seat for r in data["rows"]),
                   "wins": sum(r["treatment"] == seat and r["winner"] == seat for r in data["rows"])} for seat in (0, 1)],
        "control_identical_trajectories": control,
    }


def timing(data):
    return {"games": data["games"], "seconds": data["seconds"],
            "games_per_second": data["games_per_second"], "decisions": data["decisions"],
            "decisions_per_second": data["decisions"] / data["seconds"],
            "mean_rounds": sum(r["rounds"] for r in data["rows"]) / len(data["rows"]),
            "inference_rows": data["inferenceRows"], "branches": data["branches"],
            "clone_seconds": data["cloneSeconds"], "rollout_seconds": data["rolloutSeconds"],
            "finishing_search_seconds": data["terminalSeconds"],
            "hybrid_diagnostics": data.get("hybridDiagnostics")}


def main():
    parser = argparse.ArgumentParser()
    for key in ("paired", "control", "current-speed", "hybrid-speed", "output"):
        parser.add_argument("--" + key, type=Path, required=True)
    args = parser.parse_args()
    current, hybrid = read(args.current_speed), read(args.hybrid_speed)
    key = lambda r: (r["index"], r["seed"], r["hero0"], r["hero1"])
    assert sorted(map(key, current["rows"])) == sorted(map(key, hybrid["rows"]))
    assert current["mode"] == hybrid["mode"] == "both"
    assert current["config"]["Workers"] == hybrid["config"]["Workers"] == 8
    result = {"strength": paired(read(args.paired)), "control": paired(read(args.control), True),
              "current_selfplay": timing(current), "hybrid_selfplay": timing(hybrid),
              "throughput_ratio": hybrid["games_per_second"] / current["games_per_second"],
              "caveat": "Small fixed-budget experiment. Timing includes changed trajectories, not only implementation cost. Two games per seed are correlated. Not a balance-statistics cohort."}
    args.output.write_text(json.dumps(result, indent=2) + "\n")
    print(json.dumps(result, indent=2))


if __name__ == "__main__":
    main()
