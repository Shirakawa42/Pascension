"""Compare frozen predictions on identical saved roots; no counterfactual win claim."""
import argparse
import json
import os
from pathlib import Path
import sys
ROOT=Path(__file__).resolve().parents[2]
sys.path[:0]=[str(ROOT/'Tools/ZeroDepthTraining'),str(ROOT/'Tools/MatchupBenchmark')]
import numpy as np
import torch
from league import load_frozen_policy,sha256_file
from cpu_affinity import select_cpus
from search_experience import dense_rows


@torch.inference_mode()
def run(a):
    os.sched_setaffinity(0,select_cpus(os.sched_getaffinity(0),6));torch.set_num_threads(1)
    torch.backends.fp32_precision='ieee';torch.backends.cuda.matmul.fp32_precision='ieee'
    policies=[];identities=[]
    for folder in a.policies:
        policy,meta=load_frozen_policy(folder,device='cuda');policy.cache_frozen_table()
        policies.append(policy);identities.append(dict(path=str(folder.resolve()),sha256=meta['policy_file_sha256']))
    plan=json.loads((a.experiment/'plan.json').read_text());data=json.loads((a.experiment/'progress.json').read_text())
    if identities[0]['sha256']!=plan['candidate']['policy_file_sha256']:
        raise ValueError('First model must be the actual behavior model that generated these roots')
    if any(policy.catalog!=policies[0].catalog for policy in policies[1:]):raise ValueError('Observation catalogs differ')
    rows=0;sums=np.zeros((len(policies)-1,4),np.float64);by_hero=np.zeros((len(policies)-1,5,5),np.float64)
    for item in data['experience_files']:
        path=a.experiment/item['file']
        if sha256_file(path)!=item['sha256']:raise ValueError('Experience changed')
        with np.load(path) as archive:stored={key:archive[key] for key in archive.files}
        for start in range(0,len(stored['actions']),128):
            packet=dense_rows(stored,np.arange(start,min(start+128,len(stored['actions']))))
            x=torch.as_tensor(packet,device='cuda');obs=x[:,:24576];c=x[:,24576:-64].reshape(-1,64,48);mask=x[:,-64:]
            predictions=[]
            for policy in policies:
                logits,value=policy(obs,c,mask);predictions.append((logits.softmax(-1),value))
            p0,v0=predictions[0];hero=np.rint(packet[:,22]*5).astype(int)-1
            for index,(p,v) in enumerate(predictions[1:]):
                stats=torch.stack(((p.argmax(-1)!=p0.argmax(-1)).float(),.5*(p-p0).abs().sum(-1),
                                   (v-v0).abs(),v-v0),-1).cpu().numpy()
                sums[index]+=stats.sum(0)
                for h in range(5):
                    chosen=hero==h;by_hero[index,h,:4]+=stats[chosen].sum(0);by_hero[index,h,4]+=chosen.sum()
            rows+=len(packet)
    names=['argmax_changed_fraction','mean_policy_total_variation','mean_absolute_value_change','mean_signed_value_change']
    result=dict(rows=rows,behavior=identities[0],comparisons=[dict(identity=identity,
        overall=dict(zip(names,(sums[i]/rows).tolist())),
        by_hero={hero:dict(zip(names,(by_hero[i,h,:4]/max(1,by_hero[i,h,4])).tolist()))
                 for h,hero in enumerate(('decima','tetra','volos','kosynwu','rez'))})
        for i,identity in enumerate(identities[1:])],
        note='Prediction changes on the same actual behavior roots, weighted by decisions. Alternative policy outcomes are unknown; this is not a strength or value-calibration test.')
    a.output.write_text(json.dumps(result,indent=2));print(json.dumps(result,indent=2))


if __name__=='__main__':
    p=argparse.ArgumentParser(description=__doc__);p.add_argument('--experiment',type=Path,required=True)
    p.add_argument('--output',type=Path,required=True);p.add_argument('policies',type=Path,nargs='+');a=p.parse_args()
    if len(a.policies)<2:p.error('Specify behavior model followed by at least one comparison')
    run(a)
