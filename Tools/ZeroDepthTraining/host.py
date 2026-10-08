"""Bounded binary control pipe and directly mapped full-information batches."""
from __future__ import annotations

import json
import mmap
import os
from pathlib import Path
import select
import struct
import subprocess
import tempfile
import threading
import time

import numpy as np

from model import dimensions

HERE = Path(__file__).resolve().parent
PROJECT = HERE.parents[1]
DOTNET = Path("/home/lva/.dotnet/dotnet")
BINARY = HERE / "Host/bin/Release/net8.0/ZeroDepthHost.dll"
HEADER = struct.Struct("<8I")


def catalog(binary=BINARY):
    return json.loads(subprocess.check_output([str(DOTNET), str(binary), "catalog"], text=True, timeout=45))


class Host:
    def __init__(self, batch=128, workers=8, seed=0x2000000000000000, *, binary=BINARY,
                 automation=True, opponent_bundle=None, paired=False, timeout=60, hero_mode="policy"):
        if not 1 <= batch <= 4096 or not 1 <= workers <= 16:
            raise ValueError("Host batch/workers outside supported range")
        if type(hero_mode) is not str or hero_mode not in ("policy", "balanced_random"):
            raise ValueError("Hero mode must be policy or balanced_random")
        if hero_mode == "balanced_random" and (paired or opponent_bundle is not None):
            raise ValueError("Balanced random heroes are training-only; evaluation keeps its normal draft")
        if paired and batch % 2:
            raise ValueError("Paired evaluation requires an even batch")
        self.batch, self.workers, self.seed = batch, workers, int(seed)
        self.hero_mode = hero_mode
        self.binary, self.timeout = Path(binary), timeout
        self.catalog = catalog(binary)
        self.obs_dim, self.max_actions, self.action_dim = dimensions(self.catalog)
        floats = batch * (self.obs_dim + self.max_actions * self.action_dim + self.max_actions + 2)
        self.payload_bytes = floats * 4 + batch * 8
        descriptor, filename = tempfile.mkstemp(prefix="shards-zero-depth-", dir="/dev/shm")
        self.filename = filename
        try:
            os.ftruncate(descriptor, self.payload_bytes)
            self.shared = mmap.mmap(descriptor, self.payload_bytes)
        except BaseException:
            Path(filename).unlink(missing_ok=True)
            raise
        finally:
            os.close(descriptor)
        env = os.environ.copy()
        env.update(SHARDS_SHARED_BUFFER=filename, DOTNET_PROCESSOR_COUNT=str(workers))
        # Dedicated variables do not alter any existing campaign runtime.
        env["SHARDS_ZERO_AUTOMATION"] = "singleton" if automation else "none"
        env["SHARDS_ZERO_PAIRED"] = "1" if paired else "0"
        env["SHARDS_ZERO_HERO_MODE"] = hero_mode
        env.pop("SHARDS_ZERO_OPPONENT", None)
        if opponent_bundle is not None:
            env["SHARDS_ZERO_OPPONENT"] = str(Path(opponent_bundle).resolve())
        self.errors = []
        try:
            self.process = subprocess.Popen([str(DOTNET), str(binary), "serve"], stdin=subprocess.PIPE,
                                            stdout=subprocess.PIPE, stderr=subprocess.PIPE, bufsize=0, env=env)
            os.set_blocking(self.process.stdin.fileno(), False)
            self.stderr_thread = threading.Thread(target=self._stderr, daemon=True)
            self.stderr_thread.start()
            offset = 0
            def array(shape, dtype):
                nonlocal offset
                result = np.ndarray(shape, dtype=dtype, buffer=self.shared, offset=offset)
                offset += result.nbytes
                return result
            self.obs = array((batch, self.obs_dim), "<f4")
            self.candidates = array((batch, self.max_actions, self.action_dim), "<f4")
            self.mask = array((batch, self.max_actions), "<f4")
            self.rewards = array((batch, 2), "<f4")
            self.done = array((batch,), "<i4")
            self.actors = array((batch,), "<i4")
            self.trailer = None
            self.reset(seed)
        except BaseException:
            self.close()
            raise

    def _stderr(self):
        for line in iter(self.process.stderr.readline, b""):
            self.errors.append(line.decode("utf-8", "replace").rstrip())
            # Keep the exception message at the beginning as well as its tail;
            # AggregateException stacks otherwise erase the useful cause.
            if len(self.errors) > 120:
                self.errors = self.errors[:40] + self.errors[-80:]

    def _read(self, size):
        chunks, left = [], size
        deadline = time.monotonic() + self.timeout
        while left:
            wait = max(0., deadline - time.monotonic())
            if not wait or not select.select([self.process.stdout], [], [], wait)[0]:
                raise TimeoutError("Zero-depth host response timed out: " + "\n".join(self.errors))
            part = os.read(self.process.stdout.fileno(), left)
            if not part:
                self.stderr_thread.join(timeout=.5)
                raise RuntimeError("Zero-depth host exited: " + "\n".join(self.errors))
            chunks.append(part)
            left -= len(part)
        return b"".join(chunks)

    def _write(self, packet, *, timeout=None):
        view = memoryview(packet)
        deadline = time.monotonic() + (self.timeout if timeout is None else timeout)
        while view:
            wait = max(0., deadline - time.monotonic())
            if not wait or not select.select([], [self.process.stdin], [], wait)[1]:
                raise TimeoutError("Zero-depth host command timed out: " + "\n".join(self.errors))
            try:
                count = os.write(self.process.stdin.fileno(), view)
            except BlockingIOError:
                continue
            if not count:
                raise RuntimeError("Zero-depth host command pipe closed")
            view = view[count:]

    def receive(self):
        header = HEADER.unpack(self._read(HEADER.size))
        magic, version, count, obs, actions, action_dim, payload, trailer = header
        if magic != 0x534f4931 or version != 1 or (count, obs, actions, action_dim, payload) != (
                self.batch, self.obs_dim, self.max_actions, self.action_dim, self.payload_bytes):
            raise RuntimeError(f"Host binary/schema handshake mismatch: {header}")
        if trailer != 64:
            raise RuntimeError("Host timing trailer changed")
        self.trailer = np.frombuffer(self._read(trailer), dtype="<f8").copy()
        if not np.isin(self.done, [0, 1, 2]).all() or not np.isin(self.actors, [0, 1]).all():
            raise RuntimeError("Invalid terminal/seat host metadata")

    @property
    def metrics(self):
        """Publication telemetry; fused service includes rules and encoding.

        Counters marked cumulative restart at reset. Terminal/censored counts
        describe held lanes and are not per-response completions. Encode time
        is unavailable independently in this fused host, rather than zero cost.
        """
        if self.trailer is None:
            return {}
        service, _encode, choices, submissions, terminal, censored, events, heap = self.trailer
        return {"combined_service_seconds": float(service)/1000.,
                "policy_choices_cumulative": float(choices),
                "engine_submissions_cumulative": float(submissions),
                "held_terminal_lanes": float(terminal), "held_censored_lanes": float(censored),
                "resident_logged_events": float(events), "managed_heap_bytes": float(heap)}

    def reset(self, seed=None):
        self.seed = int(self.seed if seed is None else seed)
        self._write(struct.pack("<IIIQ", 1, self.batch, self.workers, self.seed))
        self.receive()

    def advance(self, actions):
        actions = np.asarray(actions, dtype="<i4")
        if actions.shape != (self.batch,):
            raise ValueError("Action vector differs from host batch")
        active = np.flatnonzero(actions >= 0)
        if np.any(actions < -1) or np.any(actions[active] >= self.max_actions):
            raise ValueError("Action index outside menu")
        if np.any(self.done[active]) or not np.all(self.mask[active, actions[active]] == 1):
            raise ValueError("Terminal or illegal action submitted")
        self._write(struct.pack("<I", 2) + actions.tobytes())
        self.receive()

    advance_active = advance

    def close(self):
        if getattr(self, "process", None) is not None:
            if self.process.poll() is None:
                try:
                    self._write(struct.pack("<I", 3), timeout=min(1., self.timeout))
                    self.process.wait(timeout=3)
                except (OSError, subprocess.TimeoutExpired):
                    self.process.kill()
                    self.process.wait(timeout=3)
            for pipe in (self.process.stdin, self.process.stdout, self.process.stderr):
                pipe.close()
            if getattr(self, "stderr_thread", None) is not None and self.stderr_thread.ident is not None:
                self.stderr_thread.join(timeout=.5)
        # Remove ndarray exports before closing mmap.
        for name in ("obs", "candidates", "mask", "rewards", "done", "actors"):
            if hasattr(self, name):
                delattr(self, name)
        if getattr(self, "shared", None) is not None:
            self.shared.close()
            self.shared = None
        if getattr(self, "filename", None):
            Path(self.filename).unlink(missing_ok=True)

    def __enter__(self):
        return self

    def __exit__(self, *_):
        self.close()
