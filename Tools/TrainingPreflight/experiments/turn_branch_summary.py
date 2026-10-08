"""Summarize offline turn-branch measurements and exact-key replay transcripts."""
import argparse
import collections
import json
import math
from pathlib import Path


def distribution(values):
    values = sorted(values)
    return {"count": len(values), "min": values[0], "median": values[len(values) // 2],
            "p90": values[int(len(values) * .9)], "max": values[-1], "sum": sum(values)}


def trace_summary(rows):
    starts = {}
    turns = collections.Counter()
    for row in rows:
        turns[row["game"], row["round"], row["turn"]] += 1
        if row["context"] is None and row["seat"] == row["turn"]:
            starts.setdefault((row["game"], row["round"], row["seat"]), row)
    searched = [r for r in rows if r.get("search")]
    duplicated_roots = duplicate_searches = crowded_searches = total_roots = 0
    for row in searched:
        choices = [row["legal"][c["Action"]] for c in row["search"]["choices"]]
        names = [c["name"] if c["type"] == "ShardsPlayCardAction" else c["key"] for c in choices]
        duplicate = len(names) - len(set(names))
        total_roots += len(names)
        duplicated_roots += duplicate
        duplicate_searches += duplicate > 0
        crowded_searches += duplicate > 0 and len(row["legal"]) > 8
    return {
        "games": len({r["game"] for r in rows}), "decisions": len(rows), "turn_starts": len(starts),
        "hand_size": distribution([len(r["hand"]) for r in starts.values()]),
        "initial_hand_permutations": distribution([math.factorial(len(r["hand"])) for r in starts.values()]),
        "initial_hand_multiset_permutations": distribution([
            math.factorial(len(r["hand"])) // math.prod(math.factorial(n) for n in
                collections.Counter(c["id"] for c in r["hand"]).values()) for r in starts.values()]),
        "legal_actions": distribution([len(r["legal"]) for r in rows]),
        "decisions_per_turn": distribution([turns[key] for key in starts]),
        "selected_actions": dict(collections.Counter(r["selected"] for r in rows)),
        "searches": len(searched), "total_searched_root_actions": total_roots,
        "same_named_play_roots_removed": duplicated_roots,
        "searches_with_same_named_play_roots": duplicate_searches,
        "crowded_searches_with_same_named_play_roots": crowded_searches,
        "searches_with_different_turn_players_at_leaves": sum(
            len({x["turn"] for x in r["search"]["leaves"]}) > 1 for r in searched),
    }


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--audit", type=Path, required=True)
    parser.add_argument("--reviews", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    audit = json.loads(args.audit.read_text())
    rows = [json.loads(line) for path in sorted(args.reviews.glob("game-*.jsonl")) for line in path.open()]
    rows = [r for r in rows if "legal" in r]
    assert all("selectedKey" in r and r["selectedKey"] == r["legal"][r["selectedIndex"]]["key"] for r in rows)
    modes = {}
    for mode in [t["mode"] for t in audit["rows"][0]["tests"]]:
        tests = [t for r in audit["rows"] for t in r["tests"] if t["mode"] == mode]
        modes[mode] = {
            "positions": len(tests), "node_capped": sum(t["capped"] for t in tests),
            "depth_capped": sum(t["boundaries"].get("depth-cap", 0) > 0 for t in tests),
            "nodes": distribution([t["nodes"] for t in tests]),
            "milliseconds": distribution([t["milliseconds"] for t in tests]),
            "leaves": distribution([t["leaves"] for t in tests]),
            "boundaries": dict(sum((collections.Counter(t["boundaries"]) for t in tests), collections.Counter())),
        }
    coverage_games = {1283, 1313, 1338, 1402, 1418}
    result = {"all_eight_review_games": trace_summary(rows),
              "five_hero_coverage_games": trace_summary([r for r in rows if r["game"] in coverage_games]),
              "enumeration": modes, "audit_seconds": audit["seconds"],
              "caveat": "Small selected review sample, not a random population estimate. Capped trees are lower bounds; identical-name counts do not prove interchangeability."}
    args.output.write_text(json.dumps(result, indent=2) + "\n")
    print(json.dumps({"enumeration": modes, "decisions": len(rows)}, indent=2))


if __name__ == "__main__":
    main()
