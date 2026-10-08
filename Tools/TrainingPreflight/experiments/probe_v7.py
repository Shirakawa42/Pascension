"""Frozen CPU regression screen for V7 choice mixture, on balanced heroes."""
import argparse
from collections import Counter
import copy
import json
from pathlib import Path
from unittest.mock import patch
import cpu_shadow_eval as shadow
import numpy as np
import torch
from bench_common import save_json
from learning_model import PolicyConfig
from choice_policy_v7 import ChoicePolicy
from migrate_runtime_v7 import prepare
import variant_v7_runtime as runtime
from hero_curriculum.balanced_evaluation import run_balanced_matrix


def main():
    p=argparse.ArgumentParser(description=__doc__);p.add_argument('--checkpoint',type=Path,required=True);p.add_argument('--output',type=Path,required=True)
    args=p.parse_args()
    if args.output.exists():p.error('New output required')
    shadow.configure_cpu();payload,migration=prepare(args.checkpoint)
    policy=ChoicePolicy(PolicyConfig(**payload['state']['policy_config'])).eval().requires_grad_(False)
    policy.load_state_dict(payload['state']['learner']['policy'])
    baseline=copy.deepcopy(policy);baseline.choice_exploration.zero_()
    runtime.install()
    counts={0:Counter(),1:Counter()};original=shadow.ObservedHost.advance_active
    def observe(host,actions,active):
        for lane in np.flatnonzero(active):
            role=0 if host.seats[lane]==host.seat_a else 1
            candidate=host.candidates[lane,actions[lane]];kind=int(candidate[:16].argmax())
            if kind in (6,7):counts[role][str(kind)+':'+str(int(round(float(candidate[16])*192)))]+=1
        return original(host,actions,active)
    report={'schema':'shards-v7-choice-regression-v1','migration':migration,
        'scope':'Same frozen prelearning V6 weights. A adds 6% relic and 2% destiny legal-menu probability mixtures; B preserves original probabilities. Both have zero Volos head. Balanced hero/seat panel, no optimizer and no CUDA.',
        'adoption_gate':'640 complete games, zero censors, frozen weights unchanged, point score >= .45; regression screen only, not evidence of a gain'}
    save_json(args.output,report)
    with patch.object(shadow.ObservedHost,'advance_active',observe):
        result=run_balanced_matrix(policy,baseline,pairs_per_cell=16,seed=0x7200000000000000,
            sampling_seed=97026,batch=16,max_seconds=600)
    report.update(result,choice_counts={str(k):dict(v) for k,v in counts.items()},cuda_initialized=torch.cuda.is_initialized())
    save_json(args.output,report)
    print(json.dumps({k:report.get(k) for k in ('complete','games','score_a','score_bound_95','censored_games','seconds')}))
    if not result['complete'] or result['censored_games'] or result['score_a']<.45:raise RuntimeError('Regression screen failed')

if __name__=='__main__':main()
