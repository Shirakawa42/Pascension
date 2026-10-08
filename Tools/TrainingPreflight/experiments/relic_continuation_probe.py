"""CPU-only assigned-relic continuation panel, separate from natural statistics.

All 15 active relics get the same number of assigned hero/opponent/seat games.
Only policy A recruits its assigned relic, at its first legal opportunity.
Policy B and every other in-game choice use the frozen sampled policy. This
measures this policy's continuation under assignment, not optimal relic strength.
No optimizer or live campaign changes; all overrides have explicit point-mass
behavior probabilities and never enter training.
"""
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
from learning_rollout import LearningHost


def main():
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--checkpoint',type=Path,required=True)
    parser.add_argument('--output',type=Path,required=True)
    args=parser.parse_args()
    if args.output.exists(): parser.error('New output required')
    shadow.configure_cpu();shadow.configure_variant('v6')
    selection,_=shadow.frozen_selections(args.checkpoint,'learner',args.checkpoint,'learner')
    policy=shadow.checkpoints.materialize_policy(selection,'cpu')
    metadata=json.loads((Path(__file__).resolve().parents[1]/'results/balance-card-catalog-v6.json').read_text())
    replaced={c['replaces_id'] for c in metadata['cards'] if c.get('replaces_id')}
    relics={h:sorted(c['id'] for c in metadata['cards'] if c['type']=='Relic' and c['character']==h and c['id'] not in replaced) for h in heroes.HEROES}
    if any(len(x)!=3 for x in relics.values()):raise RuntimeError('Expected three active relics per hero')
    card_ids={c:i+1 for i,c in enumerate(selection.catalog['cards'])}
    assigned=None; counts=Counter(); acquired=set();played=set();exhausted=set(); eligible=set()
    original=shadow.ObservedHost.advance_active
    def compute(actor):
        logits,values=actor.compute_logits()
        if actor.role==0:
            ids=(actor.candidates[:,:,16]*192).round().long()
            matches=(actor.candidates[:,:,7]==1)&(ids==card_ids[assigned])&actor.mask.bool()
            # An actual intervention distribution, not a forged natural-policy logp.
            logits=torch.where(matches.any(-1,keepdim=True),torch.where(matches,0.,-1.e9),logits)
        logp=logits.log_softmax(-1);prob=logp.exp()
        actor.diagnostics={'entropy':(-(prob*logp).sum(-1)).numpy().copy(),
            'max_probability':prob.max(-1).values.numpy().copy(),
            'legal_count':actor.mask.sum(-1).numpy().astype(np.int64)}
        return shadow.sample_actions(logits,values)
    def observe(host,actions,active):
        for lane in np.flatnonzero(active):
            if host.seats[lane]!=host.seat_a:continue
            key=(host.seed+int(lane),host.seat_a)
            c=host.candidates[lane];legal=host.mask[lane]==1
            ids=np.rint(c[:,16]*192).astype(int);selected=c[actions[lane]];kind=int(selected[:16].argmax())
            if np.any(legal&(c[:,7]==1)&(ids==card_ids[assigned])):eligible.add(key)
            if ids[actions[lane]]==card_ids[assigned]:
                if kind==7: acquired.add(key);counts['recruitments']+=1
                if kind in (0,4):
                    (played if kind==0 else exhausted).add(key)
                    label='plays' if kind==0 else 'exhausts';counts[label]+=1
                    mastery=int(round(float(host.obs[lane,17])*30))
                    for m in (10,15,20,30):counts[label+'_at_mastery_'+str(m)]+=int(mastery>=m)
        return original(host,actions,active)
    report={'schema':'shards-assigned-relic-continuation-v1','policy':selection.metadata,'policy_sha256':shadow.checkpoints.policy_hash(policy),
        'scope':__doc__,'cells':[],'relics':{},'seed_start':0x7100000000000000,'optimizer_updates':0,'cuda_initialized':False}
    started=time.monotonic();index=0
    with patch.object(shadow.CPUActor,'compute',compute),patch.object(shadow.ObservedHost,'advance_active',observe):
        for hero in heroes.HEROES:
            for assigned in relics[hero]:
                counts=Counter();acquired=set();played=set();exhausted=set();eligible=set()
                for opponent in heroes.HEROES:
                    if opponent==hero:continue
                    build_count=0
                    def build(n,workers,seed,**kwargs):
                        nonlocal build_count
                        pair=(hero,opponent) if build_count%2==0 else (opponent,hero);build_count+=1
                        host=LearningHost(n,workers,seed,**kwargs)
                        try:heroes.force_initial_draft(host,pair);return host
                        except BaseException:host.close();raise
                    result=shadow.run_cpu_match(policy,policy,games=16,seed=report['seed_start']+index*128,batch=8,
                        sampling_seed=270926+index,max_seconds=90,host_factory=build,step_delay_ms=3,telemetry=False)
                    result['behavior']='Policy A takes its assigned relic at first legal opportunity; every other action samples the frozen policy'
                    result['execution']['production_equivalence']='Deliberate assigned-relic evaluation intervention; never a natural-policy strength result'
                    index+=1;result.update(hero=hero,opponent=opponent,assigned_relic=assigned);report['cells'].append(result)
                    report['relics'][assigned]=dict(counts,hero=hero,assigned_games=sum(x['games'] if 'games' in x else 16 for x in report['cells'] if x['assigned_relic']==assigned),
                        eligible_games=len(eligible),recruited_games=len(acquired),played_games=len(played),exhausted_games=len(exhausted))
                    report['seconds']=time.monotonic()-started;report['complete']=len(report['cells'])==60 and all(c['complete'] for c in report['cells'])
                    save_json(args.output,report)
                    if not result['complete']:raise RuntimeError('Incomplete continuation cell')
                print(json.dumps({'relic':assigned,**report['relics'][assigned]}),flush=True)
    if torch.cuda.is_initialized():raise RuntimeError('CUDA unexpectedly initialized')

if __name__=='__main__':main()
