"""Fake-host CPU schedule/ownership tests; no real matches or CUDA execution."""
import copy
import sys
from pathlib import Path
import unittest
from unittest.mock import patch

import balanced_evaluation as balanced

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import cpu_shadow_eval as shadow
import hero_coverage_eval as helpers
import numpy as np
import torch
from learning_model import LearningPolicy, PolicyConfig


class PrefixHost:
    def __init__(self, batch, workers, seed, *, capped=False, **kwargs):
        self.batch, self.seed, self.capped = batch, seed, capped
        self.obs = np.zeros((batch, 2048), np.float32)
        self.candidates = np.zeros((batch, 64, 32), np.float32)
        self.mask = np.zeros((batch, 64), np.float32)
        self.seats = np.ones(batch, np.int32)
        self.done = np.zeros(batch, np.int32)
        self.rewards = np.zeros((batch, 2), np.float32)
        self.metrics = np.zeros(8, np.float64)
        self.phases = np.zeros(batch, np.int64)
        self.heroes = np.zeros((batch, 2), np.int64)
        self.closed = False
        self.trace = []
        for lane in range(batch):
            self.show(lane)

    @property
    def upload(self):
        return torch.from_numpy(np.concatenate((self.obs.ravel(), self.candidates.ravel(), self.mask.ravel())))

    def show(self, lane):
        phase = int(self.phases[lane])
        ended = phase >= 4+2*(lane % 2)
        if ended:
            phase = 0  # next game's visible reset is held, never submitted
        seat = 1-phase if phase < 2 else (phase-2) % 2
        self.seats[lane] = seat
        self.obs[lane].fill(0)
        self.obs[lane, 2] = .01
        self.obs[lane, [16, 64]] = 1.
        if not ended:
            self.obs[lane, 22] = self.heroes[lane, seat]/5
            self.obs[lane, 70] = self.heroes[lane, 1-seat]/5
        self.obs[lane, 112:116] = helpers.context_bytes("soi.herodraft" if phase < 2 else "")
        self.obs[lane, 4] = phase < 2
        self.candidates[lane].fill(0)
        self.mask[lane].fill(0)
        if phase < 2:
            for hero in range(1, 6):
                if not ended and hero in self.heroes[lane]:
                    continue
                # Original slots deliberately differ from hero ordinal.
                slot = hero*3
                self.mask[lane, slot] = 1
                self.candidates[lane, slot, 12] = 1
                self.candidates[lane, slot, 31] = hero/5
        else:
            for slot, kind in ((0, 0), (1, 10)):
                self.mask[lane, slot] = 1
                self.candidates[lane, slot, kind] = 1

    def advance_active(self, actions, active):
        self.done.fill(0); self.rewards.fill(0)
        for lane in np.flatnonzero(active):
            phase = int(self.phases[lane])
            if phase >= 4+2*(lane % 2):
                raise AssertionError("Held reset lane was resubmitted")
            action = int(actions[lane])
            if not self.mask[lane, action]:
                raise AssertionError("Illegal selected original slot")
            seat = int(self.seats[lane])
            if phase < 2:
                hero = int(round(float(self.candidates[lane, action, 31])*5))
                self.heroes[lane, seat] = hero
            self.trace.append((int(lane), phase, seat, action))
            self.phases[lane] += 1
            self.metrics[2:4] += 1
            if self.phases[lane] >= 4+2*(lane % 2):
                self.done[lane] = 2 if self.capped and lane == 0 else 1
                if self.done[lane] == 1:
                    self.rewards[lane] = [1, -1]
            self.show(lane)

    def close(self):
        self.closed = True


class BalancedTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        shadow.configure_cpu()
        cls.policy = LearningPolicy(PolicyConfig()).eval().requires_grad_(False)

    def test_plan_has20_distinct_cells_disjoint_seeds_and_both_seats(self):
        plan = balanced.evaluation_plan()
        self.assertEqual(plan["planned_games"], 4000)
        self.assertEqual(len(plan["cells"]), 20)
        all_seeds = []
        hero_pairs = set()
        for cell in plan["cells"]:
            hero_pairs.add((cell["policy_a_hero"], cell["policy_b_hero"]))
            self.assertNotEqual(cell["policy_a_hero"], cell["policy_b_hero"])
            all_seeds.extend(range(cell["seed_start"], cell["seed_start"]+cell["paired_seed_count"]))
        self.assertEqual(len(hero_pairs), 20)
        self.assertEqual(len(all_seeds), len(set(all_seeds)))
        self.assertEqual(len(all_seeds), 2000)
        self.assertEqual(balanced.evaluation_plan(pairs_per_cell=3)["planned_games"], 120)

    def test_forced_prefix_uses_legal_original_slots_and_stops_after_setup(self):
        for a, b in balanced.PAIRINGS:
            host = PrefixHost(1, 1, 100)
            helpers.force_initial_draft(host, (a, b))
            self.assertEqual([record[2] for record in host.trace], [1, 0])
            self.assertEqual([record[3] for record in host.trace],
                [(balanced.HEROES.index(b)+1)*3, (balanced.HEROES.index(a)+1)*3])
            self.assertEqual(host.phases[0], 2)
            self.assertEqual(host.seats[0], 0)
            self.assertEqual(host.heroes[0].tolist(), [balanced.HEROES.index(a)+1, balanced.HEROES.index(b)+1])
            with self.assertRaises(ValueError):
                helpers.force_initial_draft(host, (a, b))

    def test_fake_matrix_exact_assignments_outcomes_holds_and_frozen_weights(self):
        hosts = []
        def factory(*args, **kwargs):
            host = PrefixHost(*args, **kwargs)
            hosts.append(host)
            return host
        before = shadow.checkpoints.policy_hash(self.policy)
        with patch.object(torch.cuda, "_lazy_init", side_effect=AssertionError("No CUDA")):
            result = balanced.run_balanced_matrix(self.policy, copy.deepcopy(self.policy),
                pairs_per_cell=2, batch=2, seed=1000, max_seconds=30, host_factory=factory)
        self.assertTrue(result["complete"])
        self.assertEqual((result["games"], result["wins_a"], result["losses_a"]), (80, 40, 40))
        self.assertEqual(result["balanced_macro_score"], .5)
        self.assertEqual(len(hosts), 40)
        self.assertTrue(all(host.closed for host in hosts))
        self.assertEqual(shadow.checkpoints.policy_hash(self.policy), before)
        self.assertFalse(torch.cuda.is_initialized())
        for specification, cell in zip(result["plan"]["cells"], result["cells"]):
            self.assertEqual(cell["games"], 4)
            for episode in cell["shadow_diagnostics"]["episodes"]:
                self.assertEqual(episode["policy_a_hero"], specification["policy_a_hero"])
                self.assertEqual(episode["policy_b_hero"], specification["policy_b_hero"])

    def test_censored_cell_outcomes_are_unknown_not_draws(self):
        def factory(*args, **kwargs):
            return PrefixHost(*args, capped=True, **kwargs)
        result = balanced.run_balanced_matrix(self.policy, self.policy,
            pairs_per_cell=1, batch=1, seed=3000, max_seconds=30, host_factory=factory)
        self.assertTrue(result["complete"])
        self.assertEqual(result["censored_games"], 40)
        self.assertEqual(result["draws"], 0)
        self.assertIsNone(result["balanced_macro_score"])
        self.assertEqual(result["score_identification_interval"], [0., 1.])

    def test_invalid_plan_bounds_reject_before_execution(self):
        for options in ({"pairs_per_cell": 0}, {"pairs_per_cell": True}, {"seed": 2**64-1},
                        {"sampling_seed": 2**63-1}):
            with self.assertRaises(ValueError):
                balanced.evaluation_plan(**options)


if __name__ == "__main__":
    unittest.main()
