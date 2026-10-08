"""CPU-only frozen migration/behavior comparison; never publishes or trains."""
import argparse
from collections import Counter
import json
from pathlib import Path
import time
from unittest.mock import patch

import cpu_shadow_eval as shadow
import hero_coverage_eval as heroes
import numpy as np
import torch
from bench_common import save_json
from learning_model import LearningPolicy, PolicyConfig
from learning_rollout import LearningHost
from migrate_runtime_v6 import prepare, policies
import variant_v6_runtime as runtime
from hero_curriculum.balanced_evaluation import run_balanced_matrix


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--checkpoint', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--pairs', type=int, default=16)
    args = parser.parse_args()
    if args.output.exists():
        parser.error('Require new report path')
    shadow.configure_cpu()
    payload, migration = prepare(args.checkpoint)
    state = payload['state']
    policy = LearningPolicy(PolicyConfig(**state['policy_config'])).eval().requires_grad_(False)
    policy.load_state_dict(state['learner']['policy'])
    # All new columns are exactly zero in every retained policy and Adam state.
    assert all(torch.count_nonzero(p['core.trunk.0.weight'][:,list(runtime.SLOTS)]) == 0 for p in policies(state))
    runtime.install()
    observations = Counter()
    original = shadow.ObservedHost.advance_active
    def inspect(host, actions, active):
        expected = []
        for lane in np.flatnonzero(active):
            o = host.obs[lane]
            if round(float(o[22])*5) != 4:
                continue
            kind = int(host.candidates[lane, actions[lane], :16].argmax())
            fixed = int(host.seats[lane]) == host.seat_a
            if fixed and kind == 9:
                observations['previews'] += 1
                expected.append((lane, float(o[16]), int(host.seats[lane]), 'preview'))
            if fixed and o[1853] == 1:
                if kind == 12:
                    observations['paid_committed_banishes'] += 1
                    expected.append((lane, float(o[16])-.06, int(host.seats[lane]), 'commit'))
                elif kind == 13:
                    observations['free_declines'] += 1
                    expected.append((lane, float(o[16]), int(host.seats[lane]), 'decline'))
        original(host, actions, active)
        for lane, health, seat, what in expected:
            if host.done[lane] or host.seats[lane] != seat or abs(float(host.obs[lane,16])-health) > 1e-6:
                raise RuntimeError('Unexpected Ko health/turn change at '+what)
            if what == 'preview' and host.obs[lane,1853] != 1:
                raise RuntimeError('Missing pre-payment decision')
            if what != 'preview' and host.obs[lane,1853] != 0:
                raise RuntimeError('Preview not resolved')
    build_count = 0
    def build(n, workers, seed, **kwargs):
        nonlocal build_count
        seat_a = build_count % 2; build_count += 1
        with runtime.evaluation_seats(1 << seat_a):
            return LearningHost(n, workers, seed, **kwargs)
    # Every complete seat-swapped block constructs an even number of hosts.
    report = {'migration': migration, 'scope': 'Same frozen zero-column-migrated weights on both sides. A uses V6 safeguards and knowledge; B uses exact V5 action staging and zero new features. No learning. Balanced matrix uses disjoint paired seeds.',
        'adoption_gate': 'All structural/health assertions pass; complete balanced panel with zero censors and point score >= .45. This is a regression screen, not proof of strength gain.'}
    save_json(args.output, report)
    with patch.object(shadow.ObservedHost, 'advance_active', inspect):
        result = run_balanced_matrix(policy, policy, pairs_per_cell=args.pairs,
            seed=0x6C00000000000000, sampling_seed=76001, batch=16, max_seconds=900, host_factory=build)
    report.update(result, ko_behavior=dict(observations), cuda_initialized=torch.cuda.is_initialized())
    save_json(args.output, report)
    print(json.dumps({k:report.get(k) for k in ('complete','games','score_a','score_bound_95','censored_games','ko_behavior')}))
    if not result['complete'] or result['censored_games'] or result['score_a'] < .45:
        raise RuntimeError('Predeclared regression gate not satisfied')


if __name__ == '__main__':
    main()
