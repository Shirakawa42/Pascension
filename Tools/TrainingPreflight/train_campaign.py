"""Budgeted real-outcome self-play. Run only through supervise_training.py.

Pilots and the main run share one 12-hour ledger. Checkpoints contain finite
learner/Adam state, frozen opponents, RNG, pinned identities and next seed.
Every episode finishes under one behavior/opponent version before its outcome
is used. The preflight's synthetic LearnerLoad is never imported or called.
"""
from __future__ import annotations

import argparse
from dataclasses import asdict, dataclass
import hashlib
import json
import os
from pathlib import Path
import random
import signal
import subprocess
import time

import numpy as np
import torch

from bench_common import DOTNET, ROOT, gpu_status, save_json
from campaign_state import CampaignBudget, load_checkpoint, restore_rng, save_checkpoint_atomic
from learning_eval import evaluate_match
from learning_model import LearningActor, LearningPolicy, PolicyConfig, PPOConfig, PPOLearner
from learning_rollout import EpisodeStore, LearningHost, collect_episodes

HERE = Path(__file__).resolve().parent


@dataclass
class TrainConfig:
    width: int = 128
    batch: int = 256
    workers: int = 8
    capacity: int = 524288
    minibatch: int = 2048
    epochs: int = 3
    learner_mode: str = "eager"
    adaptive_actors: bool = False
    censor_truncated: bool = True
    censor_limit: int = 4
    learning_rate: float = 3e-4
    entropy: float = .01
    archive_fraction: float = .25
    archive_every: int = 8
    archive_limit: int = 16
    checkpoint_seconds: float = 60.
    eval_seconds: float = 600.
    eval_games: int = 256
    seed: int = 60926
    engine_seed: int = 0x1000000000000000


def file_hash(paths):
    digest = hashlib.sha256()
    for path in sorted(paths):
        digest.update(str(path.relative_to(ROOT)).encode())
        digest.update(path.read_bytes())
    return digest.hexdigest()


def identity(config):
    binary = HERE/"Host/bin/Release/net8.0/TrainingHost.dll"
    catalog = json.loads(subprocess.check_output([DOTNET, str(binary), "catalog"], text=True, timeout=20))
    if catalog.get("observationSchema") != "shards-observation-v2":
        raise RuntimeError("Rebuild the schema-v2/selective-step host before training")
    rules = []
    for folder in ("Assets/Scripts/Core", "Assets/Scripts/Shards/Engine", "Assets/Scripts/Shards/Content"):
        rules.extend((ROOT/folder).rglob("*.cs"))
    source = [HERE/name for name in ("model.py", "learning_model.py", "learning_rollout.py",
              "learning_eval.py", "train_campaign.py", "campaign_state.py", "pipeline_bench.py", "bench_common.py")]
    if config.adaptive_actors:
        source.append(HERE/"adaptive_actor.py")
    return {"schema": "shards-real-selfplay-v2", "observation_schema": catalog["observationSchema"],
        "rules_sha256": file_hash(rules), "host_binary_sha256": hashlib.sha256(binary.read_bytes()).hexdigest(),
        "catalog_sha256": hashlib.sha256(json.dumps(catalog, sort_keys=True).encode()).hexdigest(),
        "training_source_sha256": file_hash(source), "configuration": asdict(config)}, catalog


def cpu_policy(policy):
    return {key: value.detach().cpu().clone() for key, value in policy.state_dict().items()}


def policy_from_state(config, state):
    model = LearningPolicy(PolicyConfig(**config)).cuda()
    model.load_state_dict(state, strict=True)
    return model


@torch.no_grad()
def verify_behavior(policy, store, chunk=2048):
    """Check all stored behavior probabilities before any update of a generation."""
    max_logp = max_value = 0.
    for start in range(0, store.rows, chunk):
        part = slice(start, min(store.rows, start+chunk))
        logits, values = policy(store.obs[part], store.candidates[part], store.mask[part])
        recomputed = logits.log_softmax(-1).gather(1, store.actions[part, None]).squeeze(1)
        errors = torch.stack(((recomputed-store.old_logp[part]).abs().max(),
                              (values-store.old_values[part]).abs().max()))
        a, b = errors.cpu().tolist()
        if not np.isfinite([a, b]).all():
            raise RuntimeError("Nonfinite behavior-likelihood verification")
        max_logp, max_value = max(max_logp, a), max(max_value, b)
    # TF32 can choose different kernels at actor and learner batch sizes.
    if max_logp > .01 or max_value > .005:
        raise RuntimeError(f"Stored behavior mismatch: logp={max_logp}, value={max_value}")
    return {"max_abs_behavior_logp_error": max_logp, "max_abs_behavior_value_error": max_value}


def train(config, run_dir, ledger, seconds, resume=None, label="training"):
    # Reject a duplicate campaign before allocating CUDA models or rollout RAM.
    with CampaignBudget(ledger) as budget:
        return _train_locked(config, run_dir, ledger, seconds, resume, label, budget)


def _train_locked(config, run_dir, ledger, seconds, resume, label, budget):
    run_dir = Path(run_dir)
    run_dir.mkdir(parents=True, exist_ok=True)
    if not resume and (run_dir/"latest.soicp").exists():
        raise RuntimeError("Existing checkpoint requires --resume; use a new directory for a fresh pilot")
    torch.set_num_threads(1)
    torch.set_num_interop_threads(1)
    torch.backends.fp32_precision = "ieee"
    torch.backends.cuda.matmul.fp32_precision = "tf32"
    random.seed(config.seed)
    np.random.seed(config.seed)
    torch.manual_seed(config.seed)
    pinned, catalog = identity(config)
    identity_path = run_dir/"identity.json"
    if identity_path.exists() and json.loads(identity_path.read_text()) != pinned:
        raise RuntimeError("Run directory has a different pinned identity; do not overwrite it")
    save_json(identity_path, pinned)
    save_json(run_dir/"catalog.json", catalog)
    policy = LearningPolicy(PolicyConfig(width=config.width)).cuda()
    learner = PPOLearner(policy, PPOConfig(learning_rate=config.learning_rate,
        entropy_coefficient=config.entropy), mode=config.learner_mode, batch=config.minibatch)
    if config.adaptive_actors:
        from adaptive_actor import AdaptiveLearningActor
        actor = AdaptiveLearningActor(policy, config.batch, graph=True)
    else:
        actor = LearningActor(policy, config.batch, graph=True)
    opponent_policy = LearningPolicy(policy.config).cuda()
    opponent_actor = (AdaptiveLearningActor(opponent_policy, config.batch, graph=True) if config.adaptive_actors
                      else LearningActor(opponent_policy, config.batch, graph=True))
    store = EpisodeStore(config.capacity)
    updates = decisions = games = optimizer_passes = eval_index = 0
    next_seed = config.engine_seed
    archive = [{"version": 0, "policy": cpu_policy(policy)}]
    champion = {"version": 0, "policy": cpu_policy(policy)}
    initial_policy = cpu_policy(policy)
    host = None
    stop_requested = False

    def stop(signum, frame):
        nonlocal stop_requested
        stop_requested = True

    signal.signal(signal.SIGTERM, stop)
    signal.signal(signal.SIGINT, stop)
    log_file = open(run_dir/"metrics.jsonl", "a", buffering=1)

    def log(event):
        event = {"utc": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime()), **event}
        log_file.write(json.dumps(event, allow_nan=False)+"\n")
        print(json.dumps({key: value for key, value in event.items() if key in
              ("event", "generation", "games", "learning_rows", "seconds", "score_a", "reason", "charged_seconds")}), flush=True)

    try:
        if resume:
            payload = load_checkpoint(resume, expected_identity=pinned, budget=budget)
            state = payload["state"]
            learner.load_state_dict(state["learner"], restore_rng=False)
            updates, decisions, games = state["generations"], state["decisions"], state["games"]
            optimizer_passes, next_seed, eval_index = state["optimizer_passes"], state["next_engine_seed"], state["eval_index"]
            archive, champion, initial_policy = state["archive"], state["champion"], state["initial_policy"]
            actor.refresh(policy, updates)
            # Restore only after constructing/capturing actors; graph warmup
            # must not consume the saved next rollout's RNG sequence.
            restore_rng(payload)
            log({"event": "resumed", "generation": updates, "checkpoint": str(resume)})
        with budget.start_session(label, requested_seconds=seconds,
                                  stop_buffer_seconds=min(30., seconds/5, budget.remaining_seconds/5)) as session:
            save_json(run_dir/"deadline.json", {"pid": os.getpid(), "hard_deadline_wall": session.hard_deadline_wall,
                "hard_deadline_monotonic": session.hard_deadline_monotonic,
                "ledger": str(Path(ledger).resolve()), "requested_seconds": seconds})
            host = LearningHost(config.batch, config.workers, next_seed, pinned=True, transport="shared", split_branches=8)
            log({"event": "session_start", "generation": updates, "label": label,
                 "configuration": asdict(config), "charged_seconds": budget.charged_seconds,
                 "trainer_pid": os.getpid(), "host_pid": host.process.pid, "gpu": gpu_status()})
            checkpoint_games, checkpoint_decisions = games, decisions

            def checkpoint(reason):
                nonlocal checkpoint_games, checkpoint_decisions
                state = {"learner": learner.state_dict(), "generations": updates, "decisions": decisions,
                    "games": games, "optimizer_passes": optimizer_passes, "next_engine_seed": next_seed,
                    "eval_index": eval_index, "archive": archive, "champion": champion,
                    "initial_policy": initial_policy, "policy_config": policy.config.to_dict(),
                    "configuration": asdict(config), "reason": reason}
                info = save_checkpoint_atomic(run_dir/"latest.soicp", state, identity=pinned, budget=budget)
                if reason == "champion_promoted":
                    save_checkpoint_atomic(run_dir/"champion.soicp", state, identity=pinned, budget=budget)
                checkpoint_games, checkpoint_decisions = games, decisions
                session.heartbeat({"generation": updates, "unresolved_episodes": 0, "unresolved_decisions": 0})
                save_json(run_dir/"status.json", {"state": "running" if reason != "session_end" else "session_complete",
                    "pid": os.getpid(), "trainer_pid": os.getpid(), "host_pid": host.process.pid if host else None,
                    "updated_wall": time.time(), "generation": updates, "games": games, "decisions": decisions,
                    "optimizer_example_passes": optimizer_passes, "checkpoint": info,
                    "budget": budget.snapshot(), "last_reason": reason, "gpu": gpu_status()})
                return time.monotonic()

            last_checkpoint = checkpoint("initial_boundary")
            last_eval = time.monotonic()
            while not session.should_stop and not stop_requested:
                round_start = time.monotonic()
                actor.refresh(policy, updates)
                frozen = champion if random.random() < .25 else random.choice(archive)
                opponent_policy.load_state_dict(frozen["policy"])
                opponent_actor.refresh(opponent_policy, frozen["version"])
                versus_archive = np.random.random(config.batch) < config.archive_fraction
                learner_seats = (np.arange(config.batch)+updates) % 2
                collection = collect_episodes(host, actor, store, version=updates, session=session,
                    opponent=opponent_actor, opponent_lanes=versus_archive, learner_seats=learner_seats,
                    uncommitted_episodes=games-checkpoint_games, uncommitted_decisions=decisions-checkpoint_decisions,
                    stop_check=lambda: stop_requested, censor_truncated=config.censor_truncated,
                    seed_base=next_seed, debug_dir=run_dir/"censored-episodes")
                if not collection.completed:
                    budget.note_discarded(episodes=config.batch, decisions=store.rows,
                                         reason=collection.metrics["reason"])
                    # Host games are discarded. Restart skips these seeds.
                    next_seed += config.batch
                    log({"event": "discarded_collection", **collection.metrics})
                    break
                next_seed += config.batch
                accounting = budget.record_collection(
                    attempted_games=collection.metrics["attempted_games"],
                    completed_games=collection.metrics["completed_games"],
                    censored_games=collection.metrics["censored_games"],
                    learning_rows=collection.metrics["learning_rows"],
                    censored_rows=collection.metrics["censored_learning_rows"],
                    details={"run_dir": str(run_dir), "generation": updates,
                             "diagnostics": [{key: report.get(key) for key in
                                 ("lane", "engine_seed", "pre_step_round", "wrapper_decisions", "trace_path")}
                                 for report in collection.metrics.get("censored_episodes", [])][:4]})
                if collection.metrics["censored_games"]:
                    log({"event": "censored_collection", "generation": updates,
                         "collection": collection.metrics, "campaign_episode_accounting": accounting})
                if accounting["recent_censored"] >= config.censor_limit:
                    raise RuntimeError(f"Censor ceiling reached: {accounting['recent_censored']} episodes in the recent attempt window")
                if not collection.metrics.get("update_ready", True):
                    raise RuntimeError("Collection contains no completed learner experience; refusing an update")
                games += collection.metrics["completed_games"]
                decisions += store.rows
                verification_start = time.monotonic()
                parity = verify_behavior(policy, store, config.minibatch)
                verification_seconds = time.monotonic()-verification_start
                learn_start = time.monotonic()
                accepted = rejected = 0
                passes_before = optimizer_passes
                statistics = []
                for epoch in range(config.epochs):
                    indices = torch.randperm(store.rows, device="cuda")
                    # Keep the verified graph shape without dropping any real
                    # samples. Uniform extra rows fill the final minibatch;
                    # repeated uses are counted explicitly as optimizer passes.
                    extra = (-store.rows) % config.minibatch
                    if extra:
                        indices = torch.cat((indices, torch.randint(store.rows, (extra,), device="cuda")))
                    for start in range(0, len(indices), config.minibatch):
                        if session.should_stop or stop_requested:
                            break
                        minibatch_indices = indices[start:start+config.minibatch]
                        result = learner.step(**store.minibatch(minibatch_indices))
                        statistics.append(result)
                        if not result["accepted"]:
                            rejected += 1
                            break
                        accepted += 1
                        optimizer_passes += len(minibatch_indices)
                        if accepted % 16 == 0:
                            session.heartbeat({"generation": updates, "unresolved_episodes": games-checkpoint_games,
                                               "unresolved_decisions": decisions-checkpoint_decisions})
                    if rejected or session.should_stop or stop_requested:
                        break
                learning_seconds = time.monotonic()-learn_start
                updates += 1
                finite = learner.validate_state()
                if updates % config.archive_every == 0:
                    archive.append({"version": updates, "policy": cpu_policy(policy)})
                    if len(archive) > config.archive_limit:
                        # Keep initial/latest policies and spread older versions
                        # through the full history, instead of forgetting all
                        # opponents older than the last few minutes.
                        redundant = min(range(1, len(archive)-3),
                            key=lambda i: archive[i+1]["version"]-archive[i-1]["version"])
                        archive.pop(redundant)
                means = {}
                for key in ("loss", "policy_loss", "value_loss", "entropy", "approx_kl", "clip_fraction", "grad_norm"):
                    values = [row[key] for row in statistics if row.get(key) is not None]
                    if values:
                        means[key] = float(np.mean(values))
                event = {"event": "generation", "generation": updates, **collection.metrics,
                    "games": games, "decisions_total": decisions,
                    "seconds": time.monotonic()-round_start, "collection_seconds": collection.metrics["seconds"],
                    "verification_seconds": verification_seconds, "learning_seconds": learning_seconds,
                    "accepted_optimizer_steps": accepted, "rejected_minibatches": rejected,
                    "optimizer_example_passes": optimizer_passes,
                    "sample_reuse": (optimizer_passes-passes_before)/store.rows,
                    "metrics": means, "behavior_parity": parity, "finite": finite,
                    "campaign_episode_accounting": accounting,
                    "opponent_version": frozen["version"], "charged_seconds": budget.charged_seconds,
                    "gpu_allocated_bytes": torch.cuda.memory_allocated()}
                log(event)
                session.heartbeat({"generation": updates, "unresolved_episodes": games-checkpoint_games,
                                   "unresolved_decisions": decisions-checkpoint_decisions})
                if time.monotonic()-last_checkpoint >= config.checkpoint_seconds:
                    last_checkpoint = checkpoint("periodic")
                if config.eval_seconds > 0 and time.monotonic()-last_eval >= config.eval_seconds and not session.should_stop:
                    old_champion = policy_from_state(policy.config.to_dict(), champion["policy"])
                    evaluation = evaluate_match(policy, old_champion, games=config.eval_games,
                        seed=0x2000000000000000+config.seed*100000000+eval_index*100000,
                        batch=min(128, config.batch), workers=config.workers, session=session,
                        stop_check=lambda: stop_requested, censor_truncated=config.censor_truncated,
                        debug_dir=run_dir/"evaluation-caps")
                    eval_index += 1
                    last_eval = time.monotonic()
                    log({"event": "champion_evaluation", "generation": updates,
                         "champion_version": champion["version"], **evaluation})
                    if evaluation["complete"] and evaluation["score_bound_95"][0] > .5:
                        champion = {"version": updates, "policy": cpu_policy(policy)}
                        last_checkpoint = checkpoint("champion_promoted")
                    del old_champion
            # Any partially updated full batch is valid to checkpoint; no
            # incomplete episode is assigned a fabricated target.
            host.close()
            host = None
            checkpoint("session_end")
            log({"event": "session_end", "generation": updates, "games": games,
                "charged_seconds": budget.charged_seconds, "reason": "signal" if stop_requested else "session_budget"})
        save_json(run_dir/"budget-after.json", budget.snapshot())
    except BaseException as error:
        save_json(run_dir/"status.json", {"state": "failed", "pid": os.getpid(),
            "trainer_pid": os.getpid(), "host_pid": host.process.pid if host else None,
            "updated_wall": time.time(), "generation": updates, "games": games, "decisions": decisions,
            "failure_type": type(error).__name__, "message": str(error)})
        save_json(run_dir/"failure.json", {"type": type(error).__name__, "message": str(error),
            "generation": updates, "last_known_good_checkpoint": str(run_dir/"latest.soicp"),
            "automatic_retry": False, "utc": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime())})
        raise
    finally:
        if host is not None:
            host.close()
        log_file.close()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--run-dir", type=Path, required=True)
    parser.add_argument("--ledger", type=Path, required=True)
    parser.add_argument("--seconds", type=float, required=True)
    parser.add_argument("--config", type=Path, required=True)
    parser.add_argument("--resume", type=Path)
    parser.add_argument("--label", default="training")
    args = parser.parse_args()
    config = TrainConfig(**json.loads(args.config.read_text()))
    if not 1 <= args.seconds <= 43200 or not 16 <= config.batch <= 1024 or not 1 <= config.workers <= 16:
        parser.error("Invalid duration/batch/worker bounds")
    if not 1 <= config.epochs <= 8 or not 128 <= config.minibatch <= 8192 or not 0 <= config.archive_fraction <= .5:
        parser.error("Invalid PPO/league bounds")
    if config.censor_limit != 4:
        parser.error("The reviewed censor guard is fixed at four in the recent 4096-attempt window")
    train(config, args.run_dir, args.ledger, args.seconds, args.resume, args.label)


if __name__ == "__main__":
    main()
