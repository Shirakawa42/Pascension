"""Exclusive training/evaluation cycles with a wall deadline and fail-closed audits.

Does not create, extend, reset, or repair a campaign ledger. Source changes and
budget authorization belong to the invoking operator. No automatic code edits or
restart follow a failed check. Evaluation comparisons use fresh paired seeds.
"""
from __future__ import annotations

import argparse
import copy
from collections import defaultdict
from datetime import datetime
import fcntl
import hashlib
import json
import math
import os
from pathlib import Path
import shutil
import signal
import subprocess
import sys
import time

SCHEMA = "shards-overnight-controller-v1"
# Matches train_campaign.verify_behavior, not a tighter invented FP32 threshold.
LOGP_LIMIT, VALUE_LIMIT = .01, .005


class AuditFailure(RuntimeError):
    pass


class DeadlineReached(RuntimeError):
    pass


def require(ok, message):
    if not ok:
        raise AuditFailure(message)


def read_json(path):
    def reject(value):
        raise AuditFailure("Nonfinite JSON constant " + value + " in " + str(path))
    return json.loads(Path(path).read_text(), parse_constant=reject)


def atomic_json(path, value):
    path = Path(path)
    temporary = path.with_name(path.name + ".tmp")
    with temporary.open("w") as stream:
        json.dump(value, stream, indent=2, allow_nan=False)
        stream.flush()
        os.fsync(stream.fileno())
    os.replace(temporary, path)


def file_hash(path):
    digest = hashlib.sha256()
    with Path(path).open("rb") as stream:
        for block in iter(lambda: stream.read(1 << 20), b""):
            digest.update(block)
    return digest.hexdigest()


def snapshot(source, destination):
    source, destination = Path(source), Path(destination)
    destination.mkdir(parents=True, exist_ok=False)
    for name, original in (("latest.soicp", source), ("identity.json", source.parent / "identity.json")):
        with original.open("rb") as src, (destination / name).open("xb") as dst:
            shutil.copyfileobj(src, dst)
            dst.flush()
            os.fsync(dst.fileno())
        (destination / name).chmod(0o444)
    return destination / "latest.soicp"


def finite_tree(value, label="document"):
    if isinstance(value, float):
        require(math.isfinite(value), "Nonfinite number in " + label)
    elif isinstance(value, dict):
        for key, item in value.items():
            finite_tree(item, label + "." + key)
    elif isinstance(value, list):
        for item in value:
            finite_tree(item, label)


def integer(value):
    return type(value) is int and value >= 0


def audit_metrics(path, config, *, final=False):
    path = Path(path)
    if not path.exists():
        require(not final, "Training produced no metrics")
        return {"generations": 0, "completed_games": 0, "discarded_completed": 0, "warnings": []}
    data = path.read_text()
    lines = data.splitlines()
    if data and not data.endswith("\n"):
        require(not final, "Incomplete final metrics JSON line")
        lines = lines[:-1]
    rows = [json.loads(line) for line in lines if line.strip()]
    generations, discarded, discarded_censors, warnings = [], 0, 0, []
    for row in rows:
        finite_tree(row, "metrics")
        require(row.get("event") not in ("error", "session_failed", "failure"), "Trainer recorded failure event")
        if row.get("event") == "discarded_collection":
            discarded += row.get("completed_games", 0)
            discarded_censors += row.get("censored_games", 0)
        if row.get("event") != "generation":
            continue
        require(row.get("finite", {}).get("parameters_finite") is True and
                row.get("finite", {}).get("optimizer_state_finite") is True, "Nonfinite or unverified model/optimizer")
        for key in ("generation", "attempted_games", "completed_games", "censored_games", "learning_rows",
                    "attempted_learning_rows", "retained_learning_rows", "censored_learning_rows",
                    "draws", "seat0_wins", "accepted_optimizer_steps", "rejected_minibatches"):
            require(integer(row.get(key)), "Missing/invalid metric " + key)
        require(row["attempted_games"] == row["completed_games"] + row["censored_games"], "Episode totals disagree")
        require(row["attempted_games"] == config["batch"], "Collection batch differs from configuration")
        require(row["draws"] + row["seat0_wins"] <= row["completed_games"], "Impossible terminal outcomes")
        require(row["learning_rows"] == row["retained_learning_rows"] > 0 and
                row["attempted_learning_rows"] == row["learning_rows"] + row["censored_learning_rows"],
                "Learning rows do not reconcile")
        require(row.get("update_ready") is True, "Generation updated without complete learner rows")
        parity = row.get("behavior_parity", {})
        for key, limit in (("max_abs_behavior_logp_error", LOGP_LIMIT), ("max_abs_behavior_value_error", VALUE_LIMIT)):
            value = parity.get(key)
            require(type(value) in (int, float) and 0 <= value <= limit, "Behavior parity violation: " + key)
        recent = row.get("campaign_episode_accounting", {}).get("recent_censored")
        require(integer(recent) and recent < config["censor_limit"], "Configured recent censor ceiling reached")
        if generations:
            previous = generations[-1]
            require(row["generation"] == previous["generation"] + 1, "Nonconsecutive generation numbers")
            require(row.get("games") == previous.get("games") + row["completed_games"], "Cumulative completed games disagree")
        generations.append(row)
    require(not final or bool(generations), "No completed training generation")
    if final:
        require(rows[-1].get("event") == "session_end", "No clean session_end after training")
    completed = sum(row["completed_games"] for row in generations)
    censors = sum(row["censored_games"] for row in generations)
    accepted = sum(row["accepted_optimizer_steps"] for row in generations)
    rejected = sum(row["rejected_minibatches"] for row in generations)
    require(not final or accepted > 0, "Entire segment performed no accepted learning updates")
    if completed:
        seat0 = sum(row["seat0_wins"] for row in generations) / completed
        draws = sum(row["draws"] for row in generations) / completed
        if seat0 < .2 or seat0 > .8:
            warnings.append({"kind": "unusual_seat_rate", "rate": seat0, "action": "investigate; not proof of an engine bug"})
        if draws > .1:
            warnings.append({"kind": "unusual_draw_rate", "rate": draws, "action": "investigate; not proof of an engine bug"})
    if rejected:
        warnings.append({"kind": "guard_rejected_minibatches", "count": rejected, "accepted": accepted,
                         "action": "guard protected learner; inspect frequency, not an automatic numerical failure"})
    return {"generations": len(generations), "completed_games": completed, "censored_games": censors,
            "discarded_completed": discarded, "discarded_censored": discarded_censors,
            "first_generation": generations[0]["generation"] if generations else None,
            "last_generation": generations[-1]["generation"] if generations else None, "warnings": warnings}


def audit_statistics(directory, identity, metrics=None, *, final=False):
    files = sorted(Path(directory).glob("*/session-*.json"))
    require(not final or bool(files), "No training statistics publication")
    completed, censored, sessions = 0, 0, set()
    cohorts, observations = {}, []
    for path in files:
        doc = read_json(path)
        finite_tree(doc, "statistics")
        require(doc.get("session_id") not in sessions, "Duplicate statistics session")
        sessions.add(doc.get("session_id"))
        require(doc.get("host_binary_sha256") == identity["host_binary_sha256"] and
                doc.get("observation_schema") == identity["observation_schema"], "Statistics identity mismatch")
        require(doc.get("purpose") == "training_pool", "Frozen evaluation mixed into training statistics")
        require(not final or doc.get("final") is True, "Statistics writer did not finalize")
        totals = doc["totals"]
        require(all(integer(value) for value in totals.values()), "Invalid statistics totals")
        n = totals["completed_games"]
        cohort = doc.get("hero_setup", {}).get("cohort", "unspecified")
        summary = cohorts.setdefault(cohort, {"games": 0, "round_sum": 0, "heroes": {}, "matchups": set(), "choices": defaultdict(set)})
        summary["games"] += n
        summary["round_sum"] += sum(i*count for i,count in enumerate(doc["final_round_histogram"]))
        for hero in doc["hero_seat_rows"]:
            record = summary["heroes"].setdefault(hero["hero_id"], {"games": 0, "wins": 0, "draws": 0})
            for field in record:record[field] += hero[field]
        for pair in doc["matchup_rows"]:
            if pair["games"] and "seat0_hero_id" in pair:
                summary["matchups"].add((pair["seat0_hero_id"],pair["seat1_hero_id"]))
        for choice in doc["rows"]:
            if choice["pick_count"]:summary["choices"][choice["choice_kind"]].add(choice["card_id"])
        require(totals["seat0_wins"] + totals["seat1_wins"] + totals["draws"] == n, "Statistics outcomes disagree")
        completed += n
        censored += totals["censored_games"]
        hero_rows = doc["hero_seat_rows"]
        for seat in (0, 1):
            require(sum(row["games"] for row in hero_rows if row["seat"] == seat) == n,
                    "Hero seat totals disagree")
        require(sum(row["games"] for row in doc["matchup_rows"]) == n, "Matchup totals disagree")
        require(sum(doc["final_round_histogram"]) == n, "Final round histogram disagrees")
        require(doc["final_state_sums"]["player_count"] == 2 * n, "Final player count disagrees")
        for row in hero_rows:
            require(row["wins"] + row["draws"] + row["losses"] == row["games"], "Hero outcomes disagree")
        grouped = defaultdict(lambda: defaultdict(int))
        fields = ("selected_player_games", "wins", "draws", "losses", "pick_count")
        for row in doc["hero_choice_rows"]:
            for field in fields:
                grouped[(row["card_id"], row["choice_kind"])][field] += row[field]
        for row in doc["rows"] + doc["hero_choice_rows"]:
            require(all(integer(value) for key, value in row.items() if key not in ("card_id", "choice_kind", "hero_id")),
                    "Invalid card statistics count")
            require(row["wins"] + row["draws"] + row["losses"] == row["selected_player_games"],
                    "Card outcome denominator disagrees")
            require(row["selected_game_clusters"] <= row["selected_player_games"] <= row["pick_count"],
                    "Card clusters/players/picks disagree")
        for row in doc["rows"]:
            require(all(grouped[(row["card_id"], row["choice_kind"])][field] == row[field] for field in fields),
                    "Per-hero card counts disagree with global counts")
    if final and metrics:
        require(completed == metrics["completed_games"] + metrics["discarded_completed"],
                "Host completed games disagree with trained plus explicitly discarded games")
        require(censored == metrics["censored_games"] + metrics.get("discarded_censored", 0),
                "Host censors disagree with finalized and explicitly discarded collections")
    for cohort, summary in cohorts.items():
        summary["mean_final_round"] = summary.pop("round_sum") / summary["games"] if summary["games"] else None
        summary["ordered_matchup_coverage"] = len(summary.pop("matchups"))
        summary["unique_selected_cards_by_kind"] = {kind:len(ids) for kind,ids in summary.pop("choices").items()}
        for hero, row in summary["heroes"].items():
            row["score"] = (row["wins"]+.5*row["draws"])/row["games"] if row["games"] else None
            if row["games"] >= 1000 and not .2 <= row["score"] <= .8:
                observations.append({"kind":"extreme_observed_hero_score", "cohort":cohort, "hero":hero, **row,
                    "interpretation":"Association requires investigation; cannot distinguish balance from policy weakness"})
        if cohort == "forced-random" and summary["games"] >= 2000 and summary["ordered_matchup_coverage"] < 20:
            observations.append({"kind":"missing_forced_matchup_coverage", "cohort":cohort,
                                 "ordered_pairs":summary["ordered_matchup_coverage"]})
    return {"publications": len(files), "completed_games": completed, "censored_games": censored,
            "cohorts":cohorts, "observations":observations,
            "coverage_scope":"Selected/acquired cards only; no opportunity denominator, so zero selections do not establish a bug or a bad card"}


def accumulate_statistics(previous, current, source):
    """Accumulate disjoint finalized segments so rare-choice warnings cannot reset."""
    result=copy.deepcopy(previous) if previous is not None else {
        "source_segments":[],"completed_games":0,"censored_games":0,"cohorts":{}}
    require(source not in result["source_segments"],"Statistics segment already accumulated")
    result["source_segments"].append(source)
    for field in ("completed_games","censored_games"):result[field]+=current[field]
    for cohort,summary in current.get("cohorts",{}).items():
        destination=result["cohorts"].setdefault(cohort,{"games":0,"heroes":{}})
        destination["games"]+=summary["games"]
        for hero,row in summary["heroes"].items():
            total=destination["heroes"].setdefault(hero,{"games":0,"wins":0,"draws":0})
            for field in ("games","wins","draws"):total[field]+=row[field]
            total["score"]=(total["wins"]+.5*total["draws"])/total["games"] if total["games"] else None
    result["observations"]=[]
    for cohort,summary in result["cohorts"].items():
        for hero,row in summary["heroes"].items():
            if row["games"]>=1000 and not .2<=row["score"]<=.8:
                result["observations"].append({"kind":"cumulative_extreme_hero_score","cohort":cohort,
                    "hero":hero,**row,"interpretation":"Investigate rare-choice attribution, opponent selection and play quality; not causal balance"})
    return result


def classify_evaluation(report, sequence, games, seed, identity, current, opponent):
    finite_tree(report, "evaluation")
    require(report.get("complete") is True and report.get("all_terminal") is True and
            report.get("frozen_weights_unchanged") is True, "Incomplete or mutable frozen comparison")
    require(report.get("games") == games and report.get("paired_seed_count") == games // 2 and
            report.get("seed_start") == seed and report.get("seat_swapped") is True, "Evaluation seed/count pairing mismatch")
    require(report.get("censored_games") == 0 and report.get("resolved_games") == games, "Evaluation missing terminal outcomes")
    require(report.get("optimizer_updates") == 0 and report.get("training_budget_seconds_charged") == 0,
            "Evaluation performed updates or charged training")
    for key, path in (("policy_a", current), ("policy_b", opponent)):
        policy = report.get(key, {})
        require(policy.get("role") == "learner" and policy.get("run_identity") == identity and
                Path(policy.get("checkpoint", "")).resolve() == Path(path).resolve(), "Evaluation policy provenance mismatch")
    values = [report.get(key) for key in ("wins_a", "draws", "losses_a")]
    require(all(integer(value) for value in values) and sum(values) == games, "Evaluation outcomes disagree")
    score = report.get("score_a")
    require(type(score) in (int, float) and abs(score - (values[0] + .5 * values[1]) / games) < 1e-12,
            "Evaluation score disagrees with outcomes")
    alpha = .05 / (sequence * (sequence + 1))
    margin = math.sqrt(math.log(2 / alpha) / (2 * (games // 2)))
    low, high = max(0., score - margin), min(1., score + margin)
    verdict = "improved" if low > .5 else "regressed" if high < .5 else "inconclusive"
    return {"verdict": verdict, "score": score, "bound": [low, high], "alpha": alpha,
            "scope": "Summable5% paired-seed bounds across this controller's fresh natural-draft comparisons"}


def deadline_timestamp(value):
    parsed = datetime.fromisoformat(value.replace("Z", "+00:00"))
    if parsed.utcoffset() is None:
        raise ValueError("Deadline requires an explicit timezone")
    return parsed.timestamp()


def slot_plan(now, next_slot, deadline, reserve, remaining_budget, interval=1200):
    end = min(max(next_slot, now + interval), deadline)
    available = end - now - reserve - 30  # shutdown/checkpoint grace is inside the wall slot
    return {"training_seconds": min(43200., remaining_budget, max(0., available)),
            "next_slot": end, "evaluation_reserve": reserve}


def proc_identity(pid):
    try:
        # stat command names may contain spaces or parentheses.
        suffix = Path(f"/proc/{pid}/stat").read_text().rsplit(")", 1)[1].split()
        return {"ppid": int(suffix[1]), "start": int(suffix[19]), "pgid": os.getpgid(pid)}
    except (OSError, ValueError):
        return None


class Executor:
    """Owns only launched subprocess groups and verified supervisor child groups."""
    def __init__(self, hard_wall, *, wall=time.time, mono=time.monotonic, sleep=time.sleep,
                 popen=subprocess.Popen, signal_group=os.killpg, identity=proc_identity):
        self.wall, self.mono, self.sleep = wall, mono, sleep
        self.popen, self.signal_group, self.identity = popen, signal_group, identity
        self.hard_wall = hard_wall
        self.hard_mono = mono() + max(0., hard_wall - wall())
        self.stopped = False
        self.current = None

    def remaining(self):
        return max(0., min(self.hard_mono - self.mono(), self.hard_wall_remaining()))

    def hard_wall_remaining(self):
        # Anchored monotonic deadline also guards backwards wall-clock jumps.
        return self.hard_wall - self.wall() if hasattr(self, "hard_wall") else self.hard_mono - self.mono()

    def execute(self, command, log_path, *, timeout, supervisor_path=None, check=None):
        require(self.current is None, "Refusing overlapping subprocesses")
        if self.stopped or self.remaining() <= 0:
            raise DeadlineReached("Controller stopped or deadline reached")
        if supervisor_path is None:
            # An independent OS process enforces evaluation's hard ceiling even
            # if this controller blocks while inspecting a filesystem artifact.
            # GNU timeout owns the evaluation's group; do not use --foreground.
            timeout_tool = shutil.which("timeout")
            require(timeout_tool is not None, "GNU timeout is required for the independent evaluation deadline")
            command = [timeout_tool, "--signal=KILL", f"{min(timeout, self.remaining()):.3f}s", *command]
        stream = log_path.open("ab")
        try:
            process = self.popen(command, stdout=stream, stderr=subprocess.STDOUT, start_new_session=True)
        except BaseException:
            stream.close()
            raise
        self.current = process
        own = {process.pid: self.identity(process.pid)}
        limit = min(self.hard_mono, self.mono() + timeout)
        stop_at, failure, last_check = None, None, -float("inf")

        def send(sig):
            if sig == signal.SIGTERM and supervisor_path:
                # Supervisor requests a clean trainer checkpoint without killing
                # the C# host while a partial collection is being discarded.
                try:
                    process.send_signal(sig)
                except ProcessLookupError:
                    pass
                return
            for pid, original in list(own.items()):
                actual = self.identity(pid)
                # Parent may have exited leaving its same-ID process group alive.
                # Group identity is tied to the created leader, never arbitrary ledger PIDs.
                if actual is None:
                    actual = original
                if original and actual and actual["start"] == original["start"] and actual["pgid"] == pid:
                    try:
                        self.signal_group(pid, sig)
                    except ProcessLookupError:
                        pass

        try:
            while process.poll() is None:
                now = self.mono()
                if supervisor_path and Path(supervisor_path).exists():
                    try:
                        pid = read_json(supervisor_path).get("trainer_pid")
                        actual = self.identity(pid) if integer(pid) else None
                        if actual and actual["ppid"] == process.pid and actual["pgid"] == pid:
                            own[pid] = actual
                    except (OSError, ValueError):
                        pass
                if check and now - last_check >= 10 and failure is None:
                    try:
                        check()
                    except Exception as error:
                        failure = error
                    last_check = now
                stopping = self.stopped or failure is not None or now >= limit - 20 or self.remaining() <= 20
                if stopping and stop_at is None:
                    send(signal.SIGTERM)
                    stop_at = now
                if stop_at is not None and (now >= min(limit, stop_at + 20) or self.remaining() <= 0):
                    send(signal.SIGKILL)
                    failure = failure or DeadlineReached("Owned subprocess hit deadline or stop timeout")
                    break
                self.sleep(max(0., min(.5, limit - self.mono(), self.remaining())))
            code = process.wait(timeout=3)
            if failure:
                raise failure
            if self.stopped:
                raise DeadlineReached("Signal requested stop")
            if stop_at is not None and self.remaining() <= 20:
                raise DeadlineReached("Absolute deadline shutdown")
            require(code == 0, f"Owned subprocess failed with exit {code}; see {log_path}")
            if stop_at is not None and not supervisor_path:
                raise DeadlineReached("Evaluation interrupted before deadline")
            return code
        finally:
            send(signal.SIGKILL)
            self.current = None
            stream.close()


def run(args):
    root = args.run_root.resolve()
    root.mkdir(parents=True, exist_ok=True)
    with (root / "overnight-controller.lock").open("a") as lock:
        fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
        state_path = root / "overnight-controller.json"
        require(not state_path.exists(), "Controller state already exists; do not reset evidence/seed history")
        identity = read_json(args.resume.parent / "identity.json")
        config = read_json(args.config)
        require(identity.get("configuration") == config, "Resume identity differs from requested configuration")
        ledger = read_json(args.ledger)
        require(ledger.get("active") is None, "Campaign already has an active session")
        finite_tree(ledger, "ledger")
        campaign, ceiling = ledger["campaign_id"], ledger["limit_seconds"]
        charged = ledger["charged_seconds"]
        require(0 <= charged < ceiling, "No existing authorized training budget remains")
        deadline = deadline_timestamp(args.deadline)
        require(0 < deadline - time.time() <= 43200, "Absolute session deadline must be within12hours")
        executor = Executor(deadline)
        executor.hard_wall = deadline
        baseline = snapshot(args.resume, root / "retained" / "baseline")
        previous = best = baseline
        if getattr(args, "best_checkpoint", None) is not None:
            require(read_json(args.best_checkpoint.parent / "identity.json") == identity,
                    "Retained best differs in rules/configuration")
            best = snapshot(args.best_checkpoint, root / "retained" / "accepted-best")
            baseline = best
        state = {"schema": SCHEMA, "state": "starting", "pid": os.getpid(), "created_wall": time.time(),
                 "deadline_wall": deadline, "campaign_id": campaign, "authorized_limit_seconds": ceiling,
                 "baseline": str(baseline), "previous": str(previous), "best": str(best),
                 "cycles": 0, "jobs_reserved": 0, "next_seed": args.seed, "active_job": None,
                 "comparisons": [], "snapshots": [], "proof": "No improvement demonstrated yet",
                 "training_control": True, "configuration": {k: str(v) if isinstance(v, Path) else v for k, v in vars(args).items()}}
        latest_run = None

        def save():
            state["updated_wall"] = time.time()
            atomic_json(state_path, state)
            progress = dict(state, schema="shards-progress-watch-v1")
            atomic_json(root / "progress-watch.json", progress)
            if latest_run is not None:
                atomic_json(latest_run / "progress-watch.json", progress)

        def stop(signum, frame):
            executor.stopped = True
        signal.signal(signal.SIGTERM, stop)
        signal.signal(signal.SIGINT, stop)

        def check_budget(*, inactive=False):
            nonlocal charged
            doc = read_json(args.ledger)
            finite_tree(doc, "ledger")
            require(doc.get("campaign_id") == campaign and doc.get("limit_seconds") == ceiling,
                    "Campaign identity or authorized ceiling changed during controller")
            require(charged <= doc["charged_seconds"] <= ceiling + .1, "Budget charge decreased or exceeded authorization")
            charged = doc["charged_seconds"]
            require(not inactive or doc.get("active") is None, "Trainer did not release the budget lease")
            return max(0., ceiling - charged)

        def compare(current, opponent, kind, cycle):
            if executor.remaining() <= 30:
                raise DeadlineReached("Insufficient time for another safe frozen comparison")
            check_budget(inactive=True)
            before_charge = charged
            sequence = state["jobs_reserved"] + 1
            seed = state["next_seed"]
            state["next_seed"] += args.games // 2
            require(state["next_seed"] < 2**64, "Evaluation seed namespace exhausted")
            evaluation_dir = args.evaluation_dir or args.ledger.parent / "evaluations"
            output = evaluation_dir / f"overnight-{root.name}-{sequence:04d}-{kind}.json"
            output.parent.mkdir(parents=True, exist_ok=True)
            require(not output.exists(), "Evaluation output already exists; refusing to reuse evidence")
            command = [sys.executable, str(args.variant_entry), "evaluate", "--a", str(current), "--b", str(opponent),
                       "--a-role", "learner", "--b-role", "learner", "--games", str(args.games),
                       "--batch", str(args.eval_batch), "--workers", str(args.eval_workers), "--seed", str(seed),
                       "--sampling-seed", str(args.sampling_seed + sequence), "--output", str(output)]
            job = {"sequence": sequence, "cycle": cycle, "kind": kind, "current": str(current), "opponent": str(opponent),
                   "seed": seed, "games": args.games, "output": str(output), "started_wall": time.time(), "state": "running"}
            state.update(state="evaluating", active_job=job, jobs_reserved=sequence)
            save()
            hashes = (file_hash(current), file_hash(opponent))
            executor.execute(command, output.with_suffix(".log"), timeout=min(args.eval_timeout, executor.remaining()),
                             check=lambda: check_budget(inactive=True))
            require((file_hash(current), file_hash(opponent)) == hashes, "Retained checkpoint changed during evaluation")
            require(check_budget(inactive=True) >= 0 and charged == before_charge, "Frozen evaluation changed training budget")
            report = read_json(output)
            evidence = classify_evaluation(report, sequence, args.games, seed, identity, current, opponent)
            require(report["policy_a"].get("checkpoint_generation") == state["snapshots"][-1]["observed_generation"],
                    "Retained learner checkpoint lags final generation")
            report["progress_evidence"] = dict(evidence, comparison_kind=kind, sequence=sequence)
            atomic_json(output, report)
            job.update(state="completed", finished_wall=time.time(), evidence=evidence,
                       policy_a=report["policy_a"].get("version"), policy_b=report["policy_b"].get("version"))
            state["comparisons"].append(job)
            state["active_job"] = None
            state["last_result"] = job
            save()
            return job

        reserve = args.evaluation_reserve
        prior_start = None
        save()
        try:
            while not executor.stopped:
                remaining = check_budget(inactive=True)
                now = time.time()
                plan = slot_plan(now, now + args.interval, deadline, reserve, remaining, args.interval)
                if plan["training_seconds"] < 60 or executor.remaining() <= reserve + 60:
                    state.update(state="complete", reason="deadline_or_authorized_budget_cannot_fit_another_validated_segment")
                    break
                cycle = state["cycles"] + 1
                latest_run = root / f"segment-{cycle:04d}"
                latest_run.mkdir(exist_ok=False)
                started = time.time()
                state.update(state="training", current_run=str(latest_run), cycle_started_wall=started,
                             next_evaluation_wall=started + plan["training_seconds"], next_slot_wall=plan["next_slot"],
                             last_actual_start_to_start_seconds=None if prior_start is None else started - prior_start,
                             planned_training_seconds=plan["training_seconds"], evaluation_reserve_seconds=reserve)
                prior_start = started
                save()
                command = [sys.executable, str(args.variant_entry), "supervise", "--run-dir", str(latest_run),
                           "--ledger", str(args.ledger), "--seconds", str(plan["training_seconds"]),
                           "--config", str(args.config), "--resume", str(previous), "--label", f"overnight-segment-{cycle:04d}"]
                def live_check():
                    check_budget()
                    if (latest_run / "identity.json").exists():
                        require(read_json(latest_run / "identity.json") == identity, "Live training identity changed")
                    audit_metrics(latest_run / "metrics.jsonl", config)
                    audit_statistics(latest_run / "training-statistics", identity)
                executor.execute(command, latest_run / "console.log",
                                 timeout=min(plan["training_seconds"] + 180, executor.remaining()),
                                 supervisor_path=latest_run / "supervisor.json", check=live_check)
                check_budget(inactive=True)
                require(read_json(latest_run / "identity.json") == identity, "Training rules/source/host identity changed")
                audit = audit_metrics(latest_run / "metrics.jsonl", config, final=True)
                audit["statistics"] = audit_statistics(latest_run / "training-statistics", identity, audit, final=True)
                state["cumulative_statistics"]=accumulate_statistics(state.get("cumulative_statistics"),audit["statistics"],str(latest_run))
                audit["cumulative_observations"]=state["cumulative_statistics"]["observations"]
                atomic_json(latest_run / "automatic-audit.json", dict(audit, passed=True, updated_wall=time.time()))
                current = snapshot(latest_run / "latest.soicp", root / "retained" / f"segment-{cycle:04d}")
                state["snapshots"].append({"path": str(current), "sha256": file_hash(current), "wall": time.time(),
                                           "observed_generation": audit["last_generation"]})
                state["latest_health_audit"] = audit
                save()
                evaluation_started = time.monotonic()
                first = compare(current, previous, "previous", cycle)
                # A demonstrated regression is a concrete stop trigger; keep both
                # checkpoints and never silently roll back or train over evidence.
                require(first["evidence"]["verdict"] != "regressed", "Confirmed regression against previous learner")
                best_result = first if previous == best else compare(current, best, "best", cycle)
                require(best_result["evidence"]["verdict"] != "regressed", "Confirmed regression against retained best learner")
                if best_result["evidence"]["verdict"] == "improved":
                    best = current
                    state["proof"] = "Improvement demonstrated against retained best with sequential error control"
                else:
                    demonstrated=any(job["evidence"]["verdict"]=="improved" for job in state["comparisons"])
                    state["proof"] = ("Latest result inconclusive; earlier checkpoint improvements remain demonstrated" if demonstrated
                                      else "Latest result inconclusive; no demonstrated improvement yet")
                previous = current
                elapsed_eval = time.monotonic() - evaluation_started
                # Reserve for two comparisons even after a one-comparison cycle.
                jobs = 1 if best_result is first else 2
                reserve = min(args.interval - 120, max(args.evaluation_reserve, elapsed_eval / jobs * 2 * 1.25 + 30))
                state.update(cycles=cycle, previous=str(previous), best=str(best), state="waiting",
                             last_evaluation_seconds=elapsed_eval,
                             last_slot_overrun_seconds=max(0., time.time() - plan["next_slot"]),
                             next_evaluation_wall=max(time.time(), plan["next_slot"]))
                save()
                if getattr(args, "max_cycles", None) is not None and cycle >= args.max_cycles:
                    state.update(state="complete", reason="requested_cycle_limit")
                    save()
                    break
                while time.time() < plan["next_slot"] and executor.remaining() > 0 and not executor.stopped:
                    time.sleep(min(1., plan["next_slot"] - time.time(), executor.remaining()))
            if executor.stopped:
                state.update(state="stopped", reason="requested_stop")
            elif state["state"] != "complete":
                state.update(state="complete", reason="absolute_deadline")
            save()
            return 0
        except DeadlineReached as error:
            state.update(state="stopped", reason=str(error), automatic_retry=False)
            save()
            return 0
        except BaseException as error:
            state.update(state="paused_for_investigation", error={"type": type(error).__name__, "message": str(error)},
                         automatic_retry=False, action="Inspect active job, console, metrics, automatic audit and retained checkpoints. No automatic code edits or resume.")
            save()
            atomic_json(root / "action-required.json", state)
            return 1


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    for flag in ("run-root", "ledger", "config", "resume", "variant-entry"):
        parser.add_argument("--" + flag, type=Path, required=True)
    parser.add_argument("--best-checkpoint", type=Path, help="Earlier validated reference; latest resume need not have proved improvement")
    parser.add_argument("--deadline", required=True, help="Timezone-aware ISO8601 absolute deadline")
    parser.add_argument("--evaluation-dir", type=Path, help="Defaults to the campaign evaluations directory for monitoring")
    parser.add_argument("--max-cycles", type=int, help="Optional bounded integration pilot; production defaults to deadline")
    parser.add_argument("--interval", type=float, default=1200)
    parser.add_argument("--evaluation-reserve", type=float, default=240)
    parser.add_argument("--eval-timeout", type=float, default=600)
    parser.add_argument("--games", type=int, default=4096)
    parser.add_argument("--eval-batch", type=int, default=128)
    parser.add_argument("--eval-workers", type=int, default=8)
    parser.add_argument("--seed", type=lambda value: int(value, 0), default=0xB000000000000000)
    parser.add_argument("--sampling-seed", type=int, default=10270927)
    args = parser.parse_args()
    if args.max_cycles is not None and args.max_cycles < 1:parser.error("Cycle limit must be positive")
    if not (300 <= args.interval <= 3600 and 60 <= args.evaluation_reserve <= args.interval - 120 and
            60 <= args.eval_timeout <= 3600 and args.games >= 2 and args.games % 2 == 0 and
            1 <= args.eval_batch <= 256 and 1 <= args.eval_workers <= 16 and
            0 <= args.seed < 2**64 - 10000000 and 0 <= args.sampling_seed < 2**63 - 100000):
        parser.error("Invalid bounded cycle/evaluation configuration")
    for name in ("ledger", "config", "resume", "variant_entry"):
        setattr(args, name, getattr(args, name).resolve(strict=True))
    if args.best_checkpoint is not None:args.best_checkpoint=args.best_checkpoint.resolve(strict=True)
    return run(args)


if __name__ == "__main__":
    raise SystemExit(main())
