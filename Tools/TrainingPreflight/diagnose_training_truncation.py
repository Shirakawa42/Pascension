"""Bounded, charged resume with public pre-step evidence for administrative caps."""
from collections import deque
from dataclasses import asdict
import json
from pathlib import Path
import numpy as np

import train_campaign
from learning_rollout import LearningHost


ROOT = Path('/home/lva/.local/share/shards-training/2026-09-26')
OUTPUT = ROOT / 'repro128'
OUTPUT.mkdir(exist_ok=True)
original = LearningHost.advance_active
history = deque(maxlen=256)
counts = None
action_history = []
episode_base = 0x1000000000000000 + 47 * 256


def trace(self, indices, active):
    global counts, episode_base
    if counts is None:
        counts = np.zeros(self.batch, dtype=np.int64)
    counts += active
    action_history.append(np.where(active, indices, -1).astype(np.int16))
    chosen = self.candidates[np.arange(self.batch), indices].copy()
    previous = self.obs[:, :112].copy()
    history.append((previous, chosen))
    original(self, indices, active)
    bad = np.flatnonzero(self.done == 2)
    if len(bad):
        evidence = []
        for lane in bad:
            evidence.append({'lane': int(lane), 'seed': episode_base+int(lane), 'wrapper_steps': int(counts[lane]),
                'pre_reset_round': float(previous[lane,2]*100),
                'trace': [{'scalars': obs[lane].tolist(), 'chosen': action[lane].tolist()}
                          for obs, action in history]})
        (OUTPUT/'truncation-evidence.json').write_text(json.dumps(evidence, indent=2)+'\n')
        np.save(OUTPUT/'truncation-actions.npy', np.stack(action_history))
    counts[self.done != 0] = 0
    if not counts.any():
        episode_base += self.batch
        action_history.clear()


LearningHost.advance_active = trace
config = train_campaign.TrainConfig(**json.loads((ROOT/'configs/h128.json').read_text()))
train_campaign.train(config, OUTPUT, ROOT/'budget.json', 100,
    resume=ROOT/'pilot128/latest.soicp', label='charged-truncation-diagnosis')
