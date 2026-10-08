"""CPU-only collector/evaluation control tests with scripted rule-host responses.

No CUDA allocations, model updates or game-outcome learning occur. The real
collect_episodes/evaluate_match loops run against explicit ownership traces.
"""
from dataclasses import dataclass
import json
from pathlib import Path
import tempfile
from unittest import mock
import unittest

import numpy as np
import torch

from learning_rollout import EpisodeStore, collect_episodes
from learning_eval import evaluate_match


@dataclass(frozen=True)
class Decision:
    owner: int
    turn: int
    submits: bool = True
    round: int = 1


class ScriptedHost:
    def __init__(self, scripts, outcomes, *, initial_counters=True, truncated=(),
                 replay_held=False, counter_error=False, invalid_reward=False):
        self.scripts = scripts
        self.outcomes = outcomes
        self.batch = len(scripts)
        self.position = np.zeros(self.batch, dtype=np.int64)
        self.seats = np.zeros(self.batch, dtype=np.int32)
        self.done = np.zeros(self.batch, dtype=np.int32)
        self.rewards = np.zeros((self.batch, 2), dtype=np.float32)
        self.obs = np.zeros((self.batch, 2048), dtype=np.float32)
        self.candidates = np.zeros((self.batch, 64, 32), dtype=np.float32)
        self.mask = np.zeros((self.batch, 64), dtype=np.float32)
        self.metrics = np.zeros(8, dtype=np.float64)
        if initial_counters:
            self.metrics[2:6] = (100, 80, 40, 0)
        self.truncated = set(truncated)
        self.replay_held = replay_held
        self.counter_error = counter_error
        self.invalid_reward = invalid_reward
        self.calls = []
        self.executed = []
        self.closed = False
        for lane in range(self.batch):
            self._show(lane)

    def _show(self, lane):
        position = self.position[lane]
        ended = position == len(self.scripts[lane])
        decision = self.scripts[lane][-1 if ended else position]
        self.seats[lane] = 1 - decision.owner if ended else decision.owner
        self.obs[lane].fill(0)
        # Reset observations carry an unmistakable marker and changed owner.
        self.obs[lane, 0] = -1000-lane if ended else lane * 100 + position + 1
        self.obs[lane, 1] = self.seats[lane]
        self.obs[lane, 2] = decision.round/100
        self.obs[lane, 3] = decision.turn
        self.candidates[lane].fill(0)
        kind = 12 if decision.owner != decision.turn else 0
        self.candidates[lane, 0, kind] = 1
        self.candidates[lane, 1, 4] = 1
        self.mask[lane].fill(0)
        self.mask[lane, :2] = 1

    def advance_active(self, actions, active):
        self.calls.append((np.asarray(actions).copy(), np.asarray(active).copy()))
        previous_done, previous_rewards = self.done.copy(), self.rewards.copy()
        self.done.fill(0)
        self.rewards.fill(0)
        for lane in np.flatnonzero(active):
            position = int(self.position[lane])
            if position >= len(self.scripts[lane]):
                raise AssertionError("Completed lane was submitted again instead of held")
            if actions[lane] not in (0, 1):
                raise AssertionError("Fixture action is illegal")
            decision = self.scripts[lane][position]
            self.executed.append((lane, position, decision.owner, int(actions[lane])))
            self.position[lane] += 1
            self.metrics[2] += 1
            self.metrics[3] += int(decision.submits)
            if self.position[lane] == len(self.scripts[lane]):
                if lane in self.truncated:
                    self.done[lane] = 2
                    self.metrics[5] += 1
                else:
                    self.done[lane] = 1
                    self.rewards[lane] = self.outcomes[lane]
                    self.metrics[4] += 1
            self._show(lane)
        if self.replay_held:
            self.done[~active] = previous_done[~active]
            self.rewards[~active] = previous_rewards[~active]
        if self.counter_error:
            self.metrics[2] += 1
        if self.invalid_reward:
            self.rewards[0] = (1, 1)

    def close(self):
        self.closed = True


class ScriptedActor:
    def __init__(self, batch, action=0):
        self.batch = batch
        self.action = action
        self.calls = 0
        self.packet = None

    def act(self, host):
        self.calls += 1
        self.obs = host.obs.copy()
        self.candidates = host.candidates.copy()
        self.mask = host.mask.copy()
        self.packet = np.empty((self.batch, 3), dtype=np.float32)
        self.packet[:, 0] = self.action
        self.packet[:, 1] = -.25 - self.action
        self.packet[:, 2] = host.seats * .5 - .25
        return self.packet[:, 0].astype(np.int64), self.packet.copy()


class TorchScriptedActor(ScriptedActor):
    """Use the real EpisodeStore on CPU without a model or optimizer."""
    def act(self, host):
        actions, packet = super().act(host)
        self.obs = torch.from_numpy(self.obs)
        self.candidates = torch.from_numpy(self.candidates)
        self.mask = torch.from_numpy(self.mask)
        self.packet = torch.from_numpy(self.packet)
        return actions, packet


class RecordingStore:
    def __init__(self):
        self.reset(-1)
        self.seal_calls = 0

    def reset(self, version):
        self.version = version
        self.rows = 0
        self.sealed = False
        self.records = []

    def append(self, actor, packet, lanes, seats):
        if self.sealed:
            raise AssertionError("Append after seal")
        for lane, seat in zip(lanes, seats):
            self.records.append((int(lane), int(seat), float(actor.obs[lane, 0]),
                                 packet[lane].copy()))
        self.rows += len(lanes)

    def seal(self, outcomes, completed):
        self.seal_calls += 1
        self.outcomes = outcomes.copy()
        self.completed = completed.copy()
        self.credit = [float(outcomes[lane, seat]) for lane, seat, _, _ in self.records]
        self.sealed = True


class StopSession:
    def __init__(self, host, after_calls):
        self.host, self.after_calls = host, after_calls
        self.heartbeats = 0

    @property
    def should_stop(self):
        return len(self.host.calls) >= self.after_calls

    def heartbeat(self, *args, **kwargs):
        self.heartbeats += 1


def collection_fixture(**kwargs):
    # Seat 1 defends twice during seat 0's turn in lane 0. Lane 1 is current
    # self-play; lanes 0/2 use a frozen opponent. Lane 2 finishes first.
    scripts = [
        [Decision(0, 0), Decision(1, 0), Decision(1, 0, False), Decision(0, 0), Decision(1, 1)],
        [Decision(1, 1), Decision(1, 1), Decision(0, 0)],
        [Decision(1, 1), Decision(0, 1)],
    ]
    host = ScriptedHost(scripts, [(-1, 1), (1, -1), (0, 0)], **kwargs)
    return host, ScriptedActor(3), RecordingStore()


class CollectionControlTests(unittest.TestCase):
    def test_frozen_opponent_defending_ownership_holds_and_old_terminal_credit(self):
        host, actor, store = collection_fixture()
        opponent = ScriptedActor(3, action=1)
        result = collect_episodes(host, actor, store, version=7, opponent=opponent,
                                  opponent_lanes=[True, False, True], learner_seats=[1, 0, 0])
        self.assertTrue(result.completed)
        self.assertEqual(store.version, 7)
        self.assertEqual([(lane, seat, marker) for lane, seat, marker, _ in store.records],
                         [(1, 1, 101), (0, 1, 2), (1, 1, 102), (2, 0, 202),
                          (0, 1, 3), (1, 0, 103), (0, 1, 5)])
        self.assertEqual(store.credit, [-1, 1, -1, 0, 1, 1, 1])
        self.assertTrue(store.completed.all())
        self.assertTrue(all(packet[0] == 0 and packet[1] == -.25 for _, _, _, packet in store.records))
        self.assertTrue(all(marker > 0 for _, _, marker, _ in store.records), "No reset observation becomes experience")
        expected_actions = [(0, 0, 0, 1), (1, 0, 1, 0), (2, 0, 1, 1),
                            (0, 1, 1, 0), (1, 1, 1, 0), (2, 1, 0, 0),
                            (0, 2, 1, 0), (1, 2, 0, 0), (0, 3, 0, 1), (0, 4, 1, 0)]
        self.assertEqual(host.executed, expected_actions)
        self.assertEqual([active.tolist() for _, active in host.calls],
                         [[True, True, True], [True, True, True], [True, True, False],
                          [True, False, False], [True, False, False]])
        metrics = result.metrics
        self.assertEqual((metrics["wrapper_decisions"], metrics["engine_submissions"], metrics["learning_rows"]), (10, 9, 7))
        self.assertEqual((metrics["completed_games"], metrics["draws"], metrics["seat0_wins"]), (3, 1, 1))
        self.assertEqual((metrics["archive_games"], metrics["archive_score"]), (2, .75))
        self.assertAlmostEqual(metrics["lane_occupancy"], 2/3)
        self.assertEqual(metrics["max_episode_decisions"], 5)
        self.assertEqual(metrics["action_kind_counts"][0], 4)
        self.assertEqual(metrics["action_kind_counts"][12], 3)
        self.assertEqual(sum(metrics["action_kind_counts"]), 7)

    def test_immediate_budget_stop_produces_no_actions_or_sealed_batch(self):
        host, actor, store = collection_fixture()
        result = collect_episodes(host, actor, store, version=8, session=StopSession(host, 0))
        self.assertFalse(result.completed)
        self.assertEqual(result.metrics["discarded_rows"], 0)
        self.assertEqual((actor.calls, len(host.calls), store.seal_calls), (0, 0, 0))
        self.assertFalse(store.sealed)

    def test_mid_collection_budget_stop_discards_unresolved_rows_without_sealing(self):
        host, actor, store = collection_fixture()
        result = collect_episodes(host, actor, store, version=8, session=StopSession(host, 2))
        self.assertFalse(result.completed)
        self.assertEqual((result.metrics["discarded_rows"], result.metrics["completed_games"]), (6, 1))
        self.assertEqual(store.seal_calls, 0)
        self.assertFalse(store.sealed)

    def test_truncation_fails_before_sealing(self):
        host, actor, store = collection_fixture(truncated=(2,))
        with self.assertRaisesRegex(RuntimeError, "Administrative episode truncation"):
            collect_episodes(host, actor, store, version=1)
        self.assertEqual(store.seal_calls, 0)

    def test_held_terminal_replay_is_rejected(self):
        host, actor, store = collection_fixture(replay_held=True)
        with self.assertRaisesRegex(RuntimeError, "Held lane replayed"):
            collect_episodes(host, actor, store, version=1)
        self.assertEqual(store.seal_calls, 0)

    def test_counter_disagreement_is_rejected(self):
        host, actor, store = collection_fixture(counter_error=True)
        with self.assertRaisesRegex(RuntimeError, "counters disagree"):
            collect_episodes(host, actor, store, version=1)

    def test_invalid_terminal_reward_is_rejected_before_seal(self):
        host, actor, store = collection_fixture(invalid_reward=True)
        with self.assertRaisesRegex(RuntimeError, "Invalid terminal reward"):
            collect_episodes(host, actor, store, version=1)
        self.assertEqual(store.seal_calls, 0)

    def test_wrapper_cap_does_not_turn_unresolved_episode_into_draw(self):
        host, actor, store = collection_fixture()
        with self.assertRaisesRegex(RuntimeError, "wrapper-step bound"):
            collect_episodes(host, actor, store, version=1, max_steps=1)
        self.assertEqual(store.seal_calls, 0)


class CensoredCollectionTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        torch.set_num_threads(1)

    def test_whole_capped_lane_removed_before_real_credit_and_advantage_normalization(self):
        host, _, _ = collection_fixture(truncated=(0,))
        host.scripts[0][-1] = Decision(1, 1, round=400)
        store = EpisodeStore(64, "cpu")
        actor, opponent = TorchScriptedActor(3), TorchScriptedActor(3, action=1)
        with tempfile.TemporaryDirectory() as folder:
            result = collect_episodes(host, actor, store, version=7, opponent=opponent,
                opponent_lanes=[True, False, True], learner_seats=[1, 0, 0],
                censor_truncated=True, seed_base=5000, debug_dir=folder)
            files = list(Path(folder).glob("*.json"))
            self.assertEqual(len(files), 1)
            trace = json.loads(files[0].read_text())
            self.assertEqual(trace["action_indices"], [1, 0, 0, 1, 0])
            self.assertEqual((trace["engine_seed"], trace["pre_step_round"], trace["wrapper_decisions"]), (5000, 400, 5))
            self.assertEqual(trace["history_storage_dtype"], "int16")
            self.assertEqual(trace["pre_step_deciding_seat"], 1)
            self.assertTrue(trace["versus_archive"])
            self.assertEqual(result.metrics["censored_episodes"][0]["trace_path"], str(files[0].resolve()))
        self.assertTrue(result.completed and result.metrics["update_ready"] and store.sealed)
        metrics = result.metrics
        self.assertEqual((metrics["attempted_games"], metrics["completed_games"], metrics["censored_games"]), (3, 2, 1))
        self.assertEqual((metrics["attempted_learning_rows"], metrics["retained_learning_rows"], metrics["censored_learning_rows"]), (7, 4, 3))
        self.assertEqual((metrics["wrapper_decisions"], metrics["engine_submissions"]), (10, 9))
        self.assertEqual((metrics["draws"], metrics["seat0_wins"]), (1, 1))
        self.assertEqual(metrics["censored_lanes"], [0])
        self.assertEqual(metrics["censored_deciding_seats"], [1])
        self.assertEqual(metrics["censored_learner_seats"], [1])
        self.assertEqual(metrics["censored_opponent_lanes"], [True])
        self.assertEqual((metrics["archive_attempted_games"], metrics["archive_completed_games"], metrics["archive_censored_games"]), (2, 1, 1))
        self.assertEqual(metrics["archive_score"], .5)
        self.assertEqual(metrics["archive_score_bounds"], [.25, .75])
        self.assertEqual((metrics["action_kind_counts"][0], metrics["action_kind_counts"][12]), (3, 1))
        self.assertEqual(sum(metrics["action_kind_counts"]), 4)
        np.testing.assert_array_equal(store.episode_ids[:store.rows], [1, 1, 2, 1])
        np.testing.assert_array_equal(store.seats[:store.rows], [1, 1, 0, 0])
        torch.testing.assert_close(store.obs[:store.rows, 0], torch.tensor([101., 102., 202., 103.]))
        torch.testing.assert_close(store.actions[:store.rows], torch.zeros(4, dtype=torch.int64))
        torch.testing.assert_close(store.old_logp[:store.rows], torch.full((4,), -.25))
        torch.testing.assert_close(store.old_values[:store.rows], torch.tensor([.25, .25, -.25, -.25]))
        torch.testing.assert_close(store.returns, torch.tensor([-1., -1., 0., 1.]))
        raw = torch.tensor([-1.25, -1.25, .25, 1.25])
        torch.testing.assert_close(store.advantages, (raw-raw.mean())/raw.std(unbiased=False))
        self.assertTrue(bool(store.mask[:store.rows, :2].all()))
        self.assertEqual(store.candidates[2, 0, 12], 1)
        self.assertEqual(store.candidates[3, 0, 0], 1)

    def test_early_capped_lane_is_held_and_no_trace_file_without_a_cap(self):
        host, _, _ = collection_fixture(truncated=(2,))
        store = EpisodeStore(64, "cpu")
        result = collect_episodes(host, TorchScriptedActor(3), store, version=3, censor_truncated=True)
        self.assertTrue(result.completed)
        self.assertTrue(all(not active[2] for _, active in host.calls[2:]))
        self.assertFalse(np.any(store.episode_ids[:store.rows] == 2))
        self.assertEqual(result.metrics["draws"], 0, "Capped zero rewards are not draws")
        host, _, _ = collection_fixture()
        with tempfile.TemporaryDirectory() as folder:
            collect_episodes(host, TorchScriptedActor(3), EpisodeStore(64, "cpu"), version=4,
                             censor_truncated=True, debug_dir=folder)
            self.assertEqual(list(Path(folder).iterdir()), [])

    def test_all_censored_cohort_returns_accountable_metrics_but_no_update_batch(self):
        host = ScriptedHost([[Decision(0, 0)]]*4, [(0, 0)]*4, truncated=range(4))
        store = EpisodeStore(64, "cpu")
        result = collect_episodes(host, TorchScriptedActor(4), store, version=2,
                                  censor_truncated=True, opponent=TorchScriptedActor(4, 1),
                                  opponent_lanes=[True]*4, learner_seats=[0]*4)
        self.assertTrue(result.completed, "The caller must persist a fully drained cohort before enforcing its rate guard")
        self.assertEqual((result.metrics["attempted_games"], result.metrics["completed_games"], result.metrics["censored_games"]), (4, 0, 4))
        self.assertEqual(result.metrics["reason"], "all_episodes_censored")
        self.assertFalse(result.metrics["update_ready"] or store.sealed)
        self.assertEqual(store.rows, 0)
        self.assertIsNone(store.returns)
        self.assertEqual(result.metrics["draws"], 0)
        self.assertIsNone(result.metrics["archive_score"])
        self.assertEqual(result.metrics["archive_score_bounds"], [0., 1.])
        self.assertEqual(sum(result.metrics["action_kind_counts"]), 0)

    def test_censor_seat_arrays_align_with_lanes_when_caps_arrive_out_of_order(self):
        host, _, _ = collection_fixture(truncated=(0, 2))
        store = EpisodeStore(64, "cpu")
        result = collect_episodes(host, TorchScriptedActor(3), store, version=9,
                                  censor_truncated=True, learner_seats=[1, 0, 0])
        self.assertEqual([report["lane"] for report in result.metrics["censored_episodes"]], [2, 0])
        self.assertEqual(result.metrics["censored_lanes"], [0, 2])
        self.assertEqual(result.metrics["censored_deciding_seats"], [1, 0])
        self.assertEqual(result.metrics["censored_learner_seats"], [1, 0])
        self.assertEqual(result.metrics["censored_opponent_lanes"], [False, False])
        np.testing.assert_array_equal(store.episode_ids[:store.rows], [1, 1, 1])

    def test_partial_stop_after_cap_reports_unresolved_cohort_without_sealing(self):
        host, _, _ = collection_fixture(truncated=(2,))
        store = EpisodeStore(64, "cpu")
        result = collect_episodes(host, TorchScriptedActor(3), store, version=8,
                                  censor_truncated=True, session=StopSession(host, 2))
        self.assertFalse(result.completed or store.sealed)
        self.assertEqual((result.metrics["attempted_games"], result.metrics["completed_games"],
                          result.metrics["censored_games"], result.metrics["unresolved_games"]), (3, 0, 1, 2))
        self.assertEqual(result.metrics["discarded_rows"], 6)


class EvaluationControlTests(unittest.TestCase):
    def factories(self, **host_options):
        hosts, seeds = [], []

        def make_host(batch, workers, seed, **_):
            self.assertEqual(batch, 2)
            host = ScriptedHost([[Decision(0, 0)], [Decision(1, 1), Decision(0, 1), Decision(0, 0)]],
                                [(1, -1), (0, 0)], initial_counters=False, **host_options)
            hosts.append(host)
            seeds.append(seed)
            return host

        def make_actor(policy, batch, graph):
            self.assertTrue(graph)  # Exercise the graph-actor call site using a CPU fake.
            return ScriptedActor(batch, action=policy)

        return hosts, seeds, make_host, make_actor

    def test_seat_swap_uses_same_seeds_and_scores_only_old_episode_seat(self):
        hosts, seeds, make_host, make_actor = self.factories()
        with mock.patch("learning_eval.LearningHost", make_host), mock.patch("learning_model.LearningActor", make_actor):
            result = evaluate_match(0, 1, games=4, batch=2, seed=5000, telemetry=True)
        self.assertEqual(seeds, [5000, 5000])
        self.assertTrue(result["complete"])
        self.assertEqual((result["games"], result["wins_a"], result["draws"], result["losses_a"]), (4, 1, 2, 1))
        self.assertEqual(result["score_a"], .5)
        self.assertEqual((result["wrapper_decisions"], result["engine_submissions"]), (8, 8))
        self.assertEqual(result["mean_episode_decisions"], 2)
        self.assertEqual(result["max_episode_decisions"], 3)
        for seat_a, host in enumerate(hosts):
            self.assertTrue(host.closed)
            self.assertEqual([active.tolist() for _, active in host.calls],
                             [[True, True], [False, True], [False, True]])
            for _, _, owner, action in host.executed:
                self.assertEqual(action, 0 if owner == seat_a else 1)
        self.assertEqual(np.asarray(result["selected_actions_by_kind_card"]).sum(), 4)
        self.assertEqual(np.asarray(result["legal_candidate_opportunities_by_kind_card"]).sum(), 8)

    def test_evaluation_rejects_stale_held_terminal_and_closes_host(self):
        hosts, _, make_host, make_actor = self.factories(replay_held=True)
        with mock.patch("learning_eval.LearningHost", make_host), mock.patch("learning_model.LearningActor", make_actor):
            with self.assertRaisesRegex(RuntimeError, "Held evaluation lane"):
                evaluate_match(0, 1, games=4, batch=2)
        self.assertTrue(hosts[0].closed)

    def test_evaluation_truncation_is_not_a_draw(self):
        hosts, _, make_host, make_actor = self.factories(truncated=(0,))
        with mock.patch("learning_eval.LearningHost", make_host), mock.patch("learning_model.LearningActor", make_actor):
            with self.assertRaisesRegex(RuntimeError, "Truncated evaluation game"):
                evaluate_match(0, 1, games=4, batch=2)
        self.assertTrue(hosts[0].closed)


if __name__ == "__main__":
    unittest.main()
