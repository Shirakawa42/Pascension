"""Evaluation-only integer interval filter for one narrow, public combat case."""
import numpy as np
from hero_coverage_eval import context_bytes

SPLIT = context_bytes('soi.split')


def blocked_batch(observations, candidates, legal, acting_rows):
    blocked = np.zeros_like(legal, dtype=bool)
    # CPU evaluation computes both policies on every lane, but only the acting
    # policy's sampled packet is submitted. The other policy may already have
    # chosen a sublethal interval; do not apply A's intervention to those rows.
    for lane in np.flatnonzero(acting_rows):
        blocked[lane] = blocked_intervals(observations[lane], candidates[lane], legal[lane])
    if np.any(~np.any(legal & ~blocked, axis=1)):
        raise RuntimeError('Split ablation attempted to remove all legal actions')
    return blocked


def blocked_intervals(obs, candidates, legal):
    blocked = np.zeros_like(legal, dtype=bool)
    if not np.array_equal(obs[112:116], SPLIT):
        return blocked
    power = round(float(obs[13])*1000)
    if power <= 0 or round(float(obs[93])*20) != 1:
        return blocked
    if round(float(obs[6])*1000) != round(float(obs[7])*1000) or obs[6] <= 0:
        return blocked  # Includes every taunt/minimum-zero case.
    slots = np.flatnonzero(legal)
    # Only the first target (the opponent) is an explicit integer choice;
    # mandatory remaining damage goes to the sole champion afterward.
    if any(candidates[i,15] != 1 or candidates[i,16] != 0 or candidates[i,25] != 0 or candidates[i,28] != 1 for i in slots):
        return blocked
    enemy = [e for e in obs[1856:2000].reshape(24,6) if e[0] > 0 and e[1] == 1]
    if not enemy:
        return blocked
    defense = round(float(enemy[0][4]-enemy[0][3])*50)
    if defense <= 0:
        return blocked
    for i in slots:
        low, high = round(float(candidates[i,29])*1000), round(float(candidates[i,30])*1000)
        contains_all_face = low <= power <= high
        contains_possible_champion_kill = low <= min(high, power-defense)
        blocked[i] = not (contains_all_face or contains_possible_champion_kill)
    return blocked
