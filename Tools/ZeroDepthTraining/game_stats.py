"""Compact, exact rolling natural-game observations; never a learning input.

Only terminal cohorts enter this ring. A record contains outcomes, public final
player scalars and final permanent-collection presence, not action histories.
Counters are updated while a game enters/leaves the ring. Rendering does not
scan its games. The byte ring is checkpointed with the model for exact resume.
"""
from __future__ import annotations

import hashlib
import json
import math
import struct


SCHEMA = "shards-zero-depth-rolling-games-v1"
OBSERVATION_SCHEMA = "shards-zero-depth-observation-v2"
_RECORD = struct.Struct("<bbbbbb7i")


def _list(value):
    return value.tolist() if hasattr(value, "tolist") else list(value)


def _integer(value, name, minimum=None):
    if type(value) is not int or minimum is not None and value < minimum:
        raise ValueError(f"Invalid {name}")
    return value


def _mean(total, count):
    return total / count if count else None


class RollingGameStats:
    """Public API: add_cohort, snapshot, state_dict and from_state."""
    def __init__(self, catalog, window=100_000):
        _integer(window, "rolling window", 1)
        if window > 1_000_000:
            raise ValueError("Rolling window exceeds the bounded observation store")
        self.window = window
        self.cards = list(catalog.get("card_ids", []))
        self.heroes = [hero["id"] for hero in catalog.get("heroes", [])]
        if len(self.heroes) > 127 or len(set(self.cards)) != len(self.cards) or len(set(self.heroes)) != len(self.heroes):
            raise ValueError("Invalid rolling statistics catalog")
        self.catalog_signature = hashlib.sha256(json.dumps({"cards": self.cards, "heroes": self.heroes,
            "observation_schema": catalog.get("observation_schema")}, sort_keys=True).encode()).hexdigest()
        self.scalars_available = catalog.get("observation_schema") == OBSERVATION_SCHEMA
        descriptors = catalog.get("histogram_descriptors", catalog.get("histograms", []))
        descriptors = {item["name"]: item for item in descriptors}
        self.histograms = [descriptors.get(name) for name in ("own_permanent_collection", "opponent_permanent_collection")]
        self.cards_available = bool(self.cards) and all(item is not None and item["length"] == len(self.cards)
                                                      for item in self.histograms)
        self.presence_bytes = (len(self.cards) + 7) // 8
        self.stride = _RECORD.size + 2 * self.presence_bytes
        self.records = bytearray(self.window * self.stride)
        self.head = self.count = self.total_natural_games = self.total_censored_games = 0
        self._totals = dict(games=0, seat0_wins=0, seat1_wins=0, draws=0,
                           selfplay_games=0, selfplay_seat0_wins=0, selfplay_seat1_wins=0, selfplay_draws=0,
                           archive_games=0, archive_wins=0, archive_losses=0, archive_draws=0)
        self._hero = [[0] * 8 for _ in self.heroes]
        self._hero_seats = [[0] * len(self.heroes) for _ in (0, 1)]
        self._matchups = [[[0] * 5 for _ in self.heroes] for _ in self.heroes]
        self._matchup_games = 0
        self._card = [[0] * 4 for _ in self.cards]
        self._feature_games = self._card_games = self._round_sum = 0
        self._mode_rounds = [[0, 0], [0, 0]]
        self._mastery = [0, 0]
        self._health = [0, 0]
        self._decks = [0, 0]

    def _apply(self, data, sign):
        winner, archived, learner, hero0, hero1, flags, rounds, mastery0, mastery1, health0, health1, deck0, deck1 = _RECORD.unpack_from(data)
        totals = self._totals
        totals["games"] += sign
        totals["draws" if winner == -1 else f"seat{winner}_wins"] += sign
        if archived:
            totals["archive_games"] += sign
            totals["archive_draws" if winner == -1 else "archive_wins" if winner == learner else "archive_losses"] += sign
        else:
            totals["selfplay_games"] += sign
            totals["selfplay_draws" if winner == -1 else f"selfplay_seat{winner}_wins"] += sign
        if flags & 1:
            self._feature_games += sign
            self._round_sum += rounds * sign
            self._mode_rounds[archived][0] += rounds * sign
            self._mode_rounds[archived][1] += sign
            for seat, (hero, mastery, health, deck) in enumerate(((hero0, mastery0, health0, deck0), (hero1, mastery1, health1, deck1))):
                self._mastery[seat] += mastery * sign
                self._health[seat] += health * sign
                if hero >= 0:
                    self._hero_seats[seat][hero] += sign
                    row = self._hero[hero]
                    row[0] += sign
                    row[2 if winner == -1 else 1 if winner == seat else 3] += sign
                    row[4] += mastery * sign
                    row[5] += health * sign
                    row[7] += rounds * sign
                    if flags & 2:
                        row[6] += deck * sign
        if flags & 1 and hero0 >= 0 and hero1 >= 0:
            row = self._matchups[hero0][hero1]
            row[0] += sign
            row[3 if winner == -1 else 1 + winner] += sign
            row[4] += rounds * sign
            self._matchup_games += sign
        if flags & 2:
            self._card_games += sign
            self._decks[0] += deck0 * sign
            self._decks[1] += deck1 * sign
            for seat in (0, 1):
                start = _RECORD.size + seat * self.presence_bytes
                bits = int.from_bytes(data[start:start + self.presence_bytes], "little")
                while bits:
                    low = bits & -bits
                    row = self._card[low.bit_length() - 1]
                    row[0] += sign
                    row[2 if winner == -1 else 1 if winner == seat else 3] += sign
                    bits ^= low

    def _append(self, values, presence):
        data = _RECORD.pack(*values) + b"".join(bits.to_bytes(self.presence_bytes, "little") for bits in presence)
        start = self.head * self.stride
        if self.count == self.window:
            self._apply(self.records[start:start + self.stride], -1)
        else:
            self.count += 1
        self.records[start:start + self.stride] = data
        self._apply(data, 1)
        self.head = (self.head + 1) % self.window
        self.total_natural_games += 1

    def _features(self, host, lane):
        if not self.scalars_available:
            return (-1, -1, 0, -1, 0, 0, 0, 0, 0, 0), (0, 0)
        row = host.obs[lane]
        actor = int(host.actors[lane])
        if actor not in (0, 1):
            raise ValueError("Invalid terminal observation seat")
        raw_round = float(row[2])
        if not math.isfinite(raw_round) or raw_round < 0:
            raise ValueError("Invalid terminal round")
        rounds = round(raw_round * 100)
        heroes, masteries, health = [], [], []
        for seat in (0, 1):
            start = 16 + (0 if actor == seat else 64)
            values = [float(row[start + offset]) for offset in (6, 1, 0)]
            if not all(math.isfinite(value) for value in values):
                raise ValueError("Nonfinite terminal player scalar")
            hero = round(values[0] * 5) - 1
            if not -1 <= hero < len(self.heroes):
                raise ValueError("Terminal hero does not match the catalog")
            heroes.append(hero)
            masteries.append(round(values[1] * 30))
            health.append(round(values[2] * 50))
        decks, presence = [0, 0], [0, 0]
        if self.cards_available:
            for relative, descriptor in enumerate(self.histograms):
                values = row[descriptor["offset"]:descriptor["offset"] + descriptor["length"]]
                counts = _list(values)
                if any(not math.isfinite(float(value)) or value < 0 for value in counts):
                    raise ValueError("Invalid terminal permanent collection")
                seat = actor if relative == 0 else 1 - actor
                decks[seat] = round(float(values.sum() if hasattr(values, "sum") else sum(values)) * descriptor.get("scale", 1))
                nonzero = values.nonzero()[0].tolist() if hasattr(values, "nonzero") else [i for i, value in enumerate(values) if value]
                presence[seat] = sum(1 << index for index in nonzero)
        return (heroes[0], heroes[1], 1 | (2 if self.cards_available else 0), rounds,
                masteries[0], masteries[1], health[0], health[1], decks[0], decks[1]), presence

    def add_cohort(self, host, archived, learner_seats):
        """Add only actual natural terminals; administrative censors stay separate.

        host contains the final borrowed tensors and must not be reset until this
        call returns. The ring owns every stored byte and never retains host views.
        """
        done, rewards = _list(host.done), _list(host.rewards)
        archived, learner_seats = _list(archived), _list(learner_seats)
        if not (len(done) == len(rewards) == len(archived) == len(learner_seats)):
            raise ValueError("Statistics cohort arrays differ in size")
        pending = []
        for lane, status in enumerate(done):
            if status not in (1, 2) or learner_seats[lane] not in (0, 1) or type(archived[lane]) is not bool:
                raise ValueError("Statistics require a finalized cohort with valid assignments")
            utility = list(rewards[lane])
            if len(utility) != 2 or any(value not in (-1, 0, 1) for value in utility) or sum(utility):
                raise ValueError("Invalid terminal zero-sum utility")
            if status == 2:
                continue
            winner = -1 if utility == [0, 0] else 0 if utility == [1, -1] else 1
            features, presence = self._features(host, lane)
            values = (winner, int(archived[lane]), int(learner_seats[lane]), *features)
            # Check the fixed record before any mutation so invalid input cannot
            # partially credit an otherwise finalized batch.
            _RECORD.pack(*values)
            pending.append((values, presence))
        for values, presence in pending:
            self._append(values, presence)
        self.total_censored_games += done.count(2)
        return {"natural_games_added": len(pending), "censored_games_added": done.count(2)}

    def snapshot(self):
        totals = dict(self._totals)
        totals["archive_score"] = _mean(totals["archive_wins"] + .5 * totals["archive_draws"], totals["archive_games"])
        heroes = []
        for index, (hero, row) in enumerate(zip(self.heroes, self._hero)):
            heroes.append(dict(id=hero, observations=row[0], wins=row[1], draws=row[2], losses=row[3],
                seat0_games=self._hero_seats[0][index], seat1_games=self._hero_seats[1][index],
                score=_mean(row[1] + .5 * row[2], row[0]), mean_mastery=_mean(row[4], row[0]),
                mean_rounds=_mean(row[7], row[0]),
                mean_health=_mean(row[5], row[0]), mean_collection_size=_mean(row[6], row[0]) if self.cards_available else None))
        cards = [dict(id=card, observations=row[0], wins=row[1], draws=row[2], losses=row[3],
                      score=_mean(row[1] + .5 * row[2], row[0])) for card, row in zip(self.cards, self._card)]
        matchups = [dict(seat0_hero=hero0, seat1_hero=hero1, games=row[0],
                         seat0_wins=row[1], seat1_wins=row[2], draws=row[3], mean_rounds=_mean(row[4], row[0]))
                    for index0, hero0 in enumerate(self.heroes)
                    for hero1, row in zip(self.heroes, self._matchups[index0])]
        return {"schema": SCHEMA, "window": self.window, "games_in_window": self.count,
                "hero_matchups": matchups, "hero_matchup_game_coverage": self._matchup_games,
                "total_natural_games": self.total_natural_games, "total_censored_games": self.total_censored_games,
                "totals": totals, "mean_rounds": _mean(self._round_sum, self._feature_games),
                "mean_rounds_by_mode": {name: _mean(*self._mode_rounds[index])
                                       for index, name in enumerate(('selfplay', 'archive'))},
                "rounds_game_coverage_by_mode": {name: self._mode_rounds[index][1]
                                                for index, name in enumerate(('selfplay', 'archive'))},
                "mean_mastery_by_seat": [_mean(value, self._feature_games) for value in self._mastery],
                "mean_terminal_health_by_seat": [_mean(value, self._feature_games) for value in self._health],
                "mean_collection_size_by_seat": [_mean(value, self._card_games) for value in self._decks],
                "scalar_game_coverage": self._feature_games, "collection_game_coverage": self._card_games,
                "heroes": heroes, "cards": cards,
                "scope": "Exact last natural games in completion-cohort/lane order; censors are excluded, not draws.",
                "interpretation": "Both self-play seats use the learner, so overall self-play win rate is inherently balanced and is not strength evidence. Archive scores face changing historical policies. Hero/card observations are final-collection associations under this training mix, not causal rankings."}

    def state_dict(self):
        used = self.window if self.count == self.window else self.count
        return {"schema": SCHEMA, "catalog_signature": self.catalog_signature, "window": self.window,
                "head": self.head, "count": self.count, "total_natural_games": self.total_natural_games,
                "total_censored_games": self.total_censored_games,
                # Torch's safe loader rejects its GLOBAL encoding of empty
                # bytes; None is a safe, explicit empty-ring representation.
                "records": bytes(self.records[:used * self.stride]) if used else None}

    @classmethod
    def from_state(cls, catalog, state):
        if state.get("schema") != SCHEMA:
            raise ValueError("Unsupported rolling statistics checkpoint")
        result = cls(catalog, window=state["window"])
        if state.get("catalog_signature") != result.catalog_signature:
            raise ValueError("Rolling statistics catalog changed after checkpoint")
        count = _integer(state["count"], "rolling count", 0)
        head = _integer(state["head"], "rolling head", 0)
        natural = _integer(state["total_natural_games"], "natural total", 0)
        censored = _integer(state["total_censored_games"], "censored total", 0)
        records = state["records"]
        if records is None and count == 0:
            records = b""
        if count > result.window or head >= result.window or natural < count or (count < result.window and head != count):
            raise ValueError("Inconsistent rolling statistics counters")
        if type(records) is not bytes or len(records) != count * result.stride:
            raise ValueError("Invalid compact rolling statistics records")
        for offset in range(0, len(records), result.stride):
            data = records[offset:offset + result.stride]
            winner, archived, learner, hero0, hero1, flags, *_ = _RECORD.unpack_from(data)
            if winner not in (-1, 0, 1) or archived not in (0, 1) or learner not in (0, 1) or flags not in (0, 1, 3):
                raise ValueError("Invalid rolling outcome record")
            if not all(-1 <= hero < len(result.heroes) for hero in (hero0, hero1)):
                raise ValueError("Invalid rolling hero record")
            for seat in (0, 1):
                start = _RECORD.size + seat * result.presence_bytes
                if int.from_bytes(data[start:start + result.presence_bytes], "little") >> len(result.cards):
                    raise ValueError("Invalid rolling card-presence record")
            result._apply(data, 1)
        result.records[:len(records)] = records
        result.head, result.count = head, count
        result.total_natural_games, result.total_censored_games = natural, censored
        return result
