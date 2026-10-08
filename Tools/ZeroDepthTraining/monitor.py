"""Read-only local zero-depth training dashboard; no Torch or training controls.

The trainer publishes aggregate rolling statistics. This process tails cohort
metrics incrementally and samples resources separately, without scanning the
100,000-game ring or opening model checkpoints.
"""
from __future__ import annotations

import argparse
from collections import deque
from datetime import datetime
import fcntl
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
import json
import math
import os
from pathlib import Path
import shutil
import signal
import subprocess
import threading
import time
from urllib.parse import urlsplit


HERE = Path(__file__).resolve().parent


def finite(value):
    if isinstance(value, float) and not math.isfinite(value):
        return None
    if isinstance(value, dict):
        return {key: finite(item) for key, item in value.items()}
    if isinstance(value, list):
        return [finite(item) for item in value]
    return value


def timestamp(row):
    wall = row.get("wall", row.get("utc"))
    if isinstance(wall, (int, float)) and not isinstance(wall, bool):
        return float(wall) if math.isfinite(wall) else None
    if isinstance(wall, str):
        try:
            return datetime.fromisoformat(wall.replace("Z", "+00:00")).timestamp()
        except ValueError:
            return None
    return None


def _monotonic_timestamp(row):
    value = row.get("monotonic")
    if isinstance(value, (int, float)) and not isinstance(value, bool) and math.isfinite(value) and value >= 0:
        return float(value)
    return None


def _raw_timestamp(row):
    value = row.get("monotonic_raw")
    if isinstance(value, (int, float)) and not isinstance(value, bool) and math.isfinite(value) and value >= 0:
        return float(value)
    return None


def _raw_now():
    """Read the hardware clock if available; never synthesize a substitute."""
    clock = getattr(time, "CLOCK_MONOTONIC_RAW", None)
    if clock is None:
        return None
    try:
        return time.clock_gettime(clock)
    except (AttributeError, OSError):
        return None


def _boot_id():
    try:
        return Path("/proc/sys/kernel/random/boot_id").read_text().strip() or None
    except OSError:
        return None


def _generation_speed(previous, row):
    """Compare one clock domain; preserve wall timestamps solely for display."""
    if "monotonic_raw" in previous or "monotonic_raw" in row:
        boot = row.get("boot_id")
        if not isinstance(boot, str) or not boot or previous.get("boot_id") != boot:
            return None, "monotonic_raw"
        before, after = _raw_timestamp(previous), _raw_timestamp(row)
        clock = "monotonic_raw"
    else:
        before, after = timestamp(previous), timestamp(row)
        clock = "wall"
    old_games, games = previous.get("games"), row.get("games")
    if (before is None or after is None or after <= before or type(old_games) is not int
            or type(games) is not int or games < old_games):
        return None, clock
    return (games - old_games) / (after - before), clock


class JsonlTail:
    """Own complete JSONL records; keep bounded history and partial writes."""
    def __init__(self, path, max_rows=6000):
        self.path = Path(path)
        self.rows = deque(maxlen=max_rows)
        self.offset = 0
        self.pending = b""
        self.inode = None
        self.invalid_lines = 0
        self.dropping = False
        self.latest_rolling = None
        self.latest_rolling_wall = None
        self._rolling_holder = None

    def refresh(self, max_bytes=8 * 1024 * 1024):
        if self.path.is_symlink():
            return
        try:
            with self.path.open("rb") as stream:
                stat = os.fstat(stream.fileno())
                inode = stat.st_dev, stat.st_ino
                if inode != self.inode or stat.st_size < self.offset:
                    self.rows.clear()
                    self.offset = self.invalid_lines = 0
                    self.pending = b""
                    self.dropping = False
                    self.latest_rolling = self.latest_rolling_wall = self._rolling_holder = None
                    self.inode = inode
                    # Only display history is needed; cohort statistics are
                    # already computed by the trainer. Start near the end of a
                    # large existing log instead of replaying hours of snapshots.
                    if stat.st_size > max_bytes:
                        self.offset = stat.st_size - max_bytes
                        self.dropping = True
                stream.seek(self.offset)
                data = stream.read(max_bytes)
                self.offset += len(data)
        except OSError:
            return
        if self.dropping:
            cut = data.find(b"\n")
            if cut < 0:
                return
            data = data[cut + 1:]
            self.dropping = False
        parts = (self.pending + data).split(b"\n")
        self.pending = parts.pop()
        for line in parts:
            if not line.strip():
                continue
            try:
                if len(line) > 512 * 1024:
                    raise ValueError("oversize metric")
                value = json.loads(line)
                if not isinstance(value, dict):
                    raise ValueError("metric is not an object")
                value = finite(value)
                if isinstance(value.get("rolling_game_stats"), dict):
                    if self._rolling_holder is not None:
                        self._rolling_holder.pop("rolling_game_stats", None)
                    self.latest_rolling = value["rolling_game_stats"]
                    self.latest_rolling_wall = timestamp(value)
                    self._rolling_holder = value
                self.rows.append(value)
            except (ValueError, UnicodeError):
                self.invalid_lines += 1
        if len(self.pending) > 512 * 1024:
            self.pending = b""
            self.dropping = True
            self.invalid_lines += 1


def observed_rate(rows, now, *, running, window_seconds=120, raw_now=None, boot_id=None):
    """Completed-game deltas over same-boot hardware time, including idle gaps.

    A new start is a resume baseline. RAW telemetry uses the unadjusted clock;
    another boot or invalid RAW never falls back to an adjusted clock. Legacy
    records or platforms without RAW explicitly use wall time. Inactive/idle
    windows are 0. Ordinary monotonic remains a budget/heartbeat diagnostic.
    """
    rows = list(rows)
    last_start = next((index for index in range(len(rows)-1, -1, -1)
                       if rows[index].get("event") == "start"), 0)
    rows = rows[last_start:]
    use_raw = any("monotonic_raw" in row for row in rows)
    result = dict(games_per_second=0., estimated_games_per_hour=0., observed_seconds=0.,
                  completed_game_delta=0, window_seconds=window_seconds, active=bool(running),
                  clock="monotonic_raw" if use_raw else "wall",
                  clock_boot_id=boot_id if use_raw else None)
    if use_raw:
        if (not isinstance(boot_id, str) or not boot_id or not isinstance(raw_now, (int, float))
                or isinstance(raw_now, bool) or not math.isfinite(raw_now) or raw_now < 0):
            return result
        now = float(raw_now)
    points = []
    last_completion = None
    for row in rows:
        if row.get("event") == "start":
            points.clear()
            last_completion = None
        if use_raw and row.get("boot_id") != boot_id:
            points.clear()
            last_completion = None
            continue
        elapsed = _raw_timestamp(row) if use_raw else timestamp(row)
        games = row.get("games")
        if elapsed is None or elapsed > now or type(games) is not int or games < 0:
            continue
        if points and (elapsed < points[-1][0] or games < points[-1][1]
                       or (elapsed == points[-1][0] and games > points[-1][1])):
            points.clear()
            last_completion = None
        if points and games > points[-1][1]:
            last_completion = elapsed
        points.append((elapsed, games))
    if not running or len(points) < 2 or last_completion is None or last_completion <= now - window_seconds:
        return result
    cutoff = now - window_seconds
    before = [point for point in points if point[0] <= cutoff]
    anchor = before[-1] if before else points[0]
    span = now - anchor[0]
    delta = points[-1][1] - anchor[1]
    if span > 0 and delta >= 0:
        rate = delta / span
        result.update(games_per_second=rate, estimated_games_per_hour=rate * 3600,
                      observed_seconds=span, completed_game_delta=delta)
    return result


def _alive(pid):
    if type(pid) is not int or pid <= 0:
        return False
    try:
        os.kill(pid, 0)
        return True
    except (OSError, ValueError):
        return False


def gpu_sample():
    command = shutil.which("nvidia-smi")
    if command is None:
        candidate = Path("/usr/lib/wsl/lib/nvidia-smi")
        command = str(candidate) if candidate.is_file() else None
    if command is None:
        return None
    try:
        result = subprocess.run([command, "--query-gpu=utilization.gpu,memory.used,memory.total,power.draw,temperature.gpu",
                                 "--format=csv,noheader,nounits"], capture_output=True, text=True, timeout=1.5, check=True)
        first = result.stdout.splitlines()[0].split(",")
        values = [float(value) for value in first]
        if len(values) != 5 or not all(math.isfinite(value) for value in values):
            return None
        return dict(zip(("utilization_percent", "memory_used_mib", "memory_total_mib", "power_w", "temperature_c"), values))
    except (OSError, subprocess.SubprocessError, ValueError, IndexError):
        return None


class Monitor:
    def __init__(self, campaign, *, now=time.time, monotonic_now=time.monotonic, raw_now=_raw_now, boot_id=None):
        self.campaign = Path(campaign).resolve()
        self.now = now
        self.monotonic_now = monotonic_now
        self.raw_now = raw_now
        self.boot_id = _boot_id() if boot_id is None else boot_id
        self.tail = JsonlTail(self.campaign / "metrics.jsonl")
        self.cache = {}
        self.lock = threading.RLock()
        self.resources = None
        self.resource_wall = None

    def read(self, name, *, maximum=8 * 1024 * 1024):
        path = self.campaign / name
        try:
            if path.is_symlink() or not path.is_file():
                return None
            stat = path.stat()
            if stat.st_size > maximum:
                return None
            stamp = stat.st_ino, stat.st_size, stat.st_mtime_ns
            if name in self.cache and self.cache[name][0] == stamp:
                return self.cache[name][1]
            value = json.loads(path.read_bytes())
            if not isinstance(value, dict):
                return None
            value = finite(value)
            self.cache[name] = stamp, value
            return value
        except (OSError, ValueError):
            return None

    def evaluation(self):
        files = sorted((path for path in self.campaign.glob("post-training-evaluation-*.json") if not path.name.endswith(".plan.json")),
                       key=lambda path: path.stat().st_mtime_ns if not path.is_symlink() else 0, reverse=True)
        for path in files:
            value = self.read(path.name, maximum=32 * 1024 * 1024)
            if value is not None:
                # Per-game outcomes stay on disk; the monitor serves only the
                # fixed plan and its paired aggregate evidence.
                return {key: value.get(key) for key in ("purpose", "pairs", "games", "checkpoint_sha256", "summary",
                        "created_utc", "elapsed_seconds", "error", "incumbent_tactical_search")} | {"file": path.name}
        return None

    def review_report(self):
        service=self.read("review-service.json",maximum=8192)
        if not service or not isinstance(service.get("directory"),str):return None
        directory=Path(service["directory"])
        state=self.read(directory/"state.json",maximum=65536) or {}
        active=self.read(directory/"active.json",maximum=65536) or {}
        return {"interval_seconds":service.get("interval_seconds"),"state":state,"active":active,
                "overdue_seconds":max(0,self.now()-state["next_review_wall"]) if state.get("next_review_wall") else None}

    def league_report(self):
        value = self.read("league/latest.json", maximum=2 * 1024 * 1024)
        if value is None or value.get("schema") != "shards-zero-depth-neural-arena-v1":
            return None
        # JSON-only exploratory evidence; model tensors and per-game arrays stay
        # outside the monitor. It cannot replace the final incumbent gate.
        result = {key: value.get(key) for key in ("schema", "purpose", "promotion_allowed", "summary", "rounds", "hero_coverage",
                  "planned_games", "seed_base", "created_utc", "elapsed_seconds", "elapsed_clock", "sidecar_elapsed_seconds",
                  "sidecar_elapsed_clock", "stop_reason", "integrity_verified", "initial_publication_mirrors_verified")}
        for name in ("candidate", "baseline"):
            metadata = value.get(name) or {}
            result[name] = {"training": metadata.get("training"), "checkpoint_sha256": metadata.get("checkpoint_sha256"),
                            "source_fingerprint": (metadata.get("identity") or {}).get("source_fingerprint")}
        return result

    def champion_report(self):
        state = self.read("champion/state.json", maximum=16384)
        if not state or state.get("schema") != "shards-zero-depth-champion-v1": return None
        latest = self.read("champion/latest.json", maximum=65536)
        return {"state": state, "latest": latest}

    def learning_rate_change(self, active_rate):
        value = self.read("learning-rate-improvement.json", maximum=16384)
        if value is None or value.get("schema") != "shards-zero-depth-learning-rate-improvement-v1":
            return None
        before, after = value.get("before_learning_rate"), value.get("after_learning_rate")
        if (type(before) not in (int, float) or type(after) not in (int, float)
                or not 0 < before < .1 or not 0 < after < .1 or before == after or after != active_rate
                or type(value.get("at_games")) is not int or value["at_games"] < 0):
            return None
        return {key: value.get(key) for key in ("schema", "before_learning_rate", "after_learning_rate", "at_games",
                "created_utc", "reason", "evidence_file")}

    def state(self):
        with self.lock:
            now = self.now()
            monotonic_now = self.monotonic_now()
            raw_now = self.raw_now()
            self.tail.refresh()
            budget = self.read("budget.json") or {}
            status = self.read("status.json") or {}
            supervisor = self.read("supervisor.json") or {}
            configuration = self.read("config.json") or {}
            pinned = self.read("identity.json", maximum=65536) or {}
            pinned_configuration = pinned.get("configuration")
            if not isinstance(pinned_configuration, dict): pinned_configuration = {}
            learning_rate = pinned_configuration.get("learning_rate", configuration.get("learning_rate", .0003 if configuration else None))
            if type(learning_rate) not in (int, float) or not 0 < learning_rate < .1: learning_rate = None
            learning_rate_source = "identity" if "learning_rate" in pinned_configuration else "config" if "learning_rate" in configuration else "default" if configuration else None
            prepared = self.read("prepared.json") or {}
            deadline = self.read("deadline.json") or {}
            active = budget.get("active")
            rows = list(self.tail.rows)
            latest_index = next((index for index in range(len(rows)-1, -1, -1) if rows[index].get("event") == "generation"), -1)
            start_index = next((index for index in range(len(rows)-1, -1, -1) if rows[index].get("event") == "start"), -1)
            latest = rows[latest_index] if latest_index >= 0 else {}
            start = rows[start_index] if start_index >= 0 else {}
            evaluation = self.evaluation()
            launcher_alive = _alive(supervisor.get("supervisor_pid"))
            trainer_alive = _alive(active.get("pid") if active else supervisor.get("trainer_pid"))
            heartbeat = active.get("last_heartbeat_wall") if active else None
            heartbeat_age = max(0., now - heartbeat) if isinstance(heartbeat, (float, int)) else None
            same_boot_heartbeat = (active and self.boot_id and active.get("boot_id") == self.boot_id
                                   and isinstance(active.get("last_heartbeat_monotonic"), (int, float))
                                   and not isinstance(active.get("last_heartbeat_monotonic"), bool)
                                   and math.isfinite(active["last_heartbeat_monotonic"]))
            if same_boot_heartbeat:
                heartbeat_age = max(0., monotonic_now - active["last_heartbeat_monotonic"])
            if active:
                phase = "training" if trainer_alive else "trainer_missing"
                if heartbeat_age is not None and heartbeat_age > 60 and trainer_alive:
                    phase = "heartbeat_stale"
            elif evaluation:
                summary = evaluation.get("summary") or {}
                phase = "complete" if summary.get("evaluation_finished") else "evaluation_failed" if evaluation.get("error") else "evaluating" if launcher_alive else "evaluation_incomplete"
                if evaluation.get("purpose") == "pipeline_validation":
                    phase = "prepared" if not supervisor else "training_complete"
            elif supervisor.get("state") == "failed":
                phase = "failed"
            elif supervisor.get("state") == "complete":
                phase = "evaluation_pending" if launcher_alive and status.get("optimizer_steps", 0) else "training_stopped" if supervisor.get("stop_reason") == "requested_stop" else "training_complete"
            elif supervisor:
                phase = "starting" if launcher_alive or trainer_alive else "launcher_missing"
            else:
                phase = "prepared" if prepared else "awaiting_preparation"
            running = phase == "training"
            rate = observed_rate(rows, now, running=running, raw_now=raw_now, boot_id=self.boot_id)
            charged = float(budget.get("charged_seconds", 0))
            if active and trainer_alive and isinstance(active.get("last_heartbeat_monotonic"), (int, float)):
                # A current heartbeat is an elapsed lower bound, not a completed
                # cohort counter. Extrapolate charged elapsed time only while owned
                # training is alive and within its original grant.
                elapsed = (max(0., monotonic_now - active["last_heartbeat_monotonic"]) if same_boot_heartbeat
                           else max(0., now - float(active.get("last_heartbeat_wall", now))))
                charged = min(float(budget.get("limit_seconds", 43200)), charged + elapsed)
            limit = float(budget.get("limit_seconds", 43200))
            rolling = self.tail.latest_rolling or status.get("rolling_game_stats")
            latest_games = latest.get("games")
            # Resume may intentionally roll back an uncommitted metrics tail.
            if start_index > latest_index:
                latest_games = start.get("games", status.get("games", 0))
                rolling = status.get("rolling_game_stats")
                latest = {}
            series = []
            previous = None
            for row in rows:
                if row.get("event") == "start":
                    previous = row
                    continue
                if row.get("event") != "generation":
                    continue
                wall = timestamp(row)
                speed = None
                clock = None
                if previous:
                    speed, clock = _generation_speed(previous, row)
                series.append({key: row.get(key) for key in ("generation", "games", "loss", "entropy", "approx_kl", "gradient_norm")} |
                              {"wall": wall, "observed_games_per_second": speed, "rate_clock": clock})
                previous = row
            if len(series) > 600:
                series = [series[round(index * (len(series) - 1) / 599)] for index in range(600)]
            catalog = self.read("catalog.json") or {}
            presentation = {item.get("id"): {"name": item.get("Name", item.get("name", item.get("id"))),
                            "type": item.get("Type", item.get("type")), "set": item.get("Set", item.get("set"))}
                            for item in catalog.get("static_catalog", [])}
            collection = budget.get("collection_accounting") or {}
            return finite({"schema": "shards-zero-depth-monitor-v1", "wall": now, "campaign": self.campaign.name,
                "phase": phase, "trainer_alive": trainer_alive, "launcher_alive": launcher_alive,
                "heartbeat_age_seconds": heartbeat_age, "rate": rate,
                "model": {"width": status.get("model_width", configuration.get("width")),
                          "learning_rate": learning_rate, "learning_rate_source": learning_rate_source,
                          "hero_mode": configuration.get("hero_mode", "policy"),
                          "parameters": status.get("model_parameters", start.get("model_parameters", prepared.get("model_parameters"))),
                          "lookahead_depth": 0},
                "progress": {"games": latest_games if type(latest_games) is int else status.get("games", 0),
                    "generations": latest.get("generation", status.get("generations", 0)),
                    "optimizer_steps": latest.get("total_optimizer_steps", status.get("optimizer_steps")),
                    "decisions": latest.get("total_decisions", status.get("decisions")),
                    "attempted_games": collection.get("attempted_games", status.get("attempts", 0)),
                    "censored_games": collection.get("censored_games", status.get("censored", 0)),
                    "discarded_games": budget.get("discarded_episodes", 0),
                    "discarded_decisions": budget.get("discarded_decisions", 0)},
                "budget": {"limit_seconds": limit, "charged_seconds": charged, "remaining_seconds": max(0., limit - charged),
                           "fraction": min(1., charged / limit) if limit else 0},
                "losses": {key: latest.get(key) for key in ("loss", "entropy", "approx_kl", "gradient_norm", "behavior_logp_error", "behavior_value_error",
                    "diagnostic_scope", "accepted_minibatches", "diagnostic_rows", "normalized_entropy", "mean_legal_actions", "clip_fraction",
                    "value_mse", "value_explained_variance", "rejected_kl", "rejected_minibatch_rows", "effective_epochs", "update_coverage")},
                "rolling_game_stats": rolling, "card_catalog": presentation,
                "archive_pool": latest.get("archive_pool", status.get("archive_pool")),
                "selected_opponent": latest.get("selected_opponent"), "league": self.league_report(), "champion": self.champion_report(),
                "learning_rate_improvement": self.learning_rate_change(learning_rate), "reviews":self.review_report(),
                "series": series, "evaluation": evaluation, "supervisor": supervisor,
                "status": status.get("state"), "checkpoint_age_seconds": self.checkpoint_age(now),
                "resources": self.resources, "resource_wall": self.resource_wall,
                "invalid_metric_lines": self.tail.invalid_lines, "deadline": deadline,
                "interpretation": "Rates measure completed-game deltas over same-boot unadjusted hardware elapsed time (CLOCK_MONOTONIC_RAW), including PPO/archive/checkpoint/reset/idle gaps; legacy or unavailable-RAW telemetry explicitly uses wall time. Games/hour is a current-rate projection. Self-play outcome balance and changing archive scores do not prove AI strength; the fixed post-training incumbent evaluation supplies that evidence."})

    def checkpoint_age(self, now):
        try:
            return max(0., now - (self.campaign / "latest.soicp").stat().st_mtime)
        except OSError:
            return None


def make_server(monitor, *, port=8766, bind="127.0.0.1"):
    class Handler(BaseHTTPRequestHandler):
        def log_message(self, *_args):
            pass

        def do_GET(self):
            path = urlsplit(self.path).path
            if path in ("/", "/dashboard.html"):
                body, kind, code = (HERE / "dashboard.html").read_bytes(), "text/html; charset=utf-8", 200
            elif path == "/api/state":
                body, kind, code = json.dumps(monitor.state(), allow_nan=False, separators=(",", ":")).encode(), "application/json", 200
            elif path == "/health":
                body, kind, code = b'{"monitor_alive":true}', "application/json", 200
            else:
                body, kind, code = b"Not found", "text/plain", 404
            self.send_response(code)
            self.send_header("Content-Type", kind)
            self.send_header("Content-Length", str(len(body)))
            self.send_header("Cache-Control", "no-store")
            self.send_header("X-Content-Type-Options", "nosniff")
            self.end_headers()
            try:
                self.wfile.write(body)
            except (BrokenPipeError, ConnectionResetError):
                pass

    server = ThreadingHTTPServer((bind, port), Handler)
    server.daemon_threads = True
    return server


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--campaign", type=Path, required=True)
    parser.add_argument("--port", type=int, default=8766)
    parser.add_argument("--bind", choices=("127.0.0.1", "0.0.0.0"), default="127.0.0.1")
    parser.add_argument("--interval", type=float, default=3)
    parser.add_argument("--no-gpu-sampling", action="store_true")
    args = parser.parse_args()
    if not 0 <= args.port <= 65535 or not 1 <= args.interval <= 60:
        parser.error("Require a valid port and a 1..60-second monitor interval")
    if not args.campaign.is_dir():
        parser.error("Campaign directory must already exist")
    monitor = Monitor(args.campaign)
    with (monitor.campaign / "monitor.lock").open("a") as lock:
        try:
            fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
        except BlockingIOError:
            raise RuntimeError("A monitor already owns this campaign")
        server = make_server(monitor, port=args.port, bind=args.bind)
        port = server.server_address[1]
        url = f"http://127.0.0.1:{port}"
        stop = threading.Event()
        signal.signal(signal.SIGTERM, lambda *_: stop.set())
        signal.signal(signal.SIGINT, lambda *_: stop.set())
        def publish(state):
            path = monitor.campaign / "monitor.json"
            temporary = path.with_suffix(".tmp")
            temporary.write_text(json.dumps(dict(pid=os.getpid(), url=url, bind=args.bind, port=port,
                updated_wall=time.time(), state=state, read_only_training=True), indent=2) + "\n")
            temporary.replace(path)
        worker = threading.Thread(target=server.serve_forever, daemon=True)
        worker.start()
        publish("running")
        print(json.dumps({"monitor": "running", "url": url, "campaign": str(monitor.campaign), "pid": os.getpid()}), flush=True)
        try:
            while not stop.is_set():
                if not args.no_gpu_sampling:
                    resource = gpu_sample()
                    with monitor.lock:
                        monitor.resources = resource
                        monitor.resource_wall = time.time()
                monitor.state()
                publish("running")
                stop.wait(args.interval)
        finally:
            server.shutdown()
            server.server_close()
            worker.join(timeout=2)
            publish("stopped")


if __name__ == "__main__":
    main()
