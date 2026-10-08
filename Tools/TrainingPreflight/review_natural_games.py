"""Screen completed headless transcripts; flags require counterfactual review.

Writes exact-key positions for PositionReview, including relic/destiny choices
that have the same display name. Never changes or publishes game statistics.
"""
import argparse
import collections
import json
from pathlib import Path


def review(directory):
    records = json.loads((directory / "games.json").read_text())["rows"]
    counts = collections.Counter()
    positions, cancellations, banishes, longshots, warnings = [], [], [], [], []
    deferred_paid_banishes = []
    endings = {
        "ShardsPlayCardAction", "ShardsExhaustAction", "ShardsHeroAbilityAction",
        "ShardsFocusAction", "ShardsRecruitRelicAction", "ShardsTakeDestinyAction",
    }
    for spec in records:
        transcript = directory / "reviews" / f"game-{spec['index']}.jsonl"
        data = [json.loads(line) for line in transcript.read_text().splitlines()]
        final = data[-1]
        rows = [row for row in data if "legal" in row]
        if (final.get("finished") is not True or final["winner"] != spec["winner"]
                or len(rows) != spec["steps"]
                or [row["step"] for row in rows] != list(range(len(rows)))):
            raise ValueError(f"Incomplete or inconsistent transcript: {transcript}")
        counts["games"] += 1
        counts["actions"] += len(rows)
        # Damage responses can temporarily switch the actor within a turn.
        # Use turn/round, not actor, to identify the actual last turn.
        last_turn = (rows[-1]["round"], rows[-1]["turn"])
        declined_banishes = []
        for index, row in enumerate(rows):
            location = {"game": row["game"], "step": row["step"]}
            turn = (row["round"], row["turn"], row["seat"])
            # Match the exact instance, not merely another copy of the same card.
            # This is a review flag: intervening triggers can justify a delay.
            if row["context"] == "soi.banish" and row["selected"] == "kind:13":
                previous = rows[index - 1]["selected"] if index else None
                if previous != "hero:kosynwu":
                    identities = {tuple(action["key"].split(":")[4:])
                                  for action in row["legal"]
                                  if action["key"].startswith("12:")}
                    declined_banishes.append((turn, row["step"], previous, identities))
            if row["selected"] == "ShardsEndTurnAction":
                counts["end_turns"] += 1
                winning = (row["round"], row["turn"]) == last_turn and final["winner"] == row["seat"]
                for action in row["legal"]:
                    if action["type"] in endings:
                        if action.get("inactive", False):
                            counts["certified_inactive_end_alternatives"] += 1
                            continue
                        positions.append({**location, "keys": [action["key"]],
                                          "description": action["name"],
                                          "winning_final_turn": winning})
                        if not winning:
                            counts["nonwinning_end_alternatives"] += 1
            if row["selected"] == "hero:kosynwu" and index + 2 < len(rows):
                menu, after = rows[index + 1:index + 3]
                if menu["context"] != "soi.banish":
                    raise ValueError(f"Unexpected Ko continuation: {location}")
                counts["ko_activations"] += 1
                if menu["selectedKey"].startswith("12:"):
                    identity = tuple(menu["selectedKey"].split(":")[4:])
                    for earlier_turn, earlier_step, source, identities in declined_banishes:
                        if earlier_turn == turn and identity in identities:
                            deferred_paid_banishes.append({
                                **location, "declined_step": earlier_step,
                                "declined_source": source, "target": menu["selected"],
                                "target_identity": identity,
                                "hp_before": row["hp"], "hp_after": after["hp"],
                            })
                if menu["selected"] == "kind:13":
                    counts["ko_cancellations"] += 1
                    cancellations.append({**location, "round": row["round"],
                                          "hp_before": row["hp"], "hp_after": after["hp"],
                                          "next": after["selected"]})
                elif menu["selected"] not in ("crystal", "blaster"):
                    banishes.append({**location, "target": menu["selected"],
                                     "mastery": row["mastery"], "hp": row["hp"]})
            if row["selected"] == "play:longshot" and row["hero"] == "rez":
                longshots.append({**location, "mastery": row["mastery"],
                                  "scry_used": row["HeroAbilityUsedThisTurn"],
                                  "known_center_top": row["knownCenterTop"]})
            if row["context"] == "soi.volos":
                counts["volos:" + row["selected"]] += 1
            warnings.extend({**location, "finding": finding} for finding in row["findings"])
    summary = {"counts": dict(counts), "ko_cancellations": cancellations,
               "declined_banish_then_paid_for_same_card": deferred_paid_banishes,
               "nonstarter_ko_banishes": banishes, "rez_longshots": longshots,
               "recorder_warnings": warnings,
               "limitation": "Screening only: legal alternatives and unusual actions are not proof of mistakes."}
    for name, data in [("replay-outcomes.json", records),
                       ("expanded-audit-positions.json", positions),
                       ("expanded-audit-screen.json", summary)]:
        (directory / name).write_text(json.dumps(data, indent=2) + "\n")
    return summary


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("directory", type=Path)
    args = parser.parse_args()
    print(json.dumps(review(args.directory)["counts"], indent=2))
