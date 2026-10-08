"""CPU-only pool-statistics regressions using the actual C# self-test fixture.

Synthetic publications scale that fixture's cumulative counters, never run a
game or import a learner. Files are written only below TemporaryDirectory.
"""
from copy import deepcopy
import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

import balance_host_statistics as s


FIXTURE = Path(__file__).parent / "experiments/HostV4/statistics-fixture.json"


def scaled(raw, factor, *, session=None, published=None):
    result = deepcopy(raw)
    for name, fields in (("totals", s.TOTAL_FIELDS), ("final_state_sums", s.STATE_FIELDS)):
        for key in fields:
            result[name][key] *= factor
    for name, fields in (("rows", s.ROW_FIELDS), ("hero_choice_rows", s.ROW_FIELDS),
                         ("hero_seat_rows", s.HERO_FIELDS), ("matchup_rows", s.MATCH_FIELDS)):
        for row in result[name]:
            for field in fields:
                row[field] *= factor
    result["final_round_histogram"] = [n * factor for n in raw["final_round_histogram"]]
    result["publication_sequence"] = factor + 1
    if session is not None:
        result["session_id"] = session
    if published is not None:
        result["published_utc"] = published
    return result


class PoolFixture(unittest.TestCase):
    def setUp(self):
        self.raw = json.loads(FIXTURE.read_text())
        self.metadata = json.loads(s.CATALOG.read_text())

    def aggregate(self, raw=None):
        return s.combine([s.delta(raw or self.raw)], self.metadata, run_id="fixture")

    def item(self, result, card, kind, *, hero=None):
        rows = result["hero_choice_rows"] if hero else [row for values in result["rankings"].values() for row in values]
        return next(row for row in rows if row["id"].split(":")[0] == card
                    and row.get("choice_kind") == kind and (hero is None or row["hero_id"] == hero))


class CounterTests(PoolFixture):
    def test_actual_fixture_seat_clusters_censors_and_final_state(self):
        result = self.aggregate()
        self.assertEqual(result["totals"]["resolved_games"], 2)
        self.assertEqual(result["totals"]["attempted_games"], 3)
        self.assertEqual(result["totals"]["unfinished_discarded_games"], 1)
        self.assertEqual(result["totals"]["mean_rounds"], 10.5)
        buy = self.item(result, "fungal_hermit", "buy")
        self.assertEqual((buy["games"], buy["wins"], buy["losses"], buy["paired_clusters"]), (2, 1, 1, 1))
        self.assertEqual((buy["pick_count"], buy["mean_acquisition_round"], buy["mean_cost_paid"]), (3, 2, 3))
        self.assertEqual(buy["score"], .5)
        self.assertIsNone(buy["pick_rate"])
        self.assertIsNone(buy["opportunities"])
        fast = self.item(result, "fungal_hermit", "fastplay")
        self.assertEqual((fast["games"], fast["censored_games"], fast["paired_clusters"]), (0, 2, 1))
        self.assertIsNone(fast["score"])
        self.assertEqual(fast["score_identification_interval"], [0, 1])
        self.assertEqual((fast["pick_count"], fast["censored_pick_count"]), (0, 2))
        self.assertIsNone(fast["mean_acquisition_round"])
        self.assertEqual(result["final_state_sums"]["player_count"], 4)
        self.assertEqual(sum(result["final_round_histogram"]), 2)

    def test_exact_hero_acquisitions_and_mirror_match_do_not_double_clusters(self):
        result = self.aggregate()
        decima = next(row for row in result["rankings"]["heroes"] if row["id"] == "decima")
        self.assertEqual((decima["games"], decima["censored_games"], decima["paired_clusters"]), (3, 2, 3))
        self.assertEqual(decima["score_identification_interval"], [.3, .7])
        mirror = next(row for row in result["hero_matchups"] if row["hero_a"] == row["hero_b"] == "decima")
        self.assertEqual((mirror["games"], mirror["wins"], mirror["losses"], mirror["censored_games"], mirror["paired_clusters"]), (2, 1, 1, 2, 2))
        per_hero = self.item(result, "fungal_hermit", "buy", hero="decima")
        self.assertEqual((per_hero["games"], per_hero["paired_clusters"]), (2, 1))
        destiny = self.item(result, "datic_secrets", "destiny", hero="tetra")
        self.assertEqual((destiny["games"], destiny["draws"]), (1, 1))
        self.assertFalse(any(r["hero_id"] == "decima" and r["id"].startswith("datic_secrets:") for r in result["hero_choice_rows"]))

    def test_cumulative_subtraction_is_not_repeated_outcomes(self):
        window = s.delta(scaled(self.raw, 9), scaled(self.raw, 6))
        result = s.combine([window], self.metadata, run_id="fixture")
        self.assertEqual((window["anchor_completed_games"], window["latest_completed_games"]), (12, 18))
        self.assertEqual(result["totals"]["resolved_games"], 6)
        self.assertEqual(self.item(result, "fungal_hermit", "buy")["games"], 6)
        self.assertEqual(self.item(result, "fungal_hermit", "buy")["paired_clusters"], 3)
        self.assertEqual(result["final_round_histogram"][9], 3)

    def test_regressions_dropped_rows_duplicates_and_noninteger_counters_rejected(self):
        previous = scaled(self.raw, 2)
        for mutate in (
            lambda x: x["totals"].update(completed_games=3),
            lambda x: x["rows"].pop(),
            lambda x: x["rows"].append(deepcopy(x["rows"][0])),
            lambda x: x["totals"].update(draws=True),
            lambda x: x["totals"].update(draws=1.5),
            lambda x: x.update(final_round_histogram=[0]),
        ):
            current = scaled(self.raw, 3)
            mutate(current)
            with self.subTest(mutation=mutate), self.assertRaises(ValueError):
                s.delta(current, previous)

    def test_anchor_identity_and_terminal_histogram_corruption_rejected(self):
        for key in ("session_id", "schema", "host_binary_sha256", "purpose", "observation_schema"):
            anchor = deepcopy(self.raw)
            anchor[key] = "different"
            with self.subTest(field=key), self.assertRaises(ValueError):
                s.delta(scaled(self.raw, 2), anchor)
        corrupt = deepcopy(self.raw)
        corrupt["final_round_histogram"][9] += 1
        with self.assertRaises(ValueError):
            self.aggregate(corrupt)

    def test_final_state_player_and_decisive_totals_must_match_real_games(self):
        for field in ("player_count", "resolved_decisive_games"):
            corrupt = deepcopy(self.raw)
            corrupt["final_state_sums"][field] += 1
            with self.subTest(field=field), self.assertRaises(ValueError):
                self.aggregate(corrupt)

    def test_matchup_and_hero_total_inconsistencies_are_rejected(self):
        corrupt = deepcopy(self.raw)
        corrupt["matchup_rows"][0]["seat0_wins"] += 1
        with self.assertRaises(ValueError):
            self.aggregate(corrupt)
        corrupt = deepcopy(self.raw)
        # Keep each row internally consistent while breaking the real-game sum.
        corrupt["hero_seat_rows"][0]["games"] += 1
        corrupt["hero_seat_rows"][0]["draws"] += 1
        with self.assertRaises(ValueError):
            self.aggregate(corrupt)

    def test_hero_choice_outcomes_must_reconcile_to_global_choice_rows(self):
        corrupt = deepcopy(self.raw)
        row = corrupt["hero_choice_rows"][0]
        row["wins"] += 1
        row["losses"] -= 1
        with self.assertRaises(ValueError):
            self.aggregate(corrupt)


class PublicationTests(PoolFixture):
    def setUp(self):
        super().setUp()
        self.temp = tempfile.TemporaryDirectory(prefix="shards-pool-statistics-")
        self.addCleanup(self.temp.cleanup)
        self.campaign = Path(self.temp.name)
        self.run = self.campaign / "main-v4"
        self.folder = self.run / "training-statistics"
        self.folder.mkdir(parents=True)
        self.identity = {key: self.metadata[key] for key in ("rules_sha256", "catalog_sha256", "observation_schema")}
        self.identity["host_binary_sha256"] = self.raw["host_binary_sha256"]
        self.runs = {"main-v4": {"path": self.run, "identity": self.identity}}

    @staticmethod
    def write(path, raw):
        path.parent.mkdir(parents=True, exist_ok=True)
        temporary = path.with_suffix(".json.tmp")
        temporary.write_text(json.dumps(raw))
        temporary.replace(path)

    def publication(self, raw):
        path = self.folder / ("session-" + raw["session_id"] + ".json")
        self.write(path, raw)
        return path

    def history(self, raw, *, named_completed=None):
        completed = raw["totals"]["completed_games"] if named_completed is None else named_completed
        path = self.folder / "history" / (f"session-{raw['session_id']}-completed-{completed:012d}-seq-{raw['publication_sequence']:08d}.json")
        self.write(path, raw)
        return path

    def test_unchanged_publication_is_cached_and_new_cumulative_file_not_added_twice(self):
        self.publication(self.raw)
        cache = s.TrainingPoolCache(self.campaign, metadata=self.metadata)
        first = cache.refresh(self.runs)
        output = self.campaign / "balance-statistics.json"
        stamp = output.stat().st_mtime_ns
        with patch.object(s, "load", side_effect=AssertionError("unchanged snapshot reparsed")):
            self.assertIs(cache.refresh(self.runs), first)
        self.assertEqual(output.stat().st_mtime_ns, stamp)
        self.publication(scaled(self.raw, 3))
        second = cache.refresh(self.runs)
        self.assertEqual(second["totals"]["resolved_games"], 6)
        self.assertNotEqual(second["snapshot_id"], first["snapshot_id"])

    def test_recent_window_uses_numeric_history_boundary_and_remains_at_least_target(self):
        current = scaled(self.raw, 100000)  # 200k completed; no large data allocation.
        self.publication(current)
        lower = self.history(scaled(self.raw, 45000))  # 90k boundary, yielding110k.
        self.history(scaled(self.raw, 55000))  # 110k would retain only90k.
        cache = s.TrainingPoolCache(self.campaign, target_games=100000, metadata=self.metadata)
        result = cache.refresh(self.runs)
        self.assertEqual(result["totals"]["resolved_games"], 110000)
        self.assertEqual(result["source_files"][0]["anchor_file"], str(lower))
        self.assertEqual(result["window"]["actual_completed_games"], 110000)

    def test_retired_sessions_remain_in_lifetime_total_but_not_recent_outcomes(self):
        self.publication(scaled(self.raw, 100000, session="older", published="2026-09-26T10:00:00Z"))
        self.publication(scaled(self.raw, 50000, session="newer", published="2026-09-26T11:00:00Z"))
        cache = s.TrainingPoolCache(self.campaign, target_games=100000, metadata=self.metadata)
        result = cache.refresh(self.runs)
        self.assertEqual(result["totals"]["resolved_games"], 100000)
        self.assertEqual(len(result["window"]["sessions"]), 1)
        self.assertEqual(result["refresh"]["total_games"], 300000)

    def test_purpose_host_schema_and_run_identity_are_guarded(self):
        for field in ("purpose", "host_binary_sha256", "observation_schema"):
            raw = deepcopy(self.raw)
            raw[field] = "wrong"
            self.publication(raw)
            with self.subTest(field=field):
                self.assertIsNone(s.TrainingPoolCache(self.campaign, metadata=self.metadata).refresh(self.runs))
        self.publication(self.raw)
        incompatible = deepcopy(self.runs)
        incompatible["main-v4"]["identity"]["rules_sha256"] = "wrong"
        self.assertIsNone(s.TrainingPoolCache(self.campaign, metadata=self.metadata).refresh(incompatible))

    def test_changed_identity_invalidates_cache_without_touching_raw_file(self):
        self.publication(self.raw)
        cache = s.TrainingPoolCache(self.campaign, metadata=self.metadata)
        self.assertIsNotNone(cache.refresh(self.runs))
        changed = deepcopy(self.runs)
        changed["main-v4"]["identity"]["host_binary_sha256"] = "different"
        self.assertIsNone(cache.refresh(changed))

    def test_history_filename_must_agree_with_payload_counts(self):
        self.publication(scaled(self.raw, 20))
        self.history(scaled(self.raw, 16), named_completed=10)  # would retain8 instead of30.
        cache = s.TrainingPoolCache(self.campaign, target_games=30, metadata=self.metadata)
        with self.assertRaises(ValueError):
            cache.refresh(self.runs)

    def test_duplicate_session_and_symlink_sources_do_not_duplicate_data(self):
        path = self.publication(self.raw)
        duplicate = self.folder / "session-duplicate.json"
        self.write(duplicate, self.raw)
        with self.assertRaises(ValueError):
            s.TrainingPoolCache(self.campaign, metadata=self.metadata).refresh(self.runs)
        duplicate.unlink()
        (self.folder / "session-link.json").symlink_to(path)
        result = s.TrainingPoolCache(self.campaign, metadata=self.metadata).refresh(self.runs)
        self.assertEqual(result["totals"]["resolved_games"], 2)

    def test_random_and_natural_cohorts_never_mix_with_each_other_or_legacy(self):
        self.publication(self.raw)
        for cohort, factor in (("natural-draft", 2), ("forced-random", 3)):
            raw = scaled(self.raw, factor, session=cohort)
            raw["hero_setup"] = {"cohort": cohort, "schema": "fixture"}
            self.write(self.folder/cohort/("session-"+cohort+".json"), raw)
        legacy = s.TrainingPoolCache(self.campaign, metadata=self.metadata).refresh(self.runs)
        self.assertEqual(legacy["totals"]["resolved_games"], 2)
        for cohort, expected in (("natural-draft", 4), ("forced-random", 6)):
            result = s.TrainingPoolCache(self.campaign, metadata=self.metadata, cohort=cohort).refresh(self.runs)
            self.assertEqual(result["totals"]["resolved_games"], expected)
            self.assertEqual(result["scope"]["hero_setup_cohort"], cohort)
            self.assertTrue((self.campaign/("balance-statistics-"+cohort+".json")).is_file())

    def test_new_runtime_cohort_replaces_old_metadata_without_mixing_populations(self):
        cohort = "forced-random"
        old_raw = deepcopy(self.raw);old_raw["hero_setup"] = {"cohort":cohort}
        self.identity["schema"] = "shards-real-selfplay-v7"
        self.write(self.folder/cohort/"session-old.json", old_raw)
        new_metadata = deepcopy(self.metadata)
        new_metadata.update(rules_sha256="new-rules",catalog_sha256="new-catalog",observation_schema="new-observation")
        new_run = self.campaign/"new-run"
        identity = dict(self.identity, **{key:new_metadata[key] for key in ("rules_sha256","catalog_sha256","observation_schema")}, schema="shards-real-selfplay-v12")
        self.runs["new-run"] = {"path":new_run,"identity":identity}
        raw = deepcopy(old_raw);raw["session_id"] = "new-session";raw["observation_schema"] = "new-observation"
        self.write(new_run/"training-statistics"/cohort/"session-new.json", raw)
        cache = s.TrainingPoolCache(self.campaign,metadata=self.metadata,cohort=cohort)
        cache.auto_metadata = True;cache.metadata_options = [self.metadata,new_metadata]
        result = cache.refresh(self.runs)
        self.assertEqual(result["scope"]["run_id"],"new-run")
        self.assertEqual(result["totals"]["resolved_games"],2)
        self.assertEqual(cache.metadata["observation_schema"],"new-observation")
        self.assertEqual(len(result["source_files"]),1)

    def test_cohort_label_and_window_anchor_must_match_directory(self):
        raw = deepcopy(self.raw)
        raw["hero_setup"] = {"cohort": "natural-draft"}
        self.write(self.folder/"forced-random"/"session-wrong.json", raw)
        cache = s.TrainingPoolCache(self.campaign, metadata=self.metadata, cohort="forced-random")
        self.assertIsNone(cache.refresh(self.runs))
        self.assertIsNotNone(cache.error)
        current = deepcopy(raw)
        current["hero_setup"] = {"cohort": "forced-random"}
        with self.assertRaises(ValueError):
            s.delta(current, raw)


if __name__ == "__main__":
    unittest.main()
