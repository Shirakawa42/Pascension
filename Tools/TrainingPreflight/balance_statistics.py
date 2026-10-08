"""Batched descriptive balance statistics, outside the training process.

Only completed frozen CPU reports with explicit per-game observations qualify.
Player A is tracked; the other side supplies its opponent, not a second sample.
Intervals group the two seat-swapped games by seed. They describe conditional
outcomes after a choice, never a causal card effect or an equilibrium ranking.
"""
from __future__ import annotations

import argparse
from collections import Counter, defaultdict
import hashlib
import json
import math
from pathlib import Path
import time


HERE = Path(__file__).resolve().parent
SCHEMA = "shards-balance-statistics-v1"
CATALOG = HERE/"results/balance-card-catalog.json"


def paired_ratio_interval(success, total, clusters, unresolved=0, alpha=.05):
    """Bound a ratio of bounded paired-cluster means; include unknown outcomes.

    Within a selected seed cluster there are one or two qualifying games.
    Normalize score sum and game count by two: Y in [0,1], D in [.5,1].
    A union bound over two two-sided Hoeffding intervals bounds E[Y]/E[D].
    Conditioning on inclusion makes these choice associations, not treatment
    effects. Across versions the target is the sampled mixture. These pointwise
    bounds do not correct for ranking many cards or repeated dashboard views.
    """
    if not total or not clusters:
        return None
    if not clusters <= total <= 2*clusters or not 0 <= success <= total-unresolved:
        raise ValueError("Invalid paired-cluster accounting")
    margin = math.sqrt(math.log(4/alpha)/(2*clusters))
    count_mean = total/(2*clusters)
    low_score = success/(2*clusters)
    high_score = (success+unresolved)/(2*clusters)
    lower = max(0., (low_score-margin)/(count_mean+.5*margin))
    upper = 1. if count_mean <= .5*margin else min(1., (high_score+margin)/(count_mean-.5*margin))
    return [lower, upper]


class Outcomes:
    def __init__(self):
        self.wins = self.draws = self.losses = self.censored = 0
        self.clusters = set()
        self.round_sum = self.decision_sum = 0.
        self.round_count = self.decision_count = 0

    def add(self, game, cluster):
        outcome = game.get("outcome")
        self.clusters.add(cluster)
        if game.get("censored") or outcome is None:
            self.censored += 1
            return
        if outcome not in (-1, 0, 1):
            raise ValueError("Invalid terminal utility")
        self.wins += outcome == 1
        self.draws += outcome == 0
        self.losses += outcome == -1
        rounds = game.get("last_observed_round")
        if isinstance(rounds, (int, float)) and math.isfinite(rounds):
            self.round_sum += rounds
            self.round_count += 1
        decisions = game.get("wrapper_decisions")
        if isinstance(decisions, (int, float)) and math.isfinite(decisions):
            self.decision_sum += decisions
            self.decision_count += 1

    def result(self):
        resolved = self.wins+self.draws+self.losses
        total = resolved+self.censored
        success = self.wins+.5*self.draws
        return {"games": resolved, "wins": self.wins, "draws": self.draws, "losses": self.losses,
            "censored_games": self.censored, "paired_clusters": len(self.clusters),
            "score": success/resolved if resolved and not self.censored else None,
            "score_resolved_only": success/resolved if resolved else None,
            "score_identification_interval": [success/total, (success+self.censored)/total] if total else None,
            "score_bound_95": paired_ratio_interval(success, total, len(self.clusters), self.censored),
            "mean_last_observed_round": self.round_sum/self.round_count if self.round_count else None,
            "mean_wrapper_decisions": self.decision_sum/self.decision_count if self.decision_count else None}


def build_statistics(reports, metadata, *, run_id=None, every_games=100000,
                     training_games=0, snapshot_training_games=None, expected_identity=None):
    cards = {card["card_id"]: card for card in metadata["cards"]}
    ordered_ids = [cards[i]["id"] for i in range(1, len(cards)+1)]
    hero_names = {row["id"]: row["name"] for row in metadata["heroes"]}
    heroes = {hero: Outcomes() for hero in hero_names}
    hero_seats, matchups, choices = {}, {}, {}
    all_games = Outcomes()
    versions, opponents, sources, seen_games, seed_cohorts = set(), set(), [], set(), {}
    seat_wins = Counter()
    selected_games = Counter()
    last_public = defaultdict(list)
    excluded = []
    for report_name, report in reports:
        observations = report.get("balance_observations", {})
        if not report.get("complete") or observations.get("schema") != "shards-balance-observations-v1":
            continue
        if report.get("catalog", {}).get("cards") != ordered_ids:
            excluded.append({"report": report_name, "reason": "catalog_mismatch"})
            continue
        a, b = report["policy_a"], report["policy_b"]
        if expected_identity is not None:
            fields = ("rules_sha256", "catalog_sha256", "observation_schema")
            if any(not expected_identity.get(field) or policy.get("run_identity", {}).get(field) != expected_identity[field]
                   for policy in (a, b) for field in fields):
                excluded.append({"report": report_name, "reason": "rules_or_observation_identity_mismatch"})
                continue
        # Keep CPU panels distinct from any future GPU/forced-draft series.
        if report.get("execution", {}).get("device") != "cpu" or report.get("intervention"):
            excluded.append({"report": report_name, "reason": "different_evaluation_scope"})
            continue
        cohort = (a["policy_sha256"], b["policy_sha256"], report["configuration"]["sampling_seed"])
        report_seeds = {game["paired_seed"] for game in observations.get("games", []) if game.get("policy") == "a"}
        if any(seed in seed_cohorts and seed_cohorts[seed] != cohort for seed in report_seeds):
            excluded.append({"report": report_name, "reason": "engine_seed_reused_across_cohorts"})
            continue
        seed_cohorts.update({seed: cohort for seed in report_seeds})
        versions.add(a["version"])
        opponents.add((b["role"], b["version"]))
        sources.append(report_name)
        for game in observations.get("games", []):
            if game.get("policy") != "a":
                continue
            key = (*cohort, game["paired_seed"], game["seat"])
            if key in seen_games:
                continue
            seen_games.add(key)
            cluster = (*cohort, game["paired_seed"])
            all_games.add(game, cluster)
            hero, opponent = game.get("hero"), game.get("opponent_hero")
            if hero in heroes:
                heroes[hero].add(game, cluster)
                hero_seats.setdefault((hero, game["seat"]), Outcomes()).add(game, cluster)
                if opponent in heroes:
                    matchups.setdefault((hero, opponent), Outcomes()).add(game, cluster)
            if not game.get("censored") and game.get("outcome") in (-1, 1):
                winning_seat = game["seat"] if game["outcome"] == 1 else 1-game["seat"]
                seat_wins[winning_seat] += 1
            kinds = set()
            unique_choices = set()
            for choice in game.get("choices", []):
                card_id, kind = choice["card_id"], choice["choice_kind"]
                if card_id not in cards or kind not in ("buy", "fastplay", "relic", "destiny"):
                    raise ValueError("Unrecognized choice metadata")
                ck = (card_id, kind)
                if ck in unique_choices:
                    raise ValueError("Duplicate choice summary within a game")
                unique_choices.add(ck)
                row = choices.setdefault(ck, {"outcomes": Outcomes(), "exposed_games": 0,
                    "opportunities": 0, "pick_count": 0, "round_sum": 0., "round_count": 0})
                menus, picks = choice.get("opportunity_menus", 0), choice.get("pick_count", 0)
                if picks < 0 or menus < picks:
                    raise ValueError("Picks must be covered by legal-menu opportunities")
                row["opportunities"] += menus
                row["exposed_games"] += menus > 0
                row["pick_count"] += picks
                if picks:
                    row["outcomes"].add(game, cluster)
                    kinds.add(kind)
                    if choice.get("acquisition_round_sum") is not None:
                        row["round_sum"] += choice["acquisition_round_sum"]
                        row["round_count"] += picks
            selected_games.update(kinds)
            if not game.get("censored"):
                public = game.get("last_observed_public", {})
                for field in ("mastery", "health", "deck_count", "hand_count"):
                    value = public.get(field)
                    if isinstance(value, (int, float)) and math.isfinite(value):
                        last_public[field].append(value)
    rankings = {"heroes": [{"id": hero, "name": name, **heroes[hero].result()}
                           for hero, name in hero_names.items()], "cards": [], "relics": [], "destinies": []}
    for card_id, card in cards.items():
        category = {"Relic": "relics", "Destiny": "destinies"}.get(card["type"], "cards")
        default_kind = {"relics": "relic", "destinies": "destiny"}.get(category, "buy")
        kinds = {kind for cid, kind in choices if cid == card_id} | {default_kind}
        if card["type"] == "Mercenary":
            kinds.add("fastplay")
        for kind in sorted(kinds):
            data = choices.get((card_id, kind))
            outcome = data["outcomes"].result() if data else Outcomes().result()
            row = {**card, "id": card["id"]+":"+kind, "choice_kind": kind,
                "acquisition_mode": kind, **outcome, "exposed_games": data["exposed_games"] if data else 0,
                "pick_count": data["pick_count"] if data else 0,
                "opportunities": data["opportunities"] if data else 0,
                "pick_rate": data["pick_count"]/data["opportunities"] if data and data["opportunities"] else None,
                "mean_acquisition_round": data["round_sum"]/data["round_count"] if data and data["round_count"] else None}
            rankings[category].append(row)
    totals = all_games.result()
    total_games = totals["games"]+totals["censored_games"]
    observed_heroes = sum(row["games"]+row["censored_games"] > 0 for row in rankings["heroes"])
    snapshot_training_games = training_games if snapshot_training_games is None else snapshot_training_games
    return {"schema": SCHEMA, "state": "ready" if total_games else "awaiting_samples", "updated_wall": time.time(),
        "snapshot_id": hashlib.sha256(json.dumps(sources).encode()).hexdigest()[:16],
        "scope": {"run_id": run_id, "policy_label": "Recent frozen learner A versus champion / fixed anchor",
            "policy_versions": sorted(versions), "opponents": [{"role": role, "version": version} for role, version in sorted(opponents)],
            "inference": "CPU IEEE FP32; sampled policies", "sample_unit": "completed learner-perspective evaluation game",
            "confidence_method": "Pointwise conservative 95% bounds across paired engine seeds; unknown censored outcomes included",
            "notes": ["Choice-conditioned outcomes are associations, not causal strength or balance estimates.",
                "Only selected legal buy, fastplay, relic and destiny actions are counted; effect-driven gains are not inferred.",
                "Pick rate is selections per legal decision menu, not per shop appearance; menus repeat during turns.",
                "Unobserved cards or heroes are unknown. Older replaced definitions may remain in the catalog.",
                "Rankings combine the listed recent learner/opponent versions. Intervals are not corrected for multiple rankings or repeated views.",
                "Round/public scalars are latest pre-action observations from either player; own collection is last seen at that player's decision. Neither is final engine state.",
                "Reports must match the current rules/observation identity; overlapping engine seeds from different cohorts are excluded."]},
        "refresh": {"every_games": every_games, "games_since_snapshot": max(0, training_games-snapshot_training_games),
            "total_games": training_games, "snapshot_training_games": snapshot_training_games,
            "evaluation_sample_count": total_games},
        "totals": {"attempted_games": total_games, "resolved_games": totals["games"],
            "censored_games": totals["censored_games"], "seat0_wins": seat_wins[0], "seat1_wins": seat_wins[1],
            "draws": totals["draws"], "mean_rounds": totals["mean_last_observed_round"],
            "mean_wrapper_decisions": totals["mean_wrapper_decisions"], "score": totals["score"],
            "score_bound_95": totals["score_bound_95"]}, "rankings": rankings,
        "hero_seats": [{"id": hero+":"+str(seat), "hero_id": hero, "name": hero_names[hero], "seat": seat, **outcomes.result()}
                       for (hero, seat), outcomes in sorted(hero_seats.items())],
        "hero_matchups": [{"id": hero+":"+opponent, "hero_a": hero, "hero_b": opponent, **outcomes.result()}
                          for (hero, opponent), outcomes in sorted(matchups.items())],
        "insights": [f"Observed {observed_heroes} of {len(hero_names)} heroes across {total_games:,} evaluation games.",
            *[f"{kind}: chosen in {count:,} learner games." for kind, count in sorted(selected_games.items())],
            *[f"Mean last-observed {field.replace('_', ' ')}: {sum(values)/len(values):.1f}." for field, values in sorted(last_public.items()) if values]],
        "source_reports": sources, "excluded_reports": excluded}


def publish(campaign, run_dir, *, every_games=100000, max_reports=16,
            output=None, snapshot_training_games=None, reports_dir=None):
    campaign, run_dir = Path(campaign), Path(run_dir)
    reports = []
    folder = Path(reports_dir) if reports_dir is not None else campaign/"evaluations"
    candidates = sorted(folder.glob("cpu-shadow-*.json"), key=lambda p: p.stat().st_mtime, reverse=True)
    for path in candidates:
        if path.stat().st_size > 64*1024*1024:
            continue
        report = json.loads(path.read_text())
        if report.get("complete") and report.get("balance_observations", {}).get("schema") == "shards-balance-observations-v1":
            reports.append((path.name, report))
        if len(reports) == max_reports:
            break
    status = json.loads((run_dir/"status.json").read_text())
    identity = json.loads((run_dir/"identity.json").read_text())
    catalog_path = CATALOG.with_name("balance-card-catalog-v6.json") if identity.get("schema") in ("shards-real-selfplay-v6", "shards-real-selfplay-v7") else CATALOG
    metadata = json.loads(catalog_path.read_text())
    result = build_statistics(list(reversed(reports)), metadata, run_id=run_dir.name,
                              every_games=every_games, training_games=status.get("games", 0),
                              snapshot_training_games=snapshot_training_games, expected_identity=identity)
    result["window"] = {"max_reports": max_reports, "reports": len(reports)}
    destination = Path(output) if output is not None else campaign/"balance-statistics-frozen.json"
    if destination.resolve().parent != campaign.resolve() or destination.name not in (
            "balance-statistics-frozen.json", "balance-statistics.json"):
        raise ValueError("Statistics output must be a campaign statistics sidecar")
    temporary = destination.with_suffix(".json.tmp")
    temporary.write_text(json.dumps(result, allow_nan=False, separators=(",", ":"))+"\n")
    temporary.replace(destination)
    return result


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--campaign-dir", type=Path, required=True)
    parser.add_argument("--run-dir", type=Path, required=True)
    parser.add_argument("--every-games", type=int, default=100000)
    parser.add_argument("--max-reports", type=int, default=16)
    parser.add_argument("--output", type=Path)
    parser.add_argument("--snapshot-training-games", type=int)
    parser.add_argument("--reports-dir", type=Path)
    args = parser.parse_args()
    if args.every_games not in (0,) and args.every_games < 1000 or not 1 <= args.max_reports <= 64:
        parser.error("Require every-games0 or >=1000 and max-reports1..64")
    report = publish(args.campaign_dir, args.run_dir, every_games=args.every_games, max_reports=args.max_reports,
                     output=args.output, snapshot_training_games=args.snapshot_training_games,
                     reports_dir=args.reports_dir)
    print(json.dumps({"state": report["state"], "totals": report["totals"], "reports": report["window"]["reports"]}))
