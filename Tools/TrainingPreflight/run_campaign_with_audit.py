"""Run one supervised training session, then frozen evaluations outside its budget.

This wrapper never retries failed training. Evaluation failures are recorded
separately and never discard a trained checkpoint.
"""
import argparse
import os
from pathlib import Path
import signal
import subprocess
import sys
import time

from bench_common import save_json

HERE = Path(__file__).resolve().parent


def run(args):
    args.run_dir.mkdir(parents=True, exist_ok=True)
    status_path = args.run_dir / "post-evaluation.json"
    evaluation_dir = args.run_dir.parent / "evaluations"
    evaluation_dir.mkdir(exist_ok=True)
    active = None
    evaluation_active = False
    stopping = False

    def stop(signum, frame):
        nonlocal stopping
        stopping = True
        if active is not None and active.poll() is None:
            try:
                if evaluation_active:
                    os.killpg(active.pid, signal.SIGTERM)
                else:
                    active.send_signal(signal.SIGTERM)
            except ProcessLookupError:
                pass

    signal.signal(signal.SIGTERM, stop)
    signal.signal(signal.SIGINT, stop)

    def execute(command, *, evaluation=False):
        nonlocal active, evaluation_active
        evaluation_active = evaluation
        active = subprocess.Popen(command, start_new_session=True)
        try:
            return active.wait(timeout=3600 if evaluation else None)
        except subprocess.TimeoutExpired:
            os.killpg(active.pid, signal.SIGKILL)
            active.wait()
            return 124
        finally:
            if evaluation:
                # Clean only descendants of this owned evaluation process.
                try:
                    os.killpg(active.pid, signal.SIGKILL)
                except ProcessLookupError:
                    pass
            active = None

    save_json(status_path, {"state": "waiting_for_training", "updated_wall": time.time()})
    entry = getattr(args, "variant_entry", None)
    training_prefix = ([sys.executable, str(entry), "supervise"] if entry else
                       [sys.executable, str(HERE / "supervise_training.py")])
    command = [*training_prefix,
               "--run-dir", str(args.run_dir), "--ledger", str(args.ledger),
               "--seconds", str(args.seconds), "--config", str(args.config),
               "--label", args.label]
    if args.resume:
        command += ["--resume", str(args.resume)]
    code = execute(command)
    if code or stopping:
        save_json(status_path, {"state": "stopped" if stopping else "skipped_training_failed", "returncode": code,
                               "updated_wall": time.time(), "automatic_retry": False})
        return code
    checkpoint = args.run_dir / "latest.soicp"
    reports = []
    for index, role in enumerate(("champion", "initial")):
        if stopping:
            save_json(status_path, {"state": "stopped", "completed_reports": reports,
                                   "updated_wall": time.time(), "automatic_retry": False})
            return 0
        output = evaluation_dir / f"{args.run_dir.name}-final-vs-{role}.json"
        save_json(status_path, {"state": "running", "opponent_role": role,
                               "completed_reports": reports, "updated_wall": time.time()})
        evaluation_prefix = ([sys.executable, str(entry), "evaluate"] if entry else
                             [sys.executable, str(HERE / "evaluate_checkpoints.py")])
        evaluation = [*evaluation_prefix,
                      "--a", str(checkpoint), "--b", str(checkpoint),
                      "--a-role", "learner", "--b-role", role,
                      "--games", str(args.audit_games), "--batch", "128", "--workers", "8",
                      "--seed", str(0x4000000000000000 + index * 1000000),
                      "--sampling-seed", str(40926 + index), "--telemetry", "--output", str(output)]
        code = execute(evaluation, evaluation=True)
        if code:
            save_json(status_path, {"state": "failed", "opponent_role": role,
                                   "returncode": code, "completed_reports": reports,
                                   "updated_wall": time.time(), "automatic_retry": False})
            return code
        reports.append(str(output))
    save_json(status_path, {"state": "complete", "completed_reports": reports,
                           "updated_wall": time.time(), "checkpoint": str(checkpoint),
                           "scope": "Frozen paired matches; card choices describe usage, not causal balance."})
    return 0


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--run-dir", type=Path, required=True)
    parser.add_argument("--ledger", type=Path, required=True)
    parser.add_argument("--seconds", type=float, default=43200)
    parser.add_argument("--config", type=Path, required=True)
    parser.add_argument("--resume", type=Path)
    parser.add_argument("--label", default="main-training")
    parser.add_argument("--variant-entry", type=Path,
                        help="Explicit identified continuation entry, used for both training and frozen audits")
    parser.add_argument("--audit-games", type=int, default=4096)
    args = parser.parse_args()
    if args.audit_games < 2 or args.audit_games % 2:
        parser.error("Audit game count must be positive and even")
    return run(args)


if __name__ == "__main__":
    raise SystemExit(main())
