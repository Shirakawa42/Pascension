#!/usr/bin/env python3
"""Tiny loss/gradient sticky-flag regression; no model, optimizer or training.

Run only in an allocated GPU window, under an outer timeout. A nonfinite
gradient-norm signal with a finite loss must remain visible even after a later
healthy graph replay, and the existing group-boundary check must reject it.
"""

import argparse
import hashlib
import json
from pathlib import Path
import time

import torch

from gpu_bench import capture
from pipeline_bench import LearnerLoad


def run_case(mode, failure):
    # Exercise precisely the production flag recorder and group-boundary check,
    # without allocating a model or triggering any disposable optimizer work.
    learner = object.__new__(LearnerLoad)
    learner.health = torch.zeros((), dtype=torch.int32, device="cuda")
    learner.failed = False
    loss = torch.tensor(.25, device="cuda")
    norm = torch.tensor(.5, device="cuda")
    step = lambda: learner.record_health(loss, norm)
    owner = None
    if mode == "graph":
        step, owner = capture(step)
    else:
        step()
    torch.cuda.synchronize()
    if int(learner.health.item()) != 0:
        raise AssertionError("Healthy loss/gradient unexpectedly set a failure flag")
    expected_bit = 2 if failure == "gradient_norm" else 1
    (norm if failure == "gradient_norm" else loss).fill_(float("inf"))
    step()
    # No intermediate CPU read/reset: a later finite iteration must not hide
    # the earlier failure when the group finally synchronizes.
    loss.fill_(.25)
    norm.fill_(.5)
    step()
    torch.cuda.synchronize()
    observed = int(learner.health.item())
    if observed != expected_bit:
        raise AssertionError(f"Transient failure was not retained: {observed}, expected {expected_bit}")
    try:
        learner.check_health("focused regression")
    except RuntimeError as error:
        rejection = str(error)
    else:
        raise AssertionError("Group-boundary check accepted a retained failure")
    if not learner.failed:
        raise AssertionError("Rejected learner was not marked unusable")
    if failure == "gradient_norm" and not bool(torch.isfinite(loss)):
        raise AssertionError("Gradient-only test accidentally relied on a bad loss")
    return {"mode": mode, "injected_failure": failure, "expected_flag": expected_bit,
            "observed_flag": observed, "later_healthy_iteration_does_not_clear_failure": True,
            "group_boundary_rejected_failure": True, "learner_marked_failed": True,
            "rejection": rejection}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    started = time.monotonic()
    torch.set_num_threads(1)
    with torch.no_grad():
        cases = [run_case(mode, failure) for mode in ("eager", "graph")
                 for failure in ("gradient_norm", "loss")]
    report = {"schema": "shards-synthetic-learner-health-regression-v1",
              "training_campaign_started": False, "semantic_version": LearnerLoad.SEMANTIC_VERSION,
              "scope": "Four tiny failure-flag checks; no model/optimizer or throughput measurement",
              "torch": torch.__version__, "seconds": time.monotonic() - started, "cases": cases,
              "source_sha256": {name: hashlib.sha256(Path(__file__).with_name(name).read_bytes()).hexdigest()
                                for name in ("check_learner_health.py", "pipeline_bench.py", "gpu_bench.py")}}
    args.output.parent.mkdir(parents=True, exist_ok=True)
    temporary = args.output.with_suffix(args.output.suffix + ".tmp")
    temporary.write_text(json.dumps(report, indent=2, allow_nan=False) + "\n")
    temporary.replace(args.output)
    print({"passed": len(cases), "semantic_version": LearnerLoad.SEMANTIC_VERSION})


if __name__ == "__main__":
    main()
