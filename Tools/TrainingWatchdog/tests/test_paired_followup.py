import json
from pathlib import Path
import sys
import tempfile
import unittest
from dataclasses import asdict
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from paired_followup import compare_heroes, merge_blocks, run
from league import ArenaGame, heroes_for_seed, summarize_arena


def block(index, batch=40):
    seed = 11529215046702000000 + index * 2000
    games = [ArenaGame(value, seat, seat, False, 10, *heroes_for_seed(value))
             for value in range(seed, seed + 2000) for seat in (0, 1)]
    summary, rounds, coverage = summarize_arena(games, planned_pairs=2000)
    return dict(candidate={"sha": "candidate"}, baseline={"sha": "baseline"},
                hero_mode="balanced_random", hero_assignment_version="v1",
                pairing="both seats", action_selection="sampled", search_depth=0,
                device="cuda", precision="fp32", batch=batch, workers=2,
                elapsed_clock="monotonic_raw", promotion_allowed=False,
                integrity_verified=True, initial_publication_mirrors_verified=True,
                summary=summary, rounds=rounds, hero_coverage=coverage,
                planned_games=4000, seed_base=seed, results=[asdict(game) for game in games],
                mirror_initial_publications=[dict(seed_start=seed + offset, lanes=min(batch, 2000 - offset),
                                                 initial_publication_sha256="a" * 64)
                                             for offset in range(0, 2000, batch)],
                max_seconds=600, elapsed_seconds=100, policy_seed=1 + index * 100000)


class PairedFollowupTests(unittest.TestCase):
    def test_complete_composition_recomputes_8000_games(self):
        result = merge_blocks([block(0), block(1)])
        self.assertEqual(result["summary"]["recorded_games"], 8000)
        self.assertEqual(result["summary"]["wins"], 8000)
        self.assertEqual(len(result["mirror_initial_publications"]), 100)
        self.assertEqual(result["planned_pairs"], 4000)
        self.assertFalse(result["promotion_allowed"])
        for hero in result["hero_comparisons"]["heroes"].values():
            self.assertEqual(hero["games_per_model"], 1600)
            self.assertEqual(hero["candidate_score"], 1.)
            self.assertEqual(hero["baseline_score"], 0.)

    def test_same_seat_winners_are_not_mistaken_for_hero_policy_progress(self):
        games = block(0)["results"]
        for game in games:
            game["winner"] = 0
        result = compare_heroes(games)
        for hero in result["heroes"].values():
            self.assertEqual(hero["candidate_score"], .5)
            self.assertEqual(hero["baseline_score"], .5)
            self.assertEqual(hero["paired_score_difference"], 0.)

    def test_four_blocks_recompute_16000_games_and_3200_per_hero(self):
        result = merge_blocks([block(index) for index in range(4)])
        self.assertEqual(result["planned_games"], 16000)
        self.assertEqual(result["summary"]["complete_pairs"], 8000)
        self.assertEqual(result["summary"]["recorded_games"], 16000)
        for hero in result["hero_comparisons"]["heroes"].values():
            self.assertEqual(hero["games_per_model"], 3200)

    def test_hero_comparison_rejects_partial_and_censored_samples(self):
        games = block(0)["results"]
        with self.assertRaises(ValueError):
            compare_heroes(games[:-1])
        games[0].update(censored=True, winner=None, rounds=None)
        with self.assertRaises(ValueError):
            compare_heroes(games)

    def test_rejects_each_integrity_break(self):
        mutations = {
            "invalid": lambda r: r.update(integrity_verified=False),
            "policy_changed": lambda r: r["candidate"].update(sha="changed"),
            "seed_overlap": lambda r: r.update(seed_base=r["seed_base"] - 2000),
            "missing_game": lambda r: r["results"].pop(),
            "duplicate_game": lambda r: r["results"].__setitem__(1, r["results"][0]),
            "censor": lambda r: r["results"][0].update(censored=True),
            "summary_mismatch": lambda r: r["summary"].update(wins=5),
            "missing_mirror": lambda r: r["mirror_initial_publications"].pop(),
            "wrong_hero": lambda r: r["results"][0].update(hero0="rez", hero1="rez"),
            "search_changed": lambda r: r.update(search_depth=1),
            "sampling_seed_reused": lambda r: r.update(policy_seed=1),
            "promotion_bypass": lambda r: r.update(promotion_allowed=True),
        }
        for name, mutate in mutations.items():
            bad = block(1)
            mutate(bad)
            with self.subTest(name=name), self.assertRaises(ValueError):
                merge_blocks([block(0), bad])

    def test_runner_preserves_each_cap_deadline_and_stop_guard(self):
        calls = []
        def arena(candidate, baseline, path, **kwargs):
            calls.append(kwargs)
            result = block(len(calls) - 1)
            path.write_text(json.dumps(result))
            return result
        stop = lambda: False
        with tempfile.TemporaryDirectory() as temporary:
            output = Path(temporary) / "comparison.json"
            with patch("paired_followup.run_neural_arena", arena):
                result = run("candidate", "baseline", output,
                             seed_base=11529215046702000000, policy_seed=1,
                             baseline_manifest_sha256="a" * 64,
                             stop_check=stop, deadline_monotonic=1234.)
            self.assertEqual(len(result["verified_blocks"]), 2)
            self.assertEqual(len(calls), 2)
            for call in calls:
                self.assertEqual(call["games"], 4000)
                self.assertEqual(call["max_seconds"], 600)
                self.assertEqual(call["deadline_monotonic"], 1234.)
                self.assertIs(call["stop_check"], stop)
            self.assertEqual(calls[1]["seed_base"] - calls[0]["seed_base"], 2000)
            with self.assertRaises(FileExistsError):
                run("candidate", "baseline", output,
                    seed_base=11529215046702000000, policy_seed=1,
                    baseline_manifest_sha256="a" * 64,
                    stop_check=stop, deadline_monotonic=1234.)

    def test_incomplete_block_never_publishes_aggregate(self):
        bad = block(0)
        bad["integrity_verified"] = False
        with tempfile.TemporaryDirectory() as temporary:
            output = Path(temporary) / "comparison.json"
            with patch("paired_followup.run_neural_arena", return_value=bad) as arena:
                with self.assertRaisesRegex(RuntimeError, "incomplete"):
                    run("candidate", "baseline", output,
                        seed_base=11529215046702000000, policy_seed=1,
                        baseline_manifest_sha256="a" * 64,
                        stop_check=lambda: False, deadline_monotonic=1234.)
            self.assertEqual(arena.call_count, 1)
            self.assertFalse(output.exists())

    def test_explicit_larger_batch_preserves_pairs_caps_and_mirror_coverage(self):
        for batch in (96, 100):
            calls = []
            def arena(candidate, baseline, path, **kwargs):
                calls.append(kwargs)
                result = block(len(calls) - 1, batch=batch)
                path.write_text(json.dumps(result))
                return result
            with self.subTest(batch=batch), tempfile.TemporaryDirectory() as temporary:
                with patch("paired_followup.run_neural_arena", arena):
                    result = run("candidate", "baseline", Path(temporary) / "comparison.json",
                                 seed_base=11529215046702000000, policy_seed=1, batch=batch,
                                 baseline_manifest_sha256="a" * 64,
                                 stop_check=lambda: False, deadline_monotonic=1234.)
                self.assertEqual(result["summary"]["recorded_games"], 8000)
                self.assertEqual(result["batch"], batch)
                self.assertEqual(len(result["mirror_initial_publications"]), 2 * ((2000 + batch - 1) // batch))
                for call in calls:
                    self.assertEqual((call["batch"], call["workers"], call["games"], call["max_seconds"]),
                                     (batch, 2, 4000, 600))

    def test_invalid_batching_fails_before_any_games(self):
        for options in ({"batch": 0}, {"batch": True}, {"batch": 129}, {"batch": 100.},
                        {"workers": 0}, {"workers": True}, {"workers": 9}):
            with self.subTest(options=options), patch("paired_followup.run_neural_arena") as arena:
                with self.assertRaises(ValueError):
                    run("candidate", "baseline", "/unused-comparison.json", **options,
                        seed_base=11529215046702000000, policy_seed=1,
                        baseline_manifest_sha256="a" * 64,
                        stop_check=lambda: False, deadline_monotonic=1234.)
                arena.assert_not_called()

    def test_returned_batching_must_match_the_declared_plan(self):
        with tempfile.TemporaryDirectory() as temporary:
            with patch("paired_followup.run_neural_arena", return_value=block(0)):
                with self.assertRaisesRegex(RuntimeError, "batching plan"):
                    run("candidate", "baseline", Path(temporary) / "comparison.json", batch=100,
                        seed_base=11529215046702000000, policy_seed=1,
                        baseline_manifest_sha256="a" * 64,
                        stop_check=lambda: False, deadline_monotonic=1234.)

    def test_second_block_conflict_or_invalid_seed_fails_before_first_game(self):
        with tempfile.TemporaryDirectory() as temporary:
            output = Path(temporary) / "comparison.json"
            conflict = output.with_name("comparison-block-2.plan.json")
            conflict.write_text("occupied")
            options = dict(seed_base=11529215046702000000, policy_seed=1,
                           baseline_manifest_sha256="a" * 64,
                           stop_check=lambda: False, deadline_monotonic=1234.)
            with patch("paired_followup.run_neural_arena") as arena:
                with self.assertRaises(FileExistsError):
                    run("candidate", "baseline", output, **options)
                conflict.unlink()
                options["policy_seed"] = (1 << 32) - 1
                with self.assertRaises(ValueError):
                    run("candidate", "baseline", output, **options)
                arena.assert_not_called()

    def test_game_budget_must_be_explicit_bounded_and_divisible(self):
        for games in (True, 0, 4000, 8001, 16000., 36000):
            with self.subTest(games=games), self.assertRaises(ValueError):
                run("candidate", "baseline", "/unused-comparison.json", games=games,
                    seed_base=11529215046702000000, policy_seed=1,
                    baseline_manifest_sha256="a" * 64,
                    stop_check=lambda: False, deadline_monotonic=1234.)


if __name__ == "__main__":
    unittest.main()
