"""Local card designs, separate from game definitions and balance feedback."""
from __future__ import annotations

from copy import deepcopy
import datetime as dt
import fcntl
import json
import os
from pathlib import Path
import re
import stat
import threading
import uuid

from balance_review_store import ReviewError, strict_json


SCHEMA = "shards-card-drafts-v1"
MAX_STORE_BYTES = 8 * 1024 * 1024
MAX_CARDS = 100
IDENTIFIER = re.compile(r"[A-Za-z0-9][A-Za-z0-9_.-]{0,127}\Z")
FACTIONS = frozenset(("None", "Order", "Undergrowth", "Wraethe", "Homodeus", "Aion", "Monster"))
CARD_TYPES = frozenset(("Ally", "Mercenary", "Champion", "Relic", "Destiny", "Monster", "Starter", "Hero"))
FIELDS = frozenset(("id", "name", "faction", "type", "cost", "quantity", "defense", "shield",
                    "rules_text", "notes", "art_id", "source_proposal_id"))
TIMESTAMPS = frozenset(("created_at", "updated_at"))


def _invalid(message):
    return ReviewError(400, message, code="invalid_card_draft")


def _text(value, name, maximum, *, required=False):
    if not isinstance(value, str) or len(value) > maximum or (required and not value.strip()):
        raise _invalid(f"{name} must be text {'of at least one character and ' if required else ''}at most {maximum} characters")
    try:
        value.encode("utf-8")
    except UnicodeError as error:
        raise _invalid(f"{name} must be valid Unicode") from error
    return value


def _timestamp(value):
    if not isinstance(value, str) or len(value) > 64:
        raise _invalid("Card timestamps must be valid UTC timestamps")
    try:
        parsed = dt.datetime.fromisoformat(value.replace("Z", "+00:00"))
        if parsed.tzinfo is None or parsed.utcoffset() != dt.timedelta(0):
            raise ValueError("Expected UTC")
    except ValueError as error:
        raise _invalid("Card timestamps must be valid UTC timestamps") from error


def validate_cards(cards, *, persisted=False):
    if not isinstance(cards, list) or len(cards) > MAX_CARDS:
        raise _invalid("cards must be a list of at most 100 card designs")
    result, seen = [], set()
    for card in cards:
        if not isinstance(card, dict) or not FIELDS.issubset(card) or set(card) - FIELDS - TIMESTAMPS:
            raise _invalid("Each card needs all editor fields and no unknown fields")
        key = card["id"]
        if not isinstance(key, str) or not IDENTIFIER.fullmatch(key) or key in seen:
            raise _invalid("Card IDs must be valid and unique")
        seen.add(key)
        _text(card["name"], "name", 160, required=True)
        _text(card["rules_text"], "rules_text", 10000)
        _text(card["notes"], "notes", 10000)
        if not isinstance(card["faction"], str) or card["faction"] not in FACTIONS:
            raise _invalid("Unknown card faction")
        if not isinstance(card["type"], str) or card["type"] not in CARD_TYPES:
            raise _invalid("Unknown card type")
        for name in ("art_id", "source_proposal_id"):
            if not isinstance(card[name], str) or (card[name] and not IDENTIFIER.fullmatch(card[name])):
                raise _invalid(f"{name} must be empty or a valid identifier")
        for name in ("cost", "defense", "shield", "quantity"):
            low, high = (1, 99) if name == "quantity" else (0, 999)
            if type(card[name]) is not int or not low <= card[name] <= high:
                raise _invalid(f"{name} must be an integer from {low} to {high}")
        if persisted and not TIMESTAMPS.issubset(card):
            raise _invalid("Saved card timestamps are missing")
        for name in TIMESTAMPS & set(card):
            _timestamp(card[name])
        result.append(deepcopy(card))
    return result


class CardDraftStore:
    def __init__(self, campaign):
        self.campaign = Path(campaign)
        self.lock = threading.RLock()

    @staticmethod
    def _empty():
        return {"schema": SCHEMA, "revision": 0, "cards": [], "updated_at": None}

    def _directory(self):
        return os.open(self.campaign, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW)

    @staticmethod
    def _load(directory):
        try:
            fd = os.open("card-design-drafts.json", os.O_RDONLY | os.O_NOFOLLOW | os.O_NONBLOCK, dir_fd=directory)
        except FileNotFoundError:
            return CardDraftStore._empty()
        try:
            info = os.fstat(fd)
            if not stat.S_ISREG(info.st_mode) or info.st_size > MAX_STORE_BYTES:
                raise ValueError("Card design store is not a regular file or exceeds its size limit")
            with os.fdopen(os.dup(fd), "rb") as stream:
                raw = stream.read(MAX_STORE_BYTES + 1)
            if len(raw) > MAX_STORE_BYTES:
                raise ValueError("Card design store exceeds its size limit")
            data = strict_json(raw)
            if (not isinstance(data, dict) or set(data) != {"schema", "revision", "cards", "updated_at"}
                    or data["schema"] != SCHEMA or type(data["revision"]) is not int or data["revision"] < 1):
                raise ValueError("Unrecognized card design store")
            try:
                validate_cards(data["cards"], persisted=True)
                _timestamp(data["updated_at"])
            except ReviewError as error:
                raise ValueError(str(error)) from error
            return data
        finally:
            os.close(fd)

    @staticmethod
    def _bytes(data):
        raw = (json.dumps(data, ensure_ascii=False, indent=2, allow_nan=False) + "\n").encode("utf-8")
        if len(raw) > MAX_STORE_BYTES:
            raise ReviewError(507, "Card design storage limit exceeded; existing designs were preserved", code="draft_store_full")
        return raw

    @staticmethod
    def _atomic_file(directory, name, raw):
        temporary = ".card-design-drafts." + uuid.uuid4().hex + ".tmp"
        fd = os.open(temporary, os.O_WRONLY | os.O_CREAT | os.O_EXCL | os.O_NOFOLLOW, 0o600, dir_fd=directory)
        try:
            with os.fdopen(fd, "wb") as stream:
                stream.write(raw)
                stream.flush()
                os.fsync(stream.fileno())
            try:
                target = os.stat(name, dir_fd=directory, follow_symlinks=False)
                if not stat.S_ISREG(target.st_mode):
                    raise ValueError("Card design target is not a regular file")
            except FileNotFoundError:
                pass
            os.replace(temporary, name, src_dir_fd=directory, dst_dir_fd=directory)
            os.fsync(directory)
        finally:
            try:
                os.unlink(temporary, dir_fd=directory)
            except FileNotFoundError:
                pass

    @classmethod
    def _archive(cls, directory, current):
        if current["revision"] == 0:
            return
        try:
            os.mkdir("card-design-draft-history", mode=0o700, dir_fd=directory)
            os.fsync(directory)
        except FileExistsError:
            pass
        history = os.open("card-design-draft-history", os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW, dir_fd=directory)
        try:
            name = f"revision-{current['revision']:08d}.json"
            raw = cls._bytes(current)
            try:
                fd = os.open(name, os.O_RDONLY | os.O_NOFOLLOW | os.O_NONBLOCK, dir_fd=history)
            except FileNotFoundError:
                cls._atomic_file(history, name, raw)
            else:
                try:
                    info = os.fstat(fd)
                    if not stat.S_ISREG(info.st_mode) or info.st_size != len(raw):
                        raise ValueError("An existing card design snapshot is inconsistent")
                    with os.fdopen(os.dup(fd), "rb") as stream:
                        if stream.read(MAX_STORE_BYTES + 1) != raw:
                            raise ValueError("An existing card design snapshot is inconsistent")
                finally:
                    os.close(fd)
        finally:
            os.close(history)

    def get(self):
        with self.lock:
            try:
                directory = self._directory()
                try:
                    return self._load(directory)
                finally:
                    os.close(directory)
            except (OSError, ValueError) as error:
                raise ReviewError(503, "Cannot read saved card designs: " + str(error), code="draft_unavailable") from error

    def save(self, payload):
        if (not isinstance(payload, dict) or set(payload) != {"revision", "cards"}
                or type(payload["revision"]) is not int or payload["revision"] < 0):
            raise _invalid("A nonnegative integer revision and cards list are required")
        cards = validate_cards(payload["cards"])
        with self.lock:
            try:
                directory = self._directory()
                try:
                    lock = os.open("card-design-drafts.lock", os.O_RDWR | os.O_CREAT | os.O_NOFOLLOW | os.O_NONBLOCK,
                                   0o600, dir_fd=directory)
                    try:
                        if not stat.S_ISREG(os.fstat(lock).st_mode):
                            raise ValueError("Card design lock is not a regular file")
                        fcntl.flock(lock, fcntl.LOCK_EX)
                        current = self._load(directory)
                        if payload["revision"] != current["revision"]:
                            raise ReviewError(409, "Card designs changed in another tab; load them before saving",
                                              code="revision_conflict", draft=current)
                        now = dt.datetime.now(dt.timezone.utc).isoformat().replace("+00:00", "Z")
                        old = {card["id"]: card for card in current["cards"]}
                        for card in cards:
                            previous = old.get(card["id"])
                            unchanged = previous is not None and all(card[key] == previous[key] for key in FIELDS)
                            card["created_at"] = previous["created_at"] if previous else now
                            card["updated_at"] = previous["updated_at"] if unchanged else now
                        result = {"schema": SCHEMA, "revision": current["revision"] + 1, "cards": cards, "updated_at": now}
                        raw = self._bytes(result)
                        self._archive(directory, current)
                        self._atomic_file(directory, "card-design-drafts.json", raw)
                        return deepcopy(result)
                    finally:
                        os.close(lock)
                finally:
                    os.close(directory)
            except (OSError, ValueError) as error:
                raise ReviewError(503, "Cannot save card designs; existing designs were preserved: " + str(error),
                                  code="draft_unavailable") from error
