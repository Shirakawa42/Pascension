"""Discarded GPU optimizer probe on saved public inputs; never a strength test."""
import argparse
import json
import os
from pathlib import Path
from types import SimpleNamespace

import numpy as np
import torch

from train_against_search import configure_learning_scope, verify_frozen_state
from search_candidate import load_frozen_policy, select_cpus, sha256_file
from search_experience import dense_rows
from scry_policy import ScryPolicy, require_calibration
from train import Learner, TrainConfig
from client import OBS, ACTIONS, FEATURES


def run(a):
    os.sched_setaffinity(0, select_cpus(os.sched_getaffinity(0), 6))
    torch.set_num_threads(1)
    torch.manual_seed(80171)
    np.random.seed(80171)
    torch.backends.fp32_precision = 'ieee'
    torch.backends.cuda.matmul.fp32_precision = 'ieee'
    result = json.loads((a.experience/'result.json').read_text())
    if result['state'] != 'complete':
        raise ValueError('Experience is not from complete games')
    source = result['experience_files'][0]
    path = a.experience/source['file']
    if sha256_file(path) != source['sha256']:
        raise ValueError('Saved experience changed')
    with np.load(path) as loaded:
        data = {key: loaded[key] for key in loaded.files}
    packet = dense_rows(data, np.arange(len(data['actions'])))
    base, manifest = load_frozen_policy(a.policy, device='cuda', allow_external_calibration=True)
    require_calibration(manifest, 64., 0.)
    metadata, frozen = configure_learning_scope(base, 'actor-head')
    policy = ScryPolicy(base, temperature=64.)
    contexts = policy.catalog['contexts']
    scry = (packet[:, 144] > .5) & (np.rint(packet[:, 157]*len(contexts)) == contexts.index('soi.scry'))
    selected = np.r_[np.flatnonzero(scry)[:96], np.flatnonzero(~scry)[:160]]
    packet = packet[selected]
    obs = torch.from_numpy(packet[:, :OBS]).cuda()
    candidates = torch.from_numpy(packet[:, OBS:-ACTIONS].reshape(-1, ACTIONS, FEATURES)).cuda()
    mask = torch.from_numpy(packet[:, -ACTIONS:].astype(bool)).cuda()
    with torch.no_grad():
        before_z, before_v = policy(obs, candidates, mask)
        actions = torch.multinomial(before_z.softmax(-1), 1)[:, 0]
        logp = before_z.log_softmax(-1).gather(1, actions[:, None])[:, 0]
    store = SimpleNamespace(gpu=False, obs=obs.cpu().numpy(), candidates=candidates.cpu().numpy(),
        mask=mask.cpu().numpy(), packet=torch.stack((actions, logp, before_v), dim=1).cpu().numpy())
    # Deliberately artificial labels: this is an optimizer isolation test only.
    # The mutated model is discarded and never used for evaluation or export.
    returns = np.where(np.arange(len(selected))%2, 1., -1.).astype(np.float32)
    config = TrainConfig(minibatch=128, epochs=1, learning_rate=3e-5, graph=False)
    learner = Learner(policy, config)
    stats = learner.update(store, np.arange(len(selected)), returns, returns-before_v.cpu().numpy())
    verify_frozen_state(policy, frozen)
    with torch.no_grad():
        after_z, after_v = policy(obs, candidates, mask)
    if not torch.equal(before_v, after_v):
        raise RuntimeError('GPU update changed critic outputs')
    if torch.equal(before_z, after_z) or stats['optimizer_steps'] < 1:
        raise RuntimeError('Probe failed to exercise a real actor update')
    if any(not parameter.requires_grad for parameter in learner.optimizer.state):
        raise RuntimeError('Frozen parameter acquired optimizer state')
    report = dict(passed=True, policy_sha256=manifest['policy_file_sha256'],
        experience_sha256=source['sha256'], rows=len(selected), scry_rows=int(scry[selected].sum()),
        learning_scope=metadata, critic_outputs_bitwise_identical=True,
        all_frozen_tensors_bitwise_identical=True, actor_logits_changed=True,
        fused_cuda_optimizer=True, update=stats, exported_model=False,
        strength_evidence=False, limitation='Synthetic rewards exercise the optimizer; updated tensors are discarded.')
    a.output.write_text(json.dumps(report, indent=2)+'\n')
    print(json.dumps(report, indent=2))


if __name__ == '__main__':
    p = argparse.ArgumentParser(description=__doc__)
    for name in ('policy', 'experience', 'output'):
        p.add_argument('--'+name, type=Path, required=True)
    run(p.parse_args())
