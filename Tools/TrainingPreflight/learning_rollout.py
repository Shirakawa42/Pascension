"""Owned complete-episode experience for the fresh all-DLC, two-seat learner.

One generation freezes behavior weights and drains one episode per lane. A
finished lane holds the next game's initial observation until the next
generation. Thus every sample has an actual terminal outcome, and no reset
observation is used as a bootstrap target. Frozen-opponent rows are excluded.
"""
from __future__ import annotations

from dataclasses import dataclass
import json
from pathlib import Path
import struct
import time

import numpy as np
import torch

from pipeline_bench import Host


class LearningHost(Host):
    def advance_active(self, indices, active):
        indices = np.asarray(indices, dtype="<i4")
        active = np.asarray(active, dtype=bool)
        if indices.shape != (self.batch,) or active.shape != indices.shape:
            raise ValueError("Selective action/active shapes differ from host batch")
        rows = np.flatnonzero(active)
        if np.any(indices[rows] < 0) or np.any(indices[rows] >= 64):
            raise ValueError("Selective action outside original candidate slots")
        if not np.all(self.mask[rows, indices[rows]] == 1):
            raise ValueError("Illegal selective action")
        actions = np.where(active, indices, -1).astype("<i4")
        packet = memoryview(struct.pack("<I", 5) + actions.tobytes())
        while packet:
            written = self.process.stdin.write(packet)
            if not written:
                raise RuntimeError("Host command pipe closed")
            packet = packet[written:]
        self.receive()


def terminal_returns(episode_ids, seats, outcomes, completed):
    """CPU reference used by credit tests; no alternating-seat assumption."""
    episodes = np.asarray(episode_ids, dtype=np.int64)
    owners = np.asarray(seats, dtype=np.int64)
    outcomes = np.asarray(outcomes, dtype=np.float32)
    completed = np.asarray(completed, dtype=bool)
    if episodes.shape != owners.shape or outcomes.ndim != 2 or outcomes.shape[1] != 2:
        raise ValueError("Malformed episode credit arrays")
    if np.any(episodes < 0) or np.any(episodes >= len(outcomes)) or not np.isin(owners, [0, 1]).all():
        raise ValueError("Invalid episode or deciding seat")
    if completed.shape != (len(outcomes),) or not completed[episodes].all():
        raise ValueError("Unresolved/truncated episode cannot become terminal credit")
    if not np.isin(outcomes, [-1, 0, 1]).all() or np.any(outcomes.sum(1)):
        raise ValueError("Invalid terminal zero-sum outcomes")
    return outcomes[episodes, owners].copy()


class EpisodeStore:
    """Fixed-capacity GPU arrays; never wrap over unresolved trajectories."""
    def __init__(self, capacity=524288, device="cuda"):
        if not 64 <= capacity <= 1048576:
            raise ValueError("Require rollout capacity 64..1048576")
        self.capacity, self.device = capacity, device
        self.obs = torch.empty(capacity, 2048, device=device)
        self.candidates = torch.empty(capacity, 64, 32, device=device)
        self.mask = torch.empty(capacity, 64, dtype=torch.bool, device=device)
        self.actions = torch.empty(capacity, dtype=torch.int64, device=device)
        self.old_logp = torch.empty(capacity, device=device)
        self.old_values = torch.empty(capacity, device=device)
        self.episode_ids = np.empty(capacity, dtype=np.int32)
        self.seats = np.empty(capacity, dtype=np.int32)
        self.rows = 0
        self.sealed = False
        self.returns = self.advantages = None
        self.version = None

    def reset(self, version):
        self.rows = 0
        self.sealed = False
        self.returns = self.advantages = None
        self.version = int(version)

    @torch.no_grad()
    def append(self, actor, packet_gpu, lanes, seats, source_rows=None):
        if self.sealed:
            raise RuntimeError("Cannot append to an update batch")
        lanes = np.asarray(lanes, dtype=np.int64)
        seats = np.asarray(seats, dtype=np.int32)
        count = len(lanes)
        if seats.shape != lanes.shape or not np.isin(seats, [0, 1]).all():
            raise ValueError("Invalid learning-seat ownership")
        if self.rows + count > self.capacity:
            raise RuntimeError("Unresolved rollout storage full; stop without overwriting or training partial episodes")
        if not count:
            return
        source_rows = lanes if source_rows is None else np.asarray(source_rows, dtype=np.int64)
        if source_rows.shape != lanes.shape:
            raise ValueError("Source rows must match retained episode lanes")
        indices = torch.as_tensor(source_rows, device=self.device)
        target = slice(self.rows, self.rows + count)
        self.obs[target].copy_(actor.obs.index_select(0, indices))
        self.candidates[target].copy_(actor.candidates.index_select(0, indices))
        self.mask[target].copy_(actor.mask.index_select(0, indices).bool())
        packet = packet_gpu.index_select(0, indices)
        self.actions[target].copy_(packet[:, 0].long())
        self.old_logp[target].copy_(packet[:, 1])
        self.old_values[target].copy_(packet[:, 2])
        self.episode_ids[target] = lanes
        self.seats[target] = seats
        self.rows += count

    @torch.no_grad()
    def retain_completed(self, completed):
        """Remove every unresolved episode row before credit/normalization.

        Compaction owns a temporary gather per field; it never copies from an
        overlapping live view and retains the order of the surviving decisions.
        Capacity and backing pointers stay fixed for future generations.
        """
        if self.sealed:
            raise RuntimeError("Cannot censor an already sealed update batch")
        completed = np.asarray(completed, dtype=bool)
        episodes = self.episode_ids[:self.rows]
        if completed.ndim != 1 or np.any(episodes < 0) or np.any(episodes >= len(completed)):
            raise ValueError("Malformed completed-episode mask")
        kept = np.flatnonzero(completed[episodes])
        removed = self.rows-len(kept)
        if not removed:
            return 0
        indices = torch.as_tensor(kept, dtype=torch.int64, device=self.device)
        for name in ("obs", "candidates", "mask", "actions", "old_logp", "old_values"):
            buffer = getattr(self, name)
            buffer[:len(kept)].copy_(buffer.index_select(0, indices))
        self.episode_ids[:len(kept)] = self.episode_ids[kept]
        self.seats[:len(kept)] = self.seats[kept]
        self.rows = len(kept)
        self.returns = self.advantages = None
        return removed

    @torch.no_grad()
    def seal(self, outcomes, completed):
        if not self.rows:
            raise RuntimeError("Collection yielded no learning-seat decisions")
        credit = terminal_returns(self.episode_ids[:self.rows], self.seats[:self.rows], outcomes, completed)
        self.returns = torch.as_tensor(credit, device=self.device)
        raw = self.returns - self.old_values[:self.rows]
        self.advantages = (raw - raw.mean()) / raw.std(unbiased=False).clamp_min(1e-6)
        legal = self.mask[:self.rows].gather(1, self.actions[:self.rows, None]).all()
        if not bool(legal) or not bool(torch.isfinite(self.advantages).all()):
            raise RuntimeError("Invalid sealed experience")
        self.sealed = True

    def minibatch(self, indices):
        if not self.sealed:
            raise RuntimeError("Episode credit must resolve before sampling")
        return {name: getattr(self, name).index_select(0, indices) for name in
                ("obs", "candidates", "mask", "actions", "old_logp", "old_values", "returns", "advantages")}


@dataclass
class Collection:
    completed: bool
    metrics: dict


def _save_censor_report(report, debug_dir):
    """Publish a complete action prefix only when a cap actually occurs."""
    if debug_dir is None:
        return None
    folder = Path(debug_dir)
    folder.mkdir(parents=True, exist_ok=True)
    path = folder/f"censored-v{report['behavior_version']}-lane{report['lane']}-{time.time_ns()}.json"
    temporary = path.with_suffix(".json.tmp")
    temporary.write_text(json.dumps(report, indent=2, allow_nan=False)+"\n")
    temporary.replace(path)
    return str(path.resolve())


def collect_episodes(host, actor, store, *, version, session=None, opponent=None,
                     opponent_lanes=None, learner_seats=None, max_steps=30000,
                     uncommitted_episodes=0, uncommitted_decisions=0, stop_check=None,
                     censor_truncated=False, seed_base=None, debug_dir=None, record_action_history=None):
    """Collect with exactly one frozen behavior version and fixed opponents.

    Actor interface: act(host)->(actions, copied CPU packet), packet GPU tensor;
    obs/candidates/mask are owned raw current inputs. Every append precedes the
    next actor replay/host response. All these operations share one CUDA stream.

    Opt-in censoring retains only actually terminal episodes. This conditions
    learning on completion and can bias it; the caller must persist cohort
    counts and enforce its fixed censor-rate guard BEFORE updating. A drained
    all-censored cohort returns completed=True but update_ready=False and an
    unsealed store. Administrative limits and game rules are unchanged.
    """
    n = host.batch
    active = np.ones(n, dtype=bool)
    completed = np.zeros(n, dtype=bool)
    censored = np.zeros(n, dtype=bool)
    outcomes = np.zeros((n, 2), dtype=np.float32)
    lengths = np.zeros(n, dtype=np.int64)
    opponent_lanes = np.zeros(n, dtype=bool) if opponent_lanes is None else np.asarray(opponent_lanes, dtype=bool)
    learner_seats = np.arange(n, dtype=np.int32) % 2 if learner_seats is None else np.asarray(learner_seats, dtype=np.int32)
    if opponent_lanes.shape != (n,) or learner_seats.shape != (n,) or (opponent is None and opponent_lanes.any()):
        raise ValueError("Invalid fixed opponent assignment")
    if not np.isin(learner_seats, [0, 1]).all():
        raise ValueError("Invalid learning seats")
    if seed_base is not None and (not isinstance(seed_base, int) or not 0 <= seed_base <= 2**64-n):
        raise ValueError("Generation seed range must fit uint64")
    if record_action_history is None:
        record_action_history = censor_truncated or debug_dir is not None
    history = np.empty((max_steps, n), dtype=np.int16) if record_action_history else None
    capture_diagnostics = bool(censor_truncated or debug_dir is not None or record_action_history)
    censor_reports = []
    store.reset(version)
    start = time.monotonic()
    initial = host.metrics.copy()
    actor_seconds = host_seconds = storage_seconds = 0.
    lane_histograms = np.zeros((n, 16), dtype=np.int64)
    for step in range(max_steps):
        if (session is not None and session.should_stop) or (stop_check is not None and stop_check()):
            return Collection(False, {"reason": "stop_mid_collection", "discarded_rows": store.rows,
                                      "discarded_episodes": n,
                                      "attempted_games": n, "completed_games": int(completed.sum()),
                                      "censored_games": int(censored.sum()), "unresolved_games": int(active.sum()),
                                      "censored_episodes": censor_reports,
                                      "seconds": time.monotonic()-start})
        owners = host.seats.copy()
        opposing = active & opponent_lanes & (owners != learner_seats)
        learning = active & ~opposing
        lanes = np.flatnonzero(learning)
        tick = time.monotonic()
        adaptive = hasattr(actor, "act_subset")
        if adaptive:
            actions = np.zeros(n, dtype=np.int64)
            if len(lanes):
                actions[lanes], _ = actor.act_subset(host, lanes)
            if opposing.any():
                other_lanes = np.flatnonzero(opposing)
                actions[other_lanes], _ = opponent.act_subset(host, other_lanes)
        else:
            actions, _ = actor.act(host)
            if opposing.any():
                other_actions, _ = opponent.act(host)
                actions[opposing] = other_actions[opposing]
        actor_seconds += time.monotonic()-tick
        chosen_kind = host.candidates[lanes, actions[lanes], :16].argmax(1)
        lane_histograms[lanes, chosen_kind] += 1
        if history is not None:
            history[step] = np.where(active, actions, -1)
        if capture_diagnostics:
            pre_scalars = host.obs[:, :128].copy()
            pre_choices = host.candidates[np.arange(n), actions].copy()
        tick = time.monotonic()
        if len(lanes):
            if adaptive:
                store.append(actor, actor.packet, lanes, owners[lanes], source_rows=np.arange(len(lanes)))
            else:
                store.append(actor, actor.packet, lanes, owners[lanes])
        storage_seconds += time.monotonic()-tick
        lengths[active] += 1
        tick = time.monotonic()
        host.advance_active(actions, active)
        host_seconds += time.monotonic()-tick
        if np.any(host.done[~active]) or np.any(host.rewards[~active]):
            raise RuntimeError("Held lane replayed a terminal reward")
        if not np.isin(host.done, [0, 1, 2]).all() or not np.isin(host.seats, [0, 1]).all():
            raise RuntimeError("Invalid lifecycle fields")
        capped = active & (host.done == 2)
        if capped.any():
            for lane in np.flatnonzero(capped):
                report = {"schema": "shards-censored-episode-v1", "behavior_version": int(version),
                    "lane": int(lane), "engine_seed": None if seed_base is None else seed_base+int(lane),
                    "wrapper_decisions": int(lengths[lane]), "pre_step_deciding_seat": int(owners[lane]),
                    "learner_seat": int(learner_seats[lane]), "versus_archive": bool(opponent_lanes[lane]),
                    "reason": "host administrative limit; not a game outcome",
                    "host_limits": {"submitted_actions": 20000, "round_greater_than": 400, "wrapper_actions": 100000},
                    "history_storage_dtype": "int16" if history is not None else None,
                    "action_indices": history[:step+1, lane].astype(np.int64).tolist() if history is not None else None,
                    "last_action_index": int(actions[lane])}
                if capture_diagnostics:
                    report.update(pre_step_round=int(round(float(pre_scalars[lane, 2])*100)),
                                  pre_step_scalars=pre_scalars[lane].tolist(),
                                  last_candidate_features=pre_choices[lane].tolist())
                trace_path = _save_censor_report(report, debug_dir)
                if trace_path is not None:
                    # The full prefix lives in the trace file; generation logs
                    # retain a bounded summary and a path to the evidence.
                    report = {key: value for key, value in report.items() if key != "action_indices"}
                    report["trace_path"] = trace_path
                censor_reports.append(report)
            if not censor_truncated:
                raise RuntimeError("Administrative episode truncation; do not train or count as a draw")
            censored[capped] = True
            active[capped] = False
        ended = active & (host.done == 1)
        if np.any(host.rewards[~ended]) or not np.isin(host.rewards, [-1, 0, 1]).all() or np.any(host.rewards.sum(1)):
            raise RuntimeError("Invalid terminal reward")
        outcomes[ended] = host.rewards[ended]
        completed[ended] = True
        active[ended] = False
        if session is not None and step % 128 == 0:
            session.heartbeat({"generation": version, "unresolved_episodes": int(uncommitted_episodes+n),
                               "unresolved_decisions": int(uncommitted_decisions+store.rows)})
        if not active.any():
            break
    else:
        raise RuntimeError("Complete-episode collection exceeded wrapper-step bound")
    delta = host.metrics-initial
    terminal_count, censor_count = int(completed.sum()), int(censored.sum())
    if (int(delta[2]) != int(lengths.sum()) or int(delta[4]) != terminal_count or
            int(delta[5]) != censor_count or terminal_count+censor_count != n):
        raise RuntimeError("Selective episode counters disagree with collection")
    attempted_rows = store.rows
    tick = time.monotonic()
    if censor_count:
        store.retain_completed(completed)
    censored_rows = attempted_rows-store.rows
    update_ready = terminal_count > 0 and store.rows > 0
    if update_ready:
        store.seal(outcomes, completed)
    storage_seconds += time.monotonic()-tick
    # Recompute counts from the same retained episode mask used by compaction;
    # frozen-opponent decisions never entered these per-lane learning counters.
    histogram = lane_histograms[completed].sum(axis=0)
    archive_attempted = int(opponent_lanes.sum())
    archive_completed = opponent_lanes & completed
    archive_censored = int((opponent_lanes & censored).sum())
    archive_scores = (outcomes[np.arange(n), learner_seats][archive_completed]+1)*.5
    archive_bounds = [float(archive_scores.sum()/archive_attempted),
                      float((archive_scores.sum()+archive_censored)/archive_attempted)] if archive_attempted else None
    censored_lanes = np.flatnonzero(censored).tolist()
    reports_by_lane = {report["lane"]: report for report in censor_reports}
    return Collection(True, {"seconds": time.monotonic()-start, "attempted_games": n,
        "completed_games": terminal_count, "censored_games": censor_count, "censor_rate": censor_count/n,
        "update_ready": update_ready, "reason": None if update_ready else
            "all_episodes_censored" if terminal_count == 0 else "no_retained_learning_rows",
        "wrapper_decisions": int(delta[2]), "engine_submissions": int(delta[3]), "learning_rows": store.rows,
        "attempted_learning_rows": attempted_rows, "retained_learning_rows": store.rows,
        "censored_learning_rows": censored_rows,
        "mean_episode_decisions": float(lengths.mean()), "max_episode_decisions": int(lengths.max()),
        "lane_occupancy": float(lengths.sum() / (n*(step+1))), "truncated_games": censor_count,
        "draws": int((outcomes[completed, 0] == 0).sum()), "seat0_wins": int((outcomes[completed, 0] > 0).sum()),
        "censored_lanes": censored_lanes,
        "censored_deciding_seats": [reports_by_lane[lane]["pre_step_deciding_seat"] for lane in censored_lanes],
        "censored_learner_seats": learner_seats[censored].tolist(),
        "censored_opponent_lanes": opponent_lanes[censored].tolist(), "censored_episodes": censor_reports,
        "archive_games": archive_attempted, "archive_attempted_games": archive_attempted,
        "archive_completed_games": int(archive_completed.sum()), "archive_censored_games": archive_censored,
        "archive_score": float(archive_scores.mean()) if archive_scores.size else None,
        "archive_score_bounds": archive_bounds,
        "archive_score_scope": "completed archive games only; bounds include censored outcomes as unknown",
        "action_kind_counts": histogram.tolist(), "actor_seconds": actor_seconds,
        "host_seconds": host_seconds, "storage_seconds": storage_seconds,
        "behavior_version": int(version), "credit": "complete per-seat terminal outcome; gamma=lambda=1",
        "censoring_scope": "entire capped episodes excluded before returns/normalization; conditional-on-completion sampling"})
