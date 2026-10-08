"""Local training dashboard and persistent 3-second resource sampler.

No Torch import, external packages, uploads, or training control endpoints.
The HTTP listener is always 127.0.0.1. Writes are resource.jsonl, monitor state
and cached balance-statistics.json in the configured campaign directory. The
balance proposal page also saves local, versioned user feedback and card design
drafts there. Card design drafts do not modify any game definitions.
"""
from __future__ import annotations

import argparse
from collections import deque
import datetime as dt
import fcntl
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
import json
import math
import os
from pathlib import Path
import re
import shutil
import signal
import subprocess
import threading
import time
from urllib.parse import parse_qs, urlsplit

from balance_host_statistics import TrainingPoolCache
from statistics_assets import CardArtwork
from game_decklists import GameDecklists
from balance_review_store import BalanceReviewStore, ReviewError, MAX_REQUEST_BYTES, strict_json
from card_draft_store import CardDraftStore

HERE = Path(__file__).resolve().parent
RUN_ID = re.compile(r"[A-Za-z0-9][A-Za-z0-9_.-]{0,80}(?:/[A-Za-z0-9][A-Za-z0-9_.-]{0,80})?\Z")
RUN_ARTIFACTS = frozenset(("metrics.jsonl", "status.json", "deadline.json", "failure.json",
    "identity.json", "supervisor.json", "budget-after.json", "config.json", "post-evaluation.json"))
EVALUATION_NAME = re.compile(r"[A-Za-z0-9][A-Za-z0-9_.-]{0,120}\.json\Z")
TRACE_NAME = re.compile(r"[A-Za-z0-9][A-Za-z0-9_.-]{0,180}\.json\Z")
CAMPAIGN_ARTIFACTS = frozenset(("budget.json", "resource.jsonl", "monitor.json",
    "balance-statistics-final.json", "final-statistics-mode.json", "balance-statistics-frozen.json", "balance-statistics.json",
    "balance-statistics-natural-draft.json", "balance-statistics-forced-random.json"))
MAX_DOWNLOAD = 64 * 1024 * 1024


def finite(value):
    if isinstance(value, float) and not math.isfinite(value):
        return None
    if isinstance(value, dict):
        return {key: finite(item) for key, item in value.items()}
    if isinstance(value, list):
        return [finite(item) for item in value]
    return value


def timestamp(row):
    value = row.get("wall", row.get("updated_wall", row.get("utc")))
    if isinstance(value, (float, int)):
        return float(value) if math.isfinite(value) else None
    if isinstance(value, str):
        try:
            return dt.datetime.fromisoformat(value.replace("Z", "+00:00")).timestamp()
        except ValueError:
            pass
    return None


def safe_file(parent, name):
    path = parent / name
    if path.is_symlink() or not path.is_file() or path.resolve().parent != parent.resolve():
        return None
    return path


def safe_directory(root, relative):
    current = root
    for part in Path(relative).parts:
        if part in (".", "..", "/"):
            return None
        current = current / part
        if current.is_symlink() or not current.is_dir():
            return None
    return current if current.resolve().is_relative_to(root.resolve()) else None


def trace_listing(folder):
    if folder is None:
        return []
    rows = []
    for path in folder.iterdir():
        if TRACE_NAME.fullmatch(path.name) and safe_file(folder, path.name):
            info = path.stat()
            rows.append({"name": path.name, "bytes": info.st_size, "updated_wall": info.st_mtime})
    return sorted(rows, key=lambda row: row["updated_wall"], reverse=True)[:200]


def collection_accounting_view(budget):
    value = budget.get("collection_accounting")
    if not isinstance(value, dict):
        return None
    recent = value.get("recent_batches", [])
    return {key: value.get(key) for key in ("schema", "started_wall", "window_games", "collections",
            "attempted_games", "completed_games", "censored_games", "learning_rows", "censored_rows")} | {
        "recent_attempted": sum(row.get("attempted_games", 0) for row in recent),
        "recent_completed": sum(row.get("completed_games", 0) for row in recent),
        "recent_censored": sum(row.get("censored_games", 0) for row in recent),
        "recent_learning_rows": sum(row.get("learning_rows", 0) for row in recent),
        "recent_censored_rows": sum(row.get("censored_rows", 0) for row in recent),
        "recent_batch_count": len(recent)}


def read_json(parent, name, max_bytes=4 * 1024 * 1024):
    path = safe_file(parent, name)
    if path is None:
        return None
    try:
        if path.stat().st_size > max_bytes:
            return None
        value = json.loads(path.read_bytes())
        return finite(value) if isinstance(value, dict) else None
    except (OSError, ValueError):
        return None


class JsonlTail:
    """Incremental bounded log reader; incomplete final lines wait for publication."""
    def __init__(self, path, max_rows=30000):
        self.path = Path(path)
        self.rows = deque(maxlen=max_rows)
        self.offset, self.pending, self.inode = 0, b"", None
        self.invalid_lines = self.total_rows = 0
        self.dropping = False

    def refresh(self, max_read=4 * 1024 * 1024):
        path = safe_file(self.path.parent, self.path.name)
        if path is None:
            return
        try:
            with path.open("rb") as stream:
                st = os.fstat(stream.fileno())
                inode = (st.st_dev, st.st_ino)
                if inode != self.inode or st.st_size < self.offset:
                    self.offset, self.pending, self.inode = 0, b"", inode
                    self.rows.clear()
                    self.total_rows = self.invalid_lines = 0
                    self.dropping = False
                stream.seek(self.offset)
                data = stream.read(max_read)
                self.offset += len(data)
        except OSError:
            return
        if self.dropping:
            cut = data.find(b"\n")
            if cut < 0:
                return
            data, self.dropping = data[cut + 1:], False
        parts = (self.pending + data).split(b"\n")
        self.pending = parts.pop()
        for line in parts:
            if not line.strip():
                continue
            try:
                if len(line) > 256 * 1024:
                    raise ValueError("oversize JSONL row")
                row = json.loads(line)
                if not isinstance(row, dict):
                    raise ValueError("JSONL row is not an object")
                self.rows.append(finite(row))
                self.total_rows += 1
            except (ValueError, UnicodeError):
                self.invalid_lines += 1
        if len(self.pending) > 256 * 1024:
            self.pending, self.dropping = b"", True
            self.invalid_lines += 1


def sampled(rows, since, points):
    selected = [row for row in rows if (timestamp(row) or 0) >= since]
    if len(selected) <= points:
        return selected
    # Evenly spaced display samples, including endpoints. Raw downloads retain
    # every row; alerts inspect the unsampled latest data.
    return [selected[round(i * (len(selected) - 1) / (points - 1))] for i in range(points)]


def number(value):
    try:
        result = float(value)
        return result if math.isfinite(result) else None
    except (ValueError, TypeError):
        return None


def gpu_sample():
    command = shutil.which("nvidia-smi")
    if command is None:
        fallback = Path("/usr/lib/wsl/lib/nvidia-smi")
        command = str(fallback) if fallback.is_file() else None
    if command is None:
        return {"available": False, "error": "nvidia-smi unavailable"}
    try:
        result = subprocess.run([command,
            "--query-gpu=index,utilization.gpu,memory.used,memory.total,power.draw,temperature.gpu",
            "--format=csv,noheader,nounits"], capture_output=True, text=True, timeout=1.5, check=True)
        devices = []
        for line in result.stdout.splitlines():
            columns = line.split(",")
            if len(columns) == 6:
                devices.append(dict(zip(("index", "util_percent", "memory_used_mib", "memory_total_mib",
                                         "power_w", "temperature_c"), map(number, columns))))
        return {"available": bool(devices), "devices": devices,
                "memory_scope": "Driver/NVML global memory; may differ from CUDA allocator/free memory under WSL"}
    except (OSError, subprocess.SubprocessError) as error:
        return {"available": False, "error": type(error).__name__}


def process_argv_matches(argv, role, *, cwd=None, run_dir=None):
    """Recognize executable entry points, not filenames appearing in arguments."""
    if len(argv) < 2:
        return False

    def same_path(value, expected):
        candidate = Path(value)
        if not candidate.is_absolute():
            if cwd is None:
                return False
            candidate = Path(cwd)/candidate
        expected = Path(expected)
        try:
            return candidate.resolve() == expected.resolve() or candidate.samefile(expected)
        except OSError:
            return False

    if role == "host":
        if Path(argv[0]).name != "dotnet":
            return False
        if len(argv) == 5:
            return ((same_path(argv[1], HERE.parent/"BalancePatchHost/bin/Release/net8.0/BalancePatchHost.dll") or any(same_path(argv[1], HERE/f"experiments/HostV{v}/bin/Release/net8.0/TrainingHostV{v}.dll") for v in (5, 6, 8, 9, 10)))
                    and argv[2:] == ["serve", "--hero-setup", "curriculum-75-25"])
        return (len(argv) == 3 and argv[2] == "serve" and any(same_path(argv[1], binary) for binary in
                    (HERE/"Host/bin/Release/net8.0/TrainingHost.dll",
                     HERE/"experiments/HostV3/bin/Release/net8.0/TrainingHostV3.dll",
                     HERE/"experiments/HostV4/bin/Release/net8.0/TrainingHostV4.dll",
                     HERE/"experiments/HostV5/bin/Release/net8.0/TrainingHostV5.dll")))
    if role != "trainer" or not re.fullmatch(r"python(?:\d+(?:\.\d+)*)?", Path(argv[0]).name):
        return False
    index = 1
    while index < len(argv) and argv[index] in ("-u", "-B", "-E", "-I", "-O", "-OO", "-s", "-S"):
        index += 1
    if index >= len(argv):
        return False
    arguments = argv[index+1:]
    if any(same_path(argv[index], entry) for entry in (
            HERE/"experiments/variant_entry.py", HERE/"experiments/variant_v4_entry.py",
            HERE/"experiments/variant_v5_entry.py", HERE/"experiments/variant_v6_entry.py",
            HERE/"experiments/variant_v7_entry.py", HERE/"experiments/variant_v8_entry.py", HERE/"experiments/variant_v9_entry.py", HERE/"experiments/variant_v10_entry.py", HERE/"experiments/variant_v11_entry.py", HERE/"experiments/variant_v12_entry.py", HERE/"balance_patch_entry.py")):
        if not arguments or arguments[0] != "train":
            return False
        arguments = arguments[1:]
    elif not same_path(argv[index], HERE/"train_campaign.py"):
        return False
    directories = []
    for index, argument in enumerate(arguments):
        if argument == "--run-dir":
            if index+1 >= len(arguments) or arguments[index+1].startswith("--"):
                return False
            directories.append(arguments[index+1])
        elif argument.startswith("--run-dir="):
            directories.append(argument[len("--run-dir="):])
    return (len(directories) == 1 and bool(directories[0])
            and (run_dir is None or same_path(directories[0], run_dir)))


class ResourceSampler:
    def __init__(self, campaign):
        self.campaign = campaign
        self.previous_cpu = None
        self.previous_process = {}
        self.boot_wall = None
        self.hz = os.sysconf("SC_CLK_TCK")
        try:
            for line in Path("/proc/stat").read_text().splitlines():
                if line.startswith("btime "):
                    self.boot_wall = int(line.split()[1])
        except OSError:
            pass

    def process(self, pid, role, now, published_wall=None, run_dir=None):
        if not isinstance(pid, int) or pid <= 0:
            return None
        row = {"pid": pid, "role": role, "alive": False}
        try:
            raw = Path(f"/proc/{pid}/stat").read_text()
            fields = raw[raw.rfind(")") + 2:].split()
            state, start = fields[0], int(fields[19])
            ticks = int(fields[11]) + int(fields[12])
            rss = int(Path(f"/proc/{pid}/statm").read_text().split()[1]) * os.sysconf("SC_PAGE_SIZE")
            cmd = Path(f"/proc/{pid}/cmdline").read_bytes()
            argv = [os.fsdecode(part) for part in cmd.split(b"\0") if part]
            cwd = os.readlink(f"/proc/{pid}/cwd")
            identity_matches = process_argv_matches(argv, role, cwd=cwd, run_dir=run_dir)
            start_wall = self.boot_wall + start / self.hz if self.boot_wall else None
            if published_wall is not None and start_wall is not None and start_wall > published_wall + 2:
                identity_matches = False
            previous = self.previous_process.get(pid)
            cpu = None
            if previous and previous[0] == start and now > previous[2]:
                cpu = max(0., (ticks - previous[1]) / self.hz / (now - previous[2]) * 100)
            self.previous_process[pid] = (start, ticks, now)
            row.update(alive=state not in ("Z", "X") and identity_matches, pid_exists=True,
                       identity_matches=identity_matches, rss_bytes=rss, cpu_percent=cpu,
                       cpu_scope="100% equals one logical CPU", start_wall=start_wall)
        except (OSError, ValueError, IndexError):
            self.previous_process.pop(pid, None)
        return row

    def sample(self, runs):
        now = time.time()
        row = {"wall": now, "gpu": gpu_sample(), "processes": {}}
        try:
            values = list(map(int, Path("/proc/stat").read_text().splitlines()[0].split()[1:9]))
            total, idle = sum(values), values[3] + values[4]
            cpu = None
            if self.previous_cpu:
                delta = total - self.previous_cpu[0]
                if delta > 0:
                    cpu = 100 * (1 - (idle - self.previous_cpu[1]) / delta)
            self.previous_cpu = total, idle
            memory = {}
            for line in Path("/proc/meminfo").read_text().splitlines():
                key, value = line.split(":", 1)
                memory[key] = int(value.split()[0]) * 1024
            row["system"] = {"cpu_percent": cpu, "logical_cpus": os.cpu_count(),
                "ram_total_bytes": memory["MemTotal"], "ram_available_bytes": memory["MemAvailable"],
                "ram_used_bytes": memory["MemTotal"] - memory["MemAvailable"],
                "swap_used_bytes": memory.get("SwapTotal", 0) - memory.get("SwapFree", 0),
                "scope": "WSL/Linux guest, not complete Windows host"}
        except (OSError, KeyError, ValueError, IndexError):
            row["system"] = {"error": "Linux resource counters unavailable"}
        disk = shutil.disk_usage(self.campaign)
        row["disk"] = {"total_bytes": disk.total, "free_bytes": disk.free, "used_bytes": disk.used}
        for run_id, run in runs.items():
            status = run["status"] or {}
            start = run["session_start"] or {}
            supervisor = run["supervisor"] or {}
            trainer = status.get("trainer_pid", status.get("pid", start.get("trainer_pid", supervisor.get("trainer_pid"))))
            host = status.get("host_pid", start.get("host_pid"))
            published = number(status.get("updated_wall"))
            if host is None and isinstance(trainer, int):
                try:
                    children = Path(f"/proc/{trainer}/task/{trainer}/children").read_text().split()
                    for child in children[:32]:
                        command = Path(f"/proc/{int(child)}/cmdline").read_bytes()
                        argv = [os.fsdecode(part) for part in command.split(b"\0") if part]
                        if process_argv_matches(argv, "host", cwd=os.readlink(f"/proc/{int(child)}/cwd")):
                            host = int(child)
                            break
                except (OSError, ValueError):
                    pass
            row["processes"][run_id] = {"trainer": self.process(trainer, "trainer", now, published,
                                                                             run.get("path", self.campaign/run_id)),
                                        "host": self.process(host, "host", now, published)}
        return finite(row)


class Monitor:
    def __init__(self, campaign, interval=3):
        self.campaign = Path(campaign).resolve()
        self.interval = interval
        self.lock = threading.RLock()
        self.stop_event = threading.Event()
        self.tails, self.runs, self.evaluations = {}, {}, {}
        self.evaluation_cache = {}
        self.resources = JsonlTail(self.campaign / "resource.jsonl", max_rows=60000)
        self.sampler = ResourceSampler(self.campaign)
        self.budget, self.error = None, None
        self.balance_sources = {"training_pool": None, "frozen": None}
        self.pool_statistics = TrainingPoolCache(self.campaign)
        self.card_artwork = CardArtwork()
        self.statistics_cache = None
        self.cohort_statistics = {"training_random": TrainingPoolCache(self.campaign, cohort="forced-random"),
                                 "training_natural": TrainingPoolCache(self.campaign, cohort="natural-draft")}
        self.balance_error = None
        self.latest_resource = None
        self.started_wall = time.time()

    def discover(self):
        result = {}
        if not self.campaign.exists():
            return result
        for top in sorted(self.campaign.iterdir()):
            if top.is_symlink() or not top.is_dir():
                continue
            candidates = [top]
            candidates.extend(path for path in sorted(top.iterdir()) if path.is_dir() and not path.is_symlink())
            for path in candidates:
                run_id = path.relative_to(self.campaign).as_posix()
                if RUN_ID.fullmatch(run_id) and any(safe_file(path, name) for name in RUN_ARTIFACTS):
                    result[run_id] = path
        return result

    def refresh(self):
        with self.lock:
            self.budget = read_json(self.campaign, "budget.json")
            self.balance_sources = {
                "training_pool": read_json(self.campaign, "balance-statistics.json"),
                "frozen": read_json(self.campaign, "balance-statistics-frozen.json")}
            evaluation_dir = self.campaign / "evaluations"
            if evaluation_dir.is_dir() and not evaluation_dir.is_symlink():
                self.evaluations = {}
                for path in sorted(evaluation_dir.iterdir()):
                    if EVALUATION_NAME.fullmatch(path.name) and safe_file(evaluation_dir, path.name):
                        stat = path.stat()
                        stamp = (stat.st_ino, stat.st_size, stat.st_mtime_ns)
                        cached = self.evaluation_cache.get(path.name)
                        data = cached[1] if cached and cached[0] == stamp else read_json(evaluation_dir, path.name, MAX_DOWNLOAD)
                        if data is not None:
                            if not cached or cached[0] != stamp:
                                # Large per-game records remain in raw downloads. Never
                                # reparse or resend them on every dashboard refresh.
                                balance = data.get("balance_observations")
                                if isinstance(balance, dict):
                                    data["balance_observations"] = {key: balance.get(key)
                                        for key in ("schema", "scope", "totals", "hero_coverage")}
                                for key in ("catalog", "legal_candidate_opportunities_by_kind_card",
                                            "selected_actions_by_kind_card"):
                                    data.pop(key, None)
                                diagnostics = data.get("shadow_diagnostics")
                                if isinstance(diagnostics, dict):
                                    diagnostics.pop("episodes", None)
                                for key in ("policy_a", "policy_b"):
                                    policy = data.get(key)
                                    if isinstance(policy, dict):
                                        policy.pop("run_identity", None)
                                self.evaluation_cache[path.name] = (stamp, data)
                            self.evaluations[path.name] = {"name": path.name, "data": data,
                                "updated_wall": stat.st_mtime,
                                "censor_traces": trace_listing(safe_directory(evaluation_dir, path.stem + "-censored"))}
                self.evaluation_cache = {key: value for key, value in self.evaluation_cache.items()
                                         if key in self.evaluations}
            for run_id, path in self.discover().items():
                tail = self.tails.setdefault(run_id, JsonlTail(path / "metrics.jsonl"))
                tail.refresh()
                rows = list(tail.rows)
                self.runs[run_id] = {"id": run_id, "path": path,
                    "status": read_json(path, "status.json"), "failure": read_json(path, "failure.json"),
                    "deadline": read_json(path, "deadline.json"), "supervisor": read_json(path, "supervisor.json"),
                    "post_evaluation": read_json(path, "post-evaluation.json"),
                    "progress_watch": read_json(path, "progress-watch.json"),
                    "censor_traces": trace_listing(safe_directory(path, "censored-episodes")),
                    "identity": read_json(path, "identity.json"),
                    "session_start": next((r for r in reversed(rows) if r.get("event") == "session_start"), None),
                    "latest_generation": next((r for r in reversed(rows) if r.get("event") == "generation"), None),
                    "latest_event": rows[-1] if rows else None}
            final_mode = read_json(self.campaign, "final-statistics-mode.json")
            if final_mode and final_mode.get("enabled"):
                # Complete per-hero/seat balance telemetry exceeds the generic 4 MiB
                # status-file limit. Keep a separate bounded allowance for this artifact.
                self.balance_sources = {"final": read_json(self.campaign, "balance-statistics-final.json", max_bytes=16 * 1024 * 1024)}
                self.balance_error = ("Final statistics could not be loaded (invalid JSON or larger than 16 MiB)."
                    if self.balance_sources["final"] is None and safe_file(self.campaign, "balance-statistics-final.json") else None)
                self.resources.refresh()
                return
            try:
                pool = self.pool_statistics.refresh(self.runs)
                self.balance_sources["training_pool"] = pool
                self.balance_error = self.pool_statistics.error
            except (OSError, ValueError, KeyError, TypeError) as error:
                # A statistics failure cannot stop resource/health monitoring.
                self.balance_error = "Balance statistics: " + str(error)
                self.balance_sources["training_pool"] = None
            errors = [self.balance_error] if self.balance_error else []
            for key, cache in self.cohort_statistics.items():
                try:
                    self.balance_sources[key] = cache.refresh(self.runs)
                    if cache.error:
                        errors.append(key + ": " + cache.error)
                except (OSError, ValueError, KeyError, TypeError) as error:
                    self.balance_sources[key] = None
                    errors.append(key + ": " + str(error))
            self.balance_error = "; ".join(errors) or None
            self.resources.refresh()

    def sample_once(self):
        self.refresh()
        with self.lock:
            runs = dict(self.runs)
        resource = self.sampler.sample(runs)
        # Only this independent monitor writes this log. Training files are read-only.
        with (self.campaign / "resource.jsonl").open("a", encoding="utf-8") as stream:
            stream.write(json.dumps(resource, allow_nan=False, separators=(",", ":")) + "\n")
        with self.lock:
            self.latest_resource = resource
            self.resources.refresh()
            self.error = None

    def sampling_loop(self):
        while not self.stop_event.is_set():
            started = time.monotonic()
            try:
                self.sample_once()
            except Exception as error:
                with self.lock:
                    self.error = f"Resource sampler: {type(error).__name__}: {error}"
            self.stop_event.wait(max(.05, self.interval - (time.monotonic() - started)))

    def snapshot(self, window=3600, points=400):
        now = time.time()
        since = now - window if window else 0
        with self.lock:
            budget = dict(self.budget or {})
            active = budget.get("active")
            charged = number(budget.get("charged_seconds")) or 0
            projected = charged
            if active:
                # Live display is an estimate; only the locked ledger is authoritative.
                try:
                    current_boot = Path("/proc/sys/kernel/random/boot_id").read_text().strip()
                    elapsed = max(0., time.monotonic() - active["started_monotonic"])
                    if current_boot == active.get("boot_id"):
                        projected = max(charged, active["charged_at_start"] + min(elapsed, active["grant_seconds"]))
                except (OSError, KeyError, TypeError):
                    pass
            limit = number(budget.get("limit_seconds")) or 43200
            budget_view = {"exists": self.budget is not None, "charged_seconds": charged,
                "estimated_charged_seconds": projected, "remaining_seconds": max(0., limit - projected),
                "limit_seconds": limit, "active": active, "sessions": budget.get("sessions", []),
                "discarded_episodes": budget.get("discarded_episodes", 0),
                "discarded_decisions": budget.get("discarded_decisions", 0),
                "collection_accounting": collection_accounting_view(budget)}
            runs = []
            resource = self.latest_resource or (self.resources.rows[-1] if self.resources.rows else {})
            for run_id, run in self.runs.items():
                tail = self.tails[run_id]
                status, failure, supervisor = run["status"] or {}, run["failure"], run["supervisor"] or {}
                events = list(tail.rows)
                generation = run["latest_generation"] or {}
                checkpoint = safe_file(run["path"], "latest.soicp")
                checkpoint_age = max(0., now - checkpoint.stat().st_mtime) if checkpoint else None
                processes = resource.get("processes", {}).get(run_id, {})
                trainer = processes.get("trainer") or {}
                state = status.get("state", supervisor.get("state", "awaiting_training"))
                alerts = []
                if failure:
                    state = "failed"
                    alerts.append("Training failure: " + str(failure.get("message", failure.get("type", "unknown"))))
                elif supervisor.get("state") == "failed":
                    state = "failed"
                    alerts.append("Supervisor stopped training: " + str(supervisor.get("stop_reason", supervisor.get("returncode"))))
                elif state in ("running", "starting") and trainer and not trainer.get("alive"):
                    state = "stale_process"
                    alerts.append("Trainer PID is absent, exited, or no longer identifies this trainer")
                if active and active.get("pid") == trainer.get("pid"):
                    age = now - (number(active.get("last_heartbeat_wall")) or now)
                    if age > 30:
                        alerts.append(f"Budget heartbeat is {age:.0f}s old")
                config = (run["identity"] or {}).get("configuration", {})
                if trainer.get("alive") and checkpoint_age is not None and checkpoint_age > max(180, 3 * (number(config.get("checkpoint_seconds")) or 60)):
                    alerts.append(f"Checkpoint is {checkpoint_age:.0f}s old")
                if tail.invalid_lines:
                    alerts.append(f"Skipped {tail.invalid_lines} malformed/oversized metric rows")
                if generation.get("rejected_minibatches", 0):
                    alerts.append("Latest generation rejected a PPO minibatch; inspect KL/ratio guard and logs")
                latest_time = timestamp(run["latest_event"] or {})
                runs.append({"id": run_id, "state": state, "alerts": alerts, "status": status,
                    "configuration": config, "latest_generation": generation, "latest_event": run["latest_event"],
                    "last_metric_age_seconds": max(0., now - latest_time) if latest_time else None,
                    "checkpoint_age_seconds": checkpoint_age,
                    "checkpoint_bytes": checkpoint.stat().st_size if checkpoint else None,
                    "processes": processes, "failure": failure, "deadline": run["deadline"],
                    "post_evaluation": run["post_evaluation"],
                    "progress_watch": run.get("progress_watch"),
                    "censor_traces": run["censor_traces"],
                    "series": sampled((e for e in events if e.get("event") == "generation"), since, points),
                    "evaluations": [e for e in events if e.get("event") == "champion_evaluation" and (timestamp(e) or 0) >= since][-100:],
                    "latest_evaluation": next((e for e in reversed(events) if e.get("event") == "champion_evaluation" and e.get("complete")), None),
                    "events": events[-30:], "metric_rows_seen": tail.total_rows,
                    "metric_rows_retained": len(tail.rows), "metric_bytes_read": tail.offset,
                    "artifacts": [name for name in sorted(RUN_ARTIFACTS) if safe_file(run["path"], name)]})
            balance_sources = {}
            for key, source in self.balance_sources.items():
                if not isinstance(source, dict) or source.get("schema") != "shards-balance-statistics-v1":
                    balance_sources[key] = None
                    continue
                source = dict(source)
                refresh = dict(source.get("refresh", {}))
                source_run = next((run for run in runs if run["id"] == source.get("scope", {}).get("run_id")), None)
                if source_run and key == "frozen":
                    current_games = source_run["latest_generation"].get("games", source_run["status"].get("games", 0))
                    refresh["total_games"] = current_games
                    refresh["games_since_snapshot"] = max(0, current_games-refresh.get("snapshot_training_games", current_games))
                source["refresh"] = refresh
                balance_sources[key] = source
            preferred = next((balance_sources[key] for key in
                ("final", "training_random", "training_pool", "training_natural", "frozen")
                if balance_sources.get(key, {}) and balance_sources[key].get("state") == "ready"),
                balance_sources.get("final") or balance_sources.get("frozen") or balance_sources.get("training_pool"))
            return finite({"wall": now, "monitor_started_wall": self.started_wall, "sampler_error": self.error,
                "sample_interval_seconds": self.interval, "budget": budget_view, "runs": runs,
                "balance_statistics": preferred, "balance_statistics_sources": balance_sources,
                "balance_statistics_error": self.balance_error,
                "external_evaluations": list(self.evaluations.values()),
                "resource": resource, "resources": sampled(self.resources.rows, since, points),
                "scope": "Read-only local monitor. No training control. Display series are sampled; JSONL downloads retain every row."})

    def artifact(self, run_id, name):
        if run_id:
            if not RUN_ID.fullmatch(run_id) or name not in RUN_ARTIFACTS:
                return None
            with self.lock:
                run = self.runs.get(run_id)
                folder = safe_directory(self.campaign, run_id) if run else None
                return safe_file(folder, name) if folder else None
        return safe_file(self.campaign, name) if name in CAMPAIGN_ARTIFACTS else None

    def statistics_response(self):
        """Serialize only on publication; browser polling need not transfer rows."""
        with self.lock:
            stamp = (self.balance_error, tuple((key, source.get("snapshot_id"), source.get("updated_wall"),
                    source.get("state")) if source else (key, None) for key, source in self.balance_sources.items()))
            if self.statistics_cache is not None and self.statistics_cache[0] == stamp:
                return self.statistics_cache[1:]
            preferred = next((self.balance_sources[key] for key in
                ("final", "training_random", "training_pool", "training_natural", "frozen")
                if self.balance_sources.get(key) and self.balance_sources[key].get("state") == "ready"), None)
            result = {"wall": time.time(), "balance_statistics": preferred,
                "balance_statistics_sources": self.balance_sources, "balance_statistics_error": self.balance_error,
                "catalog": preferred.get("card_catalog", self.card_artwork.presentation()) if preferred else self.card_artwork.presentation()}
            body = json.dumps(finite(result), allow_nan=False, separators=(",", ":")).encode()
            import hashlib
            tag = '"'+hashlib.sha256(body).hexdigest()+'"'
            self.statistics_cache = stamp, body, tag
            return body, tag

    def evaluation_artifact(self, name):
        folder = self.campaign / "evaluations"
        if not EVALUATION_NAME.fullmatch(name) or folder.is_symlink():
            return None
        return safe_file(folder, name)

    def trace_artifact(self, name, *, run_id="", evaluation=""):
        if not TRACE_NAME.fullmatch(name) or bool(run_id) == bool(evaluation):
            return None
        if run_id:
            if not RUN_ID.fullmatch(run_id) or run_id not in self.runs:
                return None
            folder = safe_directory(self.campaign, run_id + "/censored-episodes")
        else:
            if not EVALUATION_NAME.fullmatch(evaluation) or self.evaluation_artifact(evaluation) is None:
                return None
            folder = safe_directory(self.campaign, "evaluations/" + Path(evaluation).stem + "-censored")
        return safe_file(folder, name) if folder else None


def make_server(monitor, port=8768):
    decklists = GameDecklists(monitor.campaign/'game-decklists.json') if hasattr(monitor, 'campaign') else None
    reviews = BalanceReviewStore(monitor.campaign, HERE.parent / "BalanceReview/balance_proposals.json") if hasattr(monitor, 'campaign') else None
    drafts = CardDraftStore(monitor.campaign) if hasattr(monitor, 'campaign') else None
    class Handler(BaseHTTPRequestHandler):
        def log_message(self, format, *args):
            pass

        def response_headers(self, status, kind, length, *, cache="no-store"):
            self.send_response(status)
            self.send_header("Content-Type", kind)
            self.send_header("Content-Length", str(length))
            self.send_header("Cache-Control", cache)
            self.send_header("X-Content-Type-Options", "nosniff")
            self.send_header("Content-Security-Policy", "default-src 'self'; script-src 'self' 'unsafe-inline'; style-src 'self' 'unsafe-inline'; connect-src 'self'; frame-ancestors 'none'")

        def send(self, status, data, kind="application/json; charset=utf-8"):
            body = data if isinstance(data, bytes) else json.dumps(data, allow_nan=False).encode()
            self.response_headers(status, kind, len(body))
            self.end_headers()
            self.wfile.write(body)

        def loopback_host(self):
            hosts = self.headers.get_all("Host", [])
            if len(hosts) != 1 or not re.fullmatch(r"(?:localhost|127\.0\.0\.1)(?::[0-9]{1,5})?", hosts[0], re.IGNORECASE):
                return None
            host = hosts[0].lower()
            if ":" in host and not 1 <= int(host.rsplit(":", 1)[1]) <= 65535:
                return None
            return host

        def do_POST(self):
            host = self.loopback_host()
            origins = self.headers.get_all("Origin", [])
            if (host is None or origins != ["http://" + host]
                    or self.headers.get("Sec-Fetch-Site", "same-origin") not in ("same-origin", "none")):
                self.send(403, {"error": "Same-origin loopback requests are required", "code": "origin_rejected"})
                return
            route = urlsplit(self.path).path
            if route not in ("/api/balance-reviews", "/api/card-drafts"):
                self.send(404, {"error": "Unknown endpoint"})
                return
            store = reviews if route == "/api/balance-reviews" else drafts
            if store is None:
                self.send(503, {"error": "Local review storage is unavailable"})
                return
            if len(self.headers.get_all("Content-Type", [])) != 1 or self.headers.get_content_type() != "application/json":
                self.send(415, {"error": "Content-Type application/json is required", "code": "invalid_content_type"})
                return
            lengths = self.headers.get_all("Content-Length", [])
            if self.headers.get("Transfer-Encoding") or len(lengths) != 1 or not re.fullmatch(r"[0-9]{1,10}", lengths[0]):
                self.send(400, {"error": "A single valid Content-Length is required", "code": "invalid_length"})
                return
            size = int(lengths[0])
            if size > MAX_REQUEST_BYTES:
                self.send(413, {"error": "Request exceeds 512 KiB", "code": "request_too_large"})
                return
            try:
                self.connection.settimeout(5)
                body = self.rfile.read(size)
                if len(body) != size:
                    raise ValueError("Incomplete request body")
                payload = strict_json(body)
                self.send(200, store.save(payload))
            except ReviewError as error:
                self.send(error.status, error.payload)
            except (ValueError, OSError, RecursionError) as error:
                self.send(400, {"error": "Invalid JSON request: " + str(error), "code": "invalid_json"})

        def do_GET(self):
            if self.loopback_host() is None:
                self.send(403, {"error": "Loopback Host header required"})
                return
            route = urlsplit(self.path)
            query = parse_qs(route.query)
            try:
                if route.path in ("/", "/training_dashboard.html"):
                    self.send(200, (HERE / "training_dashboard.html").read_bytes(), "text/html; charset=utf-8")
                elif route.path in ("/statistics", "/statistics/", "/statistics_dashboard.html"):
                    self.send(200, (HERE / "statistics_dashboard.html").read_bytes(), "text/html; charset=utf-8")
                elif route.path in ("/balance-proposals", "/balance-proposals/"):
                    path = safe_file(HERE, "balance_proposals.html")
                    if path is None:
                        self.send(404, {"error": "Balance proposal page unavailable"})
                    else:
                        self.send(200, path.read_bytes(), "text/html; charset=utf-8")
                elif route.path in ("/balance_proposals.css", "/balance_proposals.js"):
                    name = route.path[1:]
                    path = safe_file(HERE, name)
                    if path is None:
                        self.send(404, {"error": "Balance proposal asset unavailable"})
                    else:
                        kind = "text/css; charset=utf-8" if name.endswith(".css") else "text/javascript; charset=utf-8"
                        self.send(200, path.read_bytes(), kind)
                elif route.path == "/api/balance-proposals":
                    if reviews is None:
                        self.send(503, {"error": "Balance review storage is unavailable"})
                    else:
                        self.send(200, reviews.get())
                elif route.path == "/api/card-drafts":
                    if drafts is None:
                        self.send(503, {"error": "Card design storage is unavailable"})
                    else:
                        self.send(200, drafts.get())
                elif route.path == "/api/card-catalog":
                    path = safe_file(monitor.campaign, "card-catalog.json") if hasattr(monitor, "campaign") else None
                    if path is not None:
                        self.send(200, path.read_bytes())
                    elif hasattr(monitor, "card_artwork"):
                        self.send(200, monitor.card_artwork.presentation())
                    else:
                        self.send(503, {"error": "Card catalog unavailable"})
                elif route.path == "/api/balance-card-previews":
                    path = safe_file(HERE.parent / "BalanceReview", "balance_card_previews.json")
                    if path is None:
                        self.send(404, {"error": "Balance card previews unavailable"})
                    else:
                        self.send(200, path.read_bytes())
                elif route.path in ("/balance-review", "/balance-review/", "/balance-review-data", "/balance-review-data/"):
                    html = route.path in ("/balance-review", "/balance-review/")
                    name = "balance-review.html" if html else "balance-review-data.json"
                    path = safe_file(monitor.campaign, name) if hasattr(monitor, "campaign") else None
                    if path is None:
                        self.send(404, {"error": "No balance review published yet"})
                    else:
                        kind = "text/html; charset=utf-8" if html else "application/json; charset=utf-8"
                        self.send(200, path.read_bytes(), kind)
                elif route.path in ("/games", "/games/"):
                    self.send(200, (HERE / "game_decklists.html").read_bytes(), "text/html; charset=utf-8")
                elif route.path == "/api/games":
                    if decklists is None or not decklists.path.exists():
                        self.send(404, {"error": "No completed-game decklists exported yet"})
                    else:
                        self.send(200, decklists.query(query))
                elif route.path == "/api/card-image":
                    path = monitor.card_artwork.path(query.get("id", [""])[0])
                    if path is None:
                        self.send(404, {"error": "Unknown card artwork"})
                        return
                    stat = path.stat()
                    tag = f'"{stat.st_mtime_ns:x}-{stat.st_size:x}"'
                    if self.headers.get("If-None-Match") == tag:
                        self.response_headers(304, "image/png", 0, cache="private, max-age=3600")
                        self.send_header("ETag", tag)
                        self.end_headers()
                        return
                    body = path.read_bytes()
                    self.response_headers(200, "image/png", len(body), cache="private, max-age=3600")
                    self.send_header("ETag", tag)
                    self.end_headers()
                    self.wfile.write(body)
                elif route.path == "/api/statistics":
                    body, tag = monitor.statistics_response()
                    cached = self.headers.get("If-None-Match") == tag
                    self.response_headers(304 if cached else 200, "application/json; charset=utf-8",
                        0 if cached else len(body), cache="private, no-cache")
                    self.send_header("ETag", tag)
                    self.end_headers()
                    if not cached:
                        self.wfile.write(body)
                elif route.path == "/api/state":
                    window, points = int(query.get("window", ["3600"])[0]), int(query.get("points", ["400"])[0])
                    if not 0 <= window <= 7 * 86400 or not 2 <= points <= 1000:
                        raise ValueError("Query bounds exceeded")
                    self.send(200, monitor.snapshot(window, points))
                elif route.path in ("/api/artifact", "/api/evaluation", "/api/trace"):
                    path = (monitor.trace_artifact(query.get("name", [""])[0],
                                run_id=query.get("run", [""])[0], evaluation=query.get("evaluation", [""])[0])
                            if route.path == "/api/trace" else monitor.evaluation_artifact(query.get("name", [""])[0])
                            if route.path == "/api/evaluation" else
                            monitor.artifact(query.get("run", [""])[0], query.get("name", [""])[0]))
                    if path is None:
                        self.send(404, {"error": "Unknown artifact"})
                        return
                    with path.open("rb") as stream:
                        size = os.fstat(stream.fileno()).st_size
                        if size > MAX_DOWNLOAD:
                            self.send(413, {"error": "Download exceeds 64 MiB; inspect the local file directly"})
                            return
                        self.response_headers(200, "application/octet-stream", size)
                        self.send_header("Content-Disposition", f'attachment; filename="{path.name}"')
                        self.end_headers()
                        remaining = size
                        while remaining:
                            chunk = stream.read(min(65536, remaining))
                            if not chunk:
                                break
                            self.wfile.write(chunk)
                            remaining -= len(chunk)
                else:
                    self.send(404, {"error": "Unknown endpoint"})
            except ReviewError as error:
                self.send(error.status, error.payload)
            except (ValueError, OSError) as error:
                self.send(400, {"error": str(error)})

    server = ThreadingHTTPServer(("127.0.0.1", port), Handler)
    server.daemon_threads = True
    return server


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--campaign-dir", type=Path, required=True)
    parser.add_argument("--port", type=int, default=8768)
    parser.add_argument("--interval", type=float, default=3)
    args = parser.parse_args()
    if not 1 <= args.port <= 65535 or not 3 <= args.interval <= 60:
        parser.error("port1..65535; sampling interval3..60 seconds")
    args.campaign_dir.mkdir(parents=True, exist_ok=True)
    campaign = args.campaign_dir.resolve()
    with (campaign / "monitor.lock").open("a") as lock:
        try:
            fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
        except BlockingIOError:
            parser.error("A monitor already owns this campaign directory")
        monitor = Monitor(campaign, args.interval)
        server = make_server(monitor, args.port)
        metadata = {"pid": os.getpid(), "started_wall": time.time(), "port": args.port,
                    "url": f"http://127.0.0.1:{args.port}/", "interval_seconds": args.interval}
        temporary = campaign / "monitor.json.tmp"
        temporary.write_text(json.dumps(metadata, indent=2) + "\n")
        temporary.replace(campaign / "monitor.json")
        thread = threading.Thread(target=monitor.sampling_loop, name="resource-sampler", daemon=True)
        thread.start()
        def stop(signum, frame):
            monitor.stop_event.set()
            threading.Thread(target=server.shutdown, daemon=True).start()
        signal.signal(signal.SIGINT, stop)
        signal.signal(signal.SIGTERM, stop)
        print(metadata["url"], flush=True)
        try:
            server.serve_forever(poll_interval=.25)
        finally:
            monitor.stop_event.set()
            thread.join(timeout=3)
            server.server_close()


if __name__ == "__main__":
    main()
