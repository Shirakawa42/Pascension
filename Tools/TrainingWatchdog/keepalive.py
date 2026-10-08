"""Hold one foreground WSL attachment while its existing watchdog owns the lock.

This read-only sentinel never launches, signals, imports training code, or reads
a model. Exit 10 requests the Windows parent to retry the SAME runner/config;
exit 20 refuses a retry. The original Linux runner alone owns recovery/budget.
"""
from __future__ import annotations

import argparse
from dataclasses import asdict, dataclass
import hashlib
import json
import math
import os
from pathlib import Path
import re
import stat
import sys
import time

PROTOCOL = "shards-watchdog-keepalive-v1"
CONFIG_SCHEMA = "shards-training-watchdog-config-v1"
STATE_SCHEMA = "shards-training-watchdog-state-v1"
CLEAN = {"completed", "deadline", "stopped"}
UNSAFE = {"blocked", "retry_exhausted"}
PHASES = CLEAN | UNSAFE | {
    "starting", "training", "retry_wait", "recovering", "interrupted",
    "deadline_stopping", "evaluating",
}
RETRY = 10
REFUSED = 20


def boot_id():
    return Path("/proc/sys/kernel/random/boot_id").read_text().strip()


def bounded_json(path, *, missing=False):
    try:
        with Path(path).open("rb") as stream:
            raw = stream.read(2_097_153)
    except FileNotFoundError:
        if missing:
            return None
        raise
    if len(raw) > 2_097_152:
        raise ValueError("Control JSON exceeds the bounded read limit")
    value = json.loads(raw)
    if not isinstance(value, dict):
        raise ValueError("Control JSON must be an object")
    return value


def sha_file(path):
    with Path(path).open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


@dataclass(frozen=True)
class Process:
    pid: int
    start_ticks: int
    pgid: int
    session: int
    command: tuple[str, ...]
    boot_id: str


def inspect(pid):
    if type(pid) is not int or pid <= 0:
        return None
    try:
        folder = Path("/proc") / str(pid)
        first = (folder / "stat").read_text().rsplit(") ", 1)[1].split()
        if first[0] in ("Z", "X"):
            return None
        command = (folder / "cmdline").read_bytes()
        if not command:
            return None
        after = (folder / "stat").read_text().rsplit(") ", 1)[1].split()
        if after[0] in ("Z", "X") or first[19] != after[19] or first[2:4] != after[2:4]:
            return None
        return Process(pid, int(after[19]), int(after[2]), int(after[3]),
                       tuple(part.decode() for part in command.rstrip(b"\0").split(b"\0")), boot_id())
    except (OSError, ValueError, IndexError, UnicodeError):
        return None


def lock_busy(path):
    """Observe kernel flock metadata without ever acquiring a competing lock."""
    try:
        before = os.stat(path, follow_symlinks=False)
    except FileNotFoundError:
        return False
    if not stat.S_ISREG(before.st_mode):
        raise RuntimeError("Watchdog lock must be a regular file")
    device_inode = (os.major(before.st_dev), os.minor(before.st_dev), before.st_ino)
    busy = False
    with Path("/proc/locks").open("rb") as stream:
        content = stream.read(4_194_305)
    if len(content) > 4_194_304:
        raise RuntimeError("Kernel lock metadata exceeds bounded read limit")
    for line in content.decode().splitlines():
        fields = line.split()
        # Waiting contenders contain '->' and do not hold the flock.
        if len(fields) < 6 or fields[1] != "FLOCK":
            continue
        major, minor, inode = fields[5].split(":")
        if (int(major, 16), int(minor, 16), int(inode)) == device_inode:
            busy = True
            break
    after = os.stat(path, follow_symlinks=False)
    if (before.st_dev, before.st_ino) != (after.st_dev, after.st_ino):
        raise RuntimeError("Watchdog lock file changed during observation")
    return busy


class Sentinel:
    def __init__(self, config_path, expected_sha, *, hard_deadline_wall=None,
                 expected_runner_sha=None, poll_seconds=5., startup_grace_seconds=195.):
        self.config_path = Path(config_path).absolute()
        if not re.fullmatch(r"[0-9a-f]{64}", expected_sha):
            raise ValueError("Expected raw configuration SHA must be lowercase SHA-256")
        self.expected_sha = expected_sha
        if sha_file(self.config_path) != expected_sha:
            raise ValueError("Immutable watchdog configuration SHA mismatch")
        self.config = bounded_json(self.config_path)
        fields = {"schema", "initial_campaign", "python", "seconds", "hard_deadline_wall", "port", "bind",
                  "max_restarts", "poll_seconds", "grace_seconds", "recovery_root", "project"}
        if set(self.config) != fields or self.config["schema"] != CONFIG_SCHEMA:
            raise ValueError("Invalid watchdog configuration schema/fields")
        for name in ("seconds", "hard_deadline_wall", "poll_seconds", "grace_seconds"):
            value = self.config[name]
            if type(value) not in (int, float) or not math.isfinite(value) or value <= 0:
                raise ValueError("Invalid watchdog configuration number: " + name)
        if self.config["seconds"] > 43200:
            raise ValueError("Watchdog authorization exceeds twelve hours")
        for name in ("initial_campaign", "python", "recovery_root", "project"):
            if not isinstance(self.config[name], str) or not Path(self.config[name]).is_absolute():
                raise ValueError("Invalid absolute watchdog configuration path: " + name)
        self.deadline = self.config["hard_deadline_wall"]
        if hard_deadline_wall is not None and hard_deadline_wall != self.deadline:
            raise ValueError("Windows/Linux fixed deadline mismatch")
        if expected_runner_sha is not None and not re.fullmatch(r"[0-9a-f]{64}", expected_runner_sha):
            raise ValueError("Invalid expected runner SHA-256")
        self.runner_path = Path(self.config["project"]) / "Tools/TrainingWatchdog/runner.py"
        self.runner_sha = expected_runner_sha
        if self.runner_sha and sha_file(self.runner_path) != self.runner_sha:
            raise ValueError("Frozen runner SHA mismatch")
        for value in (poll_seconds, startup_grace_seconds):
            if not math.isfinite(value) or value <= 0:
                raise ValueError("Invalid sentinel polling/startup limit")
        self.poll_seconds = poll_seconds
        self.startup_grace = startup_grace_seconds
        self.directory = self.config_path.parent
        self.canonical_sha = hashlib.sha256(json.dumps(self.config, sort_keys=True).encode()).hexdigest()
        self.current_boot = boot_id()
        self.owner = None
        self.uncertain_since = None
        self.last_wall = None
        self.last = None

    def result(self, disposition, reason, state=None, owner=None, code=None, *, unsafe_alert=False, retained=None):
        value = dict(protocol=PROTOCOL, disposition=disposition, reason=reason,
                     config_sha256=self.expected_sha, hard_deadline_wall=self.deadline,
                     boot_id=self.current_boot, phase=state.get("status") if state else None,
                     current_campaign=state.get("current_campaign") if state else None,
                     campaign_id=state.get("campaign_id") if state else None,
                     runner=asdict(owner) if owner else None, exit_code=code, unsafe_alert=unsafe_alert,
                     retained_process=asdict(retained) if retained else None)
        self.last = value
        return value

    def matching_runner(self, owner):
        if not owner or owner.boot_id != self.current_boot or len(owner.command) != 4:
            return False
        command = owner.command
        try:
            return (Path(command[0]).samefile(self.config["python"]) and
                    Path(command[1]).samefile(self.runner_path) and command[2] == "--config" and
                    Path(command[3]).samefile(self.config_path))
        except OSError:
            return False

    def evaluation_started(self, state):
        if state.get("status") == "evaluating":
            return True
        launcher = state.get("launcher")
        if not isinstance(launcher, dict):
            return False
        supervision = bounded_json(Path(state["current_campaign"]) / "supervisor.json", missing=True)
        return bool(supervision and supervision.get("supervisor_pid") == launcher.get("pid") and
                    supervision.get("state") == "complete" and supervision.get("returncode") == 0 and
                    supervision.get("stop_reason") in (None, "hard_deadline"))

    def saved_live(self, record):
        """Keep a foreground attachment only for an exact retained identity."""
        if not isinstance(record, dict) or set(record) != {"pid", "start_ticks", "pgid", "session", "command", "boot_id"}:
            return None
        if any(type(record[key]) is not int or record[key] <= 0 for key in ("pid", "start_ticks", "pgid", "session")):
            return None
        if record["boot_id"] != self.current_boot or not isinstance(record["command"], list) or \
                not record["command"] or any(not isinstance(value, str) for value in record["command"]):
            return None
        saved = Process(record["pid"], record["start_ticks"], record["pgid"], record["session"],
                        tuple(record["command"]), record["boot_id"])
        return saved if inspect(saved.pid) == saved else None

    def retained_work(self, state):
        if not state:
            return None
        records = [state.get("trainer"), state.get("launcher")]
        children = state.get("owned_descendants", [])
        if not isinstance(children, list):
            raise RuntimeError("Invalid retained descendant identities")
        records.extend(children)
        return next((owner for record in records if (owner := self.saved_live(record)) is not None), None)

    def poll(self):
        """Return a bounded read-only decision; holding never requests a launch."""
        try:
            now = time.time()
            if self.last_wall is not None and now < self.last_wall - .01:
                raise RuntimeError("Wall clock moved backwards")
            self.last_wall = now
            if sha_file(self.config_path) != self.expected_sha:
                raise RuntimeError("Immutable watchdog configuration changed")
            if self.runner_sha and sha_file(self.runner_path) != self.runner_sha:
                raise RuntimeError("Frozen runner changed")
            if boot_id() != self.current_boot:
                raise RuntimeError("Kernel boot identity changed within a live sentinel")
            state = bounded_json(self.directory / "state.json", missing=True)
            busy = lock_busy(self.directory / "watchdog.lock")
            stopped = (self.directory / "STOP").exists()
            if state is not None:
                if state.get("schema") != STATE_SCHEMA or state.get("status") not in PHASES:
                    raise RuntimeError("Invalid watchdog state schema/phase")
                if state.get("config_sha256", self.canonical_sha) != self.canonical_sha:
                    raise RuntimeError("Watchdog state belongs to a different authorization")
                if state["status"] in CLEAN:
                    return self.result("terminal", "Watchdog reached " + state["status"], state, code=0)
                if state["status"] in UNSAFE:
                    return self.result("unsafe", "Watchdog reached " + state["status"], state, code=REFUSED)
                remaining = state.get("authorization_remaining_seconds")
                if remaining is not None and (type(remaining) not in (int, float) or not math.isfinite(remaining) or remaining < 0):
                    raise RuntimeError("Invalid durable authorization remainder")
                observed = state.get("observed_wall")
                if observed is not None and (type(observed) not in (int, float) or not math.isfinite(observed) or now < observed - .01):
                    raise RuntimeError("Durable watchdog clock continuity is uncertain")
                owner = inspect(state.get("watchdog_pid")) if state.get("watchdog_boot_id") == self.current_boot else None
                if owner is not None and not self.matching_runner(owner):
                    raise RuntimeError("Published watchdog PID now names an unrelated process")
                if busy and owner is not None:
                    if self.owner is not None and owner != self.owner and inspect(self.owner.pid) == self.owner:
                        raise RuntimeError("A second live watchdog identity appeared")
                    self.owner = owner
                    self.uncertain_since = None
                    return self.result("holding", "Durable STOP; awaiting watchdog cleanup" if stopped else
                                       "Existing watchdog owns the flock", state, owner)
            else:
                owner = None
            if busy:
                # The locked controller can be in a CPU preflight before it
                # publishes its PID. Do not manufacture another runner here.
                if self.uncertain_since is None:
                    self.uncertain_since = time.monotonic()
                if time.monotonic() - self.uncertain_since >= self.startup_grace:
                    raise RuntimeError("Busy watchdog flock lacks a verified controller after startup grace")
                return self.result("holding", "Busy flock; awaiting controller publication", state)
            if stopped:
                retained = self.retained_work(state)
                if retained:
                    return self.result("holding", "Durable STOP lost its controller; retaining owned work for cleanup", state,
                                       code=None, unsafe_alert=True, retained=retained)
                return self.result("terminal", "Durable STOP; no watchdog owns the flock", state, code=0)
            if state and self.evaluation_started(state):
                retained = self.saved_live(state.get("launcher"))
                if retained:
                    return self.result("holding", "Evaluation controller lost; retaining exact live launcher without learning retry",
                                       state, code=None, unsafe_alert=True, retained=retained)
                return self.result("unsafe", "Evaluation controller was lost; learning must not restart", state, code=REFUSED)
            if now >= self.deadline or state and state.get("authorization_remaining_seconds", 1) <= 0:
                return self.result("unsafe", "Authorization ended without an owned controller", state, code=REFUSED)
            if owner is not None:
                # An exact runner can briefly precede acquiring its flock.
                if self.uncertain_since is None:
                    self.uncertain_since = time.monotonic()
                if time.monotonic() - self.uncertain_since >= self.startup_grace:
                    raise RuntimeError("Exact watchdog did not acquire its flock within startup grace")
                return self.result("holding", "Exact controller is acquiring its flock", state, owner)
            self.uncertain_since = None
            return self.result("retry", "No watchdog owns the authorization before its fixed cap", state, code=RETRY)
        except Exception as error:
            return self.result("unsafe", str(error), code=REFUSED)

    def run(self, *, emit=None):
        announced = None
        while True:
            result = self.poll()
            signature = (result["disposition"], result["reason"], result["phase"],
                         json.dumps(result["runner"], sort_keys=True), json.dumps(result["retained_process"], sort_keys=True))
            if emit and signature != announced:
                emit(result)
                announced = signature
            if result["exit_code"] is not None:
                return result
            time.sleep(self.poll_seconds)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--config", type=Path, required=True)
    parser.add_argument("--expected-sha", required=True)
    parser.add_argument("--hard-deadline-wall", type=float)
    parser.add_argument("--expected-runner-sha")
    parser.add_argument("--poll-seconds", type=float, default=5.)
    args = parser.parse_args()
    try:
        sentinel = Sentinel(args.config, args.expected_sha, hard_deadline_wall=args.hard_deadline_wall,
                            expected_runner_sha=args.expected_runner_sha, poll_seconds=args.poll_seconds)
        result = sentinel.run(emit=lambda value: print(json.dumps(value), flush=True))
    except Exception as error:
        result = dict(protocol=PROTOCOL, disposition="unsafe", reason=str(error), exit_code=REFUSED)
        print(json.dumps(result), flush=True)
    return result["exit_code"]


if __name__ == "__main__":
    raise SystemExit(main())
