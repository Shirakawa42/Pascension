"""Fresh, search-free PPO from actual complete Shards games and seat outcomes."""
from __future__ import annotations

import argparse
import copy
from contextlib import contextmanager
from dataclasses import asdict, dataclass
import hashlib
import json
import math
from pathlib import Path
import random
import signal
import sys
import time

import numpy as np
import torch

from host import BINARY, HERE, PROJECT, Host, catalog
from game_stats import RollingGameStats
from model import Actor, Policy, PolicyConfig, cpu_state, dimensions
from opponent_archive import OpponentArchive

sys.path.append(str(HERE.parent / "TrainingPreflight"))
from campaign_state import CampaignBudget, load_checkpoint, restore_rng, save_checkpoint_atomic
from bench_common import save_json
from performance_upgrade import session_seconds


@dataclass
class TrainConfig:
    batch: int = 128
    workers: int = 8
    width: int = 512
    capacity: int = 131072
    minibatch: int = 1024
    epochs: int = 3
    learning_rate: float = 3e-4
    entropy: float = .01
    target_kl: float = .03
    archive_fraction: float = .25
    archive_every: int = 8
    archive_limit: int = 12
    archive_strategy: str = "recent"
    checkpoint_seconds: float = 60
    seed: int = 20261002
    engine_seed: int = 0x2000000000000000
    automation: bool = True
    graph: bool = True
    adaptive_actors: bool = True
    compiled_actor: bool = False
    packed_inputs: bool = True
    fused_optimizer: bool = True
    hero_mode: str = "policy"
    trace_mode: str = "decision"
    trace_decay: float = .95
    optional_exploration: float = 0.

    def validate(self):
        if isinstance(self.optional_exploration, bool) or not isinstance(self.optional_exploration, (int, float)) or not 0 <= self.optional_exploration <= .1:
            raise ValueError("Optional exploration must be finite and in [0,.1]")
        if self.trace_mode not in ("decision", "round"):
            raise ValueError("Trace mode must be decision or round")
        if isinstance(self.trace_decay, bool) or not isinstance(self.trace_decay, (int, float)) or not 0 <= self.trace_decay <= 1:
            raise ValueError("Trace decay must be finite and in [0,1]")
        if type(self.hero_mode) is not str or self.hero_mode not in ("policy", "balanced_random"):
            raise ValueError("Hero mode must be policy or balanced_random")
        if type(self.archive_strategy) is not str or self.archive_strategy not in ("recent", "historical", "prioritized"):
            raise ValueError("Opponent archive strategy must be recent or historical")
        for name in ("batch", "workers", "width", "capacity", "minibatch", "epochs", "archive_every", "archive_limit", "seed", "engine_seed"):
            if type(getattr(self, name)) is not int:
                raise ValueError(f"{name} must be an integer")
        for name in ("learning_rate", "entropy", "target_kl", "archive_fraction", "checkpoint_seconds"):
            value = getattr(self, name)
            if isinstance(value, bool) or not isinstance(value, (int, float)) or not math.isfinite(value):
                raise ValueError(f"{name} must be finite numeric data")
        if any(type(value) is not bool for value in (self.automation, self.graph, self.adaptive_actors,
                                                   self.compiled_actor, self.packed_inputs, self.fused_optimizer)):
            raise ValueError("Execution flags must be booleans")
        if not 1 <= self.batch <= 1024 or not 1 <= self.workers <= 16:
            raise ValueError("Invalid batch/workers")
        if not 64 <= self.width <= 1024 or not 64 <= self.capacity <= 1048576:
            raise ValueError("Invalid policy width/rollout capacity")
        if self.capacity < self.batch:
            raise ValueError("Rollout capacity must fit at least one complete action batch")
        if not 16 <= self.minibatch <= 8192 or not 1 <= self.epochs <= 8:
            raise ValueError("Invalid PPO minibatch/epochs")
        if not 0 <= self.archive_fraction <= .5 or self.archive_every < 1 or self.archive_limit < 1:
            raise ValueError("Invalid frozen-opponent archive")
        if self.archive_strategy in ("historical", "prioritized") and self.archive_limit < 5:
            raise ValueError("Historical opponent archive requires at least five slots")
        if not 0 < self.learning_rate < .1 or not 0 <= self.entropy <= 1 or not 0 < self.target_kl < 1:
            raise ValueError("Invalid optimizer parameters")
        if self.checkpoint_seconds < 1:
            raise ValueError("Invalid checkpoint interval")
        if not 0 <= self.seed < (1 << 32):
            raise ValueError("Sampling seed must be in NumPy's unsigned 32-bit range")
        if not 0 <= self.engine_seed or self.engine_seed + self.batch >= (1 << 63):
            raise ValueError("Training seeds must leave the high-bit evaluation namespace unused")


def source_fingerprint():
    files = []
    for folder in (HERE, PROJECT / "Assets/Scripts/Core", PROJECT / "Assets/Scripts/Shards/Engine",
                   PROJECT / "Assets/Scripts/Shards/Content", PROJECT / "Assets/Scripts/Shards/AI"):
        files += [p for p in folder.rglob("*") if p.suffix in (".py", ".cs", ".csproj")
                  and not {"bin", "obj", "__pycache__"}.intersection(p.parts)]
    # Reused persistence is part of this campaign's reproducibility boundary.
    files += [HERE.parent / "TrainingPreflight" / name for name in ("campaign_state.py", "supervise_training.py", "bench_common.py")]
    files.append(HERE.parent / "BalancePatchHost/EffectDescriptors.cs")
    digest = hashlib.sha256()
    for path in sorted(set(files)):
        digest.update(str(path.relative_to(PROJECT)).encode())
        digest.update(path.read_bytes())
    return digest.hexdigest()


def identity(config, host_catalog):
    return {"schema": "shards-zero-depth-training-v1", "configuration": asdict(config),
            "catalog_sha256": hashlib.sha256(json.dumps(host_catalog, sort_keys=True).encode()).hexdigest(),
            "source_fingerprint": source_fingerprint(), "host_sha256": hashlib.sha256(BINARY.read_bytes()).hexdigest(),
            "lookahead_depth": 0, "outcomes": "actual terminal zero-sum deciding-seat utilities"}


@contextmanager
def checkpoint_cpu_threads(workers):
    """Parallelize finite CPU snapshots while actor/PPO execution is paused."""
    previous = torch.get_num_threads()
    try:
        torch.set_num_threads(min(8, workers))
        yield
    finally:
        torch.set_num_threads(previous)


def save_trace(directory, *, config, host_catalog, seed, generation, actions, learner_seats, archived, reason, censored_lanes=()):
    """An exact rules replay needs action indices, seed, schema and automation."""
    destination = Path(directory) / "diagnostics" / f"generation-{generation:08d}-seed-{seed}"
    destination.parent.mkdir(parents=True, exist_ok=True)
    packets = np.stack(actions) if actions else np.empty((0, len(learner_seats)), dtype=np.int32)
    np.savez_compressed(destination.with_suffix(".npz"), actions=packets.astype(np.int32),
                        engine_seed=np.array([seed], dtype=np.uint64), learner_seats=learner_seats,
                        archive_lanes=archived, censored_lanes=np.asarray(censored_lanes, np.int32))
    save_json(destination.with_suffix(".json"), {"schema": "shards-zero-depth-replay-v1", "reason": reason,
              "generation": generation, "seed": seed, "steps": len(actions),
              "batch": config.batch, "workers": config.workers,
              "automation": "singleton" if config.automation else "none",
              "hero_mode": config.hero_mode,
              "source_fingerprint": source_fingerprint(),
              "host_binary_sha256": hashlib.sha256(BINARY.read_bytes()).hexdigest(),
              "catalog_sha256": hashlib.sha256(json.dumps(host_catalog, sort_keys=True, separators=(",", ":")).encode()).hexdigest()})


def lambda_returns(lanes, seats, values, rewards, trace_decay, *, rounds=None):
    """Undiscounted TD(lambda), with one trajectory per game AND deciding seat.

    Opponent actions may be absent from archived games. Advancing to the same
    player's next retained decision gives a consistent perspective in both
    archive play and self-play, including same-seat consecutive decisions.
    Terminal utility is unchanged; administrative censors are removed upstream.
    """
    if not 0 <= trace_decay <= 1:
        raise ValueError("Trace decay must be in [0,1]")
    terminal = rewards[lanes, seats].astype(np.float32)
    if not len(values) or trace_decay == 1:
        return terminal.copy()
    keys = lanes * 2 + seats
    order = np.argsort(keys, kind="stable")
    sorted_keys = keys[order]
    starts = np.r_[True, sorted_keys[1:] != sorted_keys[:-1]]
    groups = np.cumsum(starts) - 1
    positions = np.arange(len(order)) - np.maximum.accumulate(np.where(starts, np.arange(len(order)), 0))
    width = int(positions.max()) + 1
    outcomes = terminal[order[starts]]
    # Padding equals the absorbing terminal value, so short and long games
    # share the vectorized backward pass without credit crossing trajectories.
    grid = np.broadcast_to(outcomes[:, None], (len(outcomes), width + 1)).copy()
    grid[groups, positions] = values[order]
    round_grid = None
    if rounds is not None:
        rounds = np.asarray(rounds)
        if rounds.shape != np.asarray(values).shape or not np.isfinite(rounds).all():
            raise ValueError("Rounds must match every retained decision")
        ordered_rounds = rounds[order]
        if np.any((np.diff(ordered_rounds) < 0) & ~starts[1:]):
            raise ValueError("Rounds cannot decrease within a player's trajectory")
        ends = np.r_[np.flatnonzero(starts)[1:] - 1, len(order) - 1]
        round_grid = np.broadcast_to(ordered_rounds[ends, None], (len(outcomes), width + 1)).copy()
        round_grid[groups, positions] = ordered_rounds
    targets = np.empty((len(outcomes), width), np.float32)
    future = outcomes.copy()
    for column in range(width - 1, -1, -1):
        decay = (trace_decay if round_grid is None else
                 np.power(trace_decay, round_grid[:, column + 1] - round_grid[:, column]))
        future = (1 - decay) * grid[:, column + 1] + decay * future
        targets[:, column] = future
    result = np.empty(len(values), np.float32)
    result[order] = targets[groups, positions]
    return result


class Rollout:
    """Lossless owned float32 experience; only complete games get credit."""
    @staticmethod
    def estimate_bytes(capacity, host_catalog):
        obs_dim, actions, action_dim = dimensions(host_catalog)
        return capacity * (4 * (obs_dim + actions * action_dim + 3) + actions + 12)

    def __init__(self, capacity, host_catalog, device="cpu"):
        obs_dim, actions, action_dim = dimensions(host_catalog)
        self.capacity = capacity
        self.device = torch.device(device)
        self.gpu = self.device.type == "cuda"
        if self.gpu:
            self.obs = torch.empty(capacity, obs_dim, device=self.device)
            self.candidates = torch.empty(capacity, actions, action_dim, device=self.device)
            self.mask = torch.empty(capacity, actions, device=self.device, dtype=torch.bool)
            self.packet = torch.empty(capacity, 3, device=self.device)
        else:
            self.obs = np.empty((capacity, obs_dim), np.float32)
            self.candidates = np.empty((capacity, actions, action_dim), np.float32)
            self.mask = np.empty((capacity, actions), bool)
            self.packet = np.empty((capacity, 3), np.float32)
        self.lanes = np.empty(capacity, np.int32)
        self.seats = np.empty(capacity, np.int32)
        self.rounds = np.empty(capacity, np.int32)
        self.rows = 0

    @property
    def allocated_bytes(self):
        return sum(value.nbytes if isinstance(value, np.ndarray) else value.numel() * value.element_size()
                   for value in vars(self).values() if isinstance(value, (np.ndarray, torch.Tensor)))

    def reset(self):
        self.rows = 0

    @torch.no_grad()
    def append(self, host, packet, lanes, actor=None):
        lanes = np.asarray(lanes, np.int64)
        size = len(lanes)
        if not size:
            return
        if self.rows + size > self.capacity:
            raise RuntimeError("Owned rollout capacity exhausted; increase capacity or reduce batch. No partial-game update was made.")
        target = slice(self.rows, self.rows + size)
        if self.gpu:
            if actor is None:
                raise RuntimeError("GPU rollout requires the frozen actor's raw device inputs")
            source_rows = actor.source_rows(lanes) if hasattr(actor, "source_rows") else lanes
            if np.array_equal(source_rows, np.arange(size)):
                # The adaptive learner actor packs exactly these retained rows.
                # Copy its prefix straight into owned storage before another
                # inference; there is no index upload or full-size temporary.
                from gpu_owned_copy import try_copy_prefix
                if not try_copy_prefix(self, actor, size):
                    for name in ("obs", "candidates", "mask", "packet"):
                        getattr(self, name)[target].copy_(getattr(actor, name)[:size])
            else:
                source = torch.as_tensor(source_rows, device=self.device)
                for name in ("obs", "candidates", "packet"):
                    torch.index_select(getattr(actor, name), 0, source,
                                       out=getattr(self, name)[target])
                # copy_ converts float masks directly into the owned bool slice.
                self.mask[target].copy_(actor.mask.index_select(0, source))
        else:
            self.obs[target] = host.obs[lanes]
            self.candidates[target] = host.candidates[lanes]
            self.mask[target] = host.mask[lanes].astype(bool)
            self.packet[target] = packet[lanes]
        self.lanes[target] = lanes
        self.seats[target] = host.actors[lanes]
        self.rounds[target] = (np.rint(host.obs[lanes, 2] * 100).astype(np.int32)
                               if host.obs.shape[1] >= 3 else 0)
        chosen = packet[lanes, 0].astype(np.int64)
        if not np.all(host.mask[lanes, chosen] == 1):
            raise RuntimeError("Stored behavior action is illegal")
        self.rows += size

    def seal(self, done, rewards, *, gae_lambda=1., trace_mode="decision"):
        if trace_mode not in ("decision", "round"):
            raise ValueError("Trace mode must be decision or round")
        if trace_mode == "round" and self.obs.shape[1] < 3:
            raise ValueError("Round traces require a round field in the observation schema")
        done, rewards = np.asarray(done), np.asarray(rewards)
        if np.any(done == 0):
            raise RuntimeError("Cannot train unresolved games")
        if rewards.shape != (len(done), 2) or not np.isin(rewards, [-1, 0, 1]).all() or np.any(rewards.sum(1)):
            raise RuntimeError("Invalid terminal rewards")
        lanes = self.lanes[:self.rows]
        indices = np.flatnonzero(done[lanes] == 1)
        returns = rewards[lanes[indices], self.seats[indices]].astype(np.float32).copy()
        old_values = (self.packet[torch.as_tensor(indices, device=self.device), 2].cpu().numpy()
                      if self.gpu else self.packet[indices, 2])
        returns = lambda_returns(lanes[indices], self.seats[indices], old_values, rewards, gae_lambda,
                                 rounds=self.rounds[indices] if trace_mode == "round" else None)
        advantages = returns - old_values
        if len(indices):
            advantages = (advantages - advantages.mean()) / max(float(advantages.std()), 1.e-8)
        return indices, returns, advantages, self.rows - len(indices)


class Learner:
    def __init__(self, policy, config):
        self.policy, self.config = policy, config
        self.device = next(policy.parameters()).device
        self.optimizer = torch.optim.Adam(policy.parameters(), lr=config.learning_rate,
                                           fused=True if config.fused_optimizer and self.device.type == "cuda" else None)
        # The normal CUDA rollout already owns device inputs. Allocate the large
        # pinned CPU fallback only when a CPU-backed rollout is actually used.
        self.staging = None
        self.staging_ready = None
        self.graph_step = None
        self.graph_learning = self.device.type == "cuda" and config.graph and config.fused_optimizer

    def restore_optimizer(self, state):
        # Adam restores its saved LR as well as its moments. A metadata-only
        # configuration change must never silently leave the old LR running.
        groups = state.get("param_groups") if isinstance(state, dict) else None
        if (not isinstance(groups, list) or not groups or
                any(not isinstance(group, dict) or type(group.get("lr")) not in (int, float)
                    or not math.isfinite(group["lr"]) or group["lr"] != self.config.learning_rate
                    for group in groups)):
            raise RuntimeError("Checkpoint Adam learning rate differs from pinned training configuration")
        self.optimizer.load_state_dict(state)

    def batch(self, store, rows, returns=None, advantages=None):
        size = len(rows)
        if store.gpu:
            selected = torch.as_tensor(rows, device=self.device)
            output = [value.index_select(0, selected) for value in (store.obs, store.candidates, store.mask, store.packet)]
            if returns is not None:
                output += [torch.as_tensor(value, device=self.device) for value in (returns, advantages)]
            return output
        if self.staging is None:
            obs, actions, action = dimensions(self.policy.catalog)
            self.staging = [torch.empty(self.config.minibatch, *shape, pin_memory=self.device.type == "cuda", dtype=dtype)
                            for shape, dtype in (((obs,), torch.float32), ((actions, action), torch.float32),
                                                ((actions,), torch.bool), ((3,), torch.float32), ((), torch.float32), ((), torch.float32))]
        if self.staging_ready is not None:
            # Pinned sources must remain unchanged until every prior H2D copy
            # completes, even when verification no longer reads each batch back.
            self.staging_ready.synchronize()
        sources = [store.obs[rows], store.candidates[rows], store.mask[rows], store.packet[rows]]
        if returns is not None:
            sources += [returns, advantages]
        output = []
        for staging, source in zip(self.staging, sources):
            staging[:size].numpy()[:] = source
            output.append(staging[:size].to(self.device, non_blocking=self.device.type == "cuda"))
        if self.device.type == "cuda":
            if self.staging_ready is None:
                self.staging_ready = torch.cuda.Event()
            self.staging_ready.record(torch.cuda.current_stream(self.device))
        return output

    @torch.no_grad()
    def verify_behavior(self, store, indices, stop_check=None, heartbeat=None):
        behavior_graph = getattr(self, "behavior_graph", None)
        if (self.graph_learning and store.gpu and self.device.type == "cuda" and
                (behavior_graph is None or behavior_graph.store is store)):
            from gpu_behavior import verify_behavior
            return verify_behavior(self, store, indices, stop_check=stop_check, heartbeat=heartbeat)
        maximum = torch.zeros(2, device=self.device)
        if store.gpu:
            indices = torch.as_tensor(indices, device=self.device)
        for start in range(0, len(indices), self.config.minibatch):
            if stop_check is not None and stop_check():
                return {"behavior_verification_complete": False, "deadline_stop": True}
            obs, candidates, mask, packet = self.batch(store, indices[start:start + self.config.minibatch])
            logits, values = self.policy(obs, candidates, mask)
            logp = logits.log_softmax(-1).gather(1, packet[:, :1].long()).squeeze(1)
            errors = torch.stack(((logp - packet[:, 1]).abs().max(), (values - packet[:, 2]).abs().max()))
            # maximum propagates NaN, whereas Python max(0., nan) can hide it.
            # One final read also avoids synchronizing CUDA for every batch.
            maximum = torch.maximum(maximum, errors)
            if heartbeat is not None:
                heartbeat()
        maximum_logp, maximum_value = maximum.cpu().tolist()
        if not np.isfinite([maximum_logp, maximum_value]).all() or maximum_logp > .002 or maximum_value > .001:
            raise RuntimeError(f"Actor/learner behavior mismatch {maximum_logp=} {maximum_value=}")
        return {"behavior_logp_error": maximum_logp, "behavior_value_error": maximum_value,
                "behavior_verification_complete": True}

    @torch.no_grad()
    def verify_finite_state(self):
        values = list(self.policy.parameters())
        values += [value for state in self.optimizer.state.values() for value in state.values() if torch.is_tensor(value)]
        groups = {}
        for value in values:
            groups.setdefault((value.device, value.dtype), []).append(value)
        for tensors in groups.values():
            norms = torch.stack(torch._foreach_norm(tensors))
            if not bool(torch.isfinite(norms).all()):
                raise RuntimeError("Nonfinite learned parameter or Adam state; stop before collecting another game")

    def update(self, store, indices, returns, advantages, stop_check=None, heartbeat=None):
        if not len(indices):
            raise RuntimeError("No real completed-game decisions to learn")
        from gpu_ppo_step import DIAGNOSTIC_FIELDS, ppo_diagnostics
        passes, steps = 0, 0
        sums = dict.fromkeys(DIAGNOSTIC_FIELDS, 0.)
        rejected_kl = None
        rejected_rows = 0
        def metrics():
            means = {key: value / passes if passes else None for key, value in sums.items()}
            target_variance = max(0., means["target_second_moment"] - means["target_mean"] ** 2) if passes else 0.
            residual_variance = max(0., means["value_mse"] - means["residual_mean"] ** 2) if passes else 0.
            return {key: means[key] for key in DIAGNOSTIC_FIELDS[:8]} | {
                "diagnostic_scope": "row_weighted_accepted_minibatches_before_optimizer_step",
                "accepted_minibatches": steps, "diagnostic_rows": passes,
                "value_explained_variance": 1 - residual_variance / target_variance if target_variance > 1.e-12 else None,
                "rejected_kl": rejected_kl, "rejected_minibatch_rows": rejected_rows,
                "effective_epochs": passes / len(indices),
                "update_coverage": passes / (self.config.epochs * len(indices)),
            }
        def accept(summary, size):
            nonlocal passes, steps
            for key, value in zip(DIAGNOSTIC_FIELDS, summary):
                sums[key] += float(value) * size
            passes += size
            steps += 1
        if store.gpu:
            # These are small generation-wide vectors. Upload them once instead
            # of sending three separate CPU slices for every minibatch.
            all_indices = torch.as_tensor(indices, device=self.device)
            all_returns = torch.as_tensor(returns, device=self.device)
            all_advantages = torch.as_tensor(advantages, device=self.device)
        else:
            all_indices, all_returns, all_advantages = indices, returns, advantages
        result = self.verify_behavior(store, all_indices, stop_check=stop_check, heartbeat=heartbeat)
        if not result["behavior_verification_complete"]:
            return {**result, **metrics(), "optimizer_steps": 0, "example_passes": 0}
        for _ in range(self.config.epochs):
            order = np.random.permutation(len(indices))
            if store.gpu:
                selected = torch.as_tensor(order, device=self.device)
                ordered_indices = all_indices.index_select(0, selected)
                ordered_returns = all_returns.index_select(0, selected)
                ordered_advantages = all_advantages.index_select(0, selected)
            for start in range(0, len(order), self.config.minibatch):
                if stop_check is not None and stop_check():
                    return {**result, **metrics(), "optimizer_steps": steps, "example_passes": passes, "deadline_stop": True}
                if store.gpu:
                    end = start + self.config.minibatch
                    batch_rows, batch_returns, batch_advantages = (value[start:end] for value in
                        (ordered_indices, ordered_returns, ordered_advantages))
                else:
                    selected = order[start:start + self.config.minibatch]
                    batch_rows, batch_returns, batch_advantages = indices[selected], returns[selected], advantages[selected]
                obs, candidates, mask, packet, targets, advantage = self.batch(
                    store, batch_rows, batch_returns, batch_advantages)
                if self.graph_learning and len(batch_rows) == self.config.minibatch:
                    from gpu_ppo_step import GpuPpoStep, GraphSetupStopped
                    if self.graph_step is None:
                        try:
                            self.graph_step = GpuPpoStep(self.policy, self.config,
                                (obs, candidates, mask, packet, targets, advantage), self.optimizer,
                                stop_check=stop_check, heartbeat=heartbeat)
                        except GraphSetupStopped:
                            return {**result, **metrics(), "optimizer_steps": steps,
                                    "example_passes": passes, "deadline_stop": True}
                    summary = self.graph_step.run(
                        (obs, candidates, mask, packet, targets, advantage))
                    kl_value, loss_value, entropy_value, norm_value = summary[:4]
                    if not math.isfinite(kl_value):
                        raise RuntimeError("Nonfinite PPO likelihood")
                    if kl_value > self.config.target_kl:
                        rejected_kl, rejected_rows = kl_value, len(batch_rows)
                        return {**result, **metrics(), "optimizer_steps": steps,
                                "example_passes": passes, "kl_early_stop": True}
                    if not math.isfinite(loss_value):
                        raise RuntimeError("Nonfinite PPO loss")
                    if not math.isfinite(norm_value):
                        raise RuntimeError("Nonfinite PPO gradient norm")
                    if stop_check is not None and stop_check():
                        return {**result, **metrics(), "optimizer_steps": steps,
                                "example_passes": passes, "deadline_stop": True}
                    if len(summary) != len(DIAGNOSTIC_FIELDS):
                        raise RuntimeError("PPO diagnostic readback schema differs")
                    self.optimizer.step()
                    accept(summary, len(batch_rows))
                    if heartbeat is not None:
                        heartbeat()
                    continue
                logits, values = self.policy(obs, candidates, mask)
                all_logp = logits.log_softmax(-1)
                logp = all_logp.gather(1, packet[:, :1].long()).squeeze(1)
                log_ratio = logp - packet[:, 1]
                ratio = log_ratio.exp()
                kl = ((ratio - 1) - log_ratio).mean()
                kl_value = float(kl.detach())
                if not math.isfinite(kl_value):
                    raise RuntimeError("Nonfinite PPO likelihood")
                if kl_value > self.config.target_kl:
                    rejected_kl, rejected_rows = kl_value, len(batch_rows)
                    return {**result, **metrics(), "optimizer_steps": steps, "example_passes": passes, "kl_early_stop": True}
                actor_loss = -torch.minimum(ratio * advantage, ratio.clamp(.8, 1.2) * advantage).mean()
                value_loss = .5 * (values - targets).square().mean()
                entropy_per_row = -(all_logp.exp() * all_logp).sum(-1)
                entropy = entropy_per_row.mean()
                loss = actor_loss + .5 * value_loss - self.config.entropy * entropy
                loss_value = float(loss.detach())
                if not math.isfinite(loss_value):
                    raise RuntimeError("Nonfinite PPO loss")
                self.optimizer.zero_grad(set_to_none=True)
                loss.backward()
                norm = torch.nn.utils.clip_grad_norm_(self.policy.parameters(), .5, error_if_nonfinite=True)
                summary = ppo_diagnostics(kl, loss, entropy, norm, entropy_per_row, ratio, values,
                                          targets, mask, 2 * value_loss.detach()).cpu().tolist()
                self.optimizer.step()
                accept(summary, len(batch_rows))
                if heartbeat is not None:
                    heartbeat()
        return {**result, **metrics(), "optimizer_steps": steps, "example_passes": passes, "kl_early_stop": False}


def train(config, run_dir, seconds, *, max_games=None, resume=None, device="cuda"):
    config.validate()
    run_dir = Path(run_dir)
    run_dir.mkdir(parents=True, exist_ok=True)
    if not resume and (run_dir / "latest.soicp").exists():
        raise RuntimeError("Existing checkpoint requires --resume")
    torch.set_num_threads(1)
    torch.backends.fp32_precision = "ieee"
    torch.backends.cuda.matmul.fp32_precision = "ieee"
    random.seed(config.seed)
    np.random.seed(config.seed)
    torch.manual_seed(config.seed)
    host_catalog = catalog()
    pinned = identity(config, host_catalog)
    identity_file = run_dir / "identity.json"
    if identity_file.exists() and json.loads(identity_file.read_text()) != pinned:
        raise RuntimeError("Existing campaign source/rules/configuration differs")
    save_json(identity_file, pinned)
    save_json(run_dir / "catalog.json", host_catalog)
    policy = Policy(host_catalog, PolicyConfig(width=config.width)).to(device)
    model_parameters = sum(parameter.numel() for parameter in policy.parameters())
    learner = Learner(policy, config)
    generations = games = decisions = attempts = censored = example_passes = optimizer_steps = 0
    next_seed = config.engine_seed
    archive = None
    game_stats = RollingGameStats(host_catalog, window=100_000)
    # Check memory before allocation, including CUDA's already resident models.
    if device.startswith("cuda"):
        available = torch.cuda.mem_get_info(torch.device(device))[0]
    else:
        available = int(next(line.split()[1] for line in Path("/proc/meminfo").read_text().splitlines()
                             if line.startswith("MemAvailable:"))) * 1024
    if Rollout.estimate_bytes(config.capacity, host_catalog) > available * .7:
        raise RuntimeError("Rollout exceeds 70% of available device memory; reduce capacity or batch")
    store = Rollout(config.capacity, host_catalog, device=device)
    stop = False
    def request_stop(*_):
        nonlocal stop
        stop = True
    signal.signal(signal.SIGTERM, request_stop)
    signal.signal(signal.SIGINT, request_stop)
    with CampaignBudget(run_dir / "budget.json", limit_seconds=seconds) as budget:
        if resume:
            payload = load_checkpoint(resume, expected_identity=pinned, budget=budget)
            state = payload["state"]
            policy.load_state_dict(state["policy"], strict=True)
            learner.restore_optimizer(state["optimizer"])
            archive = state["archive"]
            generations, games, decisions, attempts, censored, example_passes = [state[key] for key in (
                "generations", "games", "decisions", "attempts", "censored", "example_passes")]
            optimizer_steps = state.get("optimizer_steps", 0)
            next_seed = state["next_engine_seed"]
            if "rolling_game_stats" in state:
                game_stats = RollingGameStats.from_state(host_catalog, state["rolling_game_stats"])
        if type(next_seed) is not int or next_seed < 0 or next_seed + config.batch >= (1 << 63):
            raise RuntimeError("Next training cohort would enter the reserved held-out seed namespace")
        archive_metadata = {"metadata": state["opponent_archive"]} if resume and "opponent_archive" in state else {}
        from model import legacy_exploration_state
        if resume:
            if abs(float(policy.optional_exploration) - config.optional_exploration) > 1e-8:
                raise RuntimeError("Checkpoint exploration differs from pinned configuration")
        else:
            policy.optional_exploration.fill_(config.optional_exploration)
        if archive is not None:
            archive = [legacy_exploration_state(weights) for weights in archive]
        archive = OpponentArchive(config.archive_strategy, limit=config.archive_limit,
                                  every=config.archive_every, generation=generations,
                                  current=cpu_state(policy), archive=archive,
                                  **archive_metadata)
        del archive_metadata
        if config.adaptive_actors:
            if device.startswith("cuda") and config.packed_inputs:
                from pipeline_actor import PipelineActor, act_pair
                if config.graph:
                    from combined_actor import CombinedGraphActor
                    actor_class = CombinedGraphActor
                else:
                    actor_class = PipelineActor
            else:
                from adaptive import AdaptiveActor
                actor_class = AdaptiveActor
        else:
            actor_class = Actor
        actor = actor_class(policy, config.batch, graph=config.graph and device.startswith("cuda"),
                            compiled=config.compiled_actor, packed=config.packed_inputs)
        opponent_policy = copy.deepcopy(policy)
        opponent = actor_class(opponent_policy, config.batch, graph=config.graph and device.startswith("cuda"),
                               compiled=config.compiled_actor, packed=config.packed_inputs)
        if resume:
            restore_rng(payload, include_cuda=device.startswith("cuda"))
            # The pool owns its frozen CPU snapshots. Release the deserialized
            # copies after every learned/RNG component has been restored.
            del payload, state
        with open(run_dir / "metrics.jsonl", "a", buffering=1) as metrics:
            metric_boot_id = budget.clock.boot_id()
            metric_raw_clock = getattr(time, "CLOCK_MONOTONIC_RAW", None)
            if metric_raw_clock is not None:
                try:
                    time.clock_gettime(metric_raw_clock)
                except (AttributeError, OSError, ValueError):
                    metric_raw_clock = None
            throughput_clock = (time.monotonic if metric_raw_clock is None else
                                lambda: time.clock_gettime(metric_raw_clock))
            seconds_clock = "monotonic" if metric_raw_clock is None else "monotonic_raw"
            def log(event):
                clocks = {"wall": time.time(), "monotonic": time.monotonic(), "boot_id": metric_boot_id}
                if metric_raw_clock is not None:
                    clocks["monotonic_raw"] = throughput_clock()
                metrics.write(json.dumps({"utc": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime()),
                                          **clocks, **event}, allow_nan=False) + "\n")
                print(json.dumps({key: value for key, value in event.items() if key != "rolling_game_stats"},
                                 allow_nan=False), flush=True)
            grant, absolute_deadline = session_seconds(run_dir, budget, seconds, with_deadline=True)
            with budget.start_session("zero-depth", requested_seconds=grant,
                                      stop_buffer_seconds=min(20., grant / 5, budget.remaining_seconds / 5),
                                      absolute_deadline_monotonic=absolute_deadline) as session:
                save_json(run_dir / "deadline.json", {"pid": __import__("os").getpid(),
                    "hard_deadline_monotonic": session.hard_deadline_monotonic,
                    "hard_deadline_wall": session.hard_deadline_wall})
                def checkpoint(reason):
                    with checkpoint_cpu_threads(config.workers):
                        state = {"catalog": host_catalog, "policy_config": asdict(policy.config), "policy": cpu_state(policy),
                                 "optimizer": learner.optimizer.state_dict(), "archive": archive.weights,
                                 "opponent_archive": archive.state_dict(generations), "configuration": asdict(config),
                                 "generations": generations, "games": games, "decisions": decisions, "attempts": attempts,
                                 "censored": censored, "example_passes": example_passes, "optimizer_steps": optimizer_steps,
                                 "rolling_game_stats": game_stats.state_dict(),
                                 "next_engine_seed": next_seed, "reason": reason}
                        save_checkpoint_atomic(run_dir / "latest.soicp", state, identity=pinned, budget=budget,
                                               include_cuda_rng=device.startswith("cuda"))
                    save_json(run_dir / "status.json", {"state": reason, "games": games, "attempts": attempts,
                        "censored": censored, "generations": generations, "decisions": decisions,
                        "optimizer_steps": optimizer_steps,
                        "model_parameters": model_parameters, "model_width": config.width,
                        "rolling_game_stats": game_stats.snapshot(),
                        "archive_pool": archive.snapshot(generations),
                        "lookahead_depth": 0, "budget": budget.snapshot()})
                    session.heartbeat({"games": games, "generations": generations, "unresolved_episodes": 0})
                checkpoint("initial")
                last_checkpoint = time.monotonic()
                with Host(config.batch, config.workers, next_seed, automation=config.automation,
                          hero_mode=config.hero_mode) as host:
                    log({"event": "start", "configuration": asdict(config), "rollout_bytes": store.allocated_bytes,
                         "archive_pool": archive.snapshot(generations),
                         "games": games, "generations": generations, "model_parameters": model_parameters})
                    while not stop and not session.should_stop and (max_games is None or games < max_games):
                        started = throughput_clock()
                        store.reset()
                        collection_seed = next_seed
                        action_trace = []
                        session.heartbeat({"games": games, "generations": generations,
                                           "unresolved_episodes": config.batch, "unresolved_decisions": 0})
                        actor.refresh(policy)
                        selected_weights, selected_opponent = archive.choose(generations)
                        opponent_policy.load_state_dict(selected_weights, strict=True)
                        del selected_weights
                        opponent.refresh(opponent_policy)
                        archived = np.random.random(config.batch) < config.archive_fraction
                        learner_seats = (np.arange(config.batch) + generations) % 2
                        next_heartbeat = time.monotonic() + 10
                        while not np.all(host.done):
                            if stop or session.should_stop:
                                break
                            active = host.done == 0
                            other = active & archived & (host.actors != learner_seats)
                            retained = np.flatnonzero(active & ~other)
                            try:
                                if config.adaptive_actors and device.startswith("cuda") and config.packed_inputs:
                                    (actions, packet), (other_actions, _) = act_pair(actor, opponent, host, active, other)
                                    actions[other] = other_actions[other]
                                    store.append(host, packet, retained, actor=actor)
                                else:
                                    actions, packet = (actor.act(host, active=active & ~other)
                                                       if config.adaptive_actors else actor.act(host))
                                    store.append(host, packet, retained, actor=actor)
                                    if other.any():
                                        other_actions, _ = opponent.act(host, active=other) if config.adaptive_actors else opponent.act(host)
                                        actions[other] = other_actions[other]
                                actions[~active] = -1
                                action_trace.append(actions.copy())
                                host.advance(actions)
                            except Exception as error:
                                # Every row of this unfinalized cohort is lost,
                                # including games already held at terminal. The
                                # session's exception close records these exact
                                # current counts instead of an older heartbeat.
                                session.heartbeat({"games": games, "generations": generations,
                                    "unresolved_episodes": config.batch, "unresolved_decisions": store.rows})
                                save_trace(run_dir, config=config, host_catalog=host_catalog,
                                           seed=collection_seed, generation=generations, actions=action_trace,
                                           learner_seats=learner_seats, archived=archived, reason=str(error))
                                raise
                            if time.monotonic() >= next_heartbeat:
                                session.heartbeat({"games": games, "generations": generations,
                                                   "unresolved_episodes": config.batch, "unresolved_decisions": store.rows})
                                next_heartbeat = time.monotonic() + 10
                        next_seed += config.batch
                        if not np.all(host.done):
                            budget.note_discarded(episodes=config.batch, decisions=store.rows, reason="deadline_or_signal_partial_generation")
                            session.heartbeat({"games": games, "generations": generations,
                                               "unresolved_episodes": 0, "unresolved_decisions": 0})
                            log({"event": "discarded_partial_generation", "rows": store.rows, "attempts": config.batch})
                            break
                        indices, returns, advantages, excluded = store.seal(host.done, host.rewards,
                            gae_lambda=config.trace_decay, trace_mode=config.trace_mode)
                        completed, capped = int((host.done == 1).sum()), int((host.done == 2).sum())
                        if capped:
                            save_trace(run_dir, config=config, host_catalog=host_catalog,
                                       seed=collection_seed, generation=generations, actions=action_trace,
                                       learner_seats=learner_seats, archived=archived, reason="administrative_censor",
                                       censored_lanes=np.flatnonzero(host.done == 2))
                        budget.record_collection(attempted_games=config.batch, completed_games=completed,
                            censored_games=capped, learning_rows=len(indices), censored_rows=excluded)
                        session.heartbeat({"games": games, "generations": generations,
                                           "unresolved_episodes": 0, "unresolved_decisions": 0})
                        # Any cap is observable and never fabricated as a draw.
                        if budget.collection_summary()["recent_censored"] >= 4:
                            raise RuntimeError("Four censored games in the recent 4096 attempts; inspect traces before further learning")
                        def update_heartbeat():
                            nonlocal next_heartbeat
                            if time.monotonic() >= next_heartbeat:
                                session.heartbeat({"games": games, "generations": generations, "unresolved_episodes": 0})
                                next_heartbeat = time.monotonic() + 10
                        result = learner.update(store, indices, returns, advantages,
                                                stop_check=lambda: stop or session.should_stop, heartbeat=update_heartbeat)
                        learner.verify_finite_state()
                        archive.observe_cohort(generations, selected_opponent["id"], host.done, host.rewards, archived, learner_seats)
                        game_stats.add_cohort(host, archived, learner_seats)
                        generations += 1
                        games += completed
                        attempts += config.batch
                        censored += capped
                        decisions += len(indices)
                        example_passes += result["example_passes"]
                        optimizer_steps += result["optimizer_steps"]
                        if generations % config.archive_every == 0:
                            archive.add(generations, cpu_state(policy))
                        elapsed = throughput_clock() - started
                        log({"event": "generation", "generation": generations, "games": games, "completed": completed,
                             "censored": capped, "learning_rows": len(indices), "excluded_rows": excluded,
                             "total_optimizer_steps": optimizer_steps, "total_decisions": decisions,
                             "seconds": elapsed, "seconds_clock": seconds_clock,
                             "games_per_second": completed / elapsed,
                             "archive_pool": archive.snapshot(generations), "selected_opponent": selected_opponent,
                             "rolling_game_stats": game_stats.snapshot(), **result})
                        if time.monotonic() - last_checkpoint >= config.checkpoint_seconds:
                            checkpoint("running")
                            last_checkpoint = time.monotonic()
                        if not stop and not session.should_stop and (max_games is None or games < max_games):
                            if next_seed + config.batch >= (1 << 63):
                                raise RuntimeError("Training seed namespace exhausted before reserved held-out seeds")
                            session.heartbeat({"games": games, "generations": generations,
                                               "unresolved_episodes": config.batch, "unresolved_decisions": 0})
                            host.reset(next_seed)
                checkpoint("complete")
                log({"event": "training_complete", "games": games, "attempts": attempts, "censored": censored,
                     "archive_pool": archive.snapshot(generations),
                     "generations": generations, "decisions": decisions, "example_passes": example_passes,
                     "optimizer_steps": optimizer_steps, "rolling_game_stats": game_stats.snapshot()})
    return run_dir / "latest.soicp"


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--run-dir", type=Path, required=True)
    parser.add_argument("--seconds", type=float, required=True)
    parser.add_argument("--games", type=int)
    parser.add_argument("--config", type=Path, required=True)
    parser.add_argument("--resume", type=Path)
    parser.add_argument("--device", default="cuda")
    args = parser.parse_args()
    if not 1 <= args.seconds <= 43200 or (args.games is not None and args.games < 1):
        parser.error("Require an explicit 1..43200-second allocation and positive optional game count")
    train(TrainConfig(**json.loads(args.config.read_text())), args.run_dir, args.seconds,
          max_games=args.games, resume=args.resume, device=args.device)


if __name__ == "__main__":
    main()
