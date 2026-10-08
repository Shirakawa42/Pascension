"""Bounded frozen CPU matches using the production evaluation loop unchanged.

Run in a separate process, e.g. CUDA_VISIBLE_DEVICES='' OMP_NUM_THREADS=1
nice -n 10 timeout --kill-after=5s 180s python cpu_shadow_eval.py --a ...
--b ... --games 16 --max-seconds 150 --output ... . The outer timeout also
bounds a stalled host; the cooperative limit is checked between host replies.
No campaign ledger, optimizer, CUDA context, pinned allocation or graph is used.
CPU FP32 sampling preserves the legal policy distribution, but CPU and CUDA
numerical kernels/RNG differ; see the pinned variant. Label score series explicitly.
"""
from __future__ import annotations

import argparse
from contextlib import contextmanager
import hashlib
import json
import os
from pathlib import Path
import resource
import shutil
import sys
import tempfile
import time
from unittest.mock import patch

# This standalone experiment must never use an available GPU accidentally.
os.environ["CUDA_VISIBLE_DEVICES"] = ""
os.environ["OMP_NUM_THREADS"] = "1"
os.environ["MKL_NUM_THREADS"] = "1"
sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

import numpy as np
import torch

from bench_common import save_json
import evaluate_checkpoints as checkpoints
import learning_eval
import learning_model
from model import sample_actions
from balance_telemetry import BalanceTelemetry


SCHEMA = "shards-cpu-shadow-evaluation-v1"
HEROES = ("decima", "tetra", "volos", "kosynwu", "rez")
KINDS = ("play", "buy", "fastplay", "focus", "exhaust", "monster", "destiny",
         "relic", "reroll", "hero", "end", "concede", "select", "finish", "page", "integer")
_ACTOR = learning_model.LearningActor
_HOST = learning_eval.LearningHost


def configure_cpu():
    if torch.cuda.is_initialized():
        raise RuntimeError("CPU shadow evaluation requires an isolated process with no CUDA context")
    torch.set_num_threads(1)
    if torch.get_num_interop_threads() != 1:
        torch.set_num_interop_threads(1)
    torch.backends.fp32_precision = "ieee"


def configure_variant(variant):
    """Opt into v3/v4/v5 in this process; retain strict loader identity.

    The evaluator was imported before CLI parsing, so explicitly update its two
    captured references after installing the selected strict runtime. Host transport
    resolves the runtime's binary proxy dynamically. CPUActor still forces an
    eager CPU actor with full validation, independently of training fast paths.
    """
    global _HOST
    if variant == "v2":
        return
    if variant == "v3":
        from variant_runtime import install, variant_identity
    elif variant == "v4":
        from variant_v4_runtime import install, variant_identity
    elif variant == "v5":
        from variant_v5_runtime import install, variant_identity
    elif variant == "v6":
        from variant_v6_runtime import install, variant_identity
    elif variant == "v7":
        from variant_v7_runtime import install, variant_identity
    elif variant == "v8":
        from variant_v8_runtime import install, variant_identity
    elif variant == "v9":
        from variant_v9_runtime import install, variant_identity
    else:
        raise ValueError("Expected runtime variant v2, v3, v4, v5, v6, v7 or v8")
    install()
    _HOST = learning_eval.LearningHost
    checkpoints.identity = variant_identity
    checkpoints.LearningPolicy = learning_model.LearningPolicy
    if torch.cuda.is_initialized():
        raise RuntimeError("Variant installation unexpectedly initialized CUDA")


def frozen_selections(path_a, role_a, path_b, role_b):
    """Snapshot each distinct resolved path through one open handle before validation.

    A and B roles of the same latest.soicp therefore use the exact same payload,
    even when a concurrent trainer atomically replaces that checkpoint path.
    The usual source/rules/catalog/checksum/boundary validation still applies.
    """
    paths = [Path(path).expanduser().resolve() for path in (path_a, path_b)]
    with tempfile.TemporaryDirectory(prefix="shards-shadow-snapshots-") as folder:
        copies = {}
        for original in dict.fromkeys(paths):
            destination = Path(folder)/str(len(copies))/"snapshot.soicp"
            destination.parent.mkdir()
            # Opening before copy pins the old inode across atomic replacement.
            with original.open("rb") as source, destination.open("wb") as target:
                shutil.copyfileobj(source, target)
            shutil.copyfile(original.parent/"identity.json", destination.parent/"identity.json")
            copies[original] = destination
        selections = []
        for original, role in zip(paths, (role_a, role_b)):
            selection = checkpoints.load_selection(copies[original], role)
            selection.metadata.update(checkpoint=str(original), run_dir=str(original.parent),
                                      snapshot_scope="one immutable copy per distinct resolved input path")
            selections.append(selection)
    if selections[0].catalog != selections[1].catalog:
        raise ValueError("Frozen policies have different observation/card catalogs")
    return selections


class CPUActor(_ACTOR):
    def __init__(self, policy, batch, *, role, **kwargs):
        kwargs.update(graph=False, device="cpu", validate_inputs=True)
        super().__init__(policy, batch, **kwargs)
        self.role = role

    def compute(self):
        logits, values = self.compute_logits()
        logp = logits.log_softmax(-1)
        probability = logp.exp()
        self.diagnostics = {
            "entropy": (-(probability * logp).sum(-1)).numpy().copy(),
            "max_probability": probability.max(-1).values.numpy().copy(),
            "legal_count": self.mask.sum(-1).numpy().astype(np.int64),
        }
        return sample_actions(logits, values)

    def act(self, host=None):
        actions, packet = super().act(host)
        if isinstance(host, ObservedHost):
            host.packets[self.role] = (packet, self.diagnostics)
        return actions, packet


class ShadowTelemetry:
    def __init__(self, balance=False):
        self.balance = BalanceTelemetry() if balance else None
        self.episodes = []
        self.host_advance_calls = self.sleep_calls = 0
        self.sleep_requested_seconds = self.sleep_observed_seconds = 0.
        self.policies = [{"decisions": 0, "forced_decisions": 0, "entropy_sum": 0.,
            "choice_entropy_sum": 0., "normalized_choice_entropy_sum": 0.,
            "max_probability_sum": 0., "resolved_value_rows": 0, "value_squared_error_sum": 0.,
            "legal_menu_counts": [0]*65, "action_kind_counts": [0]*16} for _ in range(2)]

    def result(self):
        policies = []
        for data in self.policies:
            count, forced = data["decisions"], data["forced_decisions"]
            choice = count-forced
            policies.append({**data,
                "mean_entropy": data["entropy_sum"]/count if count else None,
                "mean_choice_entropy": data["choice_entropy_sum"]/choice if choice else None,
                "mean_normalized_choice_entropy": data["normalized_choice_entropy_sum"]/choice if choice else None,
                "forced_fraction": forced/count if count else None,
                "mean_max_probability": data["max_probability_sum"]/count if count else None,
                "value_mse": data["value_squared_error_sum"]/data["resolved_value_rows"] if data["resolved_value_rows"] else None})
        groups = {}
        for episode in self.episodes:
            key = (episode["policy_a_seat"], episode["policy_a_hero"], episode["policy_b_hero"])
            data = groups.setdefault(key, {"policy_a_seat": key[0], "policy_a_hero": key[1],
                "policy_b_hero": key[2], "attempts": 0, "resolved": 0, "censored": 0,
                "wins_a": 0, "draws": 0, "losses_a": 0})
            data["attempts"] += 1
            score = episode["score_a"]
            if score is None:
                data["censored"] += 1
            else:
                data["resolved"] += 1
                data[{1.: "wins_a", .5: "draws", 0.: "losses_a"}[score]] += 1
        for data in groups.values():
            score_sum = data["wins_a"]+.5*data["draws"]
            data["score_a"] = score_sum/data["attempts"] if not data["censored"] else None
            data["score_identification_interval"] = [score_sum/data["attempts"],
                (score_sum+data["censored"])/data["attempts"]]
        return {"policies": dict(zip(("a", "b"), policies)), "episodes": self.episodes,
            "host_advance_calls": self.host_advance_calls, "sleep_calls": self.sleep_calls,
            "sleep_requested_seconds": self.sleep_requested_seconds,
            "sleep_observed_seconds": self.sleep_observed_seconds,
            "hero_seat_summary": list(groups.values()),
            "hero_summary_scope": "Descriptive conditional groups, without confidence claims or causal hero-strength estimates; hero choices depend on both policies and board. Global evaluation bound retains paired seeds.",
            "hero_names": list(HEROES), "action_kind_names": list(KINDS),
            "scope": "Only active lanes and the acting policy; entropy includes censored attempts. Value MSE uses actual terminal outcomes, excludes censored episodes, and weights visited decisions. Hero/outcome associations are not causal balance estimates."}


class ObservedHost:
    """Read public pre-action observations; preserve the host protocol unchanged."""
    def __init__(self, host, telemetry, seed, seat_a, on_close=None, step_delay_ms=0.):
        self.host, self.telemetry, self.seed, self.seat_a = host, telemetry, seed, seat_a
        self.packets = {}
        self.hero_codes = np.zeros((host.batch, 2), np.int64)
        self.lengths = np.zeros(host.batch, np.int64)
        self.value_count = np.zeros((host.batch, 2), np.int64)
        self.value_sum = np.zeros((host.batch, 2), np.float64)
        self.value_square_sum = np.zeros((host.batch, 2), np.float64)
        self.recorded = np.zeros(host.batch, bool)
        self.closed = False
        self.on_close = on_close
        self.step_delay_seconds = step_delay_ms/1000
        self.balance = telemetry.balance.new_batch(host.batch, seed, seat_a) if telemetry.balance is not None else None

    def __getattr__(self, name):
        return getattr(self.host, name)

    def advance_active(self, actions, active):
        lanes = np.flatnonzero(active)
        seats = self.host.seats.copy()
        # Encoder v2 fields22/70 are own/opponent public hero(index+1)/5.
        own = np.rint(self.host.obs[lanes, 22]*5).astype(np.int64)
        opponent = np.rint(self.host.obs[lanes, 70]*5).astype(np.int64)
        if np.any((own < 0) | (own > 5) | (opponent < 0) | (opponent > 5)):
            raise RuntimeError("Unexpected public hero encoding")
        self.hero_codes[lanes, seats[lanes]] = own
        self.hero_codes[lanes, 1-seats[lanes]] = opponent
        for role in (0, 1):
            selected = lanes[(seats[lanes] == self.seat_a) == (role == 0)]
            packet, diagnostics = self.packets[role]
            if not np.array_equal(packet[selected, 0], actions[selected]):
                raise RuntimeError("Shadow diagnostics do not match the submitted policy/action")
            menu = diagnostics["legal_count"][selected]
            entropy = diagnostics["entropy"][selected]
            choices = menu > 1
            data = self.telemetry.policies[role]
            data["decisions"] += len(selected)
            data["forced_decisions"] += int((menu == 1).sum())
            data["entropy_sum"] += float(entropy.sum(dtype=np.float64))
            data["choice_entropy_sum"] += float(entropy[choices].sum(dtype=np.float64))
            data["normalized_choice_entropy_sum"] += float((entropy[choices]/np.log(menu[choices])).sum())
            data["max_probability_sum"] += float(diagnostics["max_probability"][selected].sum(dtype=np.float64))
            data["legal_menu_counts"] = (np.asarray(data["legal_menu_counts"])+np.bincount(menu, minlength=65)).tolist()
            kinds = self.host.candidates[selected, actions[selected], :16].argmax(-1)
            data["action_kind_counts"] = (np.asarray(data["action_kind_counts"])+np.bincount(kinds, minlength=16)).tolist()
            values = packet[selected, 2].astype(np.float64)
            self.value_count[selected, role] += 1
            self.value_sum[selected, role] += values
            self.value_square_sum[selected, role] += values*values
        self.lengths[lanes] += 1
        if self.balance is not None:
            self.balance.observe(self.host.obs, self.host.candidates, self.host.mask,
                                 self.host.seats, actions, active)
        self.host.advance_active(actions, active)
        self.telemetry.host_advance_calls += 1
        for lane in np.flatnonzero(active & (self.host.done != 0)):
            if self.recorded[lane]:
                raise RuntimeError("Shadow attempted to record an episode twice")
            self.recorded[lane] = True
            terminal = self.host.done[lane] == 1
            if self.balance is not None:
                self.balance.finish(int(lane), self.host.rewards[lane], int(self.host.done[lane]))
            score = float((self.host.rewards[lane, self.seat_a]+1)/2) if terminal else None
            def hero(seat):
                code = self.hero_codes[lane, seat]
                return HEROES[code-1] if code else None
            self.telemetry.episodes.append({"seed": self.seed+int(lane), "policy_a_seat": self.seat_a,
                "policy_a_hero": hero(self.seat_a), "policy_b_hero": hero(1-self.seat_a),
                "score_a": score, "terminal": bool(terminal), "censored": not terminal,
                "wrapper_decisions": int(self.lengths[lane])})
            if terminal:
                for role, seat in ((0, self.seat_a), (1, 1-self.seat_a)):
                    target = float(self.host.rewards[lane, seat])
                    count = int(self.value_count[lane, role])
                    data = self.telemetry.policies[role]
                    data["resolved_value_rows"] += count
                    data["value_squared_error_sum"] += float(self.value_square_sum[lane, role]
                        - 2*target*self.value_sum[lane, role] + target*target*count)
        self.packets.clear()
        if self.step_delay_seconds:
            self.telemetry.sleep_calls += 1
            self.telemetry.sleep_requested_seconds += self.step_delay_seconds
            started = time.perf_counter()
            time.sleep(self.step_delay_seconds)
            self.telemetry.sleep_observed_seconds += time.perf_counter()-started

    def close(self):
        if not self.closed:
            self.host.close()
            self.closed = True
            if self.on_close is not None:
                self.on_close(self)


@contextmanager
def cpu_factories(telemetry, *, host_factory=None, step_delay_ms=0.):
    """Patches are local to this isolated evaluator process and always restored."""
    hosts = set()
    actor_count = host_count = 0
    def actor_factory(policy, batch, **kwargs):
        nonlocal actor_count
        actor = CPUActor(policy, batch, role=actor_count % 2, **kwargs)
        actor_count += 1
        return actor
    def make_host(batch, workers, seed, **kwargs):
        nonlocal host_count
        kwargs["pinned"] = False
        host = (host_factory or _HOST)(batch, 1, seed, **kwargs)
        observed = ObservedHost(host, telemetry, seed, host_count % 2, hosts.discard, step_delay_ms)
        hosts.add(observed)
        host_count += 1
        return observed
    try:
        with patch.object(learning_model, "LearningActor", actor_factory), \
             patch.object(learning_eval, "LearningHost", make_host):
            yield hosts
    finally:
        for host in tuple(hosts):
            host.close()


def run_cpu_match(policy_a, policy_b, *, games, seed, batch=8, sampling_seed=60927,
                  telemetry=True, max_seconds=150, debug_dir=None, host_factory=None, step_delay_ms=0.,
                  balance_telemetry=False):
    configure_cpu()
    if any(parameter.device.type != "cpu" or parameter.dtype != torch.float32
           for policy in (policy_a, policy_b) for parameter in policy.parameters()):
        raise ValueError("Shadow policies must be CPU FP32")
    if not 2 <= games <= 1000000 or games % 2 or not 1 <= batch <= 256:
        raise ValueError("Require even games2..1000000 and batch1..256")
    if not 0 < max_seconds <= 7200 or not np.isfinite(max_seconds):
        raise ValueError("Require a finite max_seconds in (0,7200]")
    if not 0 <= step_delay_ms <= 100 or not np.isfinite(step_delay_ms):
        raise ValueError("Require finite step_delay_ms in [0,100]")
    before = [checkpoints.policy_hash(policy) for policy in (policy_a, policy_b)]
    torch.manual_seed(sampling_seed)
    observations = ShadowTelemetry(balance=balance_telemetry)
    started = time.monotonic()
    wall_started, cpu_started = time.perf_counter(), time.process_time()
    children_before = resource.getrusage(resource.RUSAGE_CHILDREN)
    with cpu_factories(observations, host_factory=host_factory, step_delay_ms=step_delay_ms):
        result = learning_eval.evaluate_match(policy_a, policy_b, games=games, seed=seed,
            batch=batch, workers=1, telemetry=telemetry, censor_truncated=True,
            stop_check=lambda: time.monotonic()-started >= max_seconds, debug_dir=debug_dir)
    after = [checkpoints.policy_hash(policy) for policy in (policy_a, policy_b)]
    if before != after or torch.cuda.is_initialized():
        raise RuntimeError("Shadow evaluation mutated frozen weights or initialized CUDA")
    wall_seconds = time.perf_counter()-wall_started
    python_cpu = time.process_time()-cpu_started
    children_after = resource.getrusage(resource.RUSAGE_CHILDREN)
    host_cpu = (children_after.ru_utime+children_after.ru_stime
                - children_before.ru_utime-children_before.ru_stime)
    if not result["complete"]:
        result["reason"] = "cpu_shadow_cooperative_deadline"
    result.update(shadow_diagnostics=observations.result(), frozen_weights_unchanged=True,
                  policy_hashes=before, cuda_initialized=False,
                  cpu_load={"wall_seconds": wall_seconds, "python_cpu_seconds": python_cpu,
                    "host_children_cpu_seconds": host_cpu,
                    "cpu_seconds_per_wall_second": (python_cpu+host_cpu)/wall_seconds,
                    "host_advances_per_wall_second": observations.host_advance_calls/wall_seconds,
                    "scope": "CPU time of this Python process plus reaped host children during matches/verification; excludes model loading and all trainer CPU time"},
                  execution={"device": "cpu", "dtype": "float32", "matmul_precision": "ieee",
                    "graph": False, "pinned": False, "torch_threads": torch.get_num_threads(),
                    "torch_interop_threads": torch.get_num_interop_threads(), "host_workers": 1,
                    "step_delay_ms": step_delay_ms,
                    "production_equivalence": "same policy and evaluation rules; CPU and CUDA numerical kernels/RNG differ; see the pinned variant"})
    if observations.balance is not None:
        result["balance_observations"] = observations.balance.result()
    return result


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--a", type=Path, required=True)
    parser.add_argument("--b", type=Path, required=True)
    parser.add_argument("--a-role", choices=checkpoints.ROLES, default="learner")
    parser.add_argument("--b-role", choices=checkpoints.ROLES, default="champion")
    parser.add_argument("--variant", choices=("v2", "v3", "v4", "v5", "v6", "v7", "v8", "v9"), default="v2",
                        help="Strict matching checkpoint/host schema; never bypass identity validation")
    parser.add_argument("--games", type=int, default=32)
    parser.add_argument("--batch", type=int, default=8)
    parser.add_argument("--seed", type=lambda value: int(value, 0), default=0x6000000000000000)
    parser.add_argument("--sampling-seed", type=int, default=60927)
    parser.add_argument("--max-seconds", type=float, default=150)
    parser.add_argument("--step-delay-ms", type=float, default=0.,
                        help="Optional CPU-demand throttle after each completed host reply; does not change game time")
    parser.add_argument("--no-card-telemetry", action="store_true")
    parser.add_argument("--balance-telemetry", action="store_true",
                        help="Passive paired per-game public/own acquisition/outcome observations")
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    if not 2 <= args.games <= 1000000 or args.games % 2 or not 1 <= args.batch <= 256:
        parser.error("Require even games2..1000000 and batch1..256")
    if not 0 <= args.seed <= 2**64-args.games//2 or not 0 <= args.sampling_seed < 2**63:
        parser.error("Paired seeds must fit uint64 and sampling seed [0,2**63)")
    if not 0 < args.max_seconds <= 7200 or not np.isfinite(args.max_seconds):
        parser.error("Require finite --max-seconds in (0,7200]")
    if not 0 <= args.step_delay_ms <= 100 or not np.isfinite(args.step_delay_ms):
        parser.error("Require finite --step-delay-ms in [0,100]")
    output = args.output.expanduser().resolve()
    protected = {path.expanduser().resolve() for path in (args.a, args.b)}
    protected |= {path.parent/"identity.json" for path in protected}
    if output in protected or output.exists():
        parser.error("Output must be a new file, separate from checkpoints and identities")
    configure_cpu()
    configure_variant(args.variant)
    started = time.monotonic()
    report = {"schema": SCHEMA, "event": "external_frozen_evaluation", "state": "running",
        "phase": "loading", "complete": False, "pid": os.getpid(), "started_utc": checkpoints.utc(),
        "evaluation_only": True, "training_budget_seconds_charged": 0, "optimizer_updates": 0,
        "configuration": {"variant": args.variant, "games": args.games, "batch": args.batch, "workers": 1, "seed": args.seed,
            "sampling_seed": args.sampling_seed, "max_seconds": args.max_seconds,
            "step_delay_ms": args.step_delay_ms,
            "balance_telemetry": args.balance_telemetry,
            "telemetry": not args.no_card_telemetry},
        "helper_sha256": hashlib.sha256(Path(__file__).read_bytes()).hexdigest()}
    if args.balance_telemetry:
        report["balance_helper_sha256"] = hashlib.sha256((Path(__file__).parent/"balance_telemetry.py").read_bytes()).hexdigest()
    save_json(output, report)
    try:
        a, b = frozen_selections(args.a, args.a_role, args.b, args.b_role)
        policy_a, policy_b = (checkpoints.materialize_policy(selection) for selection in (a, b))
        report.update(policy_a=a.metadata, policy_b=b.metadata, catalog=a.catalog, phase="matches")
        for key, policy in (("policy_a", policy_a), ("policy_b", policy_b)):
            report[key]["policy_sha256"] = checkpoints.policy_hash(policy)
        save_json(output, report)
        result = run_cpu_match(policy_a, policy_b, games=args.games, seed=args.seed, batch=args.batch,
            sampling_seed=args.sampling_seed, max_seconds=args.max_seconds,
            step_delay_ms=args.step_delay_ms,
            balance_telemetry=args.balance_telemetry,
            telemetry=not args.no_card_telemetry, debug_dir=output.parent/(output.stem+"-censored"))
        report.update(result, state="completed" if result["complete"] else "incomplete", phase="finished",
                      finished_utc=checkpoints.utc(), total_seconds=time.monotonic()-started)
        save_json(output, report)
        print(json.dumps({key: report.get(key) for key in
            ("state", "games", "score_a", "score_bound_95", "censored_games", "seconds")}))
        return 0 if report["complete"] else 2
    except BaseException as error:
        report.update(state="failed", phase="finished", complete=False, finished_utc=checkpoints.utc(),
            total_seconds=time.monotonic()-started, error={"type": type(error).__name__, "message": str(error)})
        save_json(output, report)
        raise


if __name__ == "__main__":
    raise SystemExit(main())
