"""Versioned local balance feedback with atomic publication and submission history."""
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


MAX_REQUEST_BYTES = 512 * 1024
MAX_COMMENT_CHARS = 10000
MAX_GENERAL_CHARS = 20000
MAX_STORE_BYTES = 32 * 1024 * 1024
REVIEW_SCHEMA = "shards-balance-proposal-review-v1"
STORE_SCHEMA = "shards-balance-proposal-reviews-v1"
IDENTIFIER = re.compile(r"[A-Za-z0-9][A-Za-z0-9_.-]{0,127}\Z")
DECISIONS = frozenset(("unreviewed", "accept", "change", "reject"))


class ReviewError(Exception):
    def __init__(self, status, message, *, code=None, **payload):
        super().__init__(message)
        self.status = status
        self.payload = {"error": message, **({"code": code} if code else {}), **payload}


def strict_json(data):
    """Reject duplicate object members and non-JSON numbers instead of losing input."""
    def pairs(items):
        result = {}
        for key, value in items:
            if key in result:
                raise ValueError("Duplicate JSON member")
            result[key] = value
        return result

    def invalid_constant(value):
        raise ValueError("Non-finite JSON number")

    return json.loads(data, object_pairs_hook=pairs, parse_constant=invalid_constant)


def _read_json_fd(fd, limit):
    info = os.fstat(fd)
    if not stat.S_ISREG(info.st_mode) or info.st_size > limit:
        raise ValueError("Feedback file is not a regular file or exceeds its size limit")
    with os.fdopen(os.dup(fd), "rb") as stream:
        raw = stream.read(limit + 1)
    if len(raw) > limit:
        raise ValueError("Feedback file exceeds its size limit")
    return strict_json(raw)


class BalanceReviewStore:
    def __init__(self, campaign, manifest):
        self.campaign = Path(campaign)
        self.manifest = Path(manifest)
        self.lock = threading.RLock()

    def _proposal(self):
        try:
            fd = os.open(self.manifest, os.O_RDONLY | os.O_NOFOLLOW | os.O_NONBLOCK)
            try:
                proposal = _read_json_fd(fd, MAX_REQUEST_BYTES)
            finally:
                os.close(fd)
            if not isinstance(proposal, dict) or not isinstance(proposal.get("version"), str) or not IDENTIFIER.fullmatch(proposal["version"]):
                raise ValueError("Proposal manifest needs a valid version")
            rows = proposal.get("proposals")
            if not isinstance(rows, list) or not 1 <= len(rows) <= 200:
                raise ValueError("Proposal manifest needs between 1 and 200 proposals")
            ids = [row.get("id") if isinstance(row, dict) else None for row in rows]
            if any(not isinstance(key, str) or not IDENTIFIER.fullmatch(key) for key in ids) or len(set(ids)) != len(ids):
                raise ValueError("Proposal IDs must be valid and unique")
            return proposal
        except (OSError, ValueError) as error:
            raise ReviewError(503, "Balance proposals unavailable: " + str(error), code="proposal_unavailable") from error

    def _directory(self):
        return os.open(self.campaign, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW)

    def _card_review_scopes(self, proposal):
        """Only gallery groups with multiple cards need separate review scopes."""
        path = self.manifest.with_name("balance_card_previews.json")
        try:
            try:
                fd = os.open(path, os.O_RDONLY | os.O_NOFOLLOW | os.O_NONBLOCK)
            except FileNotFoundError:
                return {}
            try:
                previews = _read_json_fd(fd, MAX_REQUEST_BYTES)
            finally:
                os.close(fd)
            if not isinstance(previews, dict):
                raise ValueError("Card previews must be an object")
            if previews.get("proposal_version") != proposal["version"]:
                return {}
            groups = previews.get("proposals")
            if not isinstance(groups, dict):
                raise ValueError("Card previews need proposal groups")
            scopes = {}
            for row in proposal["proposals"]:
                group = groups.get(row["id"])
                if group is None:
                    continue
                if not isinstance(group, dict) or not isinstance(group.get("cards"), list):
                    raise ValueError("Card preview group needs a card list")
                pairs = group["cards"]
                if len(pairs) < 2:
                    continue
                ids = []
                for pair in pairs:
                    card = (pair.get("after") or pair.get("before")) if isinstance(pair, dict) else None
                    card_id = card.get("id") if isinstance(card, dict) else None
                    if not isinstance(card_id, str) or not IDENTIFIER.fullmatch(card_id) or card_id in ids:
                        raise ValueError("Cards in a preview group need valid, unique IDs")
                    ids.append(card_id)
                scopes[row["id"]] = ids
            return scopes
        except (OSError, ValueError) as error:
            raise ReviewError(503, "Card review scopes unavailable: " + str(error), code="card_previews_unavailable") from error

    def _load(self, directory):
        try:
            fd = os.open("balance-proposal-reviews.json", os.O_RDONLY | os.O_NOFOLLOW | os.O_NONBLOCK, dir_fd=directory)
        except FileNotFoundError:
            return {"schema": STORE_SCHEMA, "versions": {}}
        try:
            data = _read_json_fd(fd, MAX_STORE_BYTES)
        finally:
            os.close(fd)
        if not isinstance(data, dict) or data.get("schema") != STORE_SCHEMA or not isinstance(data.get("versions"), dict):
            raise ValueError("Unrecognized review store; existing feedback was left untouched")
        return data

    @staticmethod
    def _empty(proposal):
        return {"schema": REVIEW_SCHEMA, "proposal_version": proposal["version"], "revision": 0,
                "entries": {row["id"]: {"decision": "unreviewed", "comment": ""} for row in proposal["proposals"]},
                "card_entries": {},
                "general_comment": "", "submitted_at": None, "updated_at": None}

    def _current(self, data, proposal, card_scopes):
        version = data["versions"].get(proposal["version"])
        if version is None:
            return self._empty(proposal)
        if not isinstance(version, dict) or version.get("proposal") != proposal:
            raise ReviewError(409, "The proposal content changed without a new version; existing feedback was preserved",
                              code="proposal_content_conflict")
        review = version.get("review")
        if (not isinstance(review, dict) or review.get("schema") != REVIEW_SCHEMA
                or review.get("proposal_version") != proposal["version"]
                or type(review.get("revision")) is not int or review["revision"] < 1
                or not isinstance(version.get("submissions"), list)):
            raise ValueError("Invalid saved review; existing feedback was left untouched")
        # Validate persisted text/IDs as carefully as incoming drafts.
        self._validated({**review, "submit": False}, proposal, card_scopes=card_scopes, persisted=True)
        result = deepcopy(review)
        # Legacy group feedback and submitted snapshots keep their original form
        # on disk. Normalize the response without guessing which card was meant.
        result.setdefault("card_entries", {})
        return result

    def get(self):
        with self.lock:
            try:
                proposal = self._proposal()
                card_scopes = self._card_review_scopes(proposal)
                directory = self._directory()
                try:
                    review = self._current(self._load(directory), proposal, card_scopes)
                finally:
                    os.close(directory)
                return {"proposal": proposal, "review": review, "card_review_scopes": card_scopes}
            except (OSError, ValueError) as error:
                raise ReviewError(503, "Cannot read saved feedback: " + str(error), code="review_unavailable") from error

    @staticmethod
    def _validated_entry(item):
        if (not isinstance(item, dict) or set(item) != {"decision", "comment"}
                or not isinstance(item["decision"], str) or item["decision"] not in DECISIONS
                or not isinstance(item["comment"], str) or len(item["comment"]) > MAX_COMMENT_CHARS):
            raise ReviewError(400, "Each entry needs a valid decision and comment of at most 10,000 characters", code="invalid_review")
        try:
            item["comment"].encode("utf-8")
        except UnicodeError as error:
            raise ReviewError(400, "Review text must be valid Unicode", code="invalid_review") from error
        return dict(item)

    @staticmethod
    def _validated(payload, proposal, *, card_scopes=None, persisted=False):
        if not isinstance(payload, dict):
            raise ReviewError(400, "Review must be a JSON object", code="invalid_review")
        allowed = {"proposal_version", "revision", "entries", "card_entries", "general_comment", "submit"}
        if persisted:
            allowed |= {"schema", "submitted_at", "updated_at"}
        if set(payload) - allowed:
            raise ReviewError(400, "Unknown review fields", code="invalid_review")
        if (not isinstance(payload.get("proposal_version"), str) or type(payload.get("revision")) is not int
                or payload["revision"] < 0 or type(payload.get("submit")) is not bool):
            raise ReviewError(400, "proposal_version, nonnegative integer revision, and boolean submit are required", code="invalid_review")
        entries = payload.get("entries")
        ids = [row["id"] for row in proposal["proposals"]]
        if not isinstance(entries, dict) or set(entries) - set(ids):
            raise ReviewError(400, "entries must be an object containing only current proposal IDs", code="invalid_review")
        clean = {}
        for key in ids:
            item = entries.get(key, {"decision": "unreviewed", "comment": ""})
            clean[key] = BalanceReviewStore._validated_entry(item)
        clean_cards = None
        if "card_entries" in payload:
            card_entries = payload["card_entries"]
            scopes = card_scopes or {}
            if not isinstance(card_entries, dict) or set(card_entries) - set(scopes):
                raise ReviewError(400, "card_entries must contain only current multi-card proposal IDs", code="invalid_review")
            clean_cards = {}
            for group_id, group in card_entries.items():
                if not isinstance(group, dict) or set(group) - set(scopes[group_id]):
                    raise ReviewError(400, "Each card review must belong to its current proposal group", code="invalid_review")
                clean_cards[group_id] = {card_id: BalanceReviewStore._validated_entry(item) for card_id, item in group.items()}
        general = payload.get("general_comment")
        if not isinstance(general, str) or len(general) > MAX_GENERAL_CHARS:
            raise ReviewError(400, "general_comment must be text of at most 20,000 characters", code="invalid_review")
        try:
            general.encode("utf-8")
        except UnicodeError as error:
            raise ReviewError(400, "Review text must be valid Unicode", code="invalid_review") from error
        return clean, general, clean_cards

    @staticmethod
    def _publish(directory, data):
        raw = (json.dumps(data, ensure_ascii=False, indent=2, allow_nan=False) + "\n").encode("utf-8")
        if len(raw) > MAX_STORE_BYTES:
            raise ReviewError(507, "Feedback history is full; existing reviews are preserved", code="review_store_full")
        name = ".balance-proposal-reviews." + uuid.uuid4().hex + ".tmp"
        fd = os.open(name, os.O_WRONLY | os.O_CREAT | os.O_EXCL | os.O_NOFOLLOW, 0o600, dir_fd=directory)
        try:
            with os.fdopen(fd, "wb") as stream:
                stream.write(raw)
                stream.flush()
                os.fsync(stream.fileno())
            # The old target was opened with NOFOLLOW during _load. Refuse a
            # subsequently substituted symlink as well; replace never follows it.
            try:
                target = os.stat("balance-proposal-reviews.json", dir_fd=directory, follow_symlinks=False)
                if not stat.S_ISREG(target.st_mode):
                    raise ValueError("Review target is not a regular file")
            except FileNotFoundError:
                pass
            os.replace(name, "balance-proposal-reviews.json", src_dir_fd=directory, dst_dir_fd=directory)
            os.fsync(directory)
        finally:
            try:
                os.unlink(name, dir_fd=directory)
            except FileNotFoundError:
                pass

    def save(self, payload):
        with self.lock:
            try:
                proposal = self._proposal()
                # A stale tab gets the current proposal before its old IDs are checked.
                if isinstance(payload, dict) and isinstance(payload.get("proposal_version"), str) and payload["proposal_version"] != proposal["version"]:
                    current = self.get()
                    raise ReviewError(409, "The proposals changed; reload before reviewing this version",
                                      code="proposal_version_conflict", **current)
                card_scopes = self._card_review_scopes(proposal)
                entries, general, card_entries = self._validated(payload, proposal, card_scopes=card_scopes)
                directory = self._directory()
                try:
                    lock = os.open("balance-proposal-reviews.lock", os.O_RDWR | os.O_CREAT | os.O_NOFOLLOW | os.O_NONBLOCK,
                                   0o600, dir_fd=directory)
                    try:
                        if not stat.S_ISREG(os.fstat(lock).st_mode):
                            raise ValueError("Review lock is not a regular file")
                        fcntl.flock(lock, fcntl.LOCK_EX)
                        data = self._load(directory)
                        current = self._current(data, proposal, card_scopes)
                        if payload["revision"] != current["revision"]:
                            raise ReviewError(409, "Feedback changed in another tab; load it before saving",
                                              code="revision_conflict", review=current)
                        now = dt.datetime.now(dt.timezone.utc).isoformat().replace("+00:00", "Z")
                        review = {"schema": REVIEW_SCHEMA, "proposal_version": proposal["version"],
                                  "revision": current["revision"] + 1, "entries": entries, "general_comment": general,
                                  "card_entries": deepcopy(current["card_entries"]) if card_entries is None else card_entries,
                                  "submitted_at": now if payload["submit"] else None, "updated_at": now}
                        version = data["versions"].setdefault(proposal["version"],
                            {"proposal": deepcopy(proposal), "review": None, "submissions": []})
                        version["review"] = review
                        if payload["submit"]:
                            version["submissions"].append(deepcopy(review))
                        self._publish(directory, data)
                        return {"review": deepcopy(review)}
                    finally:
                        os.close(lock)
                finally:
                    os.close(directory)
            except (OSError, ValueError) as error:
                raise ReviewError(503, "Cannot save feedback; existing reviews were preserved: " + str(error),
                                  code="review_unavailable") from error
