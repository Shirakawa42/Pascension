"""Persistent, bounded CPU-only frozen evaluation schedule; no training controls.

Launch explicitly as a detached process with stdout redirected by the caller.
One job at a time, no retry after job failure, one Torch thread/host worker,
nice10, CPU FP32, unique reserved paired seeds, and a hard child timeout.
The campaign budget is read only. A failed or finished trainer stops the watch.
Snapshots preserve historical policies for later reproducibility (about25MiB
per cycle for the present H128 checkpoint); no old snapshot is deleted.
"""
from __future__ import annotations

import argparse
import fcntl
import hashlib
import json
import os
from pathlib import Path
import shutil
import signal
import subprocess
import sys
import time
import uuid


HERE = Path(__file__).resolve().parent
SCHEMA = "shards-cpu-shadow-watch-v1"
DEFAULT_SEED = 0x6000000000100000  # disjoint from the baseline smoke


def atomic_json(path, value):
    path = Path(path)
    path.parent.mkdir(parents=True, exist_ok=True)
    temporary = path.with_name(path.name+"."+uuid.uuid4().hex+".tmp")
    try:
        with temporary.open("w") as handle:
            json.dump(value, handle, indent=2, allow_nan=False)
            handle.write("\n")
            handle.flush()
            os.fsync(handle.fileno())
        os.replace(temporary, path)
        descriptor = os.open(path.parent, os.O_DIRECTORY)
        try:
            os.fsync(descriptor)
        finally:
            os.close(descriptor)
    finally:
        temporary.unlink(missing_ok=True)


def read_json(path):
    try:
        return json.loads(Path(path).read_text())
    except FileNotFoundError:
        return None


def process_alive(pid):
    if type(pid) is not int or pid <= 0:
        return False
    try:
        os.kill(pid, 0)
        # A zombie has finished; kill(pid,0) alone incorrectly labels it live.
        stat = Path(f"/proc/{pid}/stat").read_text()
        return stat.rsplit(")", 1)[1].strip().split()[0] != "Z"
    except (ProcessLookupError, FileNotFoundError):
        return False


def terminal_reason(run_dir, ledger):
    status, budget = read_json(Path(run_dir)/"status.json"), read_json(ledger)
    if status:
        if status.get("state") in ("failed", "session_complete", "completed", "stopped"):
            return "trainer_"+status["state"]
        pid = status.get("trainer_pid", status.get("pid"))
        if status.get("state") == "running" and not process_alive(pid):
            return "trainer_pid_not_alive"
    if budget and budget.get("charged_seconds", 0) >= budget.get("limit_seconds", 43200):
        return "training_budget_exhausted"
    return None


def snapshot_checkpoint(run_dir, destination, *, source_path=None):
    """A single open source handle survives atomic replacement by the trainer."""
    run_dir, destination = Path(run_dir), Path(destination)
    if destination.exists():
        raise FileExistsError("Refusing to replace a retained shadow snapshot")
    destination.parent.mkdir(parents=True, exist_ok=True)
    staging = destination.with_name(destination.name+".staging-"+uuid.uuid4().hex)
    staging.mkdir()
    try:
        for name in ("latest.soicp", "identity.json"):
            source_file = Path(source_path) if name == "latest.soicp" and source_path is not None else run_dir/name
            with source_file.open("rb") as source, (staging/name).open("wb") as target:
                shutil.copyfileobj(source, target)
                target.flush()
                os.fsync(target.fileno())
        # Loader in each child validates checksum/identity/boundary/finite state.
        os.rename(staging, destination)
        descriptor = os.open(destination.parent, os.O_DIRECTORY)
        try:
            os.fsync(descriptor)
        finally:
            os.close(descriptor)
        return destination/"latest.soicp"
    finally:
        if staging.exists():
            shutil.rmtree(staging)


def reserve_job(state, state_path, *, games, kind, output):
    """Persist seed consumption before spawning; failed/abandoned jobs use it up."""
    if kind not in ("champion", "anchor") or games < 2 or games % 2:
        raise ValueError("Invalid shadow job")
    seed = state["next_seed"]
    if type(seed) is not int or seed < 0 or seed+games//2 > 2**64:
        raise ValueError("Shadow seed range exhausted")
    job = {"sequence": state["jobs_reserved"], "kind": kind, "games": games,
           "seed": seed, "output": str(output), "state": "reserved", "reserved_wall": time.time()}
    state["next_seed"] += games//2
    state["jobs_reserved"] += 1
    state["active_job"] = job
    state["updated_wall"] = time.time()
    atomic_json(state_path, state)
    return job


def terminate_group(process):
    # Even a crashed/reaped Python child may have left a host in its own group.
    try:
        os.killpg(process.pid, signal.SIGTERM)
    except ProcessLookupError:
        pass
    try:
        process.wait(timeout=5)
    except subprocess.TimeoutExpired:
        try:
            os.killpg(process.pid, signal.SIGKILL)
        except ProcessLookupError:
            pass
        process.wait(timeout=5)
    finally:
        # A reaped Python parent does not prove its host descendants exited.
        # This is exclusively the session/group created for this evaluation.
        try:
            os.killpg(process.pid, signal.SIGKILL)
        except ProcessLookupError:
            pass


def job_command(args, job, snapshot):
    command = [sys.executable, str(HERE/"cpu_shadow_eval.py"), "--variant", args.variant,
        "--a", str(snapshot), "--a-role", "learner",
        "--b", str(snapshot if job["kind"] == "champion" else args.anchor),
        "--b-role", "champion" if job["kind"] == "champion" else args.anchor_role,
        "--games", str(args.games), "--batch", str(args.batch), "--seed", str(job["seed"]),
        "--step-delay-ms", str(args.step_delay_ms),
        "--sampling-seed", str(args.sampling_seed+job["sequence"]),
        "--max-seconds", str(max(1, args.child_timeout-15)), "--output", job["output"]]
    if args.balance_telemetry:
        command.append("--balance-telemetry")
    return command


def cadence(args, state, status, now):
    """Read checkpoint-cadence status counters; never parse hot metrics JSONL."""
    games = status.get("games") if status else None
    if type(games) is not int or games < 0:
        games = None
    snapshot_games = state.get("snapshot_training_games")
    target = state.get("next_training_games_target")
    progress = {"training_games": games, "snapshot_training_games": snapshot_games,
        "next_training_games_target": target,
        "games_since_snapshot": max(0, games-snapshot_games) if games is not None and snapshot_games is not None else None,
        "next_eligible_wall": state["next_eligible_wall"],
        "mode": "training_games" if args.every_training_games else "wall_time"}
    ready = bool(status and status.get("state") == "running" and now >= state["next_eligible_wall"])
    if args.every_training_games:
        ready = ready and games is not None and (state["cycles"] == 0 or
                                                target is not None and games >= target)
    return ready, progress


def record_snapshot_cadence(args, state, status, now):
    games = status.get("games")
    if type(games) is not int or games < 0:
        if args.every_training_games:
            raise ValueError("Game cadence requires a valid completed-training-game counter")
        games = None
    state.update(snapshot_training_games=games, snapshot_wall=now,
        next_training_games_target=games+args.every_training_games if games is not None and args.every_training_games else None,
        game_counter_scope="status.games observed immediately before checkpoint snapshot; status refresh follows checkpoint cadence")


def complete_cycle_cadence(args, state, now):
    state["last_cycle_completed_wall"] = now
    if args.every_training_games:
        state["next_eligible_wall"] = now+args.game_min_interval
    else:
        state["next_eligible_wall"] = max(state["next_eligible_wall"]+args.interval, now+1)


def statistics_command(args, state):
    timeout = shutil.which("timeout")
    if timeout is None:
        raise RuntimeError("System timeout is required for independently bounded statistics")
    return [timeout, "--signal=TERM", "--kill-after=5s", "55s", sys.executable,
        str(HERE.parent/"balance_statistics.py"), "--campaign-dir", str(args.ledger.parent),
        "--run-dir", str(args.run_dir), "--reports-dir", str(args.output_dir),
        "--output", str(args.ledger.parent/"balance-statistics-frozen.json"),
        "--every-games", str(args.every_training_games),
        "--snapshot-training-games", str(state.get("snapshot_training_games") or 0)]


def run_statistics(args, state, *, stopped=lambda: False):
    """One separately reported, independently timed CPU aggregation attempt."""
    output = args.ledger.parent/"balance-statistics-frozen.json"
    log_path = args.output_dir/f"balance-statistics-cycle-{state['cycles']-1:05d}.log"
    started = time.monotonic()
    try:
        previous = output.stat()
        previous_revision = (previous.st_dev, previous.st_ino, previous.st_mtime_ns)
    except FileNotFoundError:
        previous_revision = None
    process = None
    reason = None
    if stopped():
        return {"state": "cancelled", "reason": "watch_stopped", "automatic_retry": False}
    try:
        env = os.environ.copy()
        env.update(CUDA_VISIBLE_DEVICES="", OMP_NUM_THREADS="1", MKL_NUM_THREADS="1")
        with log_path.open("wb") as log:
            process = subprocess.Popen(statistics_command(args, state), env=env, stdout=log,
                stderr=subprocess.STDOUT, start_new_session=True)
            try:
                while process.poll() is None:
                    if stopped():
                        reason = "watch_stopped"
                    elif time.monotonic()-started >= 60:
                        reason = "statistics_timeout"
                    if reason:
                        break
                    time.sleep(.25)
            finally:
                terminate_group(process)
        if reason or process.returncode != 0:
            return {"state": "cancelled" if reason == "watch_stopped" else "failed",
                "reason": reason or "statistics_process_failed", "returncode": process.returncode,
                "seconds": time.monotonic()-started, "automatic_retry": False, "log": str(log_path)}
        report = read_json(output)
        if not report or report.get("schema") != "shards-balance-statistics-v1":
            raise ValueError("Statistics child did not publish the expected sidecar schema")
        current = output.stat()
        if (current.st_dev, current.st_ino, current.st_mtime_ns) == previous_revision:
            raise ValueError("Statistics child did not refresh the previous sidecar")
        if report.get("refresh", {}).get("snapshot_training_games") != (state.get("snapshot_training_games") or 0):
            raise ValueError("Statistics sidecar refers to a different snapshot game counter")
        return {"state": "completed", "output": str(output), "seconds": time.monotonic()-started,
            "automatic_retry": False, "snapshot_training_games": state.get("snapshot_training_games")}
    except Exception as error:
        return {"state": "failed", "reason": "statistics_exception", "automatic_retry": False,
            "seconds": time.monotonic()-started, "error": {"type": type(error).__name__, "message": str(error)}}


def bounded_command(args, job, snapshot):
    # The deadline survives termination of this detached watch process. GNU
    # timeout owns the same fresh process group as its helper/host descendants.
    timeout = shutil.which("timeout")
    if timeout is None:
        raise RuntimeError("System timeout is required for an independently bounded CPU child")
    return [timeout, "--signal=TERM", "--kill-after=5s", f"{args.child_timeout-5:g}s",
            *job_command(args, job, snapshot)]


def run_job(args, job, snapshot, *, stopped=lambda: False):
    output = Path(job["output"])
    env = os.environ.copy()
    env.update(CUDA_VISIBLE_DEVICES="", OMP_NUM_THREADS="1", MKL_NUM_THREADS="1")
    started = time.monotonic()
    reason = "watch_stopped" if stopped() else terminal_reason(args.run_dir, args.ledger)
    if reason:
        return {**job, "state": "cancelled", "reason": reason, "seconds": 0.}
    with output.with_suffix(".log").open("wb") as log:
        process = subprocess.Popen(bounded_command(args, job, snapshot), env=env,
            stdout=log, stderr=subprocess.STDOUT, start_new_session=True)
        try:
            while process.poll() is None:
                reason = "watch_stopped" if stopped() else terminal_reason(args.run_dir, args.ledger)
                if not reason and time.monotonic()-started >= args.child_timeout:
                    reason = "shadow_child_timeout"
                if reason:
                    terminate_group(process)
                    break
                time.sleep(1)
        finally:
            terminate_group(process)
    report = read_json(output)
    if reason or process.returncode != 0 or not report or not report.get("complete"):
        report = report or {"schema": "shards-cpu-shadow-evaluation-v1", "event": "external_frozen_evaluation"}
        cancelled = reason in ("watch_stopped", "trainer_failed", "trainer_completed",
            "trainer_session_complete", "trainer_stopped", "trainer_pid_not_alive", "training_budget_exhausted")
        report.update(state="cancelled" if cancelled else "failed", complete=False, phase="finished", evaluation_only=True,
            training_budget_seconds_charged=0, optimizer_updates=0,
            watch_failure={"reason": reason or "shadow_job_failed", "returncode": process.returncode,
                           "automatic_retry": False}, finished_wall=time.time())
        atomic_json(output, report)
        return {**job, "state": "cancelled" if cancelled else "failed", "reason": reason or "shadow_job_failed",
                "returncode": process.returncode, "seconds": time.monotonic()-started}
    return {**job, "state": "completed", "returncode": process.returncode,
            "seconds": time.monotonic()-started,
            "policy_a_version": report["policy_a"]["version"],
            "policy_b_version": report["policy_b"]["version"]}


def run(args):
    args.run_dir = args.run_dir.expanduser().resolve()
    args.anchor = args.anchor.expanduser().resolve()
    args.ledger = args.ledger.expanduser().resolve()
    args.output_dir = args.output_dir.expanduser().resolve()
    args.output_dir.mkdir(parents=True, exist_ok=True)
    state_path = args.run_dir/"shadow-watch.json"
    lock_path = args.run_dir/"shadow-watch.lock"
    stopped = False
    def stop(signum, frame):
        nonlocal stopped
        stopped = True
    signal.signal(signal.SIGTERM, stop)
    signal.signal(signal.SIGINT, stop)
    current_nice = os.getpriority(os.PRIO_PROCESS, 0)
    if current_nice < 10:
        os.nice(10-current_nice)
    started = time.monotonic()
    with lock_path.open("a") as lock:
        fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
        state = read_json(state_path)
        configuration = {"run_dir": str(args.run_dir), "anchor": str(args.anchor),
            "anchor_role": args.anchor_role, "variant": args.variant, "games": args.games,
            "batch": args.batch, "interval": args.interval, "seed_base": args.seed,
            "sampling_seed": args.sampling_seed, "output_dir": str(args.output_dir),
            "child_timeout": args.child_timeout, "step_delay_ms": args.step_delay_ms,
            "balance_telemetry": args.balance_telemetry,
            "every_training_games": args.every_training_games,
            "game_min_interval": args.game_min_interval, "initial_delay": args.initial_delay}
        if state:
            existing_configuration = dict(state.get("configuration", {}))
            # Old watches were time-only and had no balance hook. Explicit N=0
            # resumes that behavior; default new game cadence cannot silently
            # reinterpret an old watch's scheduling contract.
            existing_configuration.setdefault("every_training_games", 0)
            existing_configuration.setdefault("balance_telemetry", False)
            existing_configuration.setdefault("game_min_interval", 60.)
            existing_configuration.setdefault("initial_delay", args.initial_delay)
            if existing_configuration != configuration:
                raise RuntimeError("Existing shadow state has a different configuration")
            # Fail closed after an interrupted or failed job; do not retry it.
            if state.get("active_job") or state.get("state") == "failed":
                raise RuntimeError("Previous shadow job failed or was interrupted; explicit review required")
        else:
            state = {"schema": SCHEMA, "configuration": configuration, "created_wall": time.time(),
                     "jobs_reserved": 0, "next_seed": args.seed, "cycles": 0, "recent_jobs": [],
                     "budget_scope": "No ledger access for writing or additional evaluation charge. Concurrent training wall time still counts and CPU contention may reduce useful training throughput."}
        state["configuration"] = configuration
        state.setdefault("initial_due_wall", time.time()+args.initial_delay)
        state.setdefault("next_eligible_wall", state["initial_due_wall"])
        state.setdefault("snapshot_training_games", None)
        state.setdefault("next_training_games_target", None)
        state.setdefault("recent_statistics", [])
        if state.get("active_statistics"):
            state["statistics_disabled"] = True
            state["recent_statistics"] = (state["recent_statistics"]+[
                {"state": "failed", "reason": "interrupted_statistics_attempt", "automatic_retry": False}])[-16:]
            state["active_statistics"] = None
        if "anchor_snapshot" not in state:
            anchor = snapshot_checkpoint(args.anchor.parent, args.run_dir/"shadow-snapshots"/"anchor", source_path=args.anchor)
            state["anchor_snapshot"] = str(anchor)
            state["anchor_file_sha256"] = hashlib.sha256(anchor.read_bytes()).hexdigest()
        args.anchor = Path(state["anchor_snapshot"])
        if hashlib.sha256(args.anchor.read_bytes()).hexdigest() != state["anchor_file_sha256"]:
            raise RuntimeError("Fixed shadow anchor changed")
        state.update(pid=os.getpid(), state="waiting", updated_wall=time.time(), active_job=None)
        atomic_json(state_path, state)
        try:
            while True:
                reason = "watch_stopped" if stopped else terminal_reason(args.run_dir, args.ledger)
                if not reason and time.monotonic()-started >= args.max_runtime:
                    reason = "watch_runtime_limit"
                if reason:
                    state.update(state="stopped", reason=reason, updated_wall=time.time())
                    atomic_json(state_path, state)
                    return 0
                status = read_json(args.run_dir/"status.json")
                due, progress = cadence(args, state, status, time.time())
                if state.get("cadence") != progress:
                    state.update(cadence=progress, updated_wall=time.time())
                    atomic_json(state_path, state)
                if not due:
                    time.sleep(3)
                    continue
                cycle = state["cycles"]
                snapshot = snapshot_checkpoint(args.run_dir,
                    args.run_dir/"shadow-snapshots"/f"cycle-{cycle:05d}")
                record_snapshot_cadence(args, state, status, time.time())
                state["cycles"] += 1
                state["state"] = "evaluating"
                for kind in ("champion", "anchor"):
                    output = args.output_dir/f"cpu-shadow-{args.run_dir.name}-{cycle:05d}-{kind}.json"
                    job = reserve_job(state, state_path, games=args.games, kind=kind, output=output)
                    if kind == "anchor" and hashlib.sha256(args.anchor.read_bytes()).hexdigest() != state["anchor_file_sha256"]:
                        raise RuntimeError("Fixed shadow anchor changed")
                    result = run_job(args, job, snapshot, stopped=lambda: stopped)
                    state["recent_jobs"] = (state["recent_jobs"]+[result])[-32:]
                    state["active_job"] = None
                    state["updated_wall"] = time.time()
                    if result["state"] != "completed":
                        state.update(state="stopped" if result["state"] == "cancelled" else "failed",
                                     reason=result["reason"])
                        atomic_json(state_path, state)
                        return 0 if result["state"] == "cancelled" else 1
                    atomic_json(state_path, state)
                if args.balance_telemetry and not state.get("statistics_disabled"):
                    state.update(active_statistics={"cycle": cycle, "started_wall": time.time()},
                                 state="aggregating_statistics", updated_wall=time.time())
                    atomic_json(state_path, state)
                    statistics = run_statistics(args, state, stopped=lambda: stopped)
                    state["active_statistics"] = None
                    state["recent_statistics"] = (state["recent_statistics"]+[statistics])[-16:]
                    if statistics["state"] != "completed":
                        # Frozen-match results remain valid. A sidecar failure
                        # neither controls training nor triggers automatic retry.
                        state["statistics_disabled"] = True
                complete_cycle_cadence(args, state, time.time())
                _, state["cadence"] = cadence(args, state, status, time.time())
                state.update(state="waiting", updated_wall=time.time())
                atomic_json(state_path, state)
                if args.once:
                    state.update(state="stopped", reason="one_cycle_complete")
                    atomic_json(state_path, state)
                    return 0
        except BaseException as error:
            state.update(state="failed", updated_wall=time.time(),
                         error={"type": type(error).__name__, "message": str(error)}, automatic_retry=False)
            atomic_json(state_path, state)
            raise


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--run-dir", type=Path, required=True)
    parser.add_argument("--ledger", type=Path, required=True, help="Read-only training budget path")
    parser.add_argument("--anchor", type=Path, required=True, help="Immutable migrated1977 checkpoint with adjacent identity")
    parser.add_argument("--anchor-role", choices=("learner", "champion", "initial"), default="learner")
    parser.add_argument("--output-dir", type=Path, required=True)
    parser.add_argument("--variant", choices=("v2", "v3", "v4", "v5", "v6", "v7", "v8", "v9"), default="v3")
    parser.add_argument("--games", type=int, default=256)
    parser.add_argument("--batch", type=int, default=32)
    parser.add_argument("--interval", type=float, default=1200)
    parser.add_argument("--every-training-games", type=int, default=100000,
                        help="Subsequent cycles require this many status.games since the last snapshot;0 restores time-only --interval")
    parser.add_argument("--game-min-interval", type=float, default=60.,
                        help="Minimum wall seconds after a cycle in game-count mode")
    parser.add_argument("--balance-telemetry", action="store_true",
                        help="Collect passive per-game balance data and refresh a separate frozen-statistics sidecar")
    parser.add_argument("--initial-delay", type=float, default=1200)
    parser.add_argument("--child-timeout", type=float, default=300)
    parser.add_argument("--step-delay-ms", type=float, default=3.,
                        help="CPU throttle after each host reply; recorded in every helper result")
    parser.add_argument("--max-runtime", type=float, default=43200)
    parser.add_argument("--seed", type=lambda value: int(value, 0), default=DEFAULT_SEED)
    parser.add_argument("--sampling-seed", type=int, default=70926)
    parser.add_argument("--once", action="store_true")
    args = parser.parse_args()
    if args.games < 2 or args.games > 4096 or args.games % 2 or not 1 <= args.batch <= 256:
        parser.error("Require even games2..4096 and batch1..256")
    if not 60 <= args.interval <= 7200 or not 0 <= args.initial_delay <= 7200:
        parser.error("Interval60..7200 and initial-delay0..7200 seconds required")
    if not (args.every_training_games == 0 or 1000 <= args.every_training_games <= 1000000000) or not 60 <= args.game_min_interval <= 7200:
        parser.error("Require every-training-games0 or1000..1e9 and game-min-interval60..7200 seconds")
    if not 30 <= args.child_timeout <= 300 or not 60 <= args.max_runtime <= 43200:
        parser.error("Child timeout30..300 and watch runtime60..43200 seconds required")
    if not 0 <= args.step_delay_ms <= 100:
        parser.error("Step delay must be finite in [0,100] milliseconds")
    if not 0 <= args.seed < 2**64-10000000 or not 0 <= args.sampling_seed < 2**63-100000:
        parser.error("Seeds must leave room for the bounded watch schedule")
    raise SystemExit(run(args))


if __name__ == "__main__":
    main()
