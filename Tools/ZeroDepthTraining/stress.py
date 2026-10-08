"""Bounded frozen-policy game stress checks; never learns from game outcomes.

Runs complete cohorts with several independently initialized frozen policies.
Checks legal actions, owned packets, raw GPU input bits and eager likelihoods.
Rules failures save exact CPU-replayable action traces before closing the host.
"""
from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path
import time

import numpy as np
import torch

from adaptive import AdaptiveActor
from benchmark import canonical_json_hash, file_hash, parameter_hash, save_json
from host import BINARY, Host
from model import Policy, PolicyConfig
from train import Rollout


def check_inputs(actor, host, lanes, packet):
    """Compare the exact consumed inputs; full unused-prefix checks are included."""
    rows = actor.source_rows(lanes)
    for name in ("obs", "candidates", "mask"):
        actual = getattr(actor, name)[torch.as_tensor(rows, device=actor.device)].cpu().numpy()
        expected = getattr(host, name)[lanes]
        if not np.array_equal(actual.view(np.uint32), expected.view(np.uint32)):
            raise AssertionError(f"Consumed {name} differs from the host float32 bits")
    with torch.inference_mode():
        child = actor.active_actor
        logits, values = child.policy(actor.obs, actor.candidates, actor.mask)
        chosen = torch.as_tensor(packet[lanes, 0], device=actor.device, dtype=torch.int64)
        selected = logits.log_softmax(-1)[torch.as_tensor(rows, device=actor.device), chosen]
        expected = torch.stack((selected, values[torch.as_tensor(rows, device=actor.device)]), -1).cpu().numpy()
    error = np.max(np.abs(expected - packet[lanes, 1:]), axis=0)
    if not np.isfinite(error).all() or np.any(error > .0002):
        raise AssertionError(f"Frozen eager behavior differs from actor: {error.tolist()}")
    return dict(logp_error=float(error[0]), value_error=float(error[1]))


def save_trace(output, host, actions, seed, binary, error, *, suffix="failure", failed_submission=False):
    path = Path(output).with_name(Path(output).stem + f"-{suffix}.npz")
    metadata = dict(cohort_seed=seed, batch=host.batch, workers=host.workers,
                    automation="singleton", host_binary_sha256=file_hash(binary),
                    catalog_sha256=canonical_json_hash(host.catalog), error=str(error),
                    actions_include_failed_final_submission=failed_submission,
                    censored_lanes=np.flatnonzero(host.done == 2).tolist())
    np.savez_compressed(path, actions=np.asarray(actions, dtype=np.int32).reshape(-1, host.batch),
                        metadata=np.asarray(json.dumps(metadata)), done=host.done.copy(),
                        actors=host.actors.copy(), last_scalars=host.obs[:, :256].copy())
    return str(path)


def run(args):
    torch.set_num_threads(1)
    torch.backends.fp32_precision = "ieee"
    torch.backends.cuda.matmul.fp32_precision = "ieee"
    if not torch.cuda.is_available():
        raise RuntimeError("The complete-input GPU stress check requires CUDA")
    result = dict(kind="bounded_frozen_policy_stress", training_started=False,
                  outcome_learning_updates=0, status="running", cohorts=[],
                  configuration={key: str(value) if isinstance(value, Path) else value
                                 for key, value in vars(args).items()},
                  host_sha256=file_hash(args.binary))
    save_json(args.output, result)
    started = time.monotonic()
    try:
        return collect(args, result, started)
    except BaseException as error:
        # Startup, refresh/reset and between-cohort deadline errors also publish
        # a failed result; a stale "running" report must never imply success.
        if result["status"] != "failed":
            result.update(status="failed", error=f"{type(error).__name__}: {error}",
                          elapsed_seconds=time.monotonic() - started)
            save_json(args.output, result)
        raise


def collect(args, result, started):
    history = []
    seed = args.seed
    with Host(batch=args.batch, workers=args.workers, seed=seed, binary=args.binary,
              automation=True, timeout=120) as host:
        result["catalog_sha256"] = canonical_json_hash(host.catalog)
        policy = actor = None
        store = None
        previous_bucket = None
        for cohort in range(args.games // args.batch):
            if time.monotonic() - started > args.max_seconds:
                raise TimeoutError("Bounded frozen stress deadline reached before another cohort")
            variant = cohort % len(args.policy_seeds)
            policy_seed = args.policy_seeds[variant]
            if policy is None or variant != (cohort - 1) % len(args.policy_seeds):
                torch.manual_seed(policy_seed)
                next_policy = Policy(host.catalog, PolicyConfig(width=args.width)).cuda().eval().requires_grad_(False)
                if args.diversified:
                    with torch.no_grad():
                        next_policy.query.weight.mul_(8)
                        # Static perturbations are chosen before collecting any
                        # game and never depend on winners, utilities or losses.
                        next_policy.kind_prior[10] -= .15 * variant
                policy = next_policy
                if actor is None:
                    actor = AdaptiveActor(policy, args.batch, graph=True, packed=True, compiled=args.compiled)
                else:
                    actor.refresh(policy)
            if args.rollout_capacity and store is None:
                available = torch.cuda.mem_get_info()[0]
                if Rollout.estimate_bytes(args.rollout_capacity, host.catalog) > available * .7:
                    raise RuntimeError("Frozen validation rollout exceeds 70% of available GPU memory")
                store = Rollout(args.rollout_capacity, host.catalog, device="cuda")
            if store is not None:
                store.reset()
            immutable_hash = parameter_hash(policy)
            seed = args.seed + cohort * args.batch
            if cohort:
                host.reset(seed)
            history = []
            checks = decisions = 0
            submission_pending = False
            max_errors = np.zeros(2)
            cohort_started = time.monotonic()
            try:
                while np.any(host.done == 0):
                    if time.monotonic() - started > args.max_seconds:
                        raise TimeoutError("Bounded frozen stress deadline reached during cohort")
                    lanes = np.flatnonzero(host.done == 0)
                    actions, packet = actor.act(host)
                    if not np.isfinite(packet).all():
                        raise AssertionError("Actor packet contains a nonfinite value")
                    if np.any(actions[host.done != 0] != -1) or np.any(packet[host.done != 0] != 0):
                        raise AssertionError("Held lanes were credited a sampled decision")
                    if np.any(actions[lanes] < 0) or np.any(actions[lanes] >= host.max_actions):
                        raise AssertionError("Sampled action outside the legal menu")
                    if not np.all(host.mask[lanes, actions[lanes]] == 1):
                        raise AssertionError("Sampled an illegal action")
                    bucket = actor.active_actor.batch
                    if len(history) % args.check_every == 0 or bucket != previous_bucket:
                        errors = check_inputs(actor, host, lanes, packet)
                        max_errors = np.maximum(max_errors, [errors["logp_error"], errors["value_error"]])
                        checks += 1
                    previous_bucket = bucket
                    if store is not None:
                        store.append(host, packet, lanes, actor=actor)
                    history.append(actions.copy())
                    decisions += len(lanes)
                    submission_pending = True
                    host.advance(actions)
                    submission_pending = False
                censored = int(np.count_nonzero(host.done == 2))
                censor_trace = None
                if censored:
                    censor_trace = save_trace(args.output, host, history, seed, args.binary,
                                              "administrative_censor", suffix=f"censor-s{seed}")
                if censored and not args.allow_censors:
                    raise AssertionError(f"Stress cohort ended with {censored} administrative censors")
                if not np.isin(host.rewards, (-1., 0., 1.)).all() or not np.all(host.rewards.sum(1) == 0):
                    raise AssertionError("Non-zero-sum or invalid terminal utilities")
                if parameter_hash(policy) != immutable_hash:
                    raise AssertionError("Frozen stress model changed")
                if any(parameter_hash(child.policy) != immutable_hash for child in actor.children.values()):
                    raise AssertionError("A frozen inference bucket changed its model")
                excluded_rows = 0
                if store is not None:
                    indices, returns, _advantages, excluded_rows = store.seal(host.done, host.rewards)
                    expected = np.flatnonzero(host.done[store.lanes[:store.rows]] == 1)
                    if store.rows != decisions or not np.array_equal(indices, expected):
                        raise AssertionError("Owned rollout lost rows or retained censored rows")
                    expected_returns = host.rewards[store.lanes[indices], store.seats[indices]]
                    if not np.array_equal(returns, expected_returns):
                        raise AssertionError("Owned rollout used the wrong deciding-seat outcome")
                    # The sealed vectors are validation data only. No learner
                    # exists in this process and no optimizer is invoked.
                item = dict(seed=seed, policy_seed=policy_seed, policy_sha256=immutable_hash,
                            completed_games=int(np.count_nonzero(host.done == 1)), censored_games=censored,
                            unresolved_games=int(np.count_nonzero(host.done == 0)), wrapper_batches=len(history),
                            policy_rows=decisions, input_behavior_checks=checks,
                            maximum_logp_error=float(max_errors[0]), maximum_value_error=float(max_errors[1]),
                            censor_trace=censor_trace,
                            owned_rollout_rows=store.rows if store is not None else 0,
                            excluded_censored_rows=excluded_rows,
                            seconds=time.monotonic() - cohort_started,
                            action_sha256=hashlib.sha256(np.asarray(history, np.int32).tobytes()).hexdigest(),
                            terminal_rewards_sha256=hashlib.sha256(host.rewards.tobytes()).hexdigest())
                result["cohorts"].append(item)
                result["elapsed_seconds"] = time.monotonic() - started
                save_json(args.output, result)
                print(json.dumps({"stress_cohort": item}), flush=True)
            except BaseException as error:
                result.update(status="failed", error=f"{type(error).__name__}: {error}",
                              failure_trace=save_trace(args.output, host, history, seed, args.binary, error,
                                                       failed_submission=submission_pending),
                              elapsed_seconds=time.monotonic() - started)
                save_json(args.output, result)
                raise
    if time.monotonic() - started > args.max_seconds:
        raise TimeoutError("Bounded frozen stress deadline reached before final publication")
    result.update(status="complete", completed_games=sum(c["completed_games"] for c in result["cohorts"]),
                  censored_games=sum(c["censored_games"] for c in result["cohorts"]),
                  natural_completion_only=all(c["censored_games"] == 0 for c in result["cohorts"]),
                  policy_rows=sum(c["policy_rows"] for c in result["cohorts"]),
                  input_behavior_checks=sum(c["input_behavior_checks"] for c in result["cohorts"]),
                  unresolved_games=0, elapsed_seconds=time.monotonic() - started,
                  scope="validation with frozen synthetic policies; concurrent checks are not a throughput forecast")
    save_json(args.output, result)
    return result


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--binary", type=Path, default=BINARY)
    parser.add_argument("--games", type=int, default=4096)
    parser.add_argument("--batch", type=int, default=128)
    parser.add_argument("--workers", type=int, default=8)
    parser.add_argument("--seed", type=int, default=202610020)
    parser.add_argument("--policy-seeds", type=lambda value: [int(x) for x in value.split(",")], default=[716203, 919017, 279341, 812743])
    parser.add_argument("--width", type=int, default=512)
    parser.add_argument("--compiled", action="store_true")
    parser.add_argument("--diversified", action="store_true")
    parser.add_argument("--allow-censors", action="store_true",
                        help="Audit through bounded-policy cutoffs, retaining each exact censor trace; no outcome credit")
    parser.add_argument("--check-every", type=int, default=64)
    parser.add_argument("--rollout-capacity", type=int, default=0,
                        help="Optionally validate a GPU-owned complete-cohort rollout without updates")
    parser.add_argument("--max-seconds", type=float, default=600)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    if not 1 <= args.batch <= 1024 or not args.batch <= args.games <= 16384 or args.games % args.batch:
        raise ValueError("Stress games must be a whole number of bounded cohorts")
    if not 1 <= args.workers <= 16 or not 64 <= args.width <= 1024 or not 1 <= args.check_every <= 4096:
        raise ValueError("Invalid worker/policy/check bounds")
    if not 1 <= args.max_seconds <= 1800 or not 0 <= args.seed or args.seed + args.games >= 1 << 63:
        raise ValueError("Invalid stress deadline or seed namespace")
    if not args.policy_seeds or any(not 0 <= seed < 1 << 32 for seed in args.policy_seeds):
        raise ValueError("Policy seeds must fit uint32")
    if args.rollout_capacity != 0 and not args.batch <= args.rollout_capacity <= 1048576:
        raise ValueError("Validation rollout must be disabled or fit a bounded complete action batch")
    result = run(args)
    print(json.dumps({key: result[key] for key in ("status", "completed_games", "censored_games", "elapsed_seconds", "training_started")}))


if __name__ == "__main__":
    main()
