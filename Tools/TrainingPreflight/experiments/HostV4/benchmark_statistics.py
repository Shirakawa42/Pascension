"""Bounded CPU-only V3 / V4-off / V4-on matched request comparison."""
import argparse
import hashlib
import json
from pathlib import Path
import statistics
import struct
import tempfile
import time

import numpy as np

from check_and_benchmark import Host, OLD, NEW, compare, choices, digest


def cohort(*, active, repeat, publication=False):
    batch = 256
    directory = tempfile.TemporaryDirectory(prefix="shards-statistics-bench-")
    hosts = []
    try:
        for i, binary in enumerate((OLD, NEW, NEW)):
            hosts.append(Host(binary, batch, 11926, "shared", fixed_jit=True,
                statistics_directory=directory.name if i == 2 else None))
        rng = np.random.default_rng(48921)
        times = [[], [], []]; step_times = [[], [], []]; encode_times = [[], [], []]
        actions_hash = hashlib.sha256(); snapshots_seen = set()
        step = 0; deadline = time.monotonic() + 40
        while step < 256 if not publication else hosts[0].metrics[4] < 10240:
            if time.monotonic() > deadline:
                raise TimeoutError("Bounded CPU statistics benchmark exceeded forty seconds")
            for other in hosts[1:]:
                compare(hosts[0], other)
            action = choices(hosts[0], rng, step, active)
            if publication and step >= 192:
                # Force actual legal terminal concessions after a populated
                # acquisition prefix, to trigger real10k publication cheaply.
                for lane in range(batch):
                    legal = np.flatnonzero(hosts[0].mask[lane])
                    kinds = hosts[0].candidates[lane, legal, :16].argmax(1)
                    concede = legal[kinds == 11]
                    action[lane] = concede[0] if len(concede) else legal[0]
            packet = struct.pack("<I", 5) + action.tobytes(); actions_hash.update(packet)
            for order in range(3):
                which = (order + step + repeat) % 3
                elapsed = hosts[which].call(packet)
                if step >= 64:
                    times[which].append(elapsed)
                    step_times[which].append(hosts[which].metrics[0] / 1000)
                    encode_times[which].append(hosts[which].metrics[1] / 1000)
            if publication:
                for path in Path(directory.name).glob("session-*.json"):
                    if path.name.endswith(".error.json"):
                        raise RuntimeError(path.read_text())
                    latest = json.loads(path.read_text())
                    snapshots_seen.add(latest["totals"]["completed_games"])
            step += 1
        for other in hosts[1:]:
            compare(hosts[0], other)
        completed = int(hosts[0].metrics[4])
        if publication:
            # Wait for the already-enqueued10k background write, no additional
            # games or policy work. A deadline failure is explicit.
            until = time.monotonic() + 3
            while not any(count >= 10000 for count in snapshots_seen):
                if time.monotonic() >= until:
                    raise RuntimeError("Actual10k statistics publication did not finish")
                for path in Path(directory.name).glob("session-*.json"):
                    snapshot = json.loads(path.read_text())
                    if snapshot["schema"] != "shards-training-pool-stats-v1":
                        raise RuntimeError(str(snapshot))
                    snapshots_seen.add(snapshot["totals"]["completed_games"])
                time.sleep(.002)
        for host in hosts:
            host.close()
        hosts = []
        files = list(Path(directory.name).glob("session-*.json"))
        assert len(files) == 1
        snapshot = json.loads(files[0].read_text())
        assert snapshot["totals"]["completed_games"] == completed and snapshot["final"]
        history = list((Path(directory.name) / "history").glob("*.json"))
        totals = [sum(sample) for sample in times]
        return {"active_lanes": active, "batch": batch, "workers": 1, "repeat": repeat,
            "publication_workload": publication, "warmup_requests": 64,
            "measured_requests": len(times[0]), "fixed_jit": True,
            "modes": ["v3", "v4_statistics_off", "v4_statistics_on"],
            "request_seconds": totals, "step_seconds": [sum(s) for s in step_times],
            "encode_seconds": [sum(s) for s in encode_times],
            "median_request_ms": [statistics.median(s) * 1000 for s in times],
            "v4_on_vs_v3_request_speedup": totals[0] / totals[2],
            "statistics_increment_over_v4_off_fraction": totals[2] / totals[1] - 1,
            "completed_games": completed, "all_response_bytes_equal": True,
            "action_stream_sha256": actions_hash.hexdigest(),
            "statistics_rows": len(snapshot["rows"]), "hero_choice_rows": len(snapshot["hero_choice_rows"]),
            "latest_bytes": files[0].stat().st_size, "history_snapshots": len(history),
            "nonfinal_completed_counts_seen": sorted(snapshots_seen)}
    finally:
        for host in hosts:
            host.close()
        directory.cleanup()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--repeats", type=int, default=3)
    parser.add_argument("--publication-only", action="store_true")
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    cases = []
    if not args.publication_only:
        for active in (256, 8):
            for repeat in range(args.repeats):
                cases.append(cohort(active=active, repeat=repeat))
    cases.append(cohort(active=256, repeat=0, publication=True))
    result = {"schema": "shards-host-v4-statistics-request-benchmark-v1", "passed": True,
        "host_v3_sha256": digest(OLD), "host_v4_sha256": digest(NEW),
        "scope": "Contended one-worker CPU request comparison; no GPU, learner, ledger or live runtime edits. Publication case uses actual concessions after an acquisition prefix, not a trained-policy population.",
        "cases": cases}
    args.output.write_text(json.dumps(result, indent=2) + "\n")
    print(json.dumps({"passed": True, "cases": len(cases), "output": str(args.output)}))


if __name__ == "__main__":
    main()
