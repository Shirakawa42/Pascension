"""Frozen opponent retention and checkpoint resume, using tiny CPU policies."""
import pathlib
import random
import copy
from collections import Counter
from unittest.mock import patch
from contextlib import redirect_stdout
import io
import json
import tempfile
import signal

import numpy as np
import sys
import unittest

import torch

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parents[1]))
from opponent_archive import OpponentArchive


def weights(generation):
    return {"weight": torch.tensor([float(generation)], dtype=torch.float32)}


class ArchiveTests(unittest.TestCase):
    def setUp(self):
        previous = random.getstate()
        self.addCleanup(random.setstate, previous)
        random.seed(73411)

    def test_historical_retains_anchor_recent_and_bounded_old_opponents(self):
        pool = OpponentArchive("historical", limit=12, every=8, generation=0,
                               current=weights(0))
        for generation in range(8, 24001, 8):
            pool.add(generation, weights(generation))
        summary = pool.snapshot(24000)
        self.assertEqual(len(pool.weights), 12)
        self.assertEqual([s["generation"] for s in summary["slots"] if s["pool"] == "recent"],
                         [23984, 23992, 24000])
        self.assertEqual(summary["history_seen"], 2997)
        self.assertEqual(sum(s["pool"] == "historical" for s in summary["slots"]), 8)
        self.assertEqual(pool.weights[0]["weight"].item(), 0)
        self.assertTrue(any(s["age_generations"] > 12000 for s in summary["slots"]
                            if s["pool"] == "historical"))

    def test_legacy_migration_freezes_current_policy_and_only_known_history(self):
        legacy = [weights(-1), *[weights(g) for g in range(80, 161, 8)]]
        before = random.getstate()
        pool = OpponentArchive("historical", limit=12, every=8, generation=163,
                               current=weights(163), archive=legacy)
        self.assertEqual(random.getstate(), before)
        summary = pool.snapshot(163)
        self.assertEqual(pool.weights[0]["weight"].item(), 163)
        self.assertEqual(summary["origin_generation"], 163)
        self.assertEqual(summary["history_seen"], 8)
        self.assertEqual([s["generation"] for s in summary["slots"] if s["pool"] == "recent"],
                         [144, 152, 160])
        self.assertEqual([s["generation"] for s in summary["slots"] if s["pool"] == "historical"],
                         list(range(80, 137, 8)))
        self.assertNotIn(-1, [w["weight"].item() for w in pool.weights])

    def test_checkpoint_resume_preserves_opponents_and_python_rng_exactly(self):
        pool = OpponentArchive("historical", limit=12, every=8, generation=0,
                               current=weights(0))
        for generation in range(8, 801, 8):
            pool.add(generation, weights(generation))
            pool.choose(generation)
        saved = {"archive": copy.deepcopy(pool.weights), "metadata": pool.state_dict(803)}
        saved_rng = random.getstate()

        def continuation(archive):
            choices = []
            for generation in range(808, 1601, 8):
                archive.add(generation, weights(generation))
                choices.append(archive.choose(generation)[1])
            return choices, archive.state_dict(1603), random.getstate()

        expected = continuation(pool)
        constructor_rng = random.getstate()
        resumed = OpponentArchive("historical", limit=12, every=8, generation=803,
                                  current=weights(803), archive=saved["archive"],
                                  metadata=saved["metadata"])
        self.assertEqual(random.getstate(), constructor_rng)
        random.setstate(saved_rng)
        self.assertEqual(continuation(resumed), expected)
        self.assertEqual([w["weight"].item() for w in pool.weights],
                         [w["weight"].item() for w in resumed.weights])

    def test_default_recent_preserves_legacy_choices_and_rng_for_all_limits(self):
        for limit in (1, 2, 5, 12):
            with self.subTest(limit=limit):
                pool = OpponentArchive("recent", limit=limit, every=8, generation=0,
                                       current=weights(0))
                legacy = [weights(0)]
                saved_rng = random.getstate()
                expected = []
                for generation in range(0, 401):
                    if generation and generation % 8 == 0:
                        legacy.append(weights(generation))
                        if len(legacy) > limit:
                            legacy = [legacy[0], *legacy[-(limit - 1):]] if limit > 1 else legacy[:1]
                    expected.append(random.choice(legacy)["weight"].item())
                expected_rng = random.getstate()
                random.setstate(saved_rng)
                actual = []
                for generation in range(0, 401):
                    if generation and generation % 8 == 0:
                        pool.add(generation, weights(generation))
                    actual.append(pool.choose(generation)[0]["weight"].item())
                self.assertEqual(actual, expected)
                self.assertEqual(random.getstate(), expected_rng)
                saved = pool.state_dict(400)
                resumed = OpponentArchive("recent", limit=limit, every=8, generation=400,
                                          current=weights(400), archive=pool.weights, metadata=saved)
                self.assertEqual(resumed.state_dict(400), saved)

    def test_input_weights_and_saved_metadata_are_owned_immutable_snapshots(self):
        source = weights(0)
        pool = OpponentArchive("historical", limit=5, every=8, generation=0, current=source)
        source["weight"].fill_(123)
        later = weights(8)
        pool.add(8, later)
        later["weight"].fill_(321)
        saved = pool.state_dict(8)
        saved["slots"][0]["generation"] = 999
        self.assertEqual([w["weight"].item() for w in pool.weights], [0, 8])
        self.assertEqual(pool.state_dict(8)["slots"][0]["generation"], 0)

    def test_malformed_metadata_and_changed_policy_identity_fail_closed(self):
        pool = OpponentArchive("historical", limit=12, every=8, generation=0, current=weights(0))
        for generation in range(8, 161, 8):
            pool.add(generation, weights(generation))
        metadata = pool.state_dict(163)
        with self.assertRaises(ValueError):
            OpponentArchive("historical", limit=12, every=8, generation=163,
                            current=weights(163), archive=pool.weights, metadata=None)
        mutations = [
            lambda m: m.update(strategy="recent"),
            lambda m: m.update(limit=11),
            lambda m: m.update(every=4),
            lambda m: m.update(generation=162),
            lambda m: m.update(history_seen=m["history_seen"] + 1),
            lambda m: m.update(history_seen=True),
            lambda m: m.update(last_update_generation=152),
            lambda m: m.update(initial_recent_count=4),
            lambda m: m.update(origin_generation=1),
            lambda m: m["slots"][0].update(pool="recent"),
            lambda m: m["slots"][1].update(id="foreign:144"),
            lambda m: m["slots"][1].update(generation=145, id="snapshot:145"),
            lambda m: m["slots"][-1].update(**m["slots"][1]),
            lambda m: m["slots"].pop(),
            lambda m: m.update(unrecognized=True),
        ]
        for mutation in mutations:
            malformed = copy.deepcopy(metadata)
            mutation(malformed)
            with self.subTest(malformed=malformed), self.assertRaises(ValueError):
                OpponentArchive("historical", limit=12, every=8, generation=163,
                                current=weights(163), archive=pool.weights, metadata=malformed)
        for bad_weights in ({"renamed": torch.zeros(1)}, {"weight": torch.zeros(2)},
                            {"weight": torch.zeros(1, dtype=torch.float64)}):
            with self.subTest(weights=bad_weights), self.assertRaises(ValueError):
                OpponentArchive("historical", limit=12, every=8, generation=163,
                                current=bad_weights, archive=pool.weights, metadata=metadata)

    def test_pool_requires_valid_slots_and_sequential_snapshot_updates(self):
        for strategy, limit in (("foreign", 12), ("historical", 4), ("historical", True), ("recent", 0)):
            with self.subTest(strategy=strategy, limit=limit), self.assertRaises(ValueError):
                OpponentArchive(strategy, limit=limit, every=8, generation=0, current=weights(0))
        pool = OpponentArchive("historical", limit=5, every=8, generation=0, current=weights(0))
        for generation in (7, 16, True):
            with self.subTest(generation=generation), self.assertRaises(ValueError):
                pool.add(generation, weights(0))
        self.assertEqual(pool.state_dict(0)["last_update_generation"], 0)
        for generation in (-1, 8):
            with self.subTest(generation=generation), self.assertRaises(ValueError):
                pool.choose(generation)

    def test_reservoir_gives_every_two_of_four_old_opponents_equal_weight(self):
        outcomes = Counter()
        for first in range(3):
            for second in range(4):
                pool = OpponentArchive("historical", limit=6, every=8, generation=0, current=weights(0))
                for generation in (8, 16, 24, 32, 40):
                    pool.add(generation, weights(generation))
                with patch("opponent_archive.random.randrange", side_effect=(first, second)):
                    pool.add(48, weights(48))
                    pool.add(56, weights(56))
                old = tuple(sorted(slot["generation"] for slot in pool.snapshot(56)["slots"]
                                   if slot["pool"] == "historical"))
                outcomes[old] += 1
        self.assertEqual(outcomes, {(8, 16): 2, (8, 24): 2, (8, 32): 2,
                                    (16, 24): 2, (16, 32): 2, (24, 32): 2})

    def test_legacy_short_pools_and_nonaligned_origins_resume(self):
        for limit in (5, 12):
            for generation in (0, 1, 7, 8, 9, 31, 32, 33, 163, 1655):
                with self.subTest(limit=limit, generation=generation):
                    count = min(limit - 1, generation // 8)
                    last = generation // 8 * 8
                    legacy = [weights(-1), *[weights(last - (count - 1 - i) * 8) for i in range(count)]]
                    pool = OpponentArchive("historical", limit=limit, every=8, generation=generation,
                                           current=weights(generation), archive=legacy)
                    metadata = pool.state_dict(generation)
                    restored = OpponentArchive("historical", limit=limit, every=8, generation=generation,
                                               current=weights(generation), archive=pool.weights, metadata=metadata)
                    for next_generation in (last + 8, last + 16):
                        restored.add(next_generation, weights(next_generation))
                    OpponentArchive("historical", limit=limit, every=8, generation=last + 19,
                                    current=weights(last + 19), archive=restored.weights,
                                    metadata=restored.state_dict(last + 19))

    def test_training_configuration_preserves_recent_default_and_rejects_invalid_strategy(self):
        from train import TrainConfig
        self.assertEqual(TrainConfig().archive_strategy, "recent")
        TrainConfig(archive_strategy="historical", archive_limit=12).validate()
        for strategy, limit in (("foreign", 12), (True, 12), ("historical", 4)):
            with self.subTest(strategy=strategy, limit=limit), self.assertRaises(ValueError):
                TrainConfig(archive_strategy=strategy, archive_limit=limit).validate()

    def test_cpu_training_checkpoint_resume_preserves_complete_learned_state_and_archive(self, strategy="historical"):
        """A tiny synthetic environment exercises real train/checkpoint APIs.

        It never creates a game engine, reads a learned model, or uses CUDA.
        """
        import train
        from campaign_state import load_checkpoint
        for number in (signal.SIGTERM, signal.SIGINT):
            self.addCleanup(signal.signal, number, signal.getsignal(number))
        self.addCleanup(torch.set_num_threads, torch.get_num_threads())
        self.addCleanup(np.random.set_state, np.random.get_state())
        self.addCleanup(torch.set_rng_state, torch.get_rng_state())
        toy_catalog = {"obs_dim": 4, "max_actions": 2, "action_dim": 18,
                       "card_ids": ["toy"], "heroes": []}

        class ToyHost:
            def __init__(self, batch, workers, seed, **kwargs):
                self.batch = batch
                self.obs = np.zeros((batch, 4), np.float32)
                self.candidates = np.zeros((batch, 2, 18), np.float32)
                self.candidates[:, :, 16] = 1 / 192
                self.candidates[:, 0, 0] = 1
                self.mask = np.ones((batch, 2), np.float32)
                self.actors = np.arange(batch, dtype=np.int32) % 2
                self.done = np.zeros(batch, np.int32)
                self.rewards = np.zeros((batch, 2), np.float32)
                self.reset(seed)

            def __enter__(self):
                return self

            def __exit__(self, *args):
                return False

            def reset(self, seed):
                self.seed = seed
                self.done[:] = 0
                self.rewards[:] = 0
                self.obs[:] = float(seed % 97) / 97

            def advance(self, actions):
                if not np.isin(actions, (0, 1)).all():
                    raise AssertionError("Illegal synthetic action")
                for lane, action in enumerate(actions):
                    winner = (self.seed + lane + int(action)) % 2
                    self.rewards[lane, winner] = 1
                    self.rewards[lane, 1 - winner] = -1
                self.done[:] = 1

        config = train.TrainConfig(batch=2, workers=1, width=64, capacity=64, minibatch=16,
                                   epochs=1, graph=False, adaptive_actors=False, packed_inputs=False,
                                   fused_optimizer=False, archive_strategy=strategy, archive_every=1,
                                   archive_limit=5, seed=761, archive_fraction=.5)
        with tempfile.TemporaryDirectory(dir=pathlib.Path(__file__).parent) as temporary:
            root = pathlib.Path(temporary)
            binary = root / "toy.bin"
            binary.write_bytes(b"synthetic CPU host identity")
            with patch.object(train, "Host", ToyHost), patch.object(train, "catalog", return_value=toy_catalog), \
                    patch.object(train, "BINARY", binary), patch.object(train, "source_fingerprint", return_value="synthetic"), \
                    redirect_stdout(io.StringIO()):
                full = train.train(config, root / "full", 60, max_games=16, device="cpu")
                partial = train.train(config, root / "split", 60, max_games=8, device="cpu")
                split = train.train(config, root / "split", 60, max_games=16, resume=partial, device="cpu")
            left = load_checkpoint(full, expected_identity=json.loads((full.parent / "identity.json").read_text()))
            right = load_checkpoint(split, expected_identity=json.loads((split.parent / "identity.json").read_text()))

            def same(a, b):
                if isinstance(a, torch.Tensor):
                    return torch.equal(a, b)
                if isinstance(a, dict):
                    return a.keys() == b.keys() and all(same(a[k], b[k]) for k in a)
                if isinstance(a, (tuple, list)):
                    return len(a) == len(b) and all(same(x, y) for x, y in zip(a, b))
                return a == b

            for name in ("policy", "optimizer", "archive", "opponent_archive", "generations", "games",
                         "next_engine_seed", "decisions", "optimizer_steps", "example_passes", "rolling_game_stats"):
                self.assertTrue(same(left["state"][name], right["state"][name]), name)
            self.assertTrue(same(left["rng"], right["rng"]))
            self.assertEqual(right["state"]["opponent_archive"]["history_seen"], 5)
            status = json.loads((root / "split/status.json").read_text())
            self.assertEqual(status["archive_pool"]["counts_by_pool"],
                             {"anchor": 1, "recent": 3, "historical": 1})
            metrics = [json.loads(line) for line in (root / "split/metrics.jsonl").read_text().splitlines()]
            generations = [entry for entry in metrics if entry.get("event") == "generation"]
            self.assertEqual(len(generations), 8)
            self.assertTrue(all("selected_opponent" in entry and "archive_pool" in entry for entry in generations))


if __name__ == "__main__":
    unittest.main()

class PrioritizedArchiveTests(unittest.TestCase):
    def pool(self):
        pool = OpponentArchive('prioritized', limit=6, every=8, generation=0, current=weights(0))
        for generation in range(8, 49, 8): pool.add(generation, weights(generation))
        return pool

    def test_measured_hard_opponents_receive_more_probability_with_uniform_floor(self):
        pool = self.pool()
        ids = [r['id'] for r in pool.records]
        self.assertTrue(np.allclose(pool.probabilities(48), np.full(6, 1 / 6)))
        pool.observe(48, ids[0], wins=120, draws=0, games=128)
        pool.observe(48, ids[1], wins=8, draws=0, games=128)
        probabilities = pool.probabilities(48)
        self.assertGreater(probabilities[1], probabilities[0] * 3)
        self.assertGreaterEqual(min(probabilities), .2 / 6)
        self.assertAlmostEqual(sum(probabilities), 1)
        # Stale measurements decay towards the symmetric prior.
        for generation in range(56, 4145, 8): pool.add(generation, weights(generation))
        self.assertLess(abs(pool.probabilities(4144)[0] - 1 / 6), .001)

    def test_payoffs_and_selection_rng_survive_resume_exactly(self):
        pool = self.pool(); key = pool.records[-1]['id']
        pool.observe(48, key, wins=7, draws=2, games=32)
        state = pool.state_dict(48); rng = random.getstate()
        expected = [pool.choose(48)[1] for _ in range(30)]
        restored = OpponentArchive('prioritized', limit=6, every=8, generation=48,
                                  current=weights(48), archive=pool.weights, metadata=state)
        random.setstate(rng)
        self.assertEqual(expected, [restored.choose(48)[1] for _ in range(30)])
        self.assertEqual(state, restored.state_dict(48))

    def test_outcomes_are_validated_and_evicted_payoffs_are_pruned(self):
        pool = self.pool(); key = pool.records[-1]['id']
        for outcome in [(2, 2, 3), (-1, 0, 3), (True, 0, 3), (0, 0, -1)]:
            with self.assertRaises(ValueError): pool.observe(48, key, wins=outcome[0], draws=outcome[1], games=outcome[2])
        with self.assertRaises(ValueError): pool.observe(48, 'foreign', wins=1, draws=0, games=2)
        pool.observe(48, key, wins=4, draws=0, games=32)
        for generation in range(56, 801, 8): pool.add(generation, weights(generation))
        self.assertLessEqual(set(pool.state_dict(800)['payoffs']), {r['id'] for r in pool.records})
        malformed = pool.state_dict(800); malformed['payoffs']['foreign'] = dict(score=0., games=1., generation=800)
        with self.assertRaises(ValueError):
            OpponentArchive('prioritized', limit=6, every=8, generation=800,current=weights(800),archive=pool.weights,metadata=malformed)

    def test_only_natural_archive_games_update_payoffs(self):
        pool = self.pool(); key = pool.records[0]['id']
        pool.observe_cohort(48, key, np.array([1, 1, 2, 1]),
            np.array([[1,-1],[-1,1],[0,0],[1,-1]]), np.array([True,True,True,False]),np.array([0,1,0,0]))
        payoff = pool.state_dict(48)['payoffs'][key]
        self.assertEqual(payoff['games'], 2)
        self.assertEqual(payoff['score'], 2)

    def test_real_training_priority_checkpoint_resume_is_identical(self):
        ArchiveTests.test_cpu_training_checkpoint_resume_preserves_complete_learned_state_and_archive(self, strategy="prioritized")


class ChallengerRetentionTests(unittest.TestCase):
    def pool(self):
        pool=OpponentArchive('prioritized',limit=12,every=8,generation=0,current=weights(0))
        for generation in range(8,97,8):pool.add(generation,weights(generation))
        return pool

    def test_known_hard_opponent_survives_expiry_even_when_reservoir_rejects_it(self):
        pool=self.pool();candidate=pool.recent[0]['id']
        pool.observe(96,candidate,wins=10,draws=0,games=64)
        with patch('opponent_archive.random.randrange',return_value=100000):pool.add(104,weights(104))
        self.assertIn(candidate,[r['id'] for r in pool.historical])
        self.assertIn(candidate,pool.payoffs)

    def test_random_history_does_not_evict_protected_challengers(self):
        pool=self.pool();candidate=pool.recent[0]['id'];pool.observe(96,candidate,wins=10,draws=0,games=64)
        with patch('opponent_archive.random.randrange',return_value=100000):pool.add(104,weights(104))
        slot=next(i for i,r in enumerate(pool.historical) if r['id']==candidate)
        with patch('opponent_archive.random.randrange',return_value=slot):pool.add(112,weights(112))
        self.assertIn(candidate,[r['id'] for r in pool.historical])
        self.assertEqual(len(pool.records),12)

    def test_tiny_and_stale_samples_cannot_force_admission(self):
        for count,stamp in [(8,96),(64,0)]:
            pool=self.pool();candidate=pool.recent[0]['id']
            pool.payoffs[candidate]={'score':0.,'games':float(count),'generation':stamp}
            if stamp==0:pool.payoffs[candidate]['games']=35.
            with patch('opponent_archive.random.randrange',return_value=100000):pool.add(104,weights(104))
            self.assertNotIn(candidate,[r['id'] for r in pool.historical])

    def test_random_half_remains_available_and_recent_snapshots_continue(self):
        pool=self.pool();candidate=pool.recent[0]['id'];protected=[r['id'] for r in pool.historical[:4]]
        with patch('opponent_archive.random.randrange',return_value=5):pool.add(104,weights(104))
        self.assertEqual(pool.historical[5]['id'],candidate)
        self.assertEqual([r['id'] for r in pool.historical[:4]],protected)
        self.assertEqual([r['generation'] for r in pool.recent],[88,96,104])

    def test_protected_pool_and_payoffs_restore_without_new_state_or_rng(self):
        pool=self.pool();candidate=pool.recent[0]['id'];pool.observe(96,candidate,wins=10,draws=0,games=64)
        pool.add(104,weights(104));saved_rng=random.getstate();state=pool.state_dict(104)
        resumed=OpponentArchive('prioritized',limit=12,every=8,generation=104,current=weights(104),archive=pool.weights,metadata=state)
        self.assertEqual(random.getstate(),saved_rng);self.assertEqual(resumed.state_dict(104),state)
        def onward(p):
            for g in range(112,201,8):p.add(g,weights(g));p.choose(g)
            return p.state_dict(200),random.getstate()
        expected=onward(pool);random.setstate(saved_rng);self.assertEqual(onward(resumed),expected)

    def test_better_observed_challenger_replaces_easiest_protected_opponent_only(self):
        pool=self.pool();candidate=pool.recent[0]['id']
        for i,r in enumerate(pool.historical[:4]):pool.payoffs[r['id']]={'score':float(5+i*10),'games':100.,'generation':96}
        pool.observe(96,candidate,wins=12,draws=0,games=100)
        expected=[r['id'] for r in pool.historical];expected[3]=candidate
        with patch('opponent_archive.random.randrange',return_value=100000):pool.add(104,weights(104))
        self.assertEqual([r['id'] for r in pool.historical],expected)
