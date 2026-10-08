import copy
from types import SimpleNamespace
from unittest.mock import patch
import json
from pathlib import Path
import signal
import tempfile
import unittest

import overnight_controller as control

CONFIG = {"batch": 256, "censor_limit": 4}
IDENTITY = {"host_binary_sha256": "host", "observation_schema": "obs", "rules_sha256": "rules"}


def generation():
    return {"event": "generation", "generation": 10, "games": 2560,
            "finite": {"parameters_finite": True, "optimizer_state_finite": True},
            "attempted_games": 256, "completed_games": 256, "censored_games": 0,
            "learning_rows": 100, "attempted_learning_rows": 100, "retained_learning_rows": 100,
            "censored_learning_rows": 0, "draws": 0, "seat0_wins": 128,
            "accepted_optimizer_steps": 3, "rejected_minibatches": 0, "update_ready": True,
            "behavior_parity": {"max_abs_behavior_logp_error": .000115, "max_abs_behavior_value_error": .00001},
            "campaign_episode_accounting": {"recent_censored": 0}}


def statistics():
    row = {"card_id": "card", "choice_kind": "buy", "selected_player_games": 1,
           "selected_game_clusters": 1, "wins": 1, "draws": 0, "losses": 0, "pick_count": 1}
    return {"session_id": "s", "host_binary_sha256": "host", "observation_schema": "obs", "purpose": "training_pool",
            "final": True, "totals": {"completed_games": 1, "seat0_wins": 1, "seat1_wins": 0, "draws": 0,
                                      "censored_games": 0, "unfinished_discarded_games": 0},
            "hero_seat_rows": [{"hero_id": "a", "seat": 0, "games": 1, "wins": 1, "draws": 0, "losses": 0},
                               {"hero_id": "b", "seat": 1, "games": 1, "wins": 0, "draws": 0, "losses": 1}],
            "matchup_rows": [{"games": 1}], "final_round_histogram": [0, 1], "final_state_sums": {"player_count": 2},
            "rows": [row], "hero_choice_rows": [dict(row, hero_id="a")]}


def evaluation(score=.5, games=4096, seed=100):
    wins = int(score * games)
    return {"complete": True, "all_terminal": True, "frozen_weights_unchanged": True,
            "games": games, "paired_seed_count": games // 2, "seed_start": seed, "seat_swapped": True,
            "censored_games": 0, "resolved_games": games, "optimizer_updates": 0, "training_budget_seconds_charged": 0,
            "policy_a": {"role": "learner", "run_identity": IDENTITY, "checkpoint": "/a/latest.soicp"},
            "policy_b": {"role": "learner", "run_identity": IDENTITY, "checkpoint": "/b/latest.soicp"},
            "wins_a": wins, "draws": 0, "losses_a": games - wins, "score_a": wins / games}


class FakeClock:
    def __init__(self):
        self.now = 0
        self.wall_adjustment = 0
    def wall(self): return 1000 + self.now + self.wall_adjustment
    def mono(self): return self.now
    def sleep(self, duration): self.now += duration


class FakeProcess:
    pid = 10
    def __init__(self, clock, finish=float("inf")):
        self.clock, self.finish, self.returncode = clock, finish, None
        self.signals = []
    def poll(self):
        if self.clock.now >= self.finish and self.returncode is None: self.returncode = 0
        return self.returncode
    def send_signal(self, value): self.signals.append((self.clock.now, value))
    def wait(self, timeout=None):
        assert self.poll() is not None, "Fake process wait would hang"
        return self.returncode


class Audits(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.root = Path(self.temp.name)
    def tearDown(self): self.temp.cleanup()
    def metrics(self, row, suffix=""):
        path = self.root / "metrics.jsonl"
        path.write_text(json.dumps(row) + "\n" + json.dumps({"event": "session_end"}) + "\n" + suffix)
        return path
    def stats(self, doc):
        directory = self.root / "natural-draft"
        directory.mkdir(exist_ok=True)
        control.atomic_json(directory / "session-s.json", doc)
        return self.root
    def classify(self, doc, sequence=1):
        return control.classify_evaluation(doc, sequence, 4096, 100, IDENTITY, "/a/latest.soicp", "/b/latest.soicp")

    def test_actual_parity_tolerance_not_false_alarm(self):
        result = control.audit_metrics(self.metrics(generation()), CONFIG, final=True)
        self.assertEqual(result["completed_games"], 256)

    def test_nonfinite_parameters_and_nan_metrics_fail(self):
        for alteration in (lambda r: r["finite"].update(parameters_finite=False), lambda r: r.update(loss=float("nan"))):
            row = generation(); alteration(row)
            with self.assertRaises(control.AuditFailure): control.audit_metrics(self.metrics(row), CONFIG, final=True)

    def test_invalid_rows_outcomes_censors_and_parity_fail(self):
        mutations = [lambda r: r.update(learning_rows=99), lambda r: r.update(seat0_wins=257),
                     lambda r: r["campaign_episode_accounting"].update(recent_censored=4),
                     lambda r: r["behavior_parity"].update(max_abs_behavior_logp_error=.011)]
        for mutate in mutations:
            row = generation(); mutate(row)
            with self.assertRaises(control.AuditFailure): control.audit_metrics(self.metrics(row), CONFIG, final=True)

    def test_live_partial_line_is_deferred_but_final_rejected(self):
        path = self.metrics(generation(), '{"unfinished":')
        self.assertEqual(control.audit_metrics(path, CONFIG)["generations"], 1)
        with self.assertRaises(control.AuditFailure): control.audit_metrics(path, CONFIG, final=True)

    def test_extreme_rates_are_warnings_not_engine_failures(self):
        row = generation(); row["seat0_wins"] = 250; row["rejected_minibatches"] = 1
        result = control.audit_metrics(self.metrics(row), CONFIG, final=True)
        self.assertEqual(len(result["warnings"]), 2)

    def test_rare_hero_anomaly_accumulates_across_segments(self):
        total=None
        for i in range(20):
            current={"completed_games":2000,"censored_games":0,"cohorts":{
                "natural-draft":{"games":500,"heroes":{"kosynwu":{"games":80,"wins":0,"draws":0}}}}}
            total=control.accumulate_statistics(total,current,str(i))
        self.assertEqual(total["cohorts"]["natural-draft"]["heroes"]["kosynwu"]["games"],1600)
        self.assertEqual(total["observations"][0]["hero"],"kosynwu")
        with self.assertRaises(control.AuditFailure):control.accumulate_statistics(total,current,"19")

    def test_statistics_reconcile_discarded_finished_games(self):
        result = control.audit_statistics(self.stats(statistics()), IDENTITY,
            {"completed_games": 0, "discarded_completed": 1, "censored_games": 0}, final=True)
        self.assertEqual(result["completed_games"], 1)

    def test_statistics_missing_final_identity_or_hero_card_disagree(self):
        for mutate in (lambda d: d.update(final=False), lambda d: d.update(host_binary_sha256="changed"),
                       lambda d: d["hero_choice_rows"][0].update(pick_count=2)):
            doc = statistics(); mutate(doc)
            with self.assertRaises(control.AuditFailure): control.audit_statistics(self.stats(doc), IDENTITY, final=True)

    def test_statistical_classification_and_sequential_bounds(self):
        self.assertEqual(self.classify(evaluation())['verdict'], "inconclusive")
        self.assertEqual(self.classify(evaluation(.75))['verdict'], "improved")
        self.assertEqual(self.classify(evaluation(.25))['verdict'], "regressed")
        self.assertGreater(self.classify(evaluation(), 20)['bound'][1], self.classify(evaluation(), 1)['bound'][1])

    def test_evaluation_invalid_outcomes_wrong_roles_seed_or_rules_fail(self):
        mutations = [lambda r: r.update(resolved_games=4095), lambda r: r.update(score_a=float("nan")),
                     lambda r: r.update(seed_start=101), lambda r: r["policy_a"].update(role="champion"),
                     lambda r: r["policy_b"].update(run_identity={"rules_sha256": "wrong"}),
                     lambda r: r.update(wins_a=0)]
        for mutate in mutations:
            doc = evaluation(); mutate(doc)
            with self.assertRaises(control.AuditFailure): self.classify(doc)

    def test_snapshot_is_retained_exclusively(self):
        source = self.root / "source"; source.mkdir()
        (source / "latest.soicp").write_bytes(b"original")
        control.atomic_json(source / "identity.json", IDENTITY)
        retained = control.snapshot(source / "latest.soicp", self.root / "retained")
        (source / "latest.soicp").write_bytes(b"changed")
        self.assertEqual(retained.read_bytes(), b"original")
        self.assertEqual(retained.stat().st_mode & 0o222, 0)
        with self.assertRaises(FileExistsError): control.snapshot(source / "latest.soicp", retained.parent)

    def test_deadline_timezone_and_slot_reserve(self):
        self.assertEqual(control.deadline_timestamp("2026-09-27T11:00:00+02:00"),
                         control.deadline_timestamp("2026-09-27T09:00:00Z"))
        with self.assertRaises(ValueError): control.deadline_timestamp("2026-09-27T11:00:00")
        self.assertEqual(control.slot_plan(0, 1200, 10000, 240, 43200)["training_seconds"], 930)
        self.assertEqual(control.slot_plan(0, 1200, 600, 240, 43200)["training_seconds"], 330)
        self.assertEqual(control.slot_plan(0, 1200, 10000, 240, 100)["training_seconds"], 100)


class ProcessSafety(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(); self.root = Path(self.temp.name)
        self.clock = FakeClock(); self.process = FakeProcess(self.clock); self.calls = []
        self.identities = {10: {"ppid": 1, "start": 1, "pgid": 10}}
        def kill(pid, sig):
            self.calls.append((self.clock.now, pid, sig))
            if pid == 10 and sig == signal.SIGKILL: self.process.returncode = -9
        self.executor = control.Executor(1050, wall=self.clock.wall, mono=self.clock.mono, sleep=self.clock.sleep,
            popen=lambda *args, **kwargs: self.process, signal_group=kill, identity=lambda pid: self.identities.get(pid))
    def tearDown(self): self.temp.cleanup()
    def execute(self, **options):
        self.executor.execute(["fake"], self.root / "log", timeout=100, **options)

    def test_hard_deadline_kills_only_owned_group(self):
        with self.assertRaises(control.DeadlineReached): self.execute()
        self.assertEqual(self.clock.now, 50)
        self.assertEqual({pid for _, pid, _ in self.calls}, {10})
        self.assertIn((30, 10, signal.SIGTERM), self.calls)
        self.assertIn((50, 10, signal.SIGKILL), self.calls)

    def test_supervisor_child_ownership_and_graceful_stop(self):
        self.identities[11] = {"ppid": 10, "start": 2, "pgid": 11}
        path = self.root / "supervisor.json"; control.atomic_json(path, {"trainer_pid": 11})
        with self.assertRaises(control.DeadlineReached): self.execute(supervisor_path=path)
        self.assertEqual(self.process.signals, [(30, signal.SIGTERM)])
        self.assertEqual({pid for _, pid, _ in self.calls}, {10, 11})
        self.assertTrue(all(sig == signal.SIGKILL for _, _, sig in self.calls))

    def test_unrelated_supervisor_pid_is_never_signalled(self):
        self.identities[99] = {"ppid": 88, "start": 2, "pgid": 99}
        path = self.root / "supervisor.json"; control.atomic_json(path, {"trainer_pid": 99})
        with self.assertRaises(control.DeadlineReached): self.execute(supervisor_path=path)
        self.assertNotIn(99, {pid for _, pid, _ in self.calls})

    def test_process_failure_is_not_retried(self):
        self.process.returncode = 7
        with self.assertRaisesRegex(control.AuditFailure, "exit 7"): self.execute()
        self.assertIsNone(self.executor.current)

    def test_new_process_never_overlaps_existing(self):
        self.executor.current = object()
        with self.assertRaisesRegex(control.AuditFailure, "overlapping"): self.execute()
        self.assertEqual(self.calls, [])

    def test_failed_live_check_stops_process_and_preserves_reason(self):
        def fail(): raise control.AuditFailure("bad row")
        with self.assertRaisesRegex(control.AuditFailure, "bad row"): self.execute(check=fail)
        self.assertEqual(self.clock.now, 20)
        self.assertIn((0, 10, signal.SIGTERM), self.calls)

    def test_backwards_wall_clock_cannot_extend_deadline(self):
        self.clock.wall_adjustment = -1000
        with self.assertRaises(control.DeadlineReached): self.execute()
        self.assertEqual(self.clock.now, 50)

    def test_success_cleans_owned_descendants(self):
        self.process.finish = 5
        self.execute()
        self.assertEqual(self.clock.now, 5)
        self.assertIn((5, 10, signal.SIGKILL), self.calls)


class ControllerIntegration(unittest.TestCase):
    def exercise(self, score, separate_best=False):
        temporary = tempfile.TemporaryDirectory()
        self.addCleanup(temporary.cleanup)
        root = Path(temporary.name)
        clock = FakeClock()
        baseline = root / "source"; baseline.mkdir()
        (baseline / "latest.soicp").write_bytes(b"baseline")
        identity = dict(IDENTITY, configuration=CONFIG)
        control.atomic_json(baseline / "identity.json", identity)
        config = root / "config.json"; control.atomic_json(config, CONFIG)
        ledger = root / "budget.json"
        control.atomic_json(ledger, {"campaign_id": "campaign", "limit_seconds": 1000., "charged_seconds": 0., "active": None})
        calls = []
        class SimulatedExecutor:
            def __init__(self, deadline):
                self.stopped = False; self.deadline = deadline
            def remaining(self): return max(0., self.deadline - clock.wall())
            def execute(self, command, log, **kwargs):
                mode = command[2]; calls.append(mode)
                option = lambda name: command[command.index(name) + 1]
                if mode == "supervise":
                    run = Path(option("--run-dir"))
                    control.atomic_json(run / "identity.json", identity)
                    (run / "latest.soicp").write_bytes(b"updated")
                    (run / "metrics.jsonl").write_text(json.dumps(generation()) + "\n" + json.dumps({"event": "session_end"}) + "\n")
                    doc = statistics(); doc["totals"].update(completed_games=256, seat0_wins=128, seat1_wins=128)
                    for row in doc["hero_seat_rows"]: row.update(games=256, wins=128, losses=128)
                    doc["matchup_rows"] = [{"games": 256}]
                    doc["final_round_histogram"] = [0, 256]; doc["final_state_sums"]["player_count"] = 512
                    directory = run / "training-statistics" / "natural-draft"; directory.mkdir(parents=True)
                    control.atomic_json(directory / "session-s.json", doc)
                    grant = float(option("--seconds")); clock.sleep(grant - 5)
                    budget = control.read_json(ledger); budget["charged_seconds"] += grant - 5
                    control.atomic_json(ledger, budget)
                else:
                    report = evaluation(score, seed=int(option("--seed")))
                    for key, argument in (("policy_a", "--a"), ("policy_b", "--b")):
                        report[key].update(checkpoint=option(argument), run_identity=identity, checkpoint_generation=10, version=10)
                    control.atomic_json(Path(option("--output")), report); clock.sleep(5)
                return 0
        args = SimpleNamespace(run_root=root / "overnight", ledger=ledger, config=config,
            resume=baseline / "latest.soicp", variant_entry=root / "entry.py", deadline="1970-01-01T00:20:00+00:00",
            seed=100, sampling_seed=1000, evaluation_reserve=60., interval=300., eval_timeout=120.,
            games=4096, eval_batch=128, eval_workers=8, evaluation_dir=None)
        if separate_best:
            accepted=root/"accepted";accepted.mkdir()
            (accepted/"latest.soicp").write_bytes(b"trusted")
            control.atomic_json(accepted/"identity.json",identity)
            args.best_checkpoint=accepted/"latest.soicp"
        with patch.object(control, "Executor", SimulatedExecutor), patch.object(control.time, "time", clock.wall), \
             patch.object(control.time, "monotonic", clock.mono), patch.object(control.time, "sleep", clock.sleep), \
             patch.object(control.signal, "signal"):
            code = control.run(args)
        return code, control.read_json(args.run_root / "overnight-controller.json"), calls, args

    def test_one_whole_cycle_retains_models_preserves_budget_and_does_not_promote_tie(self):
        code, state, calls, args = self.exercise(.5)
        self.assertEqual(code, 0)
        self.assertEqual(calls, ["supervise", "evaluate"])
        self.assertEqual(state["cycles"], 1)
        self.assertEqual(state["best"], state["baseline"])
        self.assertNotEqual(state["previous"], state["baseline"])
        self.assertEqual(state["comparisons"][0]["evidence"]["verdict"], "inconclusive")
        self.assertEqual(control.read_json(args.ledger)["limit_seconds"], 1000.)
        self.assertEqual(control.read_json(args.ledger)["charged_seconds"], 105.)
        self.assertTrue((args.run_root / "segment-0001" / "progress-watch.json").exists())

    def test_unproven_resume_does_not_replace_separate_accepted_best(self):
        code,state,calls,args=self.exercise(.5,separate_best=True)
        self.assertEqual(code,0)
        self.assertEqual(calls,["supervise","evaluate","evaluate"])
        self.assertEqual(Path(state["best"]).read_bytes(),b"trusted")
        self.assertEqual([job["kind"] for job in state["comparisons"]],["previous","best"])

    def test_confirmed_regression_pauses_without_retry_or_checkpoint_overwrite(self):
        code, state, calls, args = self.exercise(.25)
        self.assertEqual(code, 1)
        self.assertEqual(calls, ["supervise", "evaluate"])
        self.assertEqual(state["state"], "paused_for_investigation")
        self.assertTrue((args.run_root / "action-required.json").exists())
        self.assertEqual(Path(state["baseline"]).read_bytes(), b"baseline")
        self.assertEqual(len(state["snapshots"]), 1)


if __name__ == "__main__": unittest.main()
