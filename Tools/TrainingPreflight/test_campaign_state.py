"""CPU-only regression tests for campaign persistence; no outcome learning."""

import copy
import hashlib
import io
import json
import os
from pathlib import Path
import random
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import patch

import numpy as np
import torch

import campaign_state as c


class Clock:
    def __init__(self):
        self.mono, self.wall, self.boot = 1000.0, 100000.0, "test-boot-a"

    def monotonic(self):
        return self.mono

    def time(self):
        return self.wall

    def boot_id(self):
        return self.boot

    def advance(self, seconds):
        self.mono += seconds
        self.wall += seconds


class CampaignFixture(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.root = Path(self.temporary.name)
        self.path = self.root / "budget.json"
        self.clock = Clock()
        # AdamW's generic health check can query/initialize CUDA even for CPU
        # parameters. These persistence tests deliberately expose CPU only.
        self.cuda_unavailable = patch("torch.cuda.is_available", return_value=False)
        self.cuda_unavailable.start()

    def tearDown(self):
        self.cuda_unavailable.stop()
        self.temporary.cleanup()

    def budget(self):
        return c.CampaignBudget(self.path, limit_seconds=100, clock=self.clock)

    def abandon(self, budget):
        # Simulate process exit releasing flock without a graceful close. A
        # separate actual os._exit subprocess test covers OS lock release too.
        os.close(budget._fd)
        budget._fd = None


class CampaignTests(CampaignFixture):
    def test_pilots_share_one_budget_and_gaps_are_free(self):
        with self.budget() as budget:
            with budget.start_session("pilot-a", 30, 5) as session:
                self.clock.advance(10)
                session.heartbeat()
                self.assertEqual(budget.charged_seconds, 10)
            self.clock.advance(500)
            self.assertEqual(budget.remaining_seconds, 90)
            with budget.start_session("pilot-b", 30, 5):
                self.clock.advance(12)
        with self.budget() as budget:
            self.assertEqual(budget.charged_seconds, 22)
            with budget.start_session("main", 200, 5) as session:
                self.assertEqual(session.remaining_seconds, 78)
                self.clock.advance(74)
                self.assertTrue(session.should_stop)
                self.assertEqual(session.hard_deadline_monotonic, self.clock.mono + 4)
                self.clock.advance(4)
            self.assertEqual(budget.remaining_seconds, 0)
            with self.assertRaises(c.BudgetExceeded):
                budget.start_session("extra", 1, 0)

    def test_wall_clock_changes_do_not_change_running_charge(self):
        with self.budget() as budget:
            with budget.start_session("pilot", 30, 5) as session:
                self.clock.advance(5)
                self.clock.wall -= 10000
                session.heartbeat()
                self.assertEqual(budget.charged_seconds, 5)

    def test_existing_limit_cannot_be_extended(self):
        with self.budget():
            pass
        with self.assertRaises(c.CampaignStateError):
            with c.CampaignBudget(self.path, limit_seconds=101, clock=self.clock):
                pass
        with self.assertRaises(c.CampaignStateError):
            c.CampaignBudget(self.path, limit_seconds=43201)

    def test_exclusive_lock_in_another_process(self):
        code = "import campaign_state as c,sys\ntry:\n with c.CampaignBudget(sys.argv[1],100): pass\nexcept c.CampaignLocked:\n sys.exit(7)\n"
        env = {**os.environ, "PYTHONPATH": str(Path(c.__file__).parent)}
        with self.budget():
            child = subprocess.run([sys.executable, "-c", code, str(self.path)], env=env, timeout=5)
            self.assertEqual(child.returncode, 7)

    def test_unclean_same_boot_charges_uncertain_gap_and_discards_reported_rows(self):
        budget = self.budget().__enter__()
        session = budget.start_session("pilot", 30, 5)
        self.clock.advance(7)
        session.heartbeat({"unresolved_episodes": 3, "unresolved_decisions": 19})
        self.abandon(budget)
        self.clock.advance(5)
        with self.budget() as recovered:
            self.assertEqual(recovered.charged_seconds, 12)
            self.assertEqual(recovered.snapshot()["discarded_episodes"], 3)
            self.assertEqual(recovered.snapshot()["discarded_decisions"], 19)
            self.assertTrue(recovered.last_recovery["unreported_tail_may_also_be_discarded"])

    def test_unclean_charge_is_capped_by_original_session_grant(self):
        budget = self.budget().__enter__()
        budget.start_session("pilot", 30, 5)
        self.abandon(budget)
        self.clock.advance(500)
        with self.budget() as recovered:
            self.assertEqual(recovered.charged_seconds, 30)
            self.assertEqual(recovered.remaining_seconds, 70)

    def test_exception_close_records_known_uncheckpointed_work_once(self):
        with self.assertRaisesRegex(RuntimeError, "learner failed"):
            with self.budget() as budget:
                with budget.start_session("pilot", 30, 5) as session:
                    self.clock.advance(7)
                    session.heartbeat({"unresolved_episodes": 3, "unresolved_decisions": 19})
                    raise RuntimeError("learner failed")
        with self.budget() as recovered:
            snapshot = recovered.snapshot()
            self.assertEqual(snapshot["charged_seconds"], 7)
            self.assertEqual(snapshot["discarded_episodes"], 3)
            self.assertEqual(snapshot["discarded_decisions"], 19)
            self.assertIsNone(recovered.last_recovery)
            self.assertEqual(len(recovered._data["discard_events"]), 1)
            self.assertTrue(recovered._data["discard_events"][0]["unknown_unreported_tail"])

    def test_reboot_or_backwards_monotonic_consumes_full_grant(self):
        for change in ("boot", "monotonic"):
            path = self.root / f"{change}.json"
            budget = c.CampaignBudget(path, 100, clock=self.clock).__enter__()
            session = budget.start_session("pilot", 30, 5)
            self.clock.advance(3)
            session.heartbeat()
            self.abandon(budget)
            if change == "boot":
                self.clock.boot = "test-boot-b"
            else:
                self.clock.mono -= 100
            with c.CampaignBudget(path, 100, clock=self.clock) as recovered:
                self.assertEqual(recovered.charged_seconds, 30)

    def test_process_crash_releases_lock_and_recovers_dirty_session(self):
        code = "import campaign_state as c,os,sys\nb=c.CampaignBudget(sys.argv[1],100).__enter__()\ns=b.start_session('crashed',2,0)\ns.heartbeat({'unresolved_episodes':1,'unresolved_decisions':2})\nos._exit(9)\n"
        env = {**os.environ, "PYTHONPATH": str(Path(c.__file__).parent)}
        child = subprocess.run([sys.executable, "-c", code, str(self.path)], env=env, timeout=5)
        self.assertEqual(child.returncode, 9)
        with c.CampaignBudget(self.path, 100) as recovered:
            self.assertIsNotNone(recovered.last_recovery)
            self.assertGreater(recovered.charged_seconds, 0)
            self.assertLessEqual(recovered.charged_seconds, 2)

    def test_corrupt_or_rolled_back_total_never_resets_allocation(self):
        self.path.write_text("{not json")
        with self.assertRaises(c.CampaignStateError):
            with self.budget():
                pass
        self.path.unlink()
        with self.budget() as budget:
            with budget.start_session("pilot", 30, 5):
                self.clock.advance(4)
        data = json.loads(self.path.read_text())
        data["charged_seconds"] = 0
        self.path.write_text(json.dumps(data))
        with self.assertRaises(c.CampaignStateError):
            with self.budget():
                pass

    def test_overrun_is_recorded_and_rejected_not_silently_capped(self):
        with self.budget() as budget:
            session = budget.start_session("pilot", 10, 2)
            self.clock.advance(12)
            with self.assertRaises(c.BudgetExceeded):
                session.close()
            self.assertEqual(budget.charged_seconds, 12)
            self.assertEqual(budget._data["sessions"][-1]["overrun_seconds"], 2)

    def test_heartbeat_failure_poisons_object_and_keeps_dirty_disk_state(self):
        budget = self.budget().__enter__()
        session = budget.start_session("pilot", 20, 2)
        self.clock.advance(3)
        with patch.object(c, "_atomic_write", side_effect=OSError("disk full")):
            with self.assertRaises(c.PersistenceError):
                session.heartbeat()
        with self.assertRaises(c.PersistenceError):
            budget.start_session("must not run", 10, 1)
        budget.__exit__(None, None, None)
        with self.budget() as recovered:
            self.assertEqual(recovered.charged_seconds, 3)


class CollectionAccountingTests(CampaignFixture):
    def test_fresh_totals_are_separate_from_discarded_work(self):
        with self.budget() as budget:
            self.assertEqual(budget.collection_summary()["attempted_games"], 0)
            details = {"generation": 3, "reason": "round_limit", "ids": [7]}
            totals = budget.record_collection(256, 255, 1, 100000, 2400, details)
            details["ids"].append(8)
            self.assertEqual(totals["attempted_games"], 256)
            self.assertEqual(totals["completed_games"], 255)
            self.assertEqual(totals["censored_games"], 1)
            self.assertEqual(totals["learning_rows"], 100000)
            self.assertEqual(totals["censored_rows"], 2400)
            self.assertEqual(totals["recent_censored"], 1)
            self.assertEqual(totals["collections"], 1)
            self.assertEqual(budget.snapshot()["discarded_episodes"], 0)
            self.assertEqual(budget._data["collection_accounting"]["recent_batches"][0]["details"]["ids"], [7])
        with self.budget() as restored:
            self.assertEqual(restored.collection_summary(), totals)

    def test_legacy_ledger_migration_preserves_time_and_records_counter_epoch(self):
        with self.budget() as budget:
            with budget.start_session("old pilot", 30, 5):
                self.clock.advance(7)
        data = json.loads(self.path.read_text())
        del data["collection_accounting"]
        self.path.write_text(json.dumps(data))
        self.clock.advance(50)
        with self.budget() as migrated:
            self.assertEqual(migrated.charged_seconds, 7)
            self.assertEqual(migrated.collection_summary()["attempted_games"], 0)
            self.assertEqual(migrated.collection_summary()["started_wall"], self.clock.wall)
            migrated.record_collection(10, 10, 0, 100, 0)
        persisted = json.loads(self.path.read_text())
        self.assertEqual(persisted["charged_seconds"], 7)
        self.assertEqual(len(persisted["sessions"]), 1)
        self.assertEqual(persisted["collection_accounting"]["attempted_games"], 10)

    def test_whole_oldest_overlapping_batch_is_retained_conservatively(self):
        with self.budget() as budget:
            for _ in range(3):
                totals = budget.record_collection(2000, 1999, 1, 6000, 100)
            self.assertEqual(totals["attempted_games"], 6000)
            self.assertEqual(totals["recent_attempted"], 6000)
            self.assertEqual(totals["recent_censored"], 3)
            totals = budget.record_collection(2000, 2000, 0, 6000, 0)
            self.assertEqual(totals["attempted_games"], 8000)
            self.assertEqual(totals["censored_games"], 3)
            self.assertEqual(totals["recent_attempted"], 6000)
            self.assertEqual(totals["recent_censored"], 2)
            self.assertEqual(totals["recent_batch_count"], 3)
        with self.budget() as restored:
            self.assertEqual(restored.collection_summary(), totals)

    def test_exact_window_boundary_and_batch_larger_than_window(self):
        with self.budget() as budget:
            budget.record_collection(2048, 2047, 1, 0, 0)
            budget.record_collection(2048, 2048, 0, 0, 0)
            totals = budget.record_collection(2048, 2048, 0, 0, 0)
            self.assertEqual(totals["recent_attempted"], 4096)
            self.assertEqual(totals["recent_censored"], 0)
            totals = budget.record_collection(5000, 4999, 1, 0, 0)
            self.assertEqual(totals["recent_attempted"], 5000)
            self.assertEqual(totals["recent_batch_count"], 1)
        with self.budget() as restored:
            self.assertEqual(restored.collection_summary()["recent_censored"], 1)

    def test_actual_process_crash_preserves_published_censor_history(self):
        code = "import campaign_state as c,os,sys\nb=c.CampaignBudget(sys.argv[1],100).__enter__()\nb.start_session('crashed',2,0)\nb.record_collection(10,9,1,80,20,{'reason':'round_limit'})\nos._exit(9)\n"
        env = {**os.environ, "PYTHONPATH": str(Path(c.__file__).parent)}
        child = subprocess.run([sys.executable, "-c", code, str(self.path)], env=env, timeout=5)
        self.assertEqual(child.returncode, 9)
        with self.budget() as restored:
            self.assertIsNotNone(restored.last_recovery)
            self.assertEqual(restored.collection_summary()["censored_games"], 1)
            self.assertEqual(restored.collection_summary()["recent_censored"], 1)

    def test_invalid_counts_or_details_do_not_mutate_or_publish(self):
        invalid = [(0, 0, 0, 0, 0), (True, 1, 0, 1, 0), (10, 9, 0, 1, 0),
                   (10, 10, 1, 1, 0), (-1, 0, 0, 0, 0), (1, 1, 0, 1.5, 0),
                   (1, 0, 1, 1, 1), (1, 1, 0, 1, 1)]
        with self.budget() as budget:
            before = self.path.read_bytes()
            summary = budget.collection_summary()
            for args in invalid:
                with self.subTest(args=args), self.assertRaises(c.CampaignStateError):
                    budget.record_collection(*args)
            for details in ([], {"value": float("nan")}, {"trace": "x" * 4096}):
                with self.subTest(details=type(details)), self.assertRaises(c.CampaignStateError):
                    budget.record_collection(1, 1, 0, 1, 0, details)
            self.assertEqual(self.path.read_bytes(), before)
            self.assertEqual(budget.collection_summary(), summary)

    def test_publication_failure_poison_stops_further_use(self):
        budget = self.budget().__enter__()
        with patch.object(c, "_atomic_write", side_effect=OSError("disk full")):
            with self.assertRaises(c.PersistenceError):
                budget.record_collection(10, 9, 1, 80, 20)
        with self.assertRaises(c.PersistenceError):
            budget.collection_summary()
        with self.assertRaises(c.PersistenceError):
            budget.record_collection(10, 10, 0, 100, 0)
        budget.__exit__(None, None, None)
        with self.budget() as restored:
            self.assertEqual(restored.collection_summary()["attempted_games"], 0)

    def test_corrupt_counter_history_is_rejected(self):
        with self.budget() as budget:
            budget.record_collection(10, 9, 1, 80, 20)
        data = json.loads(self.path.read_text())
        data["collection_accounting"]["censored_games"] = 0
        self.path.write_text(json.dumps(data))
        with self.assertRaises(c.CampaignStateError):
            with self.budget():
                pass


class CheckpointTests(CampaignFixture):
    def setUp(self):
        super().setUp()
        self.checkpoint = self.root / "model.checkpoint"
        self.identity = {"rules": "abc", "catalog": "def", "schema": "obs-v1",
                         "source": "pinned-selected-files", "config": {"width": 3}}

    def simple_state(self):
        return {"learner": {"weight": torch.tensor([1., 2., 3.])},
                "optimizer": {"state": {0: {"step": torch.tensor(2.), "exp_avg": torch.zeros(3)}}},
                "archives": [], "counters": {"updates": 2, "discarded_decisions": 0}}

    def test_atomic_snapshot_owns_tensors_and_resume_never_refunds_time(self):
        state = self.simple_state()
        with self.budget() as budget:
            with budget.start_session("pilot", 30, 5):
                self.clock.advance(4)
                c.save_checkpoint_atomic(self.checkpoint, state, identity=self.identity, budget=budget, include_cuda_rng=False)
                state["learner"]["weight"].fill_(999)
                self.clock.advance(3)
            restored = c.load_checkpoint(self.checkpoint, expected_identity=self.identity, budget=budget)
            self.assertTrue(torch.equal(restored["state"]["learner"]["weight"], torch.tensor([1., 2., 3.])))
            self.assertEqual(restored["budget"]["charged_seconds"], 4)
            self.assertEqual(budget.charged_seconds, 7)

    def test_older_checkpoint_does_not_roll_back_censor_history(self):
        with self.budget() as budget:
            budget.record_collection(256, 255, 1, 10000, 200)
            c.save_checkpoint_atomic(self.checkpoint, self.simple_state(), identity=self.identity,
                                     budget=budget, include_cuda_rng=False)
            budget.record_collection(256, 254, 2, 11000, 500)
            payload = c.load_checkpoint(self.checkpoint, expected_identity=self.identity, budget=budget)
            self.assertEqual(payload["budget"]["collection_accounting"]["censored_games"], 1)
            self.assertEqual(budget.collection_summary()["censored_games"], 3)
        with self.budget() as restored:
            c.load_checkpoint(self.checkpoint, expected_identity=self.identity, budget=restored)
            self.assertEqual(restored.collection_summary()["attempted_games"], 512)
            self.assertEqual(restored.collection_summary()["recent_censored"], 3)

    def test_rng_and_next_optimizer_step_recover_exactly_on_cpu(self):
        random.seed(38)
        np.random.seed(38)
        torch.manual_seed(38)
        model = torch.nn.Linear(3, 2)
        optimizer = torch.optim.AdamW(model.parameters(), lr=.001)
        data = torch.tensor([[1., 2., 3.], [3., 1., 2.]])

        def update(module, optim):
            optim.zero_grad(set_to_none=True)
            module(data).square().mean().backward()
            optim.step()

        update(model, optimizer)
        with self.budget() as budget:
            c.save_checkpoint_atomic(self.checkpoint,
                {"learner": model.state_dict(), "optimizer": optimizer.state_dict(), "archives": [], "counters": {"updates": 1}},
                identity=self.identity, budget=budget, include_cuda_rng=False)
            expected_rng = (random.random(), np.random.random(), torch.rand(3))
            update(model, optimizer)
            expected_parameters = copy.deepcopy(model.state_dict())
            restored = c.load_checkpoint(self.checkpoint, expected_identity=self.identity, budget=budget)
            replacement = torch.nn.Linear(3, 2)
            replacement_optimizer = torch.optim.AdamW(replacement.parameters(), lr=.001)
            replacement.load_state_dict(restored["state"]["learner"])
            replacement_optimizer.load_state_dict(restored["state"]["optimizer"])
            c.restore_rng(restored, include_cuda=False)
            self.assertEqual(random.random(), expected_rng[0])
            self.assertEqual(np.random.random(), expected_rng[1])
            self.assertTrue(torch.equal(torch.rand(3), expected_rng[2]))
            update(replacement, replacement_optimizer)
            for key, expected in expected_parameters.items():
                self.assertTrue(torch.equal(replacement.state_dict()[key], expected))
            self.assertFalse(torch.cuda.is_initialized())

    def test_identity_mismatch_different_campaign_and_corruption_rejected(self):
        with self.budget() as budget:
            c.save_checkpoint_atomic(self.checkpoint, self.simple_state(), identity=self.identity, budget=budget, include_cuda_rng=False)
            with self.assertRaises(c.CheckpointError):
                c.load_checkpoint(self.checkpoint, expected_identity={**self.identity, "rules": "changed"})
        with c.CampaignBudget(self.root / "another.json", 100, clock=self.clock) as another:
            with self.assertRaises(c.CheckpointError):
                c.load_checkpoint(self.checkpoint, expected_identity=self.identity, budget=another)
        content = bytearray(self.checkpoint.read_bytes())
        content[-10] ^= 0x40
        self.checkpoint.write_bytes(content)
        with self.assertRaisesRegex(c.CheckpointError, "checksum"):
            c.load_checkpoint(self.checkpoint, expected_identity=self.identity)

    def test_nonfinite_optimizer_state_is_rejected_before_save(self):
        state = self.simple_state()
        state["optimizer"]["state"][0]["exp_avg"][0] = float("nan")
        with self.budget() as budget:
            with self.assertRaisesRegex(c.CheckpointError, "Nonfinite"):
                c.save_checkpoint_atomic(self.checkpoint, state, identity=self.identity, budget=budget, include_cuda_rng=False)
            with self.assertRaises(c.PersistenceError):
                budget.start_session("forbidden after failure", 10, 1)
        self.assertFalse(self.checkpoint.exists())

    def test_checksummed_but_nonfinite_payload_is_rejected_on_load(self):
        with self.budget() as budget:
            c.save_checkpoint_atomic(self.checkpoint, self.simple_state(), identity=self.identity, budget=budget, include_cuda_rng=False)
        content = self.checkpoint.read_bytes()[c._HEADER.size:]
        payload = torch.load(io.BytesIO(content), weights_only=True)
        payload["state"]["learner"]["weight"][1] = float("inf")
        buffer = io.BytesIO()
        torch.save(payload, buffer)
        content = buffer.getvalue()
        self.checkpoint.write_bytes(c._HEADER.pack(c._MAGIC, len(content), hashlib.sha256(content).digest()) + content)
        with self.assertRaisesRegex(c.CheckpointError, "Nonfinite"):
            c.load_checkpoint(self.checkpoint, expected_identity=self.identity)

    def test_failed_replace_preserves_previous_complete_checkpoint(self):
        with self.budget() as budget:
            c.save_checkpoint_atomic(self.checkpoint, self.simple_state(), identity=self.identity, budget=budget, include_cuda_rng=False)
            before = self.checkpoint.read_bytes()
            original_replace = os.replace

            def fail_checkpoint(source, destination):
                if Path(destination) == self.checkpoint:
                    raise OSError("simulated checkpoint rename failure")
                return original_replace(source, destination)

            with patch.object(c.os, "replace", side_effect=fail_checkpoint):
                with self.assertRaises(c.CheckpointError):
                    c.save_checkpoint_atomic(self.checkpoint, self.simple_state(), identity=self.identity, budget=budget, include_cuda_rng=False)
            self.assertEqual(self.checkpoint.read_bytes(), before)
            self.assertFalse(list(self.root.glob("*.tmp")))


if __name__ == "__main__":
    torch.set_num_threads(1)
    unittest.main()
