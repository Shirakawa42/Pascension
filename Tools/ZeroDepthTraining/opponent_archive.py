"""Bounded CPU opponent snapshots; no game, search, or per-decision work.

Historical retention samples uniformly from snapshots which have left the three
recent slots. Prioritized archives reserve half the historical slots for
observed difficult challengers and retain random history in the other half.
The fixed anchor is separate. Python's checkpointed global RNG
owns selection and reservoir replacement, so no extra RNG state is needed.
"""
from __future__ import annotations

import copy
import math
import random

import torch


SCHEMA = "shards-zero-depth-opponent-archive-v1"
_MISSING = object()


def _integer(value, name, minimum=0):
    if type(value) is not int or value < minimum:
        raise ValueError(f"Invalid opponent archive {name}")
    return value


class OpponentArchive:
    def __init__(self, strategy, *, limit, every, generation, current, archive=None, metadata=_MISSING):
        if strategy not in ("recent", "historical", "prioritized"):
            raise ValueError("Unknown opponent archive strategy")
        self.strategy = strategy
        self.limit = _integer(limit, "limit", 1)
        self.every = _integer(every, "interval", 1)
        _integer(generation, "generation")
        if strategy in ("historical", "prioritized") and limit < 5:
            raise ValueError("Historical opponent archive requires at least five slots")
        self.origin_generation = generation
        self.last_update_generation = generation // every * every
        self.history_seen = self.initial_history_count = self.initial_recent_count = 0
        self._signature = self._weight_signature(current)
        self.anchor = self._record("anchor", generation, current)
        self.recent, self.historical = [], []
        self.payoffs = {}
        if metadata is not _MISSING:
            self._restore(generation, archive, metadata)
        elif archive is not None:
            expected = 1 + min(limit - 1, generation // every)
            if type(archive) is not list or len(archive) != expected:
                raise ValueError("Legacy opponent archive size does not match its generation/configuration")
            for state in archive:
                self._check_weights(state)
            latest = self.last_update_generation
            old = [self._record("recent", latest - (len(archive) - 2 - i) * every, state)
                   for i, state in enumerate(archive[1:])]
            if strategy == "recent":
                self.anchor = self._record("anchor", 0, archive[0])
                self.origin_generation = 0
                self.recent = old
            else:
                self.recent = old[-3:]
                self.historical = old[:-3]
                for record in self.historical:
                    record["pool"] = "historical"
                self.history_seen = self.initial_history_count = len(self.historical)
                self.initial_recent_count = len(self.recent)
        elif strategy == "recent" and generation:
            raise ValueError("A resumed recent archive requires its saved snapshots")

    @staticmethod
    def _weight_signature(state):
        if type(state) is not dict or not state or any(type(key) is not str for key in state):
            raise ValueError("Opponent weights must be a nonempty CPU policy state dictionary")
        if any(not isinstance(value, torch.Tensor) or value.device.type != "cpu" for value in state.values()):
            raise ValueError("Opponent archive owns CPU policy tensors only")
        return {key: (tuple(value.shape), value.dtype) for key, value in state.items()}

    def _check_weights(self, state):
        if self._weight_signature(state) != self._signature:
            raise ValueError("Opponent archive policy tensor names/shapes/dtypes differ")

    def _restore(self, generation, archive, metadata):
        fields = {"schema", "strategy", "limit", "every", "generation", "origin_generation",
                  "last_update_generation", "history_seen", "initial_history_count", "initial_recent_count", "slots"}
        if self.strategy == "prioritized": fields.add("payoffs")
        if type(metadata) is not dict or set(metadata) != fields:
            raise ValueError("Invalid opponent archive metadata fields")
        expected = (SCHEMA, self.strategy, self.limit, self.every, generation)
        actual = tuple(metadata[key] for key in ("schema", "strategy", "limit", "every", "generation"))
        if actual != expected:
            raise ValueError("Opponent archive metadata identity/configuration/generation differs")
        for name in ("limit", "every", "generation", "origin_generation", "last_update_generation",
                     "history_seen", "initial_history_count", "initial_recent_count"):
            _integer(metadata[name], name)
        slots = metadata["slots"]
        if type(archive) is not list or type(slots) is not list or len(archive) != len(slots) or not 1 <= len(slots) <= self.limit:
            raise ValueError("Opponent archive metadata/weight slot counts differ")
        for state in archive:
            self._check_weights(state)
        for name in ("origin_generation", "last_update_generation", "history_seen",
                     "initial_history_count", "initial_recent_count"):
            setattr(self, name, metadata[name])
        if self.origin_generation > generation or self.last_update_generation != generation // self.every * self.every:
            raise ValueError("Opponent archive generation counters are inconsistent")
        records = []
        ids = set()
        for slot, state in zip(slots, archive):
            if type(slot) is not dict or set(slot) != {"pool", "id", "generation"} or slot["pool"] not in ("anchor", "recent", "historical"):
                raise ValueError("Invalid opponent archive slot metadata")
            stamp = _integer(slot["generation"], "snapshot generation")
            prefix = "anchor" if slot["pool"] == "anchor" else "snapshot"
            if slot["id"] != f"{prefix}:{stamp}" or slot["id"] in ids or stamp > generation:
                raise ValueError("Opponent archive snapshot identity is inconsistent")
            ids.add(slot["id"])
            if slot["pool"] != "anchor" and (stamp < self.every or stamp % self.every):
                raise ValueError("Opponent snapshot is outside the configured interval")
            records.append(dict(slot, weights=copy.deepcopy(state)))
        if records[0]["pool"] != "anchor" or any(r["pool"] == "anchor" for r in records[1:]):
            raise ValueError("Opponent archive must contain exactly one first-slot anchor")
        self.anchor = records[0]
        self.recent = [record for record in records if record["pool"] == "recent"]
        self.historical = [record for record in records if record["pool"] == "historical"]
        if [r["id"] for r in records] != [r["id"] for r in self.records]:
            raise ValueError("Opponent archive pool/weight ordering differs")
        if self.strategy == "recent":
            count = min(self.limit - 1, generation // self.every)
            if self.origin_generation or self.anchor["generation"] or self.history_seen or self.historical or self.initial_history_count or self.initial_recent_count:
                raise ValueError("Recent archive has historical counters or a changed anchor")
        else:
            if self.anchor["generation"] != self.origin_generation or not 0 <= self.initial_recent_count <= 3 or not 0 <= self.initial_history_count <= self.limit - 4:
                raise ValueError("Historical archive initialization/anchor counters are inconsistent")
            if self.initial_history_count and self.initial_recent_count != 3:
                raise ValueError("Historical initial opponents require all three recent slots")
            updates = (self.last_update_generation - self.origin_generation // self.every * self.every) // self.every
            count = min(3, self.initial_recent_count + updates)
            seen = self.initial_history_count + max(0, self.initial_recent_count + updates - 3)
            if self.history_seen != seen or len(self.historical) != min(self.limit - 4, seen):
                raise ValueError("Historical reservoir counters/slot counts are inconsistent")
            initial = self.initial_recent_count + self.initial_history_count
            lower = self.origin_generation // self.every * self.every - (initial - 1) * self.every
            upper = self.last_update_generation - count * self.every
            if any(not lower <= r["generation"] <= upper for r in self.historical):
                raise ValueError("Historical snapshot is outside the known eligible history")
        expected_recent = [self.last_update_generation - (count - 1 - i) * self.every for i in range(count)]
        if [r["generation"] for r in self.recent] != expected_recent:
            raise ValueError("Recent opponent snapshots are not the latest configured generations")
        if self.strategy == "prioritized":
            payoffs = metadata["payoffs"]
            if type(payoffs) is not dict or not set(payoffs) <= ids:
                raise ValueError("Opponent payoff identities differ from retained snapshots")
            for payoff in payoffs.values():
                if type(payoff) is not dict or set(payoff) != {"score", "games", "generation"}:
                    raise ValueError("Invalid opponent payoff fields")
                for key in ("score", "games"):
                    if type(payoff[key]) not in (int, float) or not math.isfinite(payoff[key]):
                        raise ValueError("Nonfinite opponent payoff")
                if not 0 <= payoff["score"] <= payoff["games"] <= 256:
                    raise ValueError("Invalid opponent payoff counts")
                if _integer(payoff["generation"], "payoff generation") > generation:
                    raise ValueError("Opponent payoff comes from a future generation")
            self.payoffs = copy.deepcopy(payoffs)

    def _check_generation(self, generation):
        _integer(generation, "summary generation")
        if generation < self.origin_generation or generation // self.every * self.every != self.last_update_generation:
            raise ValueError("Opponent archive has not observed all configured snapshot generations")

    @staticmethod
    def _record(pool, generation, state):
        return {"pool": pool, "id": f"{'anchor' if pool == 'anchor' else 'snapshot'}:{generation}",
                "generation": generation, "weights": copy.deepcopy(state)}

    @property
    def records(self):
        return [self.anchor, *self.recent, *self.historical]

    @property
    def weights(self):
        return [record["weights"] for record in self.records]

    def add(self, generation, state):
        if _integer(generation, "update generation") != self.last_update_generation + self.every:
            raise ValueError("Opponent snapshots must follow the configured generation interval")
        self._check_weights(state)
        self.last_update_generation = generation
        self.recent.append(self._record("recent", generation, state))
        if self.strategy == "recent":
            self.recent = self.recent[-(self.limit - 1):] if self.limit > 1 else []
            return
        if len(self.recent) <= 3:
            return
        candidate = self.recent.pop(0)
        candidate["pool"] = "historical"
        self.history_seen += 1
        capacity = self.limit - 4
        if len(self.historical) < capacity:
            self.historical.append(candidate)
        else:
            slot = random.randrange(self.history_seen)
            if self.strategy == "prioritized":
                challenger = self._challenger_slot(candidate, generation, capacity)
                if challenger is not None:
                    slot = challenger
                elif slot < max(1, capacity // 2):
                    # Random reservoir admission must not evict protected
                    # challengers. The other half retains historical variety.
                    slot = capacity
            if slot < capacity:
                self.historical[slot] = candidate
        self.payoffs = {key: value for key, value in self.payoffs.items()
                        if key in {record["id"] for record in self.records}}

    def _challenger_slot(self, candidate, generation, capacity):
        """Keep measured threats, with a neutral prior and replacement margin.

        Only runs once per snapshot interval. A fresh sample of at least32
        effective games and learner score below45% is required; the challenger
        must be at least5points harder than the easiest protected incumbent.
        Stale evidence decays using the existing payoff half-life. No new
        checkpoint state or per-action model work is introduced.
        """
        key = candidate["id"]
        payoff = self.payoffs.get(key)
        if payoff is None:
            return None
        effective = payoff["games"] * 2. ** (-(generation - payoff["generation"]) / 256.)
        score = self._score(key, generation)
        if effective < 32 or score >= .45:
            return None
        protected = range(max(1, capacity // 2))
        weakest = max(protected, key=lambda i: (self._score(self.historical[i]["id"], generation),
                                                -self.historical[i]["generation"]))
        if self._score(self.historical[weakest]["id"], generation) >= score + .05:
            return weakest
        return None

    def _score(self, key, generation):
        payoff = self.payoffs.get(key)
        if payoff is None: return .5
        decay = 2. ** (-(generation - payoff["generation"]) / 256.)
        # Sixteen neutral prior games prevent one lucky cohort dominating.
        return (8. + payoff["score"] * decay) / (16. + payoff["games"] * decay)

    def probabilities(self, generation):
        self._check_generation(generation)
        weights = [(1. - self._score(record["id"], generation)) ** 2 for record in self.records]
        total = sum(weights)
        return [.2 / len(weights) + .8 * weight / total for weight in weights]

    def observe(self, generation, opponent_id, *, wins, draws, games):
        if self.strategy != "prioritized": return
        self._check_generation(generation)
        for key, value in (("wins", wins), ("draws", draws), ("games", games)):
            _integer(value, key)
        if wins + draws > games or opponent_id not in {r["id"] for r in self.records}:
            raise ValueError("Invalid opponent outcome or identity")
        if not games: return
        previous = self.payoffs.get(opponent_id, dict(score=0., games=0., generation=generation))
        decay = 2. ** (-(generation - previous["generation"]) / 256.)
        count = previous["games"] * decay + games
        score = previous["score"] * decay + wins + .5 * draws
        scale = min(1., 256. / count)
        self.payoffs[opponent_id] = dict(score=score * scale, games=count * scale, generation=generation)

    def observe_cohort(self, generation, opponent_id, done, rewards, archived, learner_seats):
        if self.strategy != "prioritized": return
        import numpy as np
        lanes = np.flatnonzero((np.asarray(done) == 1) & np.asarray(archived))
        outcomes = np.asarray(rewards)[lanes, np.asarray(learner_seats)[lanes]]
        if not np.isin(outcomes, [-1, 0, 1]).all(): raise ValueError("Invalid natural archive utilities")
        self.observe(generation, opponent_id, wins=int((outcomes == 1).sum()),
                     draws=int((outcomes == 0).sum()), games=len(lanes))

    def choose(self, generation):
        self._check_generation(generation)
        selected = (random.choices(self.records, weights=self.probabilities(generation), k=1)[0]
                    if self.strategy == "prioritized" else random.choice(self.records))
        return selected["weights"], {key: selected[key] for key in ("pool", "id", "generation")} | {
            "strategy": self.strategy, "selection_generation": generation,
            "age_generations": generation - selected["generation"],
            **({"estimated_learner_score": self._score(selected["id"], generation)}
               if self.strategy == "prioritized" else {})}

    def snapshot(self, generation):
        self._check_generation(generation)
        return {"strategy": self.strategy, "limit": self.limit, "every": self.every,
                "origin_generation": self.origin_generation, "history_seen": self.history_seen,
                "count": len(self.records), "counts_by_pool": {"anchor": 1, "recent": len(self.recent), "historical": len(self.historical)},
                "slots": [{key: record[key] for key in ("pool", "id", "generation")} | {
                    "age_generations": generation - record["generation"]} for record in self.records],
                **({"retention": "protected_challengers_and_random_history",
                    "protected_historical_ids": [r["id"] for r in self.historical[:max(1, (self.limit - 4) // 2)]],
                    "selection": [{"id": record["id"], "probability": probability,
                                   "estimated_learner_score": self._score(record["id"], generation),
                                   "effective_games": self.payoffs.get(record["id"], {}).get("games", 0.)}
                                  for record, probability in zip(self.records, self.probabilities(generation))]}
                   if self.strategy == "prioritized" else {})}

    def state_dict(self, generation):
        self._check_generation(generation)
        return {"schema": SCHEMA, "strategy": self.strategy, "limit": self.limit, "every": self.every,
                "generation": generation, "origin_generation": self.origin_generation,
                "last_update_generation": self.last_update_generation, "history_seen": self.history_seen,
                "initial_history_count": self.initial_history_count, "initial_recent_count": self.initial_recent_count,
                "slots": [{key: record[key] for key in ("pool", "id", "generation")} for record in self.records],
                **({"payoffs": copy.deepcopy(self.payoffs)} if self.strategy == "prioritized" else {})}
