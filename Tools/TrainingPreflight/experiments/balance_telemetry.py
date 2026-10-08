"""Passive public/own observation bookkeeping for bounded frozen evaluations.

No model, RNG, game state, reward shaping or training budget access. A choice
means a selected legal direct buy/fast-play/relic/destiny action, not every card
gained by an effect. The paired-seed player-game records support downstream
clustered uncertainty; this module invents no independent-game confidence bound.
"""
from __future__ import annotations

from collections import defaultdict

import numpy as np

SCHEMA = "shards-balance-observations-v1"
HEROES = ("decima", "tetra", "volos", "kosynwu", "rez")
CHOICE_KINDS = ("buy", "fastplay", "relic", "destiny")
ACTION_KINDS = (1, 2, 7, 6)
CARD_COUNT = 189
CARD_CAPACITY = 193  # zero is the no-card sentinel in candidate slot16
_KIND_INDEX = np.full(16, -1, dtype=np.int64)
_KIND_INDEX[list(ACTION_KINDS)] = np.arange(4)


class BalanceTelemetry:
    def __init__(self):
        self.games = []
        self.attempted_games = 0
        self.finished_games = 0
        self.censored_games = 0

    def new_batch(self, batch, seed, seat_a):
        result = BalanceBatch(self, batch, seed, seat_a)
        self.attempted_games += batch
        return result

    def result(self):
        heroes, choices, matchups = {}, {}, {}
        coverage = {role: {hero: 0 for hero in HEROES} for role in ("a", "b")}
        for game in self.games:
            role, hero, seat = game["policy"], game["hero"], game["seat"]
            if hero is not None:
                coverage[role][hero] += 1
            key = (role, hero, seat)
            summary = heroes.setdefault(key, {"policy": role, "hero_id": hero, "seat": seat,
                "games": 0, "wins": 0, "draws": 0, "losses": 0, "censored_games": 0,
                "last_observed_round_sum": 0, "wrapper_decisions_sum": 0,
                "last_observed_mastery_sum": 0, "last_observed_deck_count_sum": 0})
            _count_outcome(summary, game)
            if not game["censored"]:
                summary["last_observed_round_sum"] += game["last_observed_round"]
                summary["wrapper_decisions_sum"] += game["wrapper_decisions"]
                summary["last_observed_mastery_sum"] += game["last_observed_public"]["mastery"]
                summary["last_observed_deck_count_sum"] += game["last_observed_public"]["deck_count"]
            matchup_key = (role, hero, game["opponent_hero"], seat)
            matchup = matchups.setdefault(matchup_key, {"policy": role, "hero_id": hero,
                "opponent_hero": game["opponent_hero"], "seat": seat,
                "games": 0, "wins": 0, "draws": 0, "losses": 0, "censored_games": 0})
            _count_outcome(matchup, game)
            for choice in game["choices"]:
                key = (role, choice["card_id"], choice["choice_kind"])
                summary = choices.setdefault(key, {"policy": role, "card_id": key[1],
                    "choice_kind": key[2], "exposed_games": 0, "selected_games": 0,
                    "pick_count": 0, "opportunity_menus": 0,
                    "selected_wins": 0, "selected_draws": 0, "selected_losses": 0,
                    "censored_exposed_games": 0, "censored_selected_games": 0,
                    "censored_pick_count": 0, "acquisition_round_sum": 0})
                picked = choice["pick_count"] > 0
                if game["censored"]:
                    summary["censored_exposed_games"] += int(choice["opportunity_menus"] > 0)
                    summary["censored_selected_games"] += int(picked)
                    summary["censored_pick_count"] += choice["pick_count"]
                    continue
                summary["exposed_games"] += int(choice["opportunity_menus"] > 0)
                summary["selected_games"] += int(picked)
                summary["pick_count"] += choice["pick_count"]
                summary["opportunity_menus"] += choice["opportunity_menus"]
                summary["acquisition_round_sum"] += choice["acquisition_round_sum"]
                if picked:
                    summary[{1: "selected_wins", 0: "selected_draws", -1: "selected_losses"}[game["outcome"]]] += 1
        return {"schema": SCHEMA, "games": self.games,
            "totals": {"unique_attempted_games": self.attempted_games,
                "resolved_games": self.finished_games-self.censored_games,
                "censored_games": self.censored_games,
                "unfinished_games": self.attempted_games-self.finished_games,
                "policy_player_records": len(self.games)},
            "heroes": list(heroes.values()), "choices": list(choices.values()),
            "hero_matchups": list(matchups.values()), "hero_coverage": coverage,
            "scope": {"sample_unit": "policy-player-game; two correlated records per engine game",
                "pairing": "paired_seed identifies the seat-swapped seed cluster within this report; join report identity/seed cohort across reports",
                "policy_metadata": "policy a/b joins top-level policy_a/policy_b, including frozen version/hash",
                "catalog": "card_id is1-based index into top-level catalog.cards;0 is excluded; catalog lists definition IDs, not display names",
                "choices": "Direct selected legal action kinds1 buy,2 fastplay,7 relic,6 destiny only. Fastplay is temporary play, not permanent acquisition. Generic select12/effect-driven acquisitions are excluded.",
                "opportunities": "Menus containing at least one legal candidate of this definition and choice kind; duplicate instances in one menu count once. Held lanes excluded.",
                "outcomes": "Aggregate outcome associations use completed games only and count each selected card/kind at most once per player-game; caps are unknown and reported separately.",
                "state": "Last observed public scalars before terminal/cap, not terminal state. Own permanent collection was last seen on that player's own decision; excludes temporary fastplays, set-aside and destinies. These snapshots may precede the last action.",
                "limits": "Observational conditional associations, not causal card/hero strength. Draft coverage, policy, opponent, seat, phase and availability confound results. No confidence bounds here; cluster by paired seed downstream."}}


def _count_outcome(target, game):
    target["games"] += 1
    if game["censored"]:
        target["censored_games"] += 1
    else:
        target[{1: "wins", 0: "draws", -1: "losses"}[game["outcome"]]] += 1


class BalanceBatch:
    """Own counters only; no reference to mutable host observation buffers."""
    def __init__(self, owner, batch, seed, seat_a):
        if not 1 <= batch <= 256 or seat_a not in (0, 1) or not 0 <= seed <= 2**64-batch:
            raise ValueError("Invalid balance cohort dimensions/seed/seat")
        self.owner, self.batch, self.seed, self.seat_a = owner, batch, seed, seat_a
        shape = (batch, 2, len(CHOICE_KINDS), CARD_CAPACITY)
        self.opportunities = np.zeros(shape, np.int32)
        self.picks = np.zeros(shape, np.int32)
        self.round_sum = np.zeros(shape, np.int64)
        self.first_round = np.full(shape, -1, np.int32)
        self.public = np.zeros((batch, 2, 4), np.int64)
        self.heroes = np.zeros((batch, 2), np.int8)
        self.own_collection = np.zeros((batch, 2, CARD_COUNT), np.int32)
        self.collection_seen = np.zeros((batch, 2), bool)
        self.collection_round = np.zeros((batch, 2), np.int32)
        self.rounds = np.zeros(batch, np.int32)
        self.lengths = np.zeros(batch, np.int64)
        self.decisions = np.zeros((batch, 2), np.int64)
        self.finished = np.zeros(batch, bool)

    def observe(self, obs, candidates, mask, seats, actions, active):
        active = np.asarray(active, dtype=bool)
        lanes = np.flatnonzero(active)
        if not len(lanes):
            return
        if active.shape != (self.batch,) or np.any(self.finished[lanes]):
            raise ValueError("Balance observation attempted after completed episode")
        seats = np.asarray(seats)
        actions = np.asarray(actions)
        if not np.isin(seats[lanes], (0, 1)).all() or np.any(actions[lanes] < 0) or np.any(actions[lanes] >= 64):
            raise ValueError("Invalid deciding seat/action in balance observation")
        if not np.all(mask[lanes, actions[lanes]] == 1):
            raise ValueError("Balance observer saw an illegal selected action")
        role = (seats[lanes] != self.seat_a).astype(np.int64)
        round_values = np.rint(obs[lanes, 2]*100).astype(np.int32)
        self.rounds[lanes] = round_values
        self.lengths[lanes] += 1
        self.decisions[lanes, role] += 1
        for relative in (0, 1):
            offset = 16+48*relative
            target = role if relative == 0 else 1-role
            self.public[lanes, target] = np.rint(obs[lanes][:, [offset, offset+1, offset+5, offset+4]] *
                                                np.asarray([50, 30, 50, 20])).astype(np.int64)
            heroes = np.rint(obs[lanes, offset+6]*5).astype(np.int8)
            if np.any((heroes < 0) | (heroes > 5)):
                raise ValueError("Unexpected public hero encoding")
            self.heroes[lanes, target] = heroes
        self.own_collection[lanes, role] = np.rint(obs[lanes, 320:320+CARD_COUNT]*10).astype(np.int32)
        self.collection_seen[lanes, role] = True
        self.collection_round[lanes, role] = round_values
        features = candidates[lanes]
        kinds = features[..., :16].argmax(-1)
        categories = _KIND_INDEX[kinds]
        eligible = (mask[lanes] == 1) & (categories >= 0)
        row, slot = np.nonzero(eligible)
        identities = np.rint(features[row, slot, 16]*192).astype(np.int64)
        if np.any((identities < 1) | (identities > CARD_COUNT)):
            raise ValueError("Direct acquisition candidate has no known definition")
        # A duplicate definition in several market slots is one opportunity menu.
        keys = (((lanes[row]*2+role[row])*4+categories[row, slot])*CARD_CAPACITY+identities)
        np.add.at(self.opportunities.ravel(), np.unique(keys), 1)
        selected_categories = categories[np.arange(len(lanes)), actions[lanes]]
        selected = np.flatnonzero(selected_categories >= 0)
        ids = np.rint(features[selected, actions[lanes[selected]], 16]*192).astype(np.int64)
        locations = (lanes[selected], role[selected], selected_categories[selected], ids)
        self.picks[locations] += 1
        self.round_sum[locations] += round_values[selected]
        first = self.first_round[locations]
        self.first_round[locations] = np.where(first < 0, round_values[selected], first)

    def finish(self, lane, rewards, done):
        if not 0 <= lane < self.batch or self.finished[lane] or done not in (1, 2):
            raise ValueError("Invalid/repeated balance episode completion")
        rewards = np.asarray(rewards)
        if rewards.shape != (2,) or not np.isin(rewards, (-1, 0, 1)).all() or rewards.sum() != 0:
            raise ValueError("Invalid engine reward pair")
        if done == 2 and np.any(rewards):
            raise ValueError("Censored episode must have no fabricated reward")
        if not self.lengths[lane]:
            raise ValueError("Cannot finish an unobserved episode")
        self.finished[lane] = True
        self.owner.finished_games += 1
        self.owner.censored_games += int(done == 2)
        for role, label in enumerate(("a", "b")):
            seat = self.seat_a if role == 0 else 1-self.seat_a
            choices = []
            for kind, card in zip(*np.nonzero(self.opportunities[lane, role] | self.picks[lane, role])):
                choices.append({"card_id": int(card), "choice_kind": CHOICE_KINDS[kind],
                    "pick_count": int(self.picks[lane, role, kind, card]),
                    "opportunity_menus": int(self.opportunities[lane, role, kind, card]),
                    "first_selected_round": int(self.first_round[lane, role, kind, card]) if self.picks[lane, role, kind, card] else None,
                    "acquisition_round_sum": int(self.round_sum[lane, role, kind, card])})
            def hero(index):
                code = self.heroes[lane, index]
                return HEROES[code-1] if code else None
            own = self.own_collection[lane, role]
            self.owner.games.append({"paired_seed": self.seed+lane, "policy": label,
                "seat": seat, "hero": hero(role), "opponent_hero": hero(1-role),
                "outcome": int(rewards[seat]) if done == 1 else None, "censored": done == 2,
                "last_observed_round": int(self.rounds[lane]),
                "wrapper_decisions": int(self.lengths[lane]),
                "policy_decisions": int(self.decisions[lane, role]), "choices": choices,
                "last_observed_public": dict(zip(("health", "mastery", "deck_count", "hand_count"),
                                                    map(int, self.public[lane, role]))),
                "last_own_collection_round": int(self.collection_round[lane, role]) if self.collection_seen[lane, role] else None,
                "last_observed_own_collection": [{"card_id": int(card+1), "count": int(own[card])}
                                                  for card in np.flatnonzero(own)] if self.collection_seen[lane, role] else None})
