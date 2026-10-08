"""Independent process supervisor: hard training deadline and heartbeat checks.

Only the child process group created here is signaled. Training is never
automatically retried after a failure. Run this persistent supervisor detached
for the main session; its child still owns the shared budget lock and ledger.
"""
import argparse
import json
import os
from pathlib import Path
import signal
import subprocess
import sys
import time

from bench_common import save_json


def supervise(command, run_dir, ledger, startup_timeout=180, heartbeat_timeout=120):
    run_dir = Path(run_dir)
    run_dir.mkdir(parents=True, exist_ok=True)
    started = time.monotonic()
    process = subprocess.Popen(command, start_new_session=True)
    reason = None
    signal_requested = False

    def request_stop(signum, frame):
        nonlocal signal_requested
        signal_requested = True

    signal.signal(signal.SIGTERM, request_stop)
    signal.signal(signal.SIGINT, request_stop)
    save_json(run_dir/"supervisor.json", {"supervisor_pid": os.getpid(), "trainer_pid": process.pid,
        "started_wall": time.time(), "command": command, "state": "starting"})
    deadline = None
    stop_sent = None
    while process.poll() is None:
        now = time.monotonic()
        try:
            data = json.loads(Path(ledger).read_text())
            active = data.get("active")
            if active and active.get("pid") == process.pid:
                deadline = float(active["hard_deadline_monotonic"])
                if now-float(active["last_heartbeat_monotonic"]) > heartbeat_timeout:
                    reason = "heartbeat_timeout"
            if deadline is not None and now >= deadline-5:
                reason = reason or "hard_deadline"
        except (OSError, ValueError, KeyError, TypeError):
            pass  # Atomic publication can briefly precede the first ledger.
        if deadline is None and now-started > startup_timeout:
            reason = "startup_timeout"
        if signal_requested:
            reason = reason or "requested_stop"
        if reason and stop_sent is None:
            try:
                # Keep C# alive while Python discards a partial collection,
                # closes the host, and saves the last valid model state.
                process.send_signal(signal.SIGTERM)
            except ProcessLookupError:
                pass
            stop_sent = now
        kill_at = min(deadline if deadline is not None else float("inf"),
                      stop_sent+20 if stop_sent is not None else float("inf"))
        if now >= kill_at:
            try:
                os.killpg(process.pid, signal.SIGKILL)
            except ProcessLookupError:
                pass
            reason = reason or "deadline_kill"
            break
        time.sleep(min(.5, max(.01, kill_at-time.monotonic())))
    code = process.wait()
    # A crashed trainer may not have closed its C# child. This group was
    # created exclusively for this invocation, so clean only its descendants.
    try:
        os.killpg(process.pid, signal.SIGKILL)
    except ProcessLookupError:
        pass
    result = {"supervisor_pid": os.getpid(), "trainer_pid": process.pid,
        "finished_wall": time.time(), "elapsed_seconds": time.monotonic()-started,
        "returncode": code, "stop_reason": reason,
        "state": "complete" if code == 0 else "failed", "automatic_retry": False}
    save_json(run_dir/"supervisor.json", result)
    return code


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--run-dir", type=Path, required=True)
    parser.add_argument("--ledger", type=Path, required=True)
    parser.add_argument("--seconds", type=float, required=True)
    parser.add_argument("--config", type=Path, required=True)
    parser.add_argument("--resume", type=Path)
    parser.add_argument("--label", default="training")
    args = parser.parse_args()
    if not 1 <= args.seconds <= 43200:
        parser.error("Session grant must be within the existing 12-hour allocation")
    command = [sys.executable, str(Path(__file__).with_name("train_campaign.py")),
               "--run-dir", str(args.run_dir), "--ledger", str(args.ledger),
               "--seconds", str(args.seconds), "--config", str(args.config), "--label", args.label]
    if args.resume:
        command += ["--resume", str(args.resume)]
    raise SystemExit(supervise(command, args.run_dir, args.ledger))


if __name__ == "__main__":
    main()
