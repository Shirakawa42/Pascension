"""CPU-only passive telemetry ownership, censoring and policy-equivalence checks."""
import copy
import json
import unittest
from unittest.mock import patch

import cpu_shadow_eval as shadow
import numpy as np
import torch

from balance_telemetry import BalanceTelemetry
from learning_model import LearningPolicy, PolicyConfig
from test_cpu_shadow_eval import HostFixture


def frame(seat=0, round_number=2):
    obs = np.zeros((1, 2048), np.float32)
    obs[0, 2] = round_number/100
    obs[0, 22], obs[0, 70] = (seat+1)/5, (2-seat)/5
    obs[0, [16, 17, 20, 21]] = (40/50, 9/30, 3/20, 4/50)
    obs[0, [64, 65, 68, 69]] = (35/50, 11/30, 5/20, 6/50)
    obs[0, 320+6] = .2
    candidates = np.zeros((1, 64, 32), np.float32)
    # Duplicate buy instances in slots0/1; the same definition fastplayed in2.
    for slot, kind, identity in ((0, 1, 7), (1, 1, 7), (2, 2, 7), (3, 7, 8), (4, 6, 9), (5, 12, 10)):
        candidates[0, slot, kind] = 1
        candidates[0, slot, 16] = identity/192
    mask = np.zeros((1, 64), np.float32)
    mask[:, :6] = 1
    return obs, candidates, mask, np.asarray([seat]), np.asarray([0]), np.asarray([True])


class BalanceHost(HostFixture):
    def _show(self, lane):
        super()._show(lane)
        self.obs[lane, [16, 64]] = 1
        self.obs[lane, [17, 65]] = .3
        self.obs[lane, [21, 69]] = .2
        self.obs[lane, 320+6] = .2
        self.candidates[lane].fill(0)
        self.candidates[lane, 0, 1] = 1
        self.candidates[lane, 1, 2] = 1
        self.candidates[lane, :2, 16] = 7/192


class BalanceTests(unittest.TestCase):
    def test_duplicate_instances_and_repeat_buys_are_one_game_association(self):
        telemetry = BalanceTelemetry()
        batch = telemetry.new_batch(1, 100, 0)
        source = frame()
        untouched = [array.copy() for array in source]
        batch.observe(*source)
        later = frame(round_number=4)
        later[4][0] = 1
        batch.observe(*later)
        # A third choice is generic select, deliberately not inferred acquisition.
        third = frame(round_number=5)
        third[4][0] = 5
        batch.observe(*third)
        batch.finish(0, [1, -1], 1)
        data = telemetry.result()
        for before, after in zip(untouched, source):
            np.testing.assert_array_equal(before, after)
        a = next(game for game in data["games"] if game["policy"] == "a")
        buy = next(choice for choice in a["choices"] if choice["choice_kind"] == "buy")
        self.assertEqual(buy, {"card_id": 7, "choice_kind": "buy", "pick_count": 2,
            "opportunity_menus": 3, "first_selected_round": 2, "acquisition_round_sum": 6})
        aggregate = next(row for row in data["choices"] if row["policy"] == "a" and row["choice_kind"] == "buy")
        self.assertEqual((aggregate["selected_games"], aggregate["selected_wins"], aggregate["pick_count"]), (1, 1, 2))
        self.assertFalse(any(choice["card_id"] == 10 for choice in a["choices"]))
        self.assertEqual(a["last_observed_own_collection"], [{"card_id": 7, "count": 2}])
        self.assertEqual(a["last_observed_public"], {"health": 40, "mastery": 9, "deck_count": 4, "hand_count": 3})
        self.assertEqual(data["hero_coverage"]["a"]["rez"], 0)
        # Mutating the source after finalization cannot alter stored data.
        source[0].fill(999)
        self.assertEqual(a["last_observed_own_collection"], [{"card_id": 7, "count": 2}])
        json.dumps(data, allow_nan=False)

    def test_deciding_seat_ownership_and_actor_relative_public_state(self):
        telemetry = BalanceTelemetry()
        batch = telemetry.new_batch(1, 44, 1)
        first = frame(seat=0)
        first[4][0] = 3  # policy B recruits a relic
        batch.observe(*first)
        second = frame(seat=1, round_number=3)
        second[4][0] = 4  # policy A takes a destiny
        batch.observe(*second)
        batch.finish(0, [-1, 1], 1)
        a, b = telemetry.result()["games"]
        self.assertEqual((a["policy"], a["seat"], a["hero"], a["outcome"]), ("a", 1, "tetra", 1))
        self.assertEqual((b["policy"], b["seat"], b["hero"], b["outcome"]), ("b", 0, "decima", -1))
        self.assertEqual([row["choice_kind"] for row in a["choices"] if row["pick_count"]], ["destiny"])
        self.assertEqual([row["choice_kind"] for row in b["choices"] if row["pick_count"]], ["relic"])
        self.assertEqual(a["last_observed_public"]["mastery"], 9)
        self.assertEqual(b["last_observed_public"]["mastery"], 11)
        self.assertEqual((a["wrapper_decisions"], a["policy_decisions"]), (2, 1))

    def test_censor_unknown_and_excluded_from_resolved_choice_association(self):
        telemetry = BalanceTelemetry()
        for seed, done, rewards in ((1, 1, [1, -1]), (2, 2, [0, 0])):
            batch = telemetry.new_batch(1, seed, 0)
            batch.observe(*frame())
            batch.finish(0, rewards, done)
        data = telemetry.result()
        self.assertEqual(data["totals"], {"unique_attempted_games": 2, "resolved_games": 1,
            "censored_games": 1, "unfinished_games": 0, "policy_player_records": 4})
        buy = next(row for row in data["choices"] if row["policy"] == "a" and row["choice_kind"] == "buy")
        self.assertEqual((buy["selected_games"], buy["selected_wins"], buy["censored_selected_games"], buy["pick_count"]), (1, 1, 1, 1))
        self.assertTrue(all(row["outcome"] is None for row in data["games"] if row["censored"]))

    def test_held_reset_frames_and_unfinished_games_are_not_counted(self):
        telemetry = BalanceTelemetry()
        batch = telemetry.new_batch(1, 1, 0)
        source = frame()
        batch.observe(*source)
        batch.finish(0, [0, 0], 1)
        source[5][0] = False
        source[0].fill(999)
        batch.observe(*source)
        telemetry.new_batch(2, 8, 1)
        data = telemetry.result()
        self.assertEqual(data["totals"]["unfinished_games"], 2)
        self.assertEqual(len(data["games"]), 2)
        self.assertEqual(data["games"][0]["wrapper_decisions"], 1)
        with self.assertRaisesRegex(ValueError, "repeated"):
            batch.finish(0, [0, 0], 1)

    def test_invalid_legal_action_unknown_card_and_censor_reward_rejected(self):
        for alteration in ("illegal", "unknown"):
            batch = BalanceTelemetry().new_batch(1, 1, 0)
            source = frame()
            if alteration == "illegal":
                source[4][0] = 63
            else:
                source[1][0, 0, 16] = 190/192
            with self.assertRaises(ValueError):
                batch.observe(*source)
        batch = BalanceTelemetry().new_batch(1, 1, 0)
        batch.observe(*frame())
        with self.assertRaisesRegex(ValueError, "fabricated"):
            batch.finish(0, [1, -1], 2)

    def test_live_loop_hook_preserves_actions_rng_masks_outcomes_and_holds(self):
        shadow.configure_cpu()
        with patch.object(torch.cuda, "_lazy_init", side_effect=AssertionError("No CUDA")):
            policy = LearningPolicy(PolicyConfig()).eval().requires_grad_(False)
            copies = []
            def factory(*args, **kwargs):
                host = BalanceHost(truncated=(0,))
                copies.append(host)
                return host
            plain = shadow.run_cpu_match(policy, copy.deepcopy(policy), games=4, batch=2,
                seed=700, sampling_seed=351, host_factory=factory, balance_telemetry=False)
            plain_rng = torch.get_rng_state().clone()
            enabled = shadow.run_cpu_match(policy, copy.deepcopy(policy), games=4, batch=2,
                seed=700, sampling_seed=351, host_factory=factory, balance_telemetry=True)
            self.assertTrue(torch.equal(plain_rng, torch.get_rng_state()))
            self.assertEqual([host.executed for host in copies[:2]], [host.executed for host in copies[2:]])
            for key in ("score_a", "score_bound_95", "resolved_games", "censored_games",
                        "selected_actions_by_kind_card", "legal_candidate_opportunities_by_kind_card"):
                self.assertEqual(plain[key], enabled[key], key)
            data = enabled["balance_observations"]
            self.assertEqual(data["totals"]["unique_attempted_games"], 4)
            self.assertEqual(data["totals"]["censored_games"], 2)
            self.assertEqual(len(data["games"]), 8)
            self.assertEqual({(g["paired_seed"], g["policy"], g["seat"]) for g in data["games"]},
                             {(seed, role, seat) for seed in (700, 701) for role in ("a", "b") for seat in (0, 1)})
            self.assertTrue(all(host.closed for host in copies))
            json.dumps(data, allow_nan=False)
        self.assertFalse(torch.cuda.is_initialized())


if __name__ == "__main__":
    unittest.main()
