"""Local benchmark metadata and lightweight contention monitoring (no training)."""
from __future__ import annotations

import hashlib
import json
import os
import platform
import statistics
import subprocess
import threading
import time
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
DOTNET = os.environ.get("SHARDS_DOTNET", "/home/lva/.dotnet/dotnet")


def gpu_status():
    try:
        text = subprocess.check_output([
            "nvidia-smi", "--query-gpu=memory.used,utilization.gpu,power.draw,temperature.gpu",
            "--format=csv,noheader,nounits"], text=True, timeout=3).strip()
        values = text.splitlines()[0].split(",")
        return dict(zip(("memory_mib", "utilization_percent", "power_w", "temperature_c"),
                        (float(v.strip()) for v in values)))
    except (OSError, ValueError, subprocess.SubprocessError):
        return {}


def source_fingerprint():
    digest = hashlib.sha256()
    files = []
    for relative in ("Assets/Scripts/Core", "Assets/Scripts/Shards/Engine",
                     "Assets/Scripts/Shards/Content", "Tools/TrainingPreflight"):
        for path in sorted((ROOT / relative).rglob("*")):
            if path.suffix not in (".cs", ".csproj", ".py") or any(
                    part in ("obj", "bin", "__pycache__") for part in path.parts):
                continue
            files.append(path)
    for path in sorted(files):
        digest.update(str(path.relative_to(ROOT)).encode())
        digest.update(path.read_bytes())
    return digest.hexdigest()


def metadata():
    import torch
    binary=ROOT / "Tools/TrainingPreflight/Host/bin/Release/net8.0/TrainingHost.dll"
    return {
        "utc": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime()),
        "python": platform.python_version(), "platform": platform.platform(),
        "torch": torch.__version__, "cuda": torch.version.cuda,
        "gpu": torch.cuda.get_device_name(), "capability": torch.cuda.get_device_capability(),
        "cuda_free_total_bytes": torch.cuda.mem_get_info(), "gpu_before": gpu_status(),
        "source_sha256": source_fingerprint(), "cpu_threads": os.cpu_count(),
        "host_binary_sha256": hashlib.sha256(binary.read_bytes()).hexdigest() if binary.exists() else None,
        "training_campaign_started": False,
    }


def distribution(values):
    values = sorted(values)
    if not values:
        return {}
    return {"median": statistics.median(values), "min": values[0], "max": values[-1],
            "p95": values[min(len(values)-1, int(len(values)*0.95))], "n": len(values)}


class Monitor:
    def __init__(self):
        self.samples = []
        self.stop = threading.Event()
        self.thread = threading.Thread(target=self._run, daemon=True)

    def _run(self):
        while not self.stop.is_set():
            self.samples.append({"time": time.monotonic(), **gpu_status()})
            self.stop.wait(1.0)

    def __enter__(self):
        self.thread.start()
        return self

    def __exit__(self, *exc):
        self.stop.set()
        self.thread.join(timeout=4)

    def summary(self):
        return {key: distribution([row[key] for row in self.samples if key in row])
                for key in ("memory_mib", "utilization_percent", "power_w", "temperature_c")}


def save_json(path, value):
    path = Path(path)
    path.parent.mkdir(parents=True, exist_ok=True)
    temporary = path.with_suffix(path.suffix + ".tmp")
    temporary.write_text(json.dumps(value, indent=2) + "\n")
    temporary.replace(path)
