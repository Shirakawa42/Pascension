"""Frozen, seat-swapped matches on reserved seeds; never performs updates."""
from __future__ import annotations

import math
from pathlib import Path
import time

import numpy as np

from learning_rollout import LearningHost
from bench_common import save_json


def score_summary(scores, planned_games, censored_games):
    """Bound unknown results without assigning a win, loss, draw or midpoint."""
    samples = np.asarray(scores, dtype=np.float64)
    if planned_games < 2 or planned_games % 2 or len(samples)+censored_games != planned_games:
        raise ValueError("Evaluation counts must cover every planned paired game")
    if not np.isin(samples, [0., .5, 1.]).all() or censored_games < 0:
        raise ValueError("Invalid resolved scores or censor count")
    low, high = float(samples.sum()/planned_games), float((samples.sum()+censored_games)/planned_games)
    margin = math.sqrt(math.log(40)/(2*(planned_games//2)))
    return {"score_a": float(samples.mean()) if not censored_games else None,
        "score_a_resolved_only": float(samples.mean()) if len(samples) else None,
        "score_identification_interval": [low, high],
        "score_bound_95": [max(0., low-margin), min(1., high+margin)],
        "wins_a": int((samples==1).sum()), "draws": int((samples==.5).sum()),
        "losses_a": int((samples==0).sum()), "resolved_games": len(samples),
        "censored_games": censored_games, "all_terminal": censored_games == 0}


def evaluate_match(policy_a, policy_b, *, games=256, seed=0x2000000000000000,
                   batch=128, workers=4, session=None, telemetry=False, stop_check=None,
                   censor_truncated=False, debug_dir=None):
    from learning_model import LearningActor
    if games < 2 or games % 2 or not 1 <= batch <= 256:
        raise ValueError("Require an even game count and batch1..256")
    started = time.monotonic()
    scores, lengths = [], []
    chosen = np.zeros((16, 193), dtype=np.int64)
    available = np.zeros_like(chosen)
    wrappers = submissions = 0
    censored_games = 0
    pairs_remaining = games // 2
    offset = 0
    while pairs_remaining:
        n = min(batch, pairs_remaining)
        actors = [LearningActor(policy, n, graph=True) for policy in (policy_a, policy_b)]
        for seat_a in (0, 1):
            host = LearningHost(n, workers, seed+offset, pinned=True, transport="shared", split_branches=8)
            try:
                active = np.ones(n, dtype=bool)
                lane_lengths = np.zeros(n, dtype=np.int64)
                action_history = []
                for step in range(30000):
                    if (session is not None and session.should_stop) or (stop_check is not None and stop_check()):
                        return {"complete": False, "reason": "budget_stop_during_frozen_evaluation",
                                "games": len(scores), "seconds": time.monotonic()-started}
                    use_a = host.seats == seat_a
                    actions_a, _ = actors[0].act(host)
                    actions_b, _ = actors[1].act(host)
                    actions = np.where(use_a, actions_a, actions_b)
                    if censor_truncated:
                        action_history.append(np.where(active, actions, -1).astype(np.int16))
                        previous_round = host.obs[:, 2].copy()*100
                    if telemetry:
                        rows = np.flatnonzero(active & use_a)
                        features = host.candidates[rows]
                        kinds = features[:, :, :16].argmax(-1)
                        identities = (features[:, :, 16]*192).round().astype(np.int64)
                        keys = kinds*193+identities
                        available += np.bincount(keys[host.mask[rows].astype(bool)], minlength=16*193).reshape(16, 193)
                        selected_keys = keys[np.arange(len(rows)), actions[rows]]
                        chosen += np.bincount(selected_keys, minlength=16*193).reshape(16, 193)
                    lane_lengths[active] += 1
                    host.advance_active(actions, active)
                    if np.any(host.done[~active]) or np.any(host.rewards[~active]):
                        raise RuntimeError("Held evaluation lane repeated a terminal result")
                    capped = active & (host.done == 2)
                    if capped.any():
                        if not censor_truncated:
                            raise RuntimeError("Truncated evaluation game cannot be scored as a draw")
                        censored_games += int(capped.sum())
                        if debug_dir is not None:
                            for lane in np.flatnonzero(capped):
                                save_json(Path(debug_dir)/f"eval-{seed+offset+int(lane)}-seat{seat_a}.json",
                                    {"seed": seed+offset+int(lane), "policy_a_seat": seat_a,
                                     "lane": int(lane), "pre_cap_round": float(previous_round[lane]),
                                     "wrapper_decisions": int(lane_lengths[lane]),
                                     "actions": [int(row[lane]) for row in action_history if row[lane]>=0],
                                     "cause": "host_administrative_cap", "outcome": None})
                        active[capped] = False
                    ended = active & (host.done == 1)
                    scores.extend(((host.rewards[ended, seat_a]+1)*.5).tolist())
                    lengths.extend(lane_lengths[ended].tolist())
                    active[ended] = False
                    if session is not None and step % 128 == 0:
                        session.heartbeat()
                    if not active.any():
                        break
                else:
                    raise RuntimeError("Frozen evaluation exceeded episode-step bound")
                wrappers += int(host.metrics[2])
                submissions += int(host.metrics[3])
            finally:
                host.close()
        pairs_remaining -= n
        offset += n
    # Distribution-free bound for mean score in [0,1], including draws.
    # Match dependence from paired seeds prevents treating this as exact iid
    # game confidence; pair-level bound uses half as many independent seeds.
    result = {"complete": True, "all_attempts_complete": True, "games": games,
        **score_summary(scores, games, censored_games), "truncated_games": censored_games,
        "paired_seed_count": games//2, "seed_start": seed,
        "seat_swapped": True, "behavior": "sample actual frozen policy distribution",
        "bound_scope": "Worst/best unresolved outcomes plus Hoeffding bound across planned seat-swapped seed pairs; conservative",
        "mean_episode_decisions": float(np.mean(lengths)) if lengths else None,
        "max_episode_decisions": int(max(lengths)) if lengths else None,
        "wrapper_decisions": wrappers, "engine_submissions": submissions,
        "seconds": time.monotonic()-started}
    if telemetry:
        result["legal_candidate_opportunities_by_kind_card"] = available.tolist()
        result["selected_actions_by_kind_card"] = chosen.tolist()
        result["telemetry_scope"] = "Policy A legal candidate instances and selections across all attempted games, including censored trajectories; not causal card strength; catalog IDs required"
    return result
