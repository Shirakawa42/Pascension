"""Read-only statistical aggregation contracts; no model, engine or GPU work."""
import copy
import json
import math
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

import balance_statistics as stats

IDENTITY = {"rules_sha256": "a"*64, "catalog_sha256": "b"*64,
            "observation_schema": "shards-observation-v3"}

METADATA = {"cards": [
    {"card_id": 1, "id": "mercenary", "name": "Mercenary", "type": "Mercenary"},
    {"card_id": 2, "id": "relic", "name": "Relic", "type": "Relic"},
    {"card_id": 3, "id": "destiny", "name": "Destiny", "type": "Destiny"},
    {"card_id": 4, "id": "unseen_card", "name": "Unseen", "type": "Ally"}],
    "heroes": [{"id": hero, "name": hero.title()} for hero in ("decima", "tetra", "volos", "kosynwu", "rez")]}


def choice(card_id=1, kind="buy", picks=2, menus=5, rounds=8):
    return {"card_id": card_id, "choice_kind": kind, "pick_count": picks,
            "opportunity_menus": menus, "first_selected_round": 3 if picks else None,
            "acquisition_round_sum": rounds if picks else 0}


def game(seed=100, seat=0, outcome=1, policy="a", choices=None, hero="decima"):
    return {"paired_seed": seed, "policy": policy, "seat": seat, "hero": hero,
        "opponent_hero": "tetra", "outcome": outcome, "censored": outcome is None,
        "last_observed_round": 20, "wrapper_decisions": 400,
        "choices": [choice()] if choices is None else choices,
        "last_observed_public": {"mastery": 18, "health": 25, "deck_count": 14, "hand_count": 5}}


def report(games):
    return {"complete": True, "catalog": {"cards": [row["id"] for row in METADATA["cards"]]},
        "policy_a": {"policy_sha256": "a-hash", "version": 123, "role": "learner", "run_identity": copy.deepcopy(IDENTITY)},
        "policy_b": {"policy_sha256": "b-hash", "version": 99, "role": "champion", "run_identity": copy.deepcopy(IDENTITY)},
        "configuration": {"sampling_seed": 12345}, "execution": {"device": "cpu"},
        "balance_observations": {"schema": "shards-balance-observations-v1", "games": games}}


def row(result, identity, category="cards"):
    return next(value for value in result["rankings"][category] if value["id"] == identity)


class BalanceStatisticsTests(unittest.TestCase):
    def test_paired_ratio_uses_clusters_and_bounded_denominator(self):
        clusters, total, success = 1000, 1500, 1000
        margin = math.sqrt(math.log(80)/(2*clusters))
        expected = [(success/(2*clusters)-margin)/(total/(2*clusters)+margin/2),
                    (success/(2*clusters)+margin)/(total/(2*clusters)-margin/2)]
        self.assertEqual(stats.paired_ratio_interval(success, total, clusters), expected)
        paired = stats.paired_ratio_interval(1000, 2000, 1000)
        twice_the_independent_pairs = stats.paired_ratio_interval(2000, 4000, 2000)
        self.assertGreater(paired[1]-paired[0], twice_the_independent_pairs[1]-twice_the_independent_pairs[0])
        self.assertIsNone(stats.paired_ratio_interval(0, 0, 0))
        with self.assertRaises(ValueError):
            stats.paired_ratio_interval(1, 3, 1)

    def test_duplicate_reports_deduplicate_games_and_player_b_is_excluded(self):
        data = report([game(), game(seat=1, outcome=-1), game(policy="b"), game(policy="b", seat=1)])
        result = stats.build_statistics([("one.json", data), ("copied.json", copy.deepcopy(data))], METADATA)
        self.assertEqual(result["totals"]["attempted_games"], 2)
        self.assertEqual(result["totals"]["resolved_games"], 2)
        self.assertEqual(result["totals"]["score"], .5)
        selected = row(result, "mercenary:buy")
        self.assertEqual((selected["games"], selected["wins"], selected["losses"], selected["paired_clusters"]), (2, 1, 1, 1))
        self.assertEqual((selected["pick_count"], selected["opportunities"]), (4, 10))
        self.assertEqual(result["totals"]["seat0_wins"], 2)

    def test_repeated_picks_count_actions_but_outcomes_once_per_game(self):
        choices = [choice(picks=3, menus=7, rounds=18), choice(kind="fastplay", picks=1, menus=2, rounds=9),
                   choice(2, "relic", 1, 3, 7), choice(3, "destiny", 0, 8, 0)]
        result = stats.build_statistics([("one.json", report([game(choices=choices)]))], METADATA)
        bought = row(result, "mercenary:buy")
        self.assertEqual((bought["games"], bought["pick_count"], bought["opportunities"], bought["exposed_games"]), (1, 3, 7, 1))
        self.assertEqual(bought["pick_rate"], 3/7)
        self.assertEqual(bought["mean_acquisition_round"], 6)
        self.assertEqual(row(result, "mercenary:fastplay")["pick_count"], 1)
        self.assertEqual(row(result, "relic:relic", "relics")["games"], 1)
        self.assertEqual(row(result, "destiny:destiny", "destinies")["games"], 0)
        self.assertIsNone(row(result, "destiny:destiny", "destinies")["score"])

    def test_censored_outcomes_remain_unknown_and_enlarge_identification_interval(self):
        result = stats.build_statistics([("one.json", report([game(), game(seat=1, outcome=None)]))], METADATA)
        selected = row(result, "mercenary:buy")
        self.assertEqual((selected["games"], selected["wins"], selected["draws"], selected["censored_games"]), (1, 1, 0, 1))
        self.assertEqual(selected["score_identification_interval"], [.5, 1.])
        self.assertIsNone(selected["score"])
        self.assertEqual(selected["score_resolved_only"], 1.)
        self.assertIsNone(result["totals"]["score"])
        self.assertLessEqual(selected["score_bound_95"][0], .5)
        self.assertGreaterEqual(selected["score_bound_95"][1], 1.)
        only_cap = stats.build_statistics([("cap.json", report([game(outcome=None)]))], METADATA)
        self.assertIsNone(row(only_cap, "mercenary:buy")["score"])
        self.assertEqual(row(only_cap, "mercenary:buy")["score_identification_interval"], [0., 1.])
        self.assertEqual(only_cap["totals"]["draws"], 0)

    def test_unseen_catalog_entries_are_zero_coverage_and_unknown(self):
        result = stats.build_statistics([("one.json", report([game()]))], METADATA)
        for value in (row(result, "unseen_card:buy"), row(result, "rez", "heroes"),
                      row(result, "relic:relic", "relics")):
            self.assertEqual(value["games"], 0)
            self.assertEqual(value["censored_games"], 0)
            self.assertIsNone(value["score"])
            self.assertIsNone(value["score_bound_95"])
        self.assertIsNone(row(result, "unseen_card:buy")["pick_rate"])
        json.dumps(result, allow_nan=False)

    def test_catalog_intervention_and_device_mismatch_are_excluded(self):
        variants = [report([game()]) for _ in range(4)]
        variants[0]["catalog"]["cards"].reverse()
        variants[1]["intervention"] = "forced_hero_draft"
        variants[2]["execution"]["device"] = "cuda"
        variants[3]["complete"] = False
        result = stats.build_statistics([(str(i), value) for i, value in enumerate(variants)], METADATA)
        self.assertEqual(result["state"], "awaiting_samples")
        self.assertEqual(result["totals"]["attempted_games"], 0)
        self.assertEqual(len(result["excluded_reports"]), 3)

    def test_disjoint_seeds_allow_new_frozen_policy_cohorts(self):
        first, second = report([game()]), report([game(seed=101, outcome=-1)])
        second["policy_a"]["policy_sha256"] = "different-frozen-policy"
        second["policy_a"]["version"] = 124
        result = stats.build_statistics([("first", first), ("second", second)], METADATA)
        self.assertEqual(row(result, "mercenary:buy")["paired_clusters"], 2)
        self.assertEqual(result["scope"]["policy_versions"], [123, 124])
        self.assertEqual(result["totals"]["attempted_games"], 2)
        self.assertEqual(result["excluded_reports"], [])

    def test_seed_reuse_with_changed_policy_or_sampling_excludes_whole_report(self):
        for field in ("policy_a", "policy_b", "sampling_seed"):
            with self.subTest(field=field):
                first = report([game(seed=100), game(seed=100, seat=1)])
                second = report([game(seed=100), game(seed=200)])
                if field == "sampling_seed":
                    second["configuration"][field] += 1
                else:
                    second[field]["policy_sha256"] = "changed-policy"
                # Reject the entire second report, not only its overlapping seed.
                result = stats.build_statistics([("first", first), ("overlap", second)], METADATA,
                                                expected_identity=IDENTITY)
                self.assertEqual(result["totals"]["attempted_games"], 2)
                self.assertEqual(result["source_reports"], ["first"])
                self.assertEqual(result["excluded_reports"], [{"report": "overlap", "reason": "engine_seed_reused_across_cohorts"}])
                # A rejected report must not reserve its otherwise fresh seeds.
                third = report([game(seed=200)])
                third["policy_a"]["policy_sha256"] = "third-policy"
                result = stats.build_statistics([("first", first), ("overlap", second), ("fresh", third)], METADATA,
                                                expected_identity=IDENTITY)
                self.assertEqual(result["totals"]["attempted_games"], 3)
                self.assertEqual(result["source_reports"], ["first", "fresh"])

    def test_identity_requires_matching_rules_catalog_and_observation_for_both_policies(self):
        for role in ("policy_a", "policy_b"):
            for field in IDENTITY:
                with self.subTest(role=role, field=field):
                    changed = report([game()])
                    changed[role]["run_identity"][field] = "different"
                    result = stats.build_statistics([("changed", changed)], METADATA, expected_identity=IDENTITY)
                    self.assertEqual(result["totals"]["attempted_games"], 0)
                    self.assertEqual(result["excluded_reports"], [{"report": "changed", "reason": "rules_or_observation_identity_mismatch"}])
        missing = report([game()])
        del missing["policy_b"]["run_identity"]
        result = stats.build_statistics([("missing", missing)], METADATA, expected_identity=IDENTITY)
        self.assertEqual(result["state"], "awaiting_samples")

    def test_exact_duplicate_and_same_cohort_split_reports_keep_one_seed_cluster(self):
        first = report([game(seed=300, seat=0)])
        second = report([game(seed=300, seat=1, outcome=-1)])
        result = stats.build_statistics([("one", first), ("duplicate", copy.deepcopy(first)), ("other-seat", second)],
                                        METADATA, expected_identity=IDENTITY)
        self.assertEqual(result["totals"]["attempted_games"], 2)
        self.assertEqual(row(result, "mercenary:buy")["paired_clusters"], 1)
        self.assertEqual(result["excluded_reports"], [])

    def test_duplicate_choice_and_impossible_pick_count_fail_closed(self):
        for selections in ([choice(), choice()], [choice(picks=3, menus=2)]):
            with self.assertRaises(ValueError):
                stats.build_statistics([("bad", report([game(choices=selections)]))], METADATA)

    def test_publish_custom_report_folder_and_separate_snapshot_counter(self):
        with tempfile.TemporaryDirectory() as folder:
            campaign = Path(folder)
            run = campaign/"run"
            reports = campaign/"custom-reports"
            run.mkdir(); reports.mkdir()
            metadata = campaign/"metadata.json"
            metadata.write_text(json.dumps(METADATA))
            (run/"status.json").write_text(json.dumps({"games": 9000}))
            (run/"identity.json").write_text(json.dumps(IDENTITY))
            (reports/"cpu-shadow-fixture.json").write_text(json.dumps(report([game(), game(seat=1)])))
            output = campaign/"balance-statistics-frozen.json"
            with patch.object(stats, "CATALOG", metadata):
                value = stats.publish(campaign, run, every_games=0, reports_dir=reports,
                                      output=output, snapshot_training_games=7000)
            self.assertEqual(value["refresh"]["games_since_snapshot"], 2000)
            self.assertEqual(value["refresh"]["snapshot_training_games"], 7000)
            self.assertEqual(value["refresh"]["every_games"], 0)
            self.assertEqual(value["window"]["reports"], 1)
            self.assertEqual(json.loads(output.read_text())["totals"]["attempted_games"], 2)
            self.assertFalse((campaign/"balance-statistics.json").exists())


if __name__ == "__main__":
    unittest.main()
