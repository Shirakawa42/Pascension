"""CPU-only fixed-neural arena provenance, pairing, routing and owner tests."""
import hashlib
import json
import math
from pathlib import Path
import sys
import tempfile
import types
import unittest
from unittest import mock

import numpy as np
import torch

HERE = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(HERE))
sys.path.append(str(HERE.parent / "TrainingPreflight"))
import league
import monitor
from campaign_state import CampaignBudget, save_checkpoint_atomic, load_checkpoint as checked_load
from model import Policy, PolicyConfig

CATALOG = {"obs_dim": 96, "max_actions": 2, "action_dim": 18,
           "card_ids": ["test-card"], "heroes": [{"id": hero} for hero in league.HEROES],
           "observation_schema": "shards-zero-depth-observation-v2"}


class HeroAndSummaryTests(unittest.TestCase):
    def test_every_twenty_seed_cycle_contains_each_ordered_pair_once(self):
        expected = {(a, b) for a in league.HEROES for b in league.HEROES if a != b}
        for base in (0, 20, 1000, league.SEED_BASE, league.SEED_BASE + 200, (1 << 64) - 16):
            if base + 20 >= 1 << 64: continue
            self.assertEqual(base % 20, 0)
            pairs = [league.heroes_for_seed(base + i) for i in range(20)]
            self.assertEqual(set(pairs), expected)
            self.assertEqual(len(pairs), len(set(pairs)))
        with self.assertRaises(ValueError): league.heroes_for_seed(True)
        with self.assertRaises(ValueError): league.heroes_for_seed(1 << 64)

    def test_mirrored400_games_balance_every_hero_matchup_and_candidate_seat(self):
        records = []
        for seed in range(league.SEED_BASE, league.SEED_BASE + 200):
            heroes = league.heroes_for_seed(seed)
            for seat in (0, 1): records.append(league.ArenaGame(seed, seat, seed % 2, False, 12, *heroes))
        summary, rounds, coverage = league.summarize_arena(records, planned_pairs=200)
        self.assertEqual(summary["recorded_games"], 400)
        self.assertEqual(summary["resolved_game_score"], .5)
        self.assertFalse(summary["ahead_on_this_fixed_benchmark"])
        self.assertFalse(summary["promotion_allowed"])
        self.assertEqual(rounds["mean_natural_rounds"], 12)
        for row in coverage.values(): self.assertEqual(row, dict(games=20, candidate_seat0=10, candidate_seat1=10))

    def test_censored_and_missing_games_are_unknown_not_draws(self):
        seed = league.SEED_BASE
        records = [league.ArenaGame(seed, 0, None, True, None, *league.heroes_for_seed(seed))]
        summary, rounds, _ = league.summarize_arena(records, planned_pairs=200)
        self.assertEqual(summary["censored"], 1)
        self.assertEqual(summary["missing_games"], 399)
        self.assertEqual(summary["draws"], 0)
        self.assertEqual(summary["score_bounds_including_censors"], [0, 1])
        self.assertIsNone(summary["resolved_game_score"])
        self.assertEqual(rounds["natural_games_with_rounds"], 0)
        with self.assertRaises(ValueError): league.summarize_arena(records * 2, planned_pairs=200)

    def test_plan_preserves_reserved_namespace_alignment_and_caps(self):
        base = {"schema": league.PLAN_SCHEMA, "baseline": "/tmp/immutable-neural-baseline"}
        value = league.validate_plan(base)
        self.assertEqual(value["seed_base"], league.SEED_BASE)
        self.assertEqual(value["games"], 400)
        self.assertLess(value["max_seconds"], 121)
        self.assertEqual(league.validate_plan(base | {"max_seconds":600})["max_seconds"],600.)
        for field, invalid in (("games", 399), ("games", True), ("seed_base", 1 << 63),
                               ("seed_base", league.SEED_BASE + 1), ("max_seconds", float("nan")),
                               ("max_seconds", 601), ("workers", 9)):
            with self.subTest(field=field, invalid=invalid), self.assertRaises(ValueError):
                league.validate_plan(base | {field: invalid})


class PolicyExportTests(unittest.TestCase):
    def test_calibrated_export_cannot_silently_use_uncalibrated_inference(self):
        with tempfile.TemporaryDirectory() as temp:
            directory=Path(temp);original,_=self.make_checkpoint(directory)
            bundle=directory/'calibrated';metadata=league.export_frozen_policy(directory/'latest.soicp',bundle)
            metadata['required_inference_calibration']={'scry_temperature':64.,'scry_finish_bias':0.}
            (bundle/'manifest.json').write_text(json.dumps(metadata))
            with self.assertRaisesRegex(ValueError,'calibration-aware'):league.load_frozen_policy(bundle)
            loaded,got=league.load_frozen_policy(bundle,allow_external_calibration=True)
            self.assertEqual(got['required_inference_calibration'],metadata['required_inference_calibration'])
            for name,value in original.state_dict().items():self.assertTrue(torch.equal(value,loaded.state_dict()[name]))

    def make_checkpoint(self, directory):
        torch.set_num_threads(1)
        policy = Policy(CATALOG, PolicyConfig(width=64))
        identity = {"schema": "shards-zero-depth-training-v1", "source_fingerprint": "f" * 64,
                    "host_sha256": "a" * 64, "configuration": {"width": 64}}
        (directory / "identity.json").write_text(json.dumps(identity))
        state = {"catalog": CATALOG, "policy_config": {"width": 64, "key_width": 64},
                 "policy": policy.state_dict(), "optimizer": {"fabricated_moment": torch.zeros(8)},
                 "archive": [policy.state_dict()], "games": 100, "generations": 9,
                 "optimizer_steps": 33, "decisions": 4400}
        with CampaignBudget(directory / "budget.json", 10) as budget:
            save_checkpoint_atomic(directory / "latest.soicp", state, identity=identity, budget=budget, include_cuda_rng=False)
        return policy, identity

    def test_checked_export_is_policy_only_create_only_and_bit_exact(self):
        with tempfile.TemporaryDirectory() as temp:
            directory = Path(temp); original, identity = self.make_checkpoint(directory)
            bundle = directory / "baseline"
            metadata = league.export_frozen_policy(directory / "latest.soicp", bundle)
            loaded, got = league.load_frozen_policy(bundle, device="cpu")
            self.assertEqual(got, metadata)
            self.assertEqual(metadata["training"]["games"], 100)
            self.assertEqual(metadata["identity"], identity)
            compact = torch.load(bundle / "policy.pt", weights_only=True)
            self.assertNotIn("optimizer", compact); self.assertNotIn("archive", compact)
            for name, tensor in original.state_dict().items():
                self.assertTrue(torch.equal(tensor.reshape(-1).view(torch.uint8),
                                            loaded.state_dict()[name].reshape(-1).view(torch.uint8)), name)
            with self.assertRaises(FileExistsError): league.export_frozen_policy(directory / "latest.soicp", bundle)
            pin = league.sha256_file(bundle / "manifest.json")
            league.verify_frozen_policy(bundle, manifest_sha256=pin)
            with (bundle / "policy.pt").open("ab") as stream: stream.write(b"tampered")
            with self.assertRaises(ValueError): league.verify_frozen_policy(bundle)

    def test_atomic_source_replacement_cannot_mix_weights_and_provenance(self):
        with tempfile.TemporaryDirectory() as temp:
            directory = Path(temp); original, _ = self.make_checkpoint(directory)
            path = directory / "latest.soicp"; old_hash = league.sha256_file(path)
            def replace_then_load(snapshot, **kwargs):
                replacement = directory / "replacement.tmp"; replacement.write_bytes(b"new atomic incomplete checkpoint")
                replacement.replace(path)
                return checked_load(snapshot, **kwargs)
            with mock.patch("campaign_state.load_checkpoint", side_effect=replace_then_load):
                metadata = league.export_frozen_policy(path, directory / "baseline")
            self.assertEqual(metadata["checkpoint_sha256"], old_hash)
            self.assertEqual(metadata["training"]["games"], 100)
            self.assertEqual(path.read_bytes(), b"new atomic incomplete checkpoint")

    def test_identity_and_finite_guards_fail_before_policy_credit(self):
        with tempfile.TemporaryDirectory() as temp:
            directory = Path(temp); self.make_checkpoint(directory)
            with self.assertRaises(Exception):
                league.export_frozen_policy(directory / "latest.soicp", directory / "bad", expected_identity={"wrong": True})
            self.assertFalse((directory / "bad").exists())
            bundle = directory / "baseline"; league.export_frozen_policy(directory / "latest.soicp", bundle)
            value = torch.load(bundle / "policy.pt", weights_only=True)
            value["policy"]["query.bias"][0] = float("nan")
            torch.save(value, bundle / "policy.pt")
            metadata = json.loads((bundle / "manifest.json").read_text())
            metadata["bytes"] = (bundle / "policy.pt").stat().st_size
            metadata["policy_file_sha256"] = league.sha256_file(bundle / "policy.pt")
            (bundle / "manifest.json").write_text(json.dumps(metadata))
            with self.assertRaises(ValueError): league.load_frozen_policy(bundle, device="cpu")


class ArenaTransportTests(unittest.TestCase):
    def fixture(self, *, bad_packet=False, stop_after_one=False):
        hosts, routed = [], []
        class FakeHost:
            def __init__(self, **kwargs):
                self.batch = kwargs["batch"]; self.seed = kwargs["seed"]
                self.pass_seat = len(hosts) % 2; self.closed = False; self.steps = 0
                self.kwargs = kwargs; hosts.append(self)
                self.obs = np.zeros((self.batch, 96), np.float32)
                self.obs[:, 10] = -0.
                self.candidates = np.zeros((self.batch, 2, 18), np.float32)
                self.mask = np.ones((self.batch, 2), np.float32)
                self.done = np.zeros(self.batch, np.int32); self.actors = np.zeros(self.batch, np.int32)
                self.rewards = np.zeros((self.batch, 2), np.float32)
                self.features()
            def features(self):
                for lane in range(self.batch):
                    heroes = league.heroes_for_seed(self.seed + lane)
                    actor = int(self.actors[lane])
                    self.obs[lane, 22] = (league.HEROES.index(heroes[actor]) + 1) / 5
                    self.obs[lane, 86] = (league.HEROES.index(heroes[1 - actor]) + 1) / 5
            def advance(self, actions):
                active = self.done == 0
                expected = np.where(self.actors == self.pass_seat, 0, 1)
                np.testing.assert_array_equal(actions[active], expected[active])
                np.testing.assert_array_equal(actions[~active], -1)
                self.steps += 1
                new = active & ((np.arange(self.batch) % 3 == 0) | (self.steps > 1))
                for lane in np.flatnonzero(new):
                    seed = self.seed + int(lane)
                    self.done[lane] = 2 if seed % 17 == 0 else 1
                    self.rewards[lane] = [1, -1] if seed % 2 == 0 else [-1, 1]
                    self.mask[lane] = 0
                    self.obs[lane, 2] = .12
                self.actors[~new] = 1 - self.actors[~new]; self.features()
            def close(self): self.closed = True
        class FakeActor:
            def __init__(self, policy, batch, **kwargs): self.name = policy.name; self.batch = batch
            def act(self, host, selected):
                routed.append((self.name, selected.copy(), host.actors.copy(), host.pass_seat, host.done.copy()))
                actions = np.full(self.batch, 0 if self.name == "candidate" else 1, np.int32)
                packet = np.zeros((self.batch, 3), np.float32)
                if bad_packet: packet[selected, 1] = np.nan
                return actions, packet
        def loader(path, **kwargs):
            name = Path(path).name
            return types.SimpleNamespace(name=name, catalog=CATALOG), {"identity": {}, "training": {"games": 100}, "checkpoint_sha256": name}
        stop = (lambda: hosts and hosts[-1].steps >= 1) if stop_after_one else None
        return hosts, routed, dict(_host_factory=FakeHost, _actor_factory=FakeActor, _loader=loader, _catalog=CATALOG, stop_check=stop)

    def test_full400_games_exactmirrors_seat_routing_and_terminal_ownership(self):
        hosts, routed, fixtures = self.fixture()
        with tempfile.TemporaryDirectory() as temp:
            result = league.run_neural_arena("candidate", "baseline", Path(temp) / "arena.json", device="cpu", graph=False, **fixtures)
        self.assertTrue(result["summary"]["evaluation_finished"])
        self.assertEqual(result["summary"]["recorded_games"], 400)
        self.assertTrue(result["initial_publication_mirrors_verified"])
        self.assertEqual(len(result["mirror_initial_publications"]), 10)
        self.assertEqual(len(hosts), 20)
        for first, second in zip(hosts[::2], hosts[1::2]):
            self.assertEqual(first.seed, second.seed)
            self.assertFalse(first.kwargs["paired"])
            self.assertEqual(first.kwargs["hero_mode"], "balanced_random")
        self.assertTrue(all(host.closed for host in hosts))
        for brain, selected, actors, seat, done in routed:
            self.assertFalse(done[selected].any())
            self.assertTrue(np.all(actors[selected] == seat) if brain == "candidate" else np.all(actors[selected] != seat))
        for row in result["hero_coverage"].values():
            self.assertEqual(row, dict(games=20, candidate_seat0=10, candidate_seat1=10))
        self.assertGreater(result["summary"]["censored"], 0)
        self.assertEqual(result["summary"]["draws"], 0)

    def test_owner_stop_records_partial_as_missing_and_closes_owned_host(self):
        hosts, _, fixtures = self.fixture(stop_after_one=True)
        with tempfile.TemporaryDirectory() as temp:
            result = league.run_neural_arena("candidate", "baseline", Path(temp) / "arena.json", device="cpu", graph=False, **fixtures)
        self.assertEqual(result["stop_reason"], "deadline_or_owner_stop")
        self.assertGreater(result["summary"]["missing_games"], 0)
        self.assertFalse(result["summary"]["ahead_on_this_fixed_benchmark"])
        self.assertTrue(all(host.closed for host in hosts))

    def test_nonfinite_packet_fails_before_host_advance_and_persists_failure(self):
        hosts, _, fixtures = self.fixture(bad_packet=True)
        with tempfile.TemporaryDirectory() as temp:
            output = Path(temp) / "arena.json"
            with self.assertRaises(ValueError): league.run_neural_arena("candidate", "baseline", output, device="cpu", graph=False, **fixtures)
            saved = json.loads(output.read_text())
            self.assertIn("Nonfinite", saved["stop_reason"])
            self.assertFalse(saved["summary"]["evaluation_finished"])
            self.assertFalse(saved["integrity_verified"])
            self.assertEqual(saved["summary"]["verdict"], "invalid")
        self.assertEqual(hosts[0].steps, 0)
        self.assertTrue(hosts[0].closed)

    def test_integrity_failure_after_all_outcomes_cannot_claim_benchmark_ahead(self):
        hosts, _, fixtures = self.fixture()
        close = league._close_host
        def fail_last_close(host):
            close(host)
            if len(hosts) == 20: raise ValueError("Injected final owned-Host cleanup failure")
        with tempfile.TemporaryDirectory() as temp:
            output = Path(temp) / "arena.json"
            with mock.patch("league._close_host", side_effect=fail_last_close), self.assertRaisesRegex(ValueError, "cleanup failure"):
                league.run_neural_arena("candidate", "baseline", output, device="cpu", graph=False, **fixtures)
            result = json.loads(output.read_text())
        self.assertEqual(result["summary"]["recorded_games"], 400)
        self.assertTrue(result["summary"]["evaluation_finished"])
        self.assertFalse(result["summary"]["ahead_on_this_fixed_benchmark"])
        self.assertEqual(result["summary"]["verdict"], "invalid")
        self.assertFalse(result["integrity_verified"])
        self.assertFalse(result["initial_publication_mirrors_verified"])


class ArenaOwnerTests(unittest.TestCase):
    def test_parent_startticks_trainer_phase_and_deadline_are_required(self):
        with tempfile.TemporaryDirectory() as temp:
            campaign = Path(temp)
            parent = {"pid": 101, "startticks": 33, "command": ["python", "launch.py"]}
            trainer = {"pid": 102, "startticks": 44, "command": ["python", "train.py", "--run-dir", str(campaign)]}
            identities = {101: parent.copy(), 102: trainer.copy()}
            owner = league.ArenaOwner(campaign, 101, reader=lambda pid: identities.get(pid))
            active = {"pid": 102, "session_id": "training-session", "boot_id": owner.boot, "hard_deadline_monotonic": 1000,
                      "hard_deadline_wall": 2000, "last_heartbeat_monotonic": 100}
            def write(active): (campaign / "budget.json").write_text(json.dumps({"active": active}))
            write(active)
            with mock.patch("league.time.monotonic", return_value=100), mock.patch("league.time.time", return_value=1100):
                self.assertEqual(owner.remaining(), 900)
                identities[101] = parent | {"startticks": 34}
                self.assertIsNone(owner.remaining()); identities[101] = parent
                write(active | {"boot_id": "different"}); self.assertIsNone(owner.remaining())
                write(None); self.assertIsNone(owner.remaining())
                write(active); identities[102] = trainer | {"command": ["python", "evaluate.py"]}
                self.assertIsNone(owner.remaining()); identities[102] = trainer
                write(active | {"last_heartbeat_monotonic": 1}); self.assertIsNone(owner.remaining())
                write(active | {"last_heartbeat_monotonic": 102}); self.assertIsNone(owner.remaining())
                write(active | {"hard_deadline_monotonic": 1001}); self.assertIsNone(owner.remaining())
                write(active | {"hard_deadline_wall": 2001}); self.assertIsNone(owner.remaining())
                write(active | {"session_id": "replacement-session"}); self.assertIsNone(owner.remaining())
                write(active | {"hard_deadline_monotonic": 99}); self.assertIsNone(owner.remaining())
                write(active)
            with mock.patch("league.time.monotonic", return_value=1001), mock.patch("league.time.time", return_value=2001):
                write(active | {"last_heartbeat_monotonic": 1001})
                self.assertEqual(owner.remaining(), -1)


class WatcherTests(unittest.TestCase):
    def test_resume_threshold_does_not_repeat_previous_arena_and_stops_with_owner(self):
        with tempfile.TemporaryDirectory() as temp:
            campaign = Path(temp); baseline = campaign / "baseline"
            (campaign / "league").mkdir()
            (campaign / "league" / "latest.json").write_text(json.dumps({"schema": league.ARENA_SCHEMA,
                "baseline": {"checkpoint_sha256": "immutable-baseline"}, "candidate": {"training": {"games": 100000}}}))
            (campaign / "status.json").write_text(json.dumps({"games": 100001}))
            owner = mock.Mock(); owner.remaining.side_effect = [1000, 1000, 1000, 1000, None]
            before = {number: league.signal.getsignal(number) for number in (league.signal.SIGTERM, league.signal.SIGINT)}
            with mock.patch("league.ArenaOwner", return_value=owner), mock.patch("league.verify_frozen_policy", return_value={"training": {"games": 0}, "checkpoint_sha256": "immutable-baseline"}), mock.patch("league.export_frozen_policy") as export, mock.patch("league.time.sleep"):
                league.watch(campaign, baseline, parent_pid=10, device="cpu")
            export.assert_not_called()
            for number, handler in before.items(): self.assertEqual(league.signal.getsignal(number), handler)

    def test_export_and_arena_share_total_cap_and_raw_elapsed_includes_export(self):
        with tempfile.TemporaryDirectory() as temp:
            campaign = Path(temp); baseline = campaign / "baseline"
            (campaign / "status.json").write_text(json.dumps({"games": 100000}))
            owner = mock.Mock(); owner.remaining.return_value = 1000
            metadata = {"training": {"games": 100000}, "checkpoint_sha256": "c" * 64}
            def arena(*args, **kwargs):
                self.assertEqual(kwargs["deadline_monotonic"], 1120)
                self.assertIsNotNone(kwargs["stop_check"])
                owner.remaining.return_value = None
                return {"summary": {}, "candidate": metadata}
            with mock.patch("league.ArenaOwner", return_value=owner), mock.patch("league.verify_frozen_policy", return_value={"training": {"games": 0}, "checkpoint_sha256": "baseline"}), mock.patch("league.export_frozen_policy", return_value=metadata), mock.patch("league.run_neural_arena", side_effect=arena), mock.patch("league.time.monotonic", return_value=1000), mock.patch("league.raw_clock", side_effect=[700, 703]), mock.patch("league.time.sleep"):
                league.watch(campaign, baseline, parent_pid=10, device="cpu")
            result = json.loads((campaign / "league" / "latest.json").read_text())
            self.assertTrue(result["export_and_setup_included_in_cap"])
            self.assertEqual(result["sidecar_elapsed_seconds"], 3)

    def test_no_arena_near_deadline_and_changed_plan_fails_closed(self):
        with tempfile.TemporaryDirectory() as temp:
            campaign = Path(temp); baseline = campaign / "baseline"
            plan = {"schema": league.PLAN_SCHEMA, "baseline": str(baseline), "device": "cpu"}
            path = campaign / "league-plan.json"; path.write_text(json.dumps(plan))
            owner = mock.Mock(); owner.remaining.return_value = 139
            with mock.patch("league.ArenaOwner", return_value=owner), mock.patch("league.verify_frozen_policy", return_value={"training": {"games": 0}, "checkpoint_sha256": "baseline"}), mock.patch("league.export_frozen_policy") as export:
                league.watch(campaign, baseline, parent_pid=10, device="cpu")
            export.assert_not_called()
            owner.remaining.return_value = 1000
            def change_plan(*args, **kwargs):
                path.write_text(json.dumps(plan | {"games": 40}))
                return {"training": {"games": 0}, "checkpoint_sha256": "baseline"}
            with mock.patch("league.ArenaOwner", return_value=owner), mock.patch("league.verify_frozen_policy", side_effect=change_plan), self.assertRaisesRegex(ValueError, "plan changed"):
                league.watch(campaign, baseline, parent_pid=10, device="cpu")


class MonitorLeagueTests(unittest.TestCase):
    def test_json_only_summary_and_archive_passthrough_exclude_per_game_arrays(self):
        with tempfile.TemporaryDirectory() as temp:
            campaign = Path(temp); (campaign / "league").mkdir()
            report = {"schema": league.ARENA_SCHEMA, "purpose": "exploratory_fixed_neural_arena",
                "summary": {"promotion_allowed": False, "resolved_game_score": .5}, "promotion_allowed": False,
                "candidate": {"training": {"games": 100000}, "checkpoint_sha256": "candidate", "identity": {"source_fingerprint": "source"}},
                "baseline": {"training": {"games": 0}}, "results": [{"private_large_array": [0] * 10}],
                "integrity_verified": True, "rounds": {"mean_natural_rounds": 12}}
            (campaign / "league" / "latest.json").write_text(json.dumps(report))
            (campaign / "status.json").write_text(json.dumps({"archive_pool": {"strategy": "historical12"}}))
            (campaign / "metrics.jsonl").write_text(json.dumps({"event": "generation", "archive_pool": {"count": 12}, "selected_opponent": {"pool": "historical"}}) + "\n")
            with mock.patch("torch.load", side_effect=AssertionError("Monitor must not load checkpoints")):
                view = monitor.Monitor(campaign).state()
            self.assertEqual(view["league"]["candidate"]["training"]["games"], 100000)
            self.assertNotIn("results", view["league"])
            self.assertEqual(view["archive_pool"], {"count": 12})
            self.assertEqual(view["selected_opponent"], {"pool": "historical"})
            self.assertFalse(view["league"]["promotion_allowed"])


if __name__ == "__main__": unittest.main()

class ChampionGateTests(unittest.TestCase):
    def result(self, score, trial=1, games=400):
        from dataclasses import asdict
        seed_base = league.champion_seed(trial)
        records = []
        for index in range(games):
            seed, seat = seed_base + index // 2, index % 2
            records.append(asdict(league.ArenaGame(seed, seat, seat if index < round(score * games) else 1-seat,
                                                 False, 12, *league.heroes_for_seed(seed))))
        return {'results': records, 'planned_games':games, 'seed_base':seed_base,
                'integrity_verified':True,'initial_publication_mirrors_verified':True}

    def test_larger_late_trial_can_detect_modest_gains_without_relaxing_alpha(self):
        trial=30
        old=league.champion_decision(self.result(.5725,trial),trial)
        new=league.champion_decision(self.result(.5725,trial,3200),trial,expected_games=3200)
        self.assertFalse(old['promote']);self.assertTrue(new['promote'])
        self.assertEqual(old['alpha'],new['alpha'])
        self.assertFalse(league.champion_decision(self.result(.52,trial,3200),trial,expected_games=3200)['promote'])
        self.assertLess(league.champion_seed(trial)+1600,league.champion_seed(trial+1))

    def test_small_noisy_lead_cannot_replace_champion(self):
        self.assertFalse(league.champion_decision(self.result(.55),1)['promote'])
        self.assertTrue(league.champion_decision(self.result(.70),1)['promote'])

    def test_invalid_incomplete_or_wrong_seed_evidence_cannot_promote(self):
        for field in ['integrity_verified','initial_publication_mirrors_verified']:
            result=self.result(.9);result[field]=False
            self.assertFalse(league.champion_decision(result,1)['promote'])
        result=self.result(.9);result['results'].pop()
        self.assertFalse(league.champion_decision(result,1)['promote'])
        with self.assertRaises(ValueError):league.champion_decision(self.result(.9),2)

    def test_repeated_trials_spend_bounded_error_budget_and_use_fresh_seeds(self):
        alphas=[]
        for trial in range(1,101):
            alphas.append(league.champion_decision(self.result(.7,trial),trial)['alpha'])
        self.assertLess(sum(alphas),.05)
        self.assertGreater(league.champion_seed(2),league.champion_seed(1)+200)

    def test_bootstrap_and_promotion_keep_immutable_complete_policy(self):
        import shutil
        fixture=PolicyExportTests()
        with tempfile.TemporaryDirectory() as temporary:
            root=Path(temporary);fixture.make_checkpoint(root);checkpoint=root/"latest.soicp"
            bundle=root/'bundle';league.export_frozen_policy(checkpoint,bundle)
            campaign=root/'campaign';campaign.mkdir()
            state=league.ensure_champion(campaign,bundle)
            directory=campaign/'champion'/state['directory']
            self.assertTrue((directory/'policy.pt').is_file())
            self.assertEqual(league.ensure_champion(campaign,bundle),state)
            (directory/'policy.pt').write_bytes(b'corrupt')
            with self.assertRaises(ValueError):league.ensure_champion(campaign,bundle)

    def test_challenge_promotes_only_winner_and_keeps_trial_sequence_after_restart(self):
        fixture=PolicyExportTests()
        with tempfile.TemporaryDirectory() as temporary:
            root=Path(temporary); _,identity=fixture.make_checkpoint(root)
            old=root/'old';league.export_frozen_policy(root/'latest.soicp',old)
            campaign=root/'campaign';campaign.mkdir();league.ensure_champion(campaign,old)
            payload=checked_load(root/'latest.soicp',expected_identity=identity)
            payload['state']['games']=200
            with CampaignBudget(root/'budget.json',10) as budget:
                save_checkpoint_atomic(root/'latest.soicp',payload['state'],identity=identity,budget=budget,include_cuda_rng=False)
            candidate=root/'candidate';league.export_frozen_policy(root/'latest.soicp',candidate)
            def arena(*args,**kwargs):
                trial=league.read_champion(campaign)['trials']
                self.assertEqual(kwargs["max_seconds"],600.)
                self.assertEqual(kwargs["batch"],64)
                self.assertEqual(kwargs["deadline_monotonic"],1e12)
                result=self.result(.50 if trial==1 else .8,trial,games=kwargs["games"])
                result['baseline']=league.verify_frozen_policy(args[1])
                return result
            with mock.patch.object(league,'run_neural_arena',side_effect=arena):
                result=league.challenge_champion(campaign,candidate,stop_check=lambda:False,deadline_monotonic=1e12)
                self.assertFalse(result['promote']);self.assertEqual(league.read_champion(campaign)['training']['games'],100)
                result=league.challenge_champion(campaign,candidate,stop_check=lambda:False,deadline_monotonic=1e12)
                self.assertTrue(result['promote']);self.assertEqual(league.read_champion(campaign)['training']['games'],200)
            state=league.read_champion(campaign)
            self.assertEqual((state['trials'],state['promotions']),(2,1))
            self.assertTrue((campaign/'champion/latest.json').is_file())
