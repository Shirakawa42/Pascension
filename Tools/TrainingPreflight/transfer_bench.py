"""Bounded persistent/pageable transfer benchmark, measured with completion waits."""
from __future__ import annotations

import argparse
import time

import torch

from bench_common import Monitor, distribution, metadata, save_json


def benchmark(batch, dtype, pinned, nonblocking, seconds, repeats):
    # Same dense footprint as host: observation + candidates + legal mask.
    elements = batch * (2048 + 64 * 32 + 64)
    host = torch.ones(elements, dtype=dtype, pin_memory=pinned)
    device = torch.empty(elements, device="cuda", dtype=dtype)
    gpu_actions = torch.zeros(batch, device="cuda", dtype=torch.int32)
    cpu_actions = torch.empty(batch, dtype=torch.int32, pin_memory=pinned)

    def step():
        device.copy_(host, non_blocking=nonblocking)
        cpu_actions.copy_(gpu_actions, non_blocking=nonblocking)
        torch.cuda.synchronize()

    for _ in range(10):
        step()
    rates, latencies, iterations, requests = [], [], [], []
    for _ in range(repeats):
        start, count = time.perf_counter(), 0
        while time.perf_counter() - start < seconds:
            tick = time.perf_counter()
            step()
            requests.append((time.perf_counter() - tick)*1000)
            count += 1
        elapsed = time.perf_counter() - start
        rates.append(elements * host.element_size() * count / elapsed / 1e9)
        latencies.append(elapsed * 1000 / count)
        iterations.append(count)
    assert bool(torch.all(device == 1)) and bool(torch.all(cpu_actions == 0))
    return {"batch": batch, "dtype": str(dtype), "persistent_pinned": pinned,
            "non_blocking": nonblocking,
            "upload_bytes": elements * host.element_size(), "repeat_mean_roundtrip_ms": distribution(latencies),
            "request_roundtrip_ms": distribution(requests),
            "effective_upload_GBps": distribution(rates), "iterations": iterations}


def main():
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument("--batches", default="1,32,128,512,1024")
    p.add_argument("--seconds", type=float, default=.25)
    p.add_argument("--repeats", type=int, default=3)
    p.add_argument("--output", required=True)
    args = p.parse_args()
    if not 0 < args.seconds <= 10 or not 1 <= args.repeats <= 10:
        p.error("Use bounded probes: 0<seconds<=10 and 1<=repeats<=10")
    torch.set_num_threads(1)
    result = {"metadata": metadata(), "kind": "transfer_only_no_policy_or_simulation", "cases": []}
    with Monitor() as monitor:
        for batch in map(int, args.batches.split(",")):
            if not 1 <= batch <= 4096:
                p.error("batch must be 1..4096")
            for dtype in (torch.float32, torch.float16):
                for pinned, nonblocking in ((False,False),(False,True),(True,True)):
                    row = benchmark(batch, dtype, pinned, nonblocking, args.seconds, args.repeats)
                    result["cases"].append(row)
                    print(f"batch={batch} dtype={dtype} pinned={pinned} nonblocking={nonblocking}: {row['repeat_mean_roundtrip_ms']['median']:.3f} ms", flush=True)
                    save_json(args.output, result)
    result["gpu_monitor"] = monitor.summary()
    save_json(args.output, result)


if __name__ == "__main__":
    main()
