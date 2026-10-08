"""Cached recent-window view of passive C# training-pool outcome snapshots.

No Torch, inference, games or training-file writes. A publication is processed
once when its inode/size/mtime changes; raw immutable history retains provenance.
Both players belong to one game cluster. Pooled changing-policy observations
are explicitly separate from controlled frozen-policy comparisons.
"""
from __future__ import annotations

from collections import defaultdict
import datetime as dt
import hashlib
import json
from pathlib import Path
import re
import time

from balance_statistics import CATALOG, SCHEMA, paired_ratio_interval


RAW_SCHEMA = "shards-training-pool-stats-v1"
ROW_FIELDS = ("selected_player_games", "selected_game_clusters", "wins", "draws", "losses",
    "censored_player_games", "censored_game_clusters", "pick_count", "acquisition_round_sum",
    "cost_paid_sum_known", "cost_known_pick_count", "censored_pick_count", "censored_round_sum",
    "censored_cost_paid_sum")
HERO_FIELDS = ("games", "wins", "draws", "losses", "censored_games")
MATCH_FIELDS = ("games", "seat0_wins", "seat1_wins", "draws", "censored_games")
TOTAL_FIELDS = ("completed_games", "draws", "seat0_wins", "seat1_wins", "censored_games", "unfinished_discarded_games")
STATE_FIELDS = ("winner_mastery_sum", "winner_health_sum", "loser_mastery_sum", "resolved_decisive_games",
                "total_own_permanent_collection_sum", "player_count")


def load(path):
    path = Path(path)
    if path.is_symlink() or path.stat().st_size > 32*1024*1024:
        raise ValueError("Invalid statistics artifact")
    return json.loads(path.read_text())


def subtract_counts(current, previous, fields):
    result = {}
    for key in fields:
        newer, older = current.get(key, 0), previous.get(key, 0)
        if type(newer) is not int or type(older) is not int or not 0 <= older <= newer:
            raise ValueError("Statistics counters regressed or are not nonnegative integers: "+key)
        result[key] = newer-older
    return result


def difference_rows(current, previous, keys, fields):
    def indexed(rows):
        result = {}
        for row in rows:
            key = tuple(row[field] for field in keys)
            if key in result:
                raise ValueError("Duplicate statistics row")
            result[key] = row
        return result
    current, previous = indexed(current), indexed(previous)
    if not previous.keys() <= current.keys():
        raise ValueError("Cumulative statistics dropped a row")
    return [{**dict(zip(keys, key)), **subtract_counts(row, previous.get(key, {}), fields)}
            for key, row in current.items()]


def delta(current, previous=None):
    previous = previous or {}
    if previous and any(current.get(key) != previous.get(key) for key in
                        ("schema", "session_id", "host_binary_sha256", "purpose", "observation_schema", "hero_setup")):
        raise ValueError("Window anchor belongs to a different statistics session")
    result = {key: current.get(key) for key in ("session_id", "published_utc", "pid", "final",
                                               "publish_every_completed_games")}
    result["totals"] = subtract_counts(current["totals"], previous.get("totals", {}), TOTAL_FIELDS)
    result["final_state_sums"] = subtract_counts(current.get("final_state_sums", {}), previous.get("final_state_sums", {}), STATE_FIELDS)
    for name, keys, fields in (("rows", ("card_id", "choice_kind"), ROW_FIELDS),
                              ("hero_choice_rows", ("hero_id", "card_id", "choice_kind"), ROW_FIELDS),
                              ("hero_seat_rows", ("hero_id", "seat"), HERO_FIELDS),
                              ("matchup_rows", ("seat0_hero_id", "seat1_hero_id"), MATCH_FIELDS)):
        result[name] = difference_rows(current.get(name, []), previous.get(name, []), keys, fields)
    histogram, old = current.get("final_round_histogram", []), previous.get("final_round_histogram", [])
    if histogram and len(histogram) != 402 or old and len(old) != len(histogram):
        raise ValueError("Unexpected round histogram shape")
    result["final_round_histogram"] = [subtract_counts({"n": count}, {"n": old[i] if old else 0}, ("n",))["n"]
                                       for i, count in enumerate(histogram)]
    result["anchor_completed_games"] = previous.get("totals", {}).get("completed_games", 0)
    result["latest_completed_games"] = current["totals"]["completed_games"]
    return result


def outcome_summary(wins, draws, losses, censored, clusters):
    resolved = wins+draws+losses
    total = resolved+censored
    success = wins+.5*draws
    return {"games": resolved, "wins": wins, "draws": draws, "losses": losses,
        "censored_games": censored, "paired_clusters": clusters,
        "score": success/resolved if resolved and not censored else None,
        "score_resolved_only": success/resolved if resolved else None,
        "score_identification_interval": [success/total, (success+censored)/total] if total else None,
        "score_bound_95": paired_ratio_interval(success, total, clusters, censored)}


def combine(windows, metadata, *, run_id, target_games=100000):
    cards = {card["card_id"]: card for card in metadata["cards"]}
    card_indices = {card["id"]: index for index, card in cards.items()}
    hero_names = {hero["id"]: hero["name"] for hero in metadata["heroes"]}
    hero_metadata = {hero["id"]: hero for hero in metadata["heroes"]}
    totals, state = defaultdict(int), defaultdict(int)
    hist = [0]*402
    choices, hero_choices, hero_seats, matchups = {}, {}, {}, {}
    def add_rows(destination, rows, keys, fields):
        for row in rows:
            key = tuple(card_indices[row[field]] if field == "card_id" and isinstance(row[field], str)
                        else row[field] for field in keys)
            target = destination.setdefault(key, defaultdict(int))
            for field in fields:
                target[field] += row[field]
    for window in windows:
        for key, value in window["totals"].items(): totals[key] += value
        for key, value in window["final_state_sums"].items(): state[key] += value
        for i, value in enumerate(window["final_round_histogram"]): hist[i] += value
        add_rows(choices, window["rows"], ("card_id", "choice_kind"), ROW_FIELDS)
        add_rows(hero_choices, window["hero_choice_rows"], ("hero_id", "card_id", "choice_kind"), ROW_FIELDS)
        add_rows(hero_seats, window["hero_seat_rows"], ("hero_id", "seat"), HERO_FIELDS)
        add_rows(matchups, window["matchup_rows"], ("seat0_hero_id", "seat1_hero_id"), MATCH_FIELDS)
    if totals["seat0_wins"]+totals["seat1_wins"]+totals["draws"] != totals["completed_games"]:
        raise ValueError("Terminal counts disagree")
    if sum(hist) != totals["completed_games"]:
        raise ValueError("Terminal round histogram disagrees with completed games")
    if state["player_count"] != 2*totals["completed_games"] or state["resolved_decisive_games"] != totals["seat0_wins"]+totals["seat1_wins"]:
        raise ValueError("Final-state sample counts disagree with completed games")
    if (sum(row["games"] for row in hero_seats.values()) != 2*totals["completed_games"] or
            sum(row["censored_games"] for row in hero_seats.values()) != 2*totals["censored_games"] or
            sum(row["games"] for row in matchups.values()) != totals["completed_games"] or
            sum(row["censored_games"] for row in matchups.values()) != totals["censored_games"]):
        raise ValueError("Hero/matchup sample counts disagree with completed games")
    # Player/event counters partition exactly by hero. Unique real-game cluster
    # counts do not: both distinct heroes can choose the same card in one game.
    partition_fields = tuple(field for field in ROW_FIELDS if field not in
                             ("selected_game_clusters", "censored_game_clusters"))
    by_hero_sum = {}
    for (_, card, kind), row in hero_choices.items():
        target = by_hero_sum.setdefault((card, kind), defaultdict(int))
        for field in partition_fields:
            target[field] += row[field]
    if choices.keys() != by_hero_sum.keys() or any(
            row[field] != by_hero_sum[key][field] for key, row in choices.items() for field in partition_fields):
        raise ValueError("Hero-choice outcome/event counts disagree with global choices")
    hero_rows, seat_rows, matchup_rows = [], [], []
    for hero, name in hero_names.items():
        h = defaultdict(int)
        for seat in (0, 1):
            row = hero_seats.get((hero, seat), {})
            w, d, l, c = (row.get(key, 0) for key in ("wins", "draws", "losses", "censored_games"))
            if row.get("games", 0) != w+d+l:
                raise ValueError("Hero outcomes disagree")
            seat_rows.append({**hero_metadata[hero], "id": hero+":"+str(seat), "hero_id": hero, "name": name, "seat": seat,
                              **outcome_summary(w, d, l, c, w+d+l+c)})
            for key in HERO_FIELDS: h[key] += row.get(key, 0)
        hero_clusters = sum(row["games"]+row["censored_games"] for (first, second), row in matchups.items()
                            if hero in (first, second))
        hero_rows.append({**hero_metadata[hero], "id": hero, "name": name, **outcome_summary(h["wins"], h["draws"], h["losses"],
                          h["censored_games"], hero_clusters)})
    oriented_matchups = {}
    for (first, second), row in matchups.items():
        if first not in hero_names or second not in hero_names:
            continue
        for index, (hero, opponent, wins, losses) in enumerate((
                (first, second, row["seat0_wins"], row["seat1_wins"]),
                (second, first, row["seat1_wins"], row["seat0_wins"]))):
            aggregate = oriented_matchups.setdefault((hero, opponent), defaultdict(int))
            for key, value in (("wins", wins), ("draws", row["draws"]), ("losses", losses), ("censored", row["censored_games"])):
                aggregate[key] += value
            if index == 0 or first != second:
                aggregate["clusters"] += row["games"]+row["censored_games"]
    for (hero, opponent), aggregate in sorted(oriented_matchups.items()):
        matchup_rows.append({"hero_a": hero, "hero_b": opponent, **outcome_summary(
            aggregate["wins"], aggregate["draws"], aggregate["losses"], aggregate["censored"], aggregate["clusters"])})
    def choice_row(card_id, kind, row, hero=None):
        if card_id not in cards or kind not in ("buy", "fastplay", "relic", "destiny", "effect_acquire", "effect_fastplay"):
            raise ValueError("Unknown card or acquisition mode")
        w, d, l, c = (row.get(key, 0) for key in ("wins", "draws", "losses", "censored_player_games"))
        if row.get("selected_player_games", 0) != w+d+l:
            raise ValueError("Selected outcome counts disagree")
        clusters = row.get("selected_game_clusters", 0)+row.get("censored_game_clusters", 0)
        picks = row.get("pick_count", 0)
        return {**cards[card_id], "id": cards[card_id]["id"]+":"+kind+(":"+hero if hero else ""),
            "choice_kind": kind, "acquisition_mode": kind, "hero_id": hero,
            **outcome_summary(w, d, l, c, clusters), "pick_count": picks,
            "opportunities": None, "exposed_games": None, "pick_rate": None,
            "mean_acquisition_round": row.get("acquisition_round_sum", 0)/picks if picks else None,
            "mean_cost_paid": row.get("cost_paid_sum_known", 0)/row["cost_known_pick_count"] if row.get("cost_known_pick_count") else None,
            "censored_pick_count": row.get("censored_pick_count", 0)}
    rankings = {"heroes": hero_rows, "cards": [], "relics": [], "destinies": []}
    for card_id, card in cards.items():
        category = {"Relic": "relics", "Destiny": "destinies"}.get(card["type"], "cards")
        default_kind = {"relics": "relic", "destinies": "destiny"}.get(category, "buy")
        kinds = {kind for cid, kind in choices if cid == card_id} | {default_kind}
        if card["type"] == "Mercenary": kinds.add("fastplay")
        rankings[category].extend(choice_row(card_id, kind, choices.get((card_id, kind), {})) for kind in sorted(kinds))
    hero_choice_rows = [choice_row(card, kind, row, hero) for (hero, card, kind), row in sorted(hero_choices.items())]
    complete, censored = totals["completed_games"], totals["censored_games"]
    all_time = sum(window["latest_completed_games"] for window in windows)
    cadence = windows[-1].get("publish_every_completed_games", 10000) if windows else 10000
    observed = sum(row["games"]+row["censored_games"] > 0 for row in hero_rows)
    mean_round = sum(i*count for i, count in enumerate(hist))/complete if complete else None
    decisive = state["resolved_decisive_games"]
    insights = [f"Observed {observed} of {len(hero_names)} heroes in the current window.",
        f"Seat 0 won {totals['seat0_wins']:,} and seat 1 won {totals['seat1_wins']:,}; {totals['draws']:,} draws."]
    if decisive:
        insights.extend([f"Winner final mastery: {state['winner_mastery_sum']/decisive:.1f}; loser final mastery: {state['loser_mastery_sum']/decisive:.1f}.",
                         f"Winner final health: {state['winner_health_sum']/decisive:.1f}."])
    if state["player_count"]:
        insights.append(f"Mean final permanent collection size: {state['total_own_permanent_collection_sum']/state['player_count']:.1f} cards.")
    return {"schema": SCHEMA, "state": "ready" if complete+censored else "awaiting_samples", "updated_wall": time.time(),
        "snapshot_id": hashlib.sha256(json.dumps([(w["session_id"], w["latest_completed_games"], w["anchor_completed_games"])
                                                  for w in windows]).encode()).hexdigest()[:16],
        "scope": {"label": "Live training pool", "run_id": run_id,
            "policy_label": "Both players from pooled changing-policy training games", "policy_versions": [],
            "opponent_label": "Self-play and frozen archive opponents",
            "sample_unit": "completed player-perspective games; two correlated players per real game",
            "inference": "Production training policies and archive opponents; passive engine counters",
            "confidence_method": "Conservative descriptive 95% bounds under independent game-cluster sampling; evolving policies are pooled",
            "notes": ["Outcome associations after recorded acquisition events are not causal card strength or controlled model evaluations.",
                "Buy/fastplay identify direct purchase actions; effect_acquire/effect_fastplay are other acquisition paths. A fast-play path can later become permanent.",
                "Relic/destiny acquisition events include effects. Legal-menu exposure and pick rate are not collected in this low-overhead path.",
                "Completed simulated games count even when later PPO work is discarded. Unfinished games have no fabricated outcome.",
                "The recent window is rounded to available publication boundaries; exact sample count is shown. Old replaced definitions may be unobserved.",
                "Intervals are descriptive, pointwise and unadjusted for multiple rankings or repeatedly checking the dashboard."]},
        "refresh": {"every_games": cadence, "games_since_snapshot": None, "total_games": all_time,
            "snapshot_training_games": all_time, "evaluation_sample_count": 0},
        "window": {"target_completed_games": target_games, "actual_completed_games": complete,
            "sessions": [{"session_id": w["session_id"], "anchor_completed_games": w["anchor_completed_games"],
                          "latest_completed_games": w["latest_completed_games"]} for w in windows]},
        "totals": {"attempted_games": complete+censored, "resolved_games": complete,
            "censored_games": censored, "draws": totals["draws"], "seat0_wins": totals["seat0_wins"],
            "seat1_wins": totals["seat1_wins"], "mean_rounds": mean_round if not hist[401] else None,
            "mean_rounds_lower_bound": mean_round, "mean_wrapper_decisions": None,
            "unfinished_discarded_games": totals["unfinished_discarded_games"]},
        "rankings": rankings, "hero_seats": seat_rows, "hero_matchups": matchup_rows,
        "hero_choice_rows": hero_choice_rows, "final_round_histogram": hist,
        "final_round_overflow_from": 401, "final_state_sums": dict(state), "insights": insights}


class TrainingPoolCache:
    def __init__(self, campaign, *, target_games=100000, metadata=None, cohort=None):
        if cohort not in (None, "natural-draft", "forced-random"):
            raise ValueError("Unknown hero-setup cohort")
        self.campaign = Path(campaign)
        self.cohort = cohort
        self.target_games = target_games
        self.metadata = metadata or load(CATALOG)
        self.auto_metadata = metadata is None
        self.metadata_options = ([load(path) for path in sorted(CATALOG.parent.glob("balance-card-catalog*.json"))]
                                 if metadata is None and cohort else [])
        self.stamp, self.result = None, None
        self.error = None

    def refresh(self, runs):
        # Keep the observation/action migration a separate balance population.
        # Explicit test/caller metadata retains the existing strict behavior.
        if self.auto_metadata and self.cohort:
            # Choose the newest supported runtime population, retaining strict
            # identity matching. A newer encoder must not silently show V7 data.
            fields = ("rules_sha256", "catalog_sha256", "observation_schema")
            populations = []
            for metadata in self.metadata_options:
                matching = {key:run for key,run in runs.items()
                    if all((run.get("identity") or {}).get(field) == metadata.get(field) for field in fields)
                    and (Path(run["path"])/"training-statistics"/self.cohort).is_dir()}
                if matching:
                    versions = [re.search(r"-v(\d+)$", (run.get("identity") or {}).get("schema", "")) for run in matching.values()]
                    rank = max((int(version[1]) for version in versions if version), default=0)
                    populations.append((rank, metadata, matching))
            if populations:
                _, metadata, runs = max(populations, key=lambda population:population[0])
                if metadata != self.metadata:
                    self.metadata, self.stamp = metadata, None
        candidates = []
        for run_id, run in runs.items():
            folder = Path(run["path"])/"training-statistics"
            if not folder.is_dir() or folder.is_symlink(): continue
            if self.cohort:
                folder = folder/self.cohort
                if not folder.is_dir() or folder.is_symlink(): continue
            for path in folder.glob("session-*.json"):
                if path.is_symlink() or path.name.endswith(".error.json"): continue
                stat = path.stat()
                candidates.append((path, run_id, run, stat))
        stamp = tuple(sorted((str(path), st.st_ino, st.st_size, st.st_mtime_ns,
            tuple((run.get("identity") or {}).get(key) for key in
                  ("host_binary_sha256", "rules_sha256", "catalog_sha256", "observation_schema")))
            for path, _, run, st in candidates))
        if stamp == self.stamp:
            return self.result
        sessions, excluded, seen = [], [], set()
        for path, run_id, run, _ in candidates:
            raw = load(path)
            identity = run.get("identity") or {}
            if raw.get("schema") != RAW_SCHEMA: continue
            setup = raw.get("hero_setup")
            if (self.cohort is None and setup is not None or self.cohort is not None and
                    (not isinstance(setup, dict) or setup.get("cohort") != self.cohort)):
                excluded.append({"file": str(path), "reason": "hero_setup_cohort_mismatch"})
                continue
            if (raw.get("purpose") != "training_pool" or raw.get("host_binary_sha256") != identity.get("host_binary_sha256")
                    or raw.get("observation_schema") != identity.get("observation_schema")
                    or any(identity.get(key) != self.metadata.get(key) for key in ("rules_sha256", "catalog_sha256", "observation_schema"))):
                excluded.append({"file": str(path), "reason": "runtime_or_rules_identity_mismatch"})
                continue
            if raw["session_id"] in seen:
                raise ValueError("Duplicate training statistics session")
            seen.add(raw["session_id"])
            sessions.append((raw.get("published_utc", ""), path, run_id, raw))
        if not sessions:
            self.stamp, self.result = stamp, None
            self.error = "All training statistics sources failed runtime/rules identity validation" if excluded else None
            return None
        windows, remaining = [], self.target_games
        for _, path, run_id, raw in sorted(sessions, reverse=True):
            if remaining <= 0: break
            desired_anchor = max(0, raw["totals"]["completed_games"]-remaining)
            anchors = []
            folder = path.parent/"history"
            if desired_anchor and folder.is_dir() and not folder.is_symlink():
                for history in folder.glob("*.json"):
                    if history.is_symlink() or raw["session_id"] not in history.name: continue
                    match = re.search(r"completed-(\d+)-seq-(\d+)\.json$", history.name)
                    if match and int(match[1]) <= desired_anchor:
                        anchors.append((int(match[1]), int(match[2]), history))
            anchor_info = max(anchors) if anchors else None
            anchor_path = anchor_info[2] if anchor_info else None
            anchor = load(anchor_path) if anchor_path else None
            if anchor_info and (anchor["totals"]["completed_games"] != anchor_info[0]
                                or anchor.get("publication_sequence") != anchor_info[1]):
                raise ValueError("History filename disagrees with its snapshot payload")
            window = delta(raw, anchor)
            window["source_file"], window["anchor_file"], window["run_id"] = str(path), str(anchor_path) if anchor_path else None, run_id
            windows.append(window)
            remaining -= window["totals"]["completed_games"]
        windows.reverse()
        result = combine(windows, self.metadata, run_id=windows[-1]["run_id"], target_games=self.target_games)
        all_time_completed = sum(raw["totals"]["completed_games"] for _, _, _, raw in sessions)
        result["refresh"].update(total_games=all_time_completed, snapshot_training_games=all_time_completed)
        result["source_publication_utc"] = max(window["published_utc"] for window in windows)
        result["updated_wall"] = dt.datetime.fromisoformat(result["source_publication_utc"].replace("Z", "+00:00")).timestamp()
        result["source_files"] = [{key: window[key] for key in ("source_file", "anchor_file", "run_id")} for window in windows]
        result["excluded_sources"] = excluded
        choice_runs = {window["run_id"]: runs[window["run_id"]]["identity"]["choice_learning"]
                       for window in windows if (runs[window["run_id"]].get("identity") or {}).get("choice_learning")}
        if choice_runs:
            result["scope"]["choice_learning_by_run"] = choice_runs
            result["scope"]["notes"].append("Policies include declared legal relic/destiny exploration mixtures; these are recorded policy choices, not assigned-relic evaluation games.")
            result["insights"].append("Targeted legal relic/destiny exploration is enabled; acquisition associations still mix changing learners and archive policies.")
        if self.cohort:
            label = "Uniform random hero assignments" if self.cohort == "forced-random" else "Normal hero drafts"
            result["scope"].update(label=label, hero_setup_cohort=self.cohort)
            result["scope"]["notes"].insert(0, "Only " + label.lower() + " from the mixed training curriculum are included. Other setup modes are kept separate.")
            if self.cohort == "natural-draft":
                warning="Natural-draft hero outcomes mix learner and archive players without per-player policy attribution. Rare heroes can predominantly be chosen by weak archived policies; these scores do not estimate equal-skill hero strength."
                result["scope"]["notes"].append(warning)
                result["insights"].insert(0,warning)
        destination = self.campaign/("balance-statistics-"+self.cohort+".json" if self.cohort else "balance-statistics.json")
        temporary = destination.with_suffix(".json.tmp")
        temporary.write_text(json.dumps(result, allow_nan=False, separators=(",", ":"))+"\n")
        temporary.replace(destination)
        self.stamp, self.result = stamp, result
        self.error = "Some training statistics sources failed runtime/rules identity validation" if excluded else None
        return result
