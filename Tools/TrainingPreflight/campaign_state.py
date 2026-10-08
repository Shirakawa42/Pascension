"""Shared training budget, atomic checkpoints and explicit restart accounting.

The budget holder must be the trainer process. An external supervisor must stop
it at Session.hard_deadline_monotonic (also exposed as a wall deadline); Python
cannot interrupt an indefinitely blocked CUDA call reliably. Heartbeats and
checkpoints fail closed: persistence errors poison this object and must stop the
trainer. No GPU work or outcome learning is started by this module.

An unclean same-boot session is charged through recovery's monotonic time,
including uncertain downtime, up to its grant. A changed boot or inconsistent
monotonic clock consumes the entire outstanding grant. Time is never refunded.
Checkpoint state is caller-defined, but must describe an episode-free boundary;
engine iterators/unfinished trajectories are deliberately not serialized.
"""

from __future__ import annotations

from collections.abc import Mapping
import contextlib
import fcntl
import hashlib
import io
import json
import math
import os
from pathlib import Path
import random
import struct
import tempfile
import time
import uuid


MAX_CAMPAIGN_SECONDS = 12 * 60 * 60
COLLECTION_WINDOW_GAMES = 4096
_COLLECTION_COUNTS = ("attempted_games", "completed_games", "censored_games", "learning_rows", "censored_rows")
LEDGER_SCHEMA = "shards-training-budget-v1"
CHECKPOINT_SCHEMA = "shards-training-checkpoint-v1"
_HEADER = struct.Struct("<8sQ32s")
_MAGIC = b"SOICP001"


class CampaignStateError(RuntimeError):
    pass


class CampaignLocked(CampaignStateError):
    pass


class BudgetExceeded(CampaignStateError):
    pass


class PersistenceError(CampaignStateError):
    pass


class CheckpointError(CampaignStateError):
    pass


class _Clock:
    monotonic = staticmethod(time.monotonic)
    time = staticmethod(time.time)

    @staticmethod
    def boot_id():
        return Path("/proc/sys/kernel/random/boot_id").read_text().strip()


def _number(value, name, *, minimum=0.0):
    if isinstance(value, bool) or not isinstance(value, (int, float)) or not math.isfinite(value) or value < minimum:
        raise CampaignStateError(f"Invalid {name}: {value!r}")
    return float(value)


def _count(value, name):
    if isinstance(value, bool) or not isinstance(value, int) or value < 0:
        raise CampaignStateError(f"Invalid {name}: expected a nonnegative integer")
    return value


def _canonical(value):
    return json.dumps(value, sort_keys=True, separators=(",", ":"), allow_nan=False).encode()


def _collection_counts(data):
    counts = {key: _count(data.get(key), key) for key in _COLLECTION_COUNTS}
    if counts["attempted_games"] != counts["completed_games"] + counts["censored_games"]:
        raise CampaignStateError("Finalized collection attempts must equal completed plus censored games")
    if not counts["completed_games"] and counts["learning_rows"]:
        raise CampaignStateError("Retained learning rows require a completed game")
    if not counts["censored_games"] and counts["censored_rows"]:
        raise CampaignStateError("Censored rows require a censored game")
    return counts


def _collection_details(details):
    if details is None:
        return None
    if not isinstance(details, dict):
        raise CampaignStateError("Collection details must be a small JSON object")
    try:
        encoded = _canonical(details)
    except (TypeError, ValueError) as error:
        raise CampaignStateError("Collection details must contain finite JSON values") from error
    if len(encoded) > 4096:
        raise CampaignStateError("Collection details exceed 4096 bytes; store large traces separately")
    return json.loads(encoded)


def _new_collection_accounting(wall):
    return {"schema": "shards-collection-accounting-v1", "started_wall": wall,
            "window_games": COLLECTION_WINDOW_GAMES, "collections": 0,
            **{key: 0 for key in _COLLECTION_COUNTS}, "recent_batches": []}


def _atomic_write(path, data):
    """Commit bytes or byte chunks without joining a large checkpoint buffer."""
    path = Path(path)
    path.parent.mkdir(parents=True, exist_ok=True)
    fd, temporary = tempfile.mkstemp(prefix=f".{path.name}.", suffix=".tmp", dir=path.parent)
    try:
        with os.fdopen(fd, "wb") as destination:
            chunks = data if isinstance(data, tuple) else (data,)
            for chunk in chunks:
                destination.write(chunk)
            destination.flush()
            os.fsync(destination.fileno())
        os.replace(temporary, path)
        directory = os.open(path.parent, os.O_RDONLY | os.O_DIRECTORY)
        try:
            os.fsync(directory)
        finally:
            os.close(directory)
    finally:
        with contextlib.suppress(FileNotFoundError):
            os.unlink(temporary)


class CampaignBudget:
    """A single persistent allocation shared by all pilots and main sessions.

    Use as a context manager. The separate lock file is never replaced, avoiding
    the inode-lock race that would arise from locking the atomic JSON itself.
    ``clock`` is an injectable test clock; production uses Linux boot/monotonic
    time. A smaller limit is allowed for tests or a deliberately shorter campaign;
    an existing ledger's limit can never be changed by opening it again.
    """

    def __init__(self, path, limit_seconds=MAX_CAMPAIGN_SECONDS, *, clock=None):
        self.path = Path(path)
        self.limit_seconds = _number(limit_seconds, "campaign limit", minimum=0.001)
        if self.limit_seconds > MAX_CAMPAIGN_SECONDS:
            raise CampaignStateError("The shared training allocation cannot exceed 43200 seconds")
        self.clock = clock or _Clock()
        self._fd = None
        self._data = None
        self._session = None
        self._poisoned = False
        self.last_recovery = None

    def __enter__(self):
        if self._fd is not None:
            raise CampaignStateError("Campaign budget is already open")
        self.path.parent.mkdir(parents=True, exist_ok=True)
        fd = os.open(str(self.path) + ".lock", os.O_CREAT | os.O_RDWR, 0o600)
        os.set_inheritable(fd, False)
        try:
            try:
                fcntl.flock(fd, fcntl.LOCK_EX | fcntl.LOCK_NB)
            except BlockingIOError as error:
                raise CampaignLocked(f"Another process owns the campaign: {self.path}") from error
            self._fd = fd
            if self.path.exists():
                try:
                    self._data = json.loads(self.path.read_text())
                except (OSError, ValueError) as error:
                    raise CampaignStateError("Budget ledger is unreadable; refusing to create a new allocation") from error
                needs_collection_migration = "collection_accounting" not in self._data if isinstance(self._data, dict) else False
                self._validate()
                if self._data["active"] is not None:
                    self._recover_unclean()
                elif needs_collection_migration:
                    self._persist()
            else:
                self._data = {
                    "schema": LEDGER_SCHEMA, "campaign_id": uuid.uuid4().hex,
                    "limit_seconds": self.limit_seconds, "charged_seconds": 0.0,
                    "created_wall": self.clock.time(), "revision": 0,
                    "active": None, "sessions": [], "discarded_episodes": 0,
                    "discarded_decisions": 0, "discard_events": [],
                    "collection_accounting": _new_collection_accounting(self.clock.time()),
                }
                self._persist()
            return self
        except BaseException:
            self._fd = None
            os.close(fd)
            raise

    def __exit__(self, exc_type, exc, traceback):
        try:
            if self._session is not None and not self._session.closed and not self._poisoned:
                self._session.close(outcome="exception" if exc_type else "closed")
        finally:
            if self._fd is not None:
                os.close(self._fd)
                self._fd = None

    def _require_open(self):
        if self._fd is None or self._data is None:
            raise CampaignStateError("Open CampaignBudget with a context manager first")
        if self._poisoned:
            raise PersistenceError("Campaign state persistence failed; this trainer must stop")

    def _validate(self):
        data = self._data
        if not isinstance(data, dict) or data.get("schema") != LEDGER_SCHEMA or not isinstance(data.get("campaign_id"), str):
            raise CampaignStateError("Unknown or corrupt campaign ledger")
        if _number(data.get("limit_seconds"), "stored campaign limit") != self.limit_seconds:
            raise CampaignStateError("Cannot change an existing campaign's time allocation")
        charged = _number(data.get("charged_seconds"), "charged training time")
        _count(data.get("revision"), "ledger revision")
        _count(data.get("discarded_episodes"), "discarded episodes")
        _count(data.get("discarded_decisions"), "discarded decisions")
        if not isinstance(data.get("sessions"), list) or not isinstance(data.get("discard_events"), list) or "active" not in data:
            raise CampaignStateError("Incomplete campaign ledger")
        completed_charge = 0.0
        for item in data["sessions"]:
            if not isinstance(item, dict):
                raise CampaignStateError("Invalid completed session")
            completed_charge += _number(item.get("charged_seconds"), "completed session time")
        active = data["active"]
        if active is not None:
            if not isinstance(active, dict) or not isinstance(active.get("session_id"), str):
                raise CampaignStateError("Invalid active session")
            for key in ("started_wall", "started_monotonic", "last_heartbeat_wall", "last_heartbeat_monotonic",
                        "last_elapsed_seconds", "charged_at_start", "grant_seconds", "hard_deadline_monotonic", "hard_deadline_wall"):
                _number(active.get(key), key)
            if not isinstance(active.get("boot_id"), str) or active["grant_seconds"] <= 0:
                raise CampaignStateError("Invalid session boot/grant")
            if abs(active["charged_at_start"] - completed_charge) > 1e-6:
                raise CampaignStateError("Active session would erase previously charged time")
            completed_charge += active["last_elapsed_seconds"]
        if abs(charged - completed_charge) > 1e-5:
            raise CampaignStateError("Budget total does not reconcile with its session history")
        if "collection_accounting" not in data:
            # Old ledgers retain all time/discard history. Earlier unrecorded
            # game/censor counts cannot be inferred and are not invented.
            data["collection_accounting"] = _new_collection_accounting(self.clock.time())
        accounting = data["collection_accounting"]
        if not isinstance(accounting, dict) or accounting.get("schema") != "shards-collection-accounting-v1":
            raise CampaignStateError("Invalid persistent collection accounting")
        if accounting.get("window_games") != COLLECTION_WINDOW_GAMES:
            raise CampaignStateError("Cannot change the persistent censor window")
        _number(accounting.get("started_wall"), "collection accounting start")
        lifetime = _collection_counts(accounting)
        collections = _count(accounting.get("collections"), "collection count")
        recent = accounting.get("recent_batches")
        if not isinstance(recent, list) or len(recent) > COLLECTION_WINDOW_GAMES or len(recent) > collections:
            raise CampaignStateError("Invalid recent collection window")
        sums = {key: 0 for key in _COLLECTION_COUNTS}
        for offset, batch in enumerate(recent):
            if not isinstance(batch, dict) or batch.get("collection") != collections - len(recent) + offset + 1:
                raise CampaignStateError("Collection window is not a contiguous trailing history")
            values = _collection_counts(batch)
            if not values["attempted_games"]:
                raise CampaignStateError("Empty finalized collection in persistent window")
            _number(batch.get("wall"), "collection publication time")
            _collection_details(batch.get("details"))
            for key in sums:
                sums[key] += values[key]
        if any(sums[key] > lifetime[key] for key in sums):
            raise CampaignStateError("Collection window exceeds lifetime counters")
        if collections > lifetime["attempted_games"] or bool(collections) != bool(recent):
            raise CampaignStateError("Collection counts do not reconcile")
        if sums["attempted_games"] < min(COLLECTION_WINDOW_GAMES, lifetime["attempted_games"]):
            raise CampaignStateError("Collection window omits recent attempts")
        if recent and sums["attempted_games"] - recent[0]["attempted_games"] >= COLLECTION_WINDOW_GAMES:
            raise CampaignStateError("Collection window contains an unnecessary oldest batch")
        if lifetime["attempted_games"] <= COLLECTION_WINDOW_GAMES and sums != lifetime:
            raise CampaignStateError("Initial collection history does not reconcile")

    def _persist(self):
        self._require_open()
        self._data["revision"] += 1
        try:
            _atomic_write(self.path, _canonical(self._data) + b"\n")
        except Exception as error:
            self._poisoned = True
            raise PersistenceError("Could not durably write campaign state; stop the trainer") from error

    def _recover_unclean(self):
        active = self._data["active"]
        now = self.clock.monotonic()
        same_boot = self.clock.boot_id() == active["boot_id"]
        consistent = active["started_monotonic"] <= active["last_heartbeat_monotonic"] <= now
        if same_boot and consistent:
            elapsed = max(active["last_elapsed_seconds"], min(active["grant_seconds"], now - active["started_monotonic"]))
            rule = "same-boot monotonic time through recovery, including uncertain downtime, capped at grant"
        else:
            elapsed = max(active["last_elapsed_seconds"], active["grant_seconds"])
            rule = "full outstanding grant because boot/monotonic continuity is unavailable"
        self._data["charged_seconds"] = active["charged_at_start"] + elapsed
        progress = active.get("progress", {})
        episodes = _count(progress.get("unresolved_episodes", 0), "recovered unresolved episodes")
        decisions = _count(progress.get("unresolved_decisions", 0), "recovered unresolved decisions")
        self._record_discard(episodes, decisions, "unclean session: last reported unresolved collection", unknown_tail=True)
        recovery = {**active, "outcome": "unclean-recovered", "ended_wall": self.clock.time(),
                    "charged_seconds": elapsed, "recovery_rule": rule,
                    "discarded_episodes_last_reported": episodes, "discarded_decisions_last_reported": decisions,
                    "unreported_tail_may_also_be_discarded": True}
        self._data["sessions"].append(recovery)
        self._data["active"] = None
        self.last_recovery = json.loads(json.dumps(recovery))
        self._persist()

    @property
    def campaign_id(self):
        self._require_open()
        return self._data["campaign_id"]

    @property
    def charged_seconds(self):
        self._require_open()
        if self._session is not None and not self._session.closed:
            return self._data["active"]["charged_at_start"] + self._session.elapsed_seconds
        return self._data["charged_seconds"]

    @property
    def remaining_seconds(self):
        return max(0.0, self.limit_seconds - self.charged_seconds)

    def start_session(self, label, requested_seconds=None, stop_buffer_seconds=30,
                      *, absolute_deadline_monotonic=None):
        self._require_open()
        if self._data["active"] is not None:
            raise CampaignStateError("A training session is already active")
        if not isinstance(label, str) or not label.strip():
            raise CampaignStateError("Session label must describe its pilot/main work")
        remaining = self.remaining_seconds
        requested = remaining if requested_seconds is None else _number(requested_seconds, "requested session time", minimum=0.001)
        if remaining <= 0:
            raise BudgetExceeded("The shared training allocation is exhausted")
        grant = min(requested, remaining)
        buffer = _number(stop_buffer_seconds, "checkpoint stop buffer")
        now, wall = self.clock.monotonic(), self.clock.time()
        if absolute_deadline_monotonic is not None:
            deadline = _number(absolute_deadline_monotonic, "absolute monotonic deadline", minimum=0.001)
            grant = min(grant, deadline - now)
            if grant <= 0:
                raise BudgetExceeded("The original absolute campaign deadline is exhausted")
        if buffer >= grant:
            raise CampaignStateError("Checkpoint buffer must be smaller than this session's grant")
        active = {"session_id": uuid.uuid4().hex, "label": label, "pid": os.getpid(), "boot_id": self.clock.boot_id(),
                  "started_wall": wall, "started_monotonic": now, "last_heartbeat_wall": wall,
                  "last_heartbeat_monotonic": now, "last_elapsed_seconds": 0.0,
                  "charged_at_start": self._data["charged_seconds"], "grant_seconds": grant,
                  "stop_buffer_seconds": buffer, "hard_deadline_monotonic": now + grant,
                  "hard_deadline_wall": wall + grant, "progress": {}}
        self._data["active"] = active
        self._persist()
        self._session = Session(self, active["session_id"])
        return self._session

    def _record_discard(self, episodes, decisions, reason, *, unknown_tail=False):
        self._data["discarded_episodes"] += _count(episodes, "discarded episodes")
        self._data["discarded_decisions"] += _count(decisions, "discarded decisions")
        self._data["discard_events"].append({"wall": self.clock.time(), "episodes": episodes,
            "decisions": decisions, "reason": str(reason), "unknown_unreported_tail": bool(unknown_tail)})

    def note_discarded(self, *, episodes=0, decisions=0, reason):
        self._require_open()
        self._record_discard(episodes, decisions, reason)
        self._persist()

    def collection_summary(self):
        """Lifetime totals plus a conservative window of at least 4096 attempts.

        Counters start when this API is introduced to a ledger. They are
        authoritative here, never restored from a model checkpoint.
        """
        self._require_open()
        accounting = self._data["collection_accounting"]
        recent = accounting["recent_batches"]
        return {key: accounting[key] for key in (*_COLLECTION_COUNTS, "collections", "started_wall", "window_games")} | {
            "recent_attempted": sum(row["attempted_games"] for row in recent),
            "recent_completed": sum(row["completed_games"] for row in recent),
            "recent_censored": sum(row["censored_games"] for row in recent),
            "recent_learning_rows": sum(row["learning_rows"] for row in recent),
            "recent_censored_rows": sum(row["censored_rows"] for row in recent),
            "recent_batch_count": len(recent),
            "window_scope": "whole trailing batches, including the oldest batch overlapping the last4096 finalized attempts"}

    def record_collection(self, attempted_games, completed_games, censored_games,
                          learning_rows, censored_rows, details=None):
        """Durably count a finalized collection before any optimizer update.

        learning_rows are retained terminal-credit rows; censored_rows are
        separately excluded rows. Attempts must equal completed+censored.
        Deadline/crash-unresolved episodes belong to discard accounting instead.
        This does not advance/reset the training budget or enforce a censor
        threshold: the caller applies its pinned guard to the returned totals.
        A failed publication poisons the budget and requires the trainer to stop.
        """
        self._require_open()
        counts = _collection_counts({"attempted_games": attempted_games, "completed_games": completed_games,
            "censored_games": censored_games, "learning_rows": learning_rows, "censored_rows": censored_rows})
        if not attempted_games:
            raise CampaignStateError("A finalized collection must contain at least one attempted game")
        details = _collection_details(details)
        accounting = self._data["collection_accounting"]
        accounting["collections"] += 1
        for key, value in counts.items():
            accounting[key] += value
        recent = accounting["recent_batches"]
        recent.append({"collection": accounting["collections"], "wall": self.clock.time(),
                       **counts, "details": details})
        attempts = sum(row["attempted_games"] for row in recent)
        while len(recent) > 1 and attempts - recent[0]["attempted_games"] >= COLLECTION_WINDOW_GAMES:
            attempts -= recent.pop(0)["attempted_games"]
        self._persist()
        return self.collection_summary()

    def snapshot(self):
        self._require_open()
        if self._session is not None and not self._session.closed:
            self._session.heartbeat()
        return {"campaign_id": self.campaign_id, "limit_seconds": self.limit_seconds,
                "charged_seconds": self._data["charged_seconds"], "revision": self._data["revision"],
                "discarded_episodes": self._data["discarded_episodes"],
                "discarded_decisions": self._data["discarded_decisions"],
                "collection_accounting": self.collection_summary()}


class Session:
    def __init__(self, budget, session_id):
        self.budget, self.session_id = budget, session_id
        self.closed = False

    def __enter__(self):
        return self

    def __exit__(self, exc_type, exc, traceback):
        self.close(outcome="exception" if exc_type else "closed")

    def _active(self):
        self.budget._require_open()
        active = self.budget._data["active"]
        if self.closed or active is None or active["session_id"] != self.session_id:
            raise CampaignStateError("This training session is no longer active")
        return active

    @property
    def elapsed_seconds(self):
        active = self._active()
        elapsed = self.budget.clock.monotonic() - active["started_monotonic"]
        if elapsed < active["last_elapsed_seconds"] - 1e-6:
            self.budget._poisoned = True
            raise PersistenceError("Monotonic clock moved backwards; stop training")
        return max(elapsed, active["last_elapsed_seconds"])

    @property
    def hard_deadline_monotonic(self):
        return self._active()["hard_deadline_monotonic"]

    @property
    def hard_deadline_wall(self):
        return self._active()["hard_deadline_wall"]

    @property
    def remaining_seconds(self):
        return max(0.0, self._active()["grant_seconds"] - self.elapsed_seconds)

    @property
    def should_stop(self):
        return self.remaining_seconds <= self._active()["stop_buffer_seconds"]

    def heartbeat(self, progress=None):
        active = self._active()
        elapsed = self.elapsed_seconds
        if progress is not None:
            _canonical(progress)
            if not isinstance(progress, dict):
                raise CampaignStateError("Heartbeat progress must be a JSON object")
            for key in ("unresolved_episodes", "unresolved_decisions"):
                if key in progress:
                    _count(progress[key], key)
            active["progress"] = json.loads(json.dumps(progress))
        active["last_elapsed_seconds"] = elapsed
        active["last_heartbeat_monotonic"] = self.budget.clock.monotonic()
        active["last_heartbeat_wall"] = self.budget.clock.time()
        self.budget._data["charged_seconds"] = active["charged_at_start"] + elapsed
        self.budget._persist()
        if elapsed > active["grant_seconds"]:
            raise BudgetExceeded("Session hard deadline was exceeded; elapsed overrun was recorded")
        return self.remaining_seconds

    def close(self, outcome="closed"):
        if self.closed:
            return
        active = self._active()
        elapsed = self.elapsed_seconds
        self.budget._data["charged_seconds"] = active["charged_at_start"] + elapsed
        if outcome == "exception":
            progress = active.get("progress", {})
            self.budget._record_discard(progress.get("unresolved_episodes", 0),
                progress.get("unresolved_decisions", 0),
                "exception session: last reported uncheckpointed collection", unknown_tail=True)
        self.budget._data["sessions"].append({**active, "outcome": str(outcome),
            "ended_wall": self.budget.clock.time(), "charged_seconds": elapsed,
            "overrun_seconds": max(0.0, elapsed - active["grant_seconds"])})
        self.budget._data["active"] = None
        self.budget._persist()
        self.closed = True
        if elapsed > active["grant_seconds"]:
            raise BudgetExceeded("Session exceeded its hard deadline; no additional budget was granted")


def capture_rng(*, include_cuda=True):
    """Capture global RNGs without initializing CUDA on a CPU-only process.

    Caller-owned NumPy/Torch Generator objects belong in caller checkpoint state.
    """
    import numpy as np
    import torch
    numpy_state = np.random.get_state()
    return {"python": random.getstate(),
            "numpy": {"algorithm": numpy_state[0], "keys": numpy_state[1].tolist(),
                      "position": int(numpy_state[2]), "has_gauss": int(numpy_state[3]),
                      "cached_gaussian": float(numpy_state[4])},
            "torch_cpu": torch.get_rng_state().clone(),
            "torch_cuda": [value.cpu().clone() for value in torch.cuda.get_rng_state_all()]
                          if include_cuda and torch.cuda.is_initialized() else []}


def restore_rng(checkpoint_or_rng, *, include_cuda=True):
    import numpy as np
    import torch
    rng = checkpoint_or_rng.get("rng", checkpoint_or_rng)
    try:
        python_state = rng["python"]
        random.Random().setstate(python_state)
        state = rng["numpy"]
        numpy_state = (state["algorithm"], np.asarray(state["keys"], dtype=np.uint32),
                       state["position"], state["has_gauss"], state["cached_gaussian"])
        np.random.RandomState().set_state(numpy_state)
        torch.Generator(device="cpu").set_state(rng["torch_cpu"])
        cuda_states = rng["torch_cuda"]
        if not isinstance(cuda_states, list):
            raise ValueError("CUDA RNG list required")
        if include_cuda and cuda_states and len(cuda_states) != torch.cuda.device_count():
            raise ValueError("CUDA device count differs from saved RNG state")
    except Exception as error:
        raise CheckpointError("Invalid or incompatible saved RNG state") from error
    random.setstate(python_state)
    np.random.set_state(numpy_state)
    torch.set_rng_state(rng["torch_cpu"])
    if include_cuda and cuda_states:
        torch.cuda.set_rng_state_all(cuda_states)
    return {"python": True, "numpy_global": True, "torch_cpu": True,
            "torch_cuda_devices_restored": len(cuda_states) if include_cuda else 0}


def _snapshot(value, path="state"):
    """Validate serializable finite state and own every tensor on CPU."""
    import torch
    if isinstance(value, torch.Tensor):
        result = value.detach().to(device="cpu", copy=True)
        if (result.is_floating_point() or result.is_complex()) and not bool(torch.isfinite(result).all()):
            raise CheckpointError(f"Nonfinite checkpoint tensor: {path}")
        return result
    if value is None or isinstance(value, (str, bool, int, bytes)):
        return value
    if isinstance(value, float):
        if not math.isfinite(value):
            raise CheckpointError(f"Nonfinite checkpoint scalar: {path}")
        return value
    if isinstance(value, Mapping):
        if any(not isinstance(key, (str, int)) or isinstance(key, bool) for key in value):
            raise CheckpointError(f"Checkpoint mapping needs string/integer keys: {path}")
        return {key: _snapshot(item, f"{path}.{key}") for key, item in value.items()}
    if isinstance(value, (list, tuple)):
        result = [_snapshot(item, f"{path}[{index}]") for index, item in enumerate(value)]
        return tuple(result) if isinstance(value, tuple) else result
    raise CheckpointError(f"Unsupported checkpoint object {type(value).__name__}: {path}")


def save_checkpoint_atomic(path, state, *, identity, budget, boundary="episode_free", include_cuda_rng=True):
    """Save finite owned CPU state + RNG in one checksummed atomic file.

    All model/optimizer mutation must be paused by the caller during this call.
    Save/checksum/flush time remains inside an active charged session. This file
    wraps a normal torch.save payload with a length and SHA-256 header; use this
    module's loader rather than torch.load(path) directly. Any failure poisons
    the open campaign object so a failed checkpoint cannot be ignored and the
    training loop silently continue.
    """
    import torch
    budget._require_open()
    try:
        if boundary != "episode_free":
            raise CheckpointError("Checkpoint only at an episode-free update boundary; discard/account partial collections first")
        if not isinstance(state, Mapping) or not isinstance(identity, Mapping) or not identity:
            raise CheckpointError("Checkpoint state and nonempty identity mappings are required")
        identity_bytes = _canonical(dict(identity))
        charge = budget.snapshot()
        rng = capture_rng(include_cuda=include_cuda_rng)
        payload = {"schema": CHECKPOINT_SCHEMA, "created_wall": budget.clock.time(),
                   "identity": json.loads(identity_bytes), "identity_sha256": hashlib.sha256(identity_bytes).hexdigest(),
                   "budget": charge, "boundary": boundary,
                   "recovery": {"engine_iterators_serialized": False,
                                "resume_policy": "restart engine games; discard and account any uncommitted collection"},
                   "state": _snapshot(state), "rng": _snapshot(rng, "rng")}
        serialized = io.BytesIO()
        torch.save(payload, serialized)
        content = serialized.getbuffer()
        content_bytes = len(content)
        try:
            digest = hashlib.sha256(content).digest()
            _atomic_write(path, (_HEADER.pack(_MAGIC, content_bytes, digest), content))
        finally:
            content.release()
        # Charge serialization/fsync too. The checkpoint may trail the ledger;
        # loading it must never refund this or any later discarded work.
        after = budget.snapshot()
        return {"path": str(Path(path)), "sha256": digest.hex(), "bytes": content_bytes + _HEADER.size,
                "charged_seconds_at_snapshot": charge["charged_seconds"],
                "charged_seconds_after_save": after["charged_seconds"]}
    except Exception as error:
        budget._poisoned = True
        if isinstance(error, CampaignStateError):
            raise
        raise CheckpointError("Checkpoint persistence failed; stop the trainer") from error


def load_checkpoint(path, *, expected_identity, budget=None, max_bytes=4 * 2**30):
    """Validate checksum, identity and all finite state before exposing a resume.

    With a budget, the campaign ID must match and checkpoint time cannot exceed
    the authoritative ledger. Without a budget this is a read-only frozen-policy
    load, never authorization to start a new training allocation.
    """
    import torch
    try:
        path = Path(path)
        size = path.stat().st_size
        if size < _HEADER.size or size > max_bytes:
            raise CheckpointError("Checkpoint size is outside the allowed bounds")
        with path.open("rb") as source:
            magic, length, digest = _HEADER.unpack(source.read(_HEADER.size))
            if magic != _MAGIC or length != size - _HEADER.size:
                raise CheckpointError("Checkpoint header/length is corrupt")
            content = source.read()
        if hashlib.sha256(content).digest() != digest:
            raise CheckpointError("Checkpoint checksum mismatch")
        payload = torch.load(io.BytesIO(content), map_location="cpu", weights_only=True)
        if not isinstance(payload, dict) or payload.get("schema") != CHECKPOINT_SCHEMA or payload.get("boundary") != "episode_free":
            raise CheckpointError("Unsupported checkpoint schema/boundary")
        identity_bytes = _canonical(dict(expected_identity))
        if payload.get("identity") != json.loads(identity_bytes) or payload.get("identity_sha256") != hashlib.sha256(identity_bytes).hexdigest():
            raise CheckpointError("Checkpoint identity does not match pinned configuration/rules/catalog/schema/source")
        if not isinstance(payload.get("state"), dict) or not isinstance(payload.get("rng"), dict):
            raise CheckpointError("Checkpoint is missing state/RNG data")
        # Reuse finite/type validation. CPU copies also prevent returned state
        # from accidentally sharing a serializer-managed backing allocation.
        payload["state"] = _snapshot(payload["state"])
        payload["rng"] = _snapshot(payload["rng"], "rng")
        saved_budget = payload.get("budget", {})
        saved_charge = _number(saved_budget.get("charged_seconds"), "checkpoint charged seconds")
        if budget is not None:
            current = budget.snapshot()
            if saved_budget.get("campaign_id") != current["campaign_id"] or saved_budget.get("limit_seconds") != current["limit_seconds"]:
                raise CheckpointError("Checkpoint belongs to a different campaign allocation")
            if saved_charge > current["charged_seconds"] + 1e-6:
                raise CheckpointError("Checkpoint time is ahead of the ledger; refusing a budget rollback")
        payload["file_sha256"] = digest.hex()
        return payload
    except CampaignStateError:
        raise
    except Exception as error:
        raise CheckpointError("Checkpoint could not be safely restored") from error
