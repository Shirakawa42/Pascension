"""Evaluation-only ablation: defer exhausts whose exact immediate gate is false.

Policy A uses the saved policy renormalized over the remaining legal choices;
policy B is unchanged. This is an explicit behavior intervention, never PPO data.
It tests a sequence weakness as a group, not whether each removed action is bad.
"""
import argparse
from collections import Counter
import hashlib
from pathlib import Path
import time
from unittest.mock import patch

import cpu_shadow_eval as shadow
from strategy_coverage_probe import immediate_gate
from hero_curriculum.balanced_evaluation import run_balanced_matrix, evaluation_plan
import numpy as np
import torch
from bench_common import save_json


def main():
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument('--checkpoint', type=Path, required=True)
    p.add_argument('--output', type=Path, required=True)
    p.add_argument('--pairs', type=int, default=16)
    args = p.parse_args()
    if args.output.exists():
        p.error('Output must be new')
    shadow.configure_cpu()
    shadow.configure_variant('v7')
    selection, _ = shadow.frozen_selections(args.checkpoint, 'learner', args.checkpoint, 'learner')
    policies = [shadow.checkpoints.materialize_policy(selection, 'cpu') for _ in range(2)]
    cards = selection.catalog['cards']
    counts = Counter()
    seed, sampling = 0x7700000000000000, 270928
    declaration = {'declared_wall': time.time(), 'plan': evaluation_plan(pairs_per_cell=args.pairs,
        seed=seed, sampling_seed=sampling), 'primary_measure': 'equal-hero/seat macro score for timing intervention vs unchanged same frozen weights',
        'interpretation': 'Exploratory ablation; report paired bound and no causal per-card or training-adoption claim.',
        'policy': selection.metadata, 'optimizer_updates': 0,
        'intervention': 'Only policy A: exact known false immediate exhaust gates removed before sampling. All other choices unchanged.',
        'helper_sha256': hashlib.sha256(Path(__file__).read_bytes()).hexdigest(),
        'predicate_sha256': hashlib.sha256((Path(__file__).parent/'strategy_coverage_probe.py').read_bytes()).hexdigest()}
    save_json(args.output.with_suffix('.plan.json'), declaration)

    def compute(actor):
        logits, values = actor.compute_logits()
        obs = actor.obs.numpy()
        candidates = actor.candidates.numpy()
        legal = actor.mask.numpy().astype(bool)
        kinds = candidates[..., :16].argmax(-1)
        if actor.role == 0:
            blocked = np.zeros_like(legal)
            for lane, slot in zip(*np.nonzero(legal & (kinds == 4))):
                card = cards[int(round(float(candidates[lane, slot, 16]) * 192))-1]
                if immediate_gate(card, obs[lane]) is False:
                    blocked[lane, slot] = True
                    counts['suppressed_candidate_evaluations:' + card] += 1
            if np.any(~np.any(legal & ~blocked, axis=1)):
                raise RuntimeError('Ablation attempted to remove all legal actions')
            logits = logits.masked_fill(torch.from_numpy(blocked), -1e9)
        logp = logits.log_softmax(-1)
        probability = logp.exp()
        actor.diagnostics = {'entropy': (-(probability * logp).sum(-1)).numpy().copy(),
            'max_probability': probability.max(-1).values.numpy().copy(),
            'legal_count': actor.mask.sum(-1).numpy().astype(np.int64)}
        return shadow.sample_actions(logits, values)

    before = [shadow.checkpoints.policy_hash(policy) for policy in policies]
    with patch.object(shadow.CPUActor, 'compute', compute):
        report = run_balanced_matrix(*policies, pairs_per_cell=args.pairs, seed=seed,
            sampling_seed=sampling, batch=16, max_seconds=650)
    # Override the helper's default description: only the draft is forced there,
    # whereas this explicitly declared test changes A's subsequent distribution.
    report['intervention'] = declaration['intervention']
    report['plan']['intervention']['subsequent_behavior'] = declaration['intervention']
    report.update(declaration=declaration, policy=selection.metadata, counts=dict(counts),
        frozen_weights_unchanged=before == [shadow.checkpoints.policy_hash(policy) for policy in policies],
        count_scope='Suppression counters include inactive CPU-actor rows; use match outcomes, not these counters, for paired inference.',
        cuda_initialized=torch.cuda.is_initialized())
    if torch.cuda.is_initialized() or not report['frozen_weights_unchanged']:
        raise RuntimeError('Unexpected GPU initialization or policy mutation')
    save_json(args.output, report)
    print({k: report.get(k) for k in ('complete', 'games', 'score_a', 'score_bound_95', 'censored_games')})


if __name__ == '__main__':
    main()
