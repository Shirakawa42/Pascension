"""Describe mastery-conditioned value calibration on completed real games.

Repeated decisions are not independent games. Retain one real decision per
round and weight each game equally within a bucket; do not claim causality.
"""
import argparse
from collections import Counter
import json
import os
from pathlib import Path
import sys

ROOT=Path(__file__).resolve().parents[2]
sys.path[:0]=[str(ROOT/'Tools/ZeroDepthTraining'),str(ROOT/'Tools/MatchupBenchmark')]
import numpy as np
import torch
from cpu_affinity import select_cpus
from league import load_frozen_policy,sha256_file
from search_experience import dense_rows


def summarize(rows, chosen):
    subset=[row for row in rows if chosen(row)]
    if not subset:return dict(games=0,round_observations=0)
    counts=Counter((r['seed'],r['seat']) for r in subset)
    weight=np.array([1/counts[r['seed'],r['seat']] for r in subset]);weight/=weight.sum()
    predictions=np.array([r['win_probability'] for r in subset]);outcomes=np.array([r['outcome'] for r in subset])
    return dict(games=len(counts),round_observations=len(subset),
        mean_predicted_win_probability=float(weight@predictions),actual_win_rate=float(weight@outcomes),
        prediction_minus_actual=float(weight@(predictions-outcomes)),brier=float(weight@((predictions-outcomes)**2)))


def run(args):
    os.sched_setaffinity(0,select_cpus(os.sched_getaffinity(0),6));torch.set_num_threads(1)
    torch.backends.fp32_precision='ieee';torch.backends.cuda.matmul.fp32_precision='ieee'
    policy,manifest=load_frozen_policy(args.policy,device='cuda');policy.cache_frozen_table()
    plan=json.loads((args.experiment/'plan.json').read_text());result=json.loads((args.experiment/'result.json').read_text())
    if result['state']!='complete' or manifest['policy_file_sha256']!=plan['candidate']['policy_file_sha256']:
        raise ValueError('Requires a complete experiment and its exact behavior policy')
    games={(g['seed'],g['learner_seat']):g for g in result['completed_games']}
    if any(not g['completed'] for g in games.values()):raise ValueError('Censored game')
    rows=[];seen=set()
    with torch.inference_mode():
        for entry in result['experience_files']:
            file=args.experiment/entry['file']
            if sha256_file(file)!=entry['sha256']:raise ValueError('Experience changed')
            with np.load(file) as archive:data={k:archive[k] for k in archive.files}
            for start in range(0,len(data['actions']),64):
                indices=np.arange(start,min(start+64,len(data['actions'])))
                packet=dense_rows(data,indices);selected=[]
                for j,i in enumerate(indices):
                    seed=int(data['seeds'][i]);seat=int(data['meta'][i,1]);round_number=round(float(packet[j,2])*100)
                    key=seed,seat,round_number
                    if key in seen:continue
                    seen.add(key);selected.append(j)
                if not selected:continue
                x=torch.as_tensor(packet[selected],device='cuda')
                _,values=policy(x[:,:24576],x[:,24576:-64].reshape(-1,64,48),x[:,-64:])
                probabilities=(values.cpu().numpy()+1)/2
                for k,j in enumerate(selected):
                    i=indices[j];seed=int(data['seeds'][i]);seat=int(data['meta'][i,1]);game=games[seed,seat]
                    rows.append(dict(seed=seed,seat=seat,round=round(float(packet[j,2])*100),
                        hero=game['heroes'][seat],own_mastery=round(float(packet[j,17])*30),
                        opponent_mastery=round(float(packet[j,81])*30),win_probability=float(probabilities[k]),
                        outcome=float((data['outcomes'][i]+1)/2)))
    buckets={'all':lambda r:True,'own_mastery_0_9':lambda r:r['own_mastery']<10,
        'own_mastery_10_19':lambda r:10<=r['own_mastery']<20,'own_mastery_20_29':lambda r:20<=r['own_mastery']<30,
        'own_mastery_30':lambda r:r['own_mastery']>=30,'opponent_mastery_20_plus':lambda r:r['opponent_mastery']>=20,
        'mastery_lead_5_plus':lambda r:r['own_mastery']-r['opponent_mastery']>=5,
        'mastery_deficit_5_plus':lambda r:r['own_mastery']-r['opponent_mastery']<=-5,'rez':lambda r:r['hero']=='rez'}
    report=dict(policy_sha256=manifest['policy_file_sha256'],experiment=str(args.experiment),
        method='First recorded real learner decision per round; equal weight per seed/seat game within each bucket',
        limitations='Descriptive calibration on only80 development games, with overlapping correlated buckets. Not a causal test of mastery learning and not an independent strength test.',
        buckets={name:summarize(rows,condition) for name,condition in buckets.items()},
        games=len(games),round_observations=len(rows))
    args.output.write_text(json.dumps(report,indent=2));print(json.dumps(report,indent=2))


if __name__=='__main__':
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--experiment',type=Path,required=True);parser.add_argument('--policy',type=Path,required=True)
    parser.add_argument('--output',type=Path,required=True);run(parser.parse_args())
