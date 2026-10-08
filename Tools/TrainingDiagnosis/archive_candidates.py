"""Read-only extraction of a hard archive and nearby checkpoint average."""
import argparse
import copy
import json
import os
from pathlib import Path
import sys
ROOT=Path(__file__).resolve().parents[2]
sys.path[:0]=[str(ROOT/'Tools/ZeroDepthTraining'),str(ROOT/'Tools/TrainingPreflight'),str(ROOT/'Tools/MatchupBenchmark')]
import torch
from campaign_state import load_checkpoint
from cpu_affinity import select_cpus
from league import load_frozen_policy,sha256_file


def run(a):
    os.sched_setaffinity(0,select_cpus(os.sched_getaffinity(0),6));torch.set_num_threads(1)
    a.output.mkdir(parents=True,exist_ok=False)
    model,manifest=load_frozen_policy(a.source/'learner')
    checkpoint=a.source/'snapshot.soicp'
    if sha256_file(checkpoint)!=manifest['checkpoint_sha256']:raise ValueError('Parent checkpoint changed')
    saved=load_checkpoint(checkpoint,expected_identity=json.loads((a.source/'identity.json').read_text()))
    state=saved['state'];metadata=state['opponent_archive'];generation=state['generations']
    base=torch.load(a.source/'learner/policy.pt',map_location='cpu',weights_only=True)
    if any(not torch.equal(v,state['policy'][k]) for k,v in base['policy'].items()):raise ValueError('Parent export and checkpoint policy differ')
    records=list(zip(metadata['slots'],state['archive']));payoffs=metadata['payoffs']
    eligible=[(slot,weights) for slot,weights in records if payoffs.get(slot['id'],{}).get('games',0)>=128]
    hard=min(eligible,key=lambda item:payoffs[item[0]['id']]['score']/payoffs[item[0]['id']]['games'])
    nearby=[(slot,weights) for slot,weights in records if 0<=generation-slot['generation']<=256]
    if len(nearby)<3:raise ValueError('Too few nearby checkpoints')
    parameters=set(dict(model.named_parameters()));averaged={}
    for key,value in base['policy'].items():
        values=[weights[key] for _,weights in nearby]
        if key in parameters:
            total=values[0].double().clone()
            for v in values[1:]:total.add_(v.double())
            averaged[key]=(total/len(values)).float()
        else:
            if any(not torch.equal(v,value) for v in values):raise ValueError('Nearby snapshots changed a frozen inference buffer: '+key)
            averaged[key]=value.clone()
    outputs=[]
    candidates=[('hard-archive',hard[1],[hard[0]],hard[0]['generation']),
                ('nearby-average',averaged,[slot for slot,_ in nearby],generation)]
    for name,weights,slots,stamp in candidates:
        folder=a.output/name;folder.mkdir();payload=copy.deepcopy(base)
        payload['policy']=weights
        payload['training']={'generations':stamp,'source_checkpoint_games':state['games']}
        provenance=dict(source_checkpoint_sha256=manifest['checkpoint_sha256'],source_parent_policy_sha256=manifest['policy_file_sha256'],
            selection='lowest stored learner score with at least 128 effective games' if name=='hard-archive' else 'mean parameters of all retained snapshots within 256 generations; frozen buffers identical',
            slots=slots,exact_snapshot_game_count_unknown=True)
        payload['identity']['archive_candidate']=provenance
        torch.save(payload,folder/'policy.pt');meta=copy.deepcopy(manifest)
        for key in ('checkpoint','checkpoint_sha256','checkpoint_payload_sha256'):meta.pop(key,None)
        meta.update(identity=payload['identity'],training=payload['training'],bytes=(folder/'policy.pt').stat().st_size,
            policy_file_sha256=sha256_file(folder/'policy.pt'),archive_candidate=provenance)
        (folder/'manifest.json').write_text(json.dumps(meta,indent=2));load_frozen_policy(folder)
        outputs.append(dict(name=name,policy_sha256=meta['policy_file_sha256'],**provenance))
    result=dict(candidates=outputs,hard_archive_stored_payoff=payoffs[hard[0]['id']],
        parent_generation=generation,selection_used_deployed_opponent_results=False,strength_proven=False)
    (a.output/'result.json').write_text(json.dumps(result,indent=2));print(json.dumps(result,indent=2))


if __name__=='__main__':
    p=argparse.ArgumentParser(description=__doc__);p.add_argument('--source',type=Path,required=True)
    p.add_argument('--output',type=Path,required=True);run(p.parse_args())
