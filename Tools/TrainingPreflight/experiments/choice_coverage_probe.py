"""Passive frozen-policy choice coverage; CPU only, balanced legal hero drafts.

No policy overrides after setup, no optimizer, no budget access. Counts legal
menus and unique player-games separately. Acquisition is distinct from play.
"""
import argparse
from collections import Counter, defaultdict
import itertools
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
    p=argparse.ArgumentParser(description=__doc__)
    p.add_argument('--checkpoint',type=Path,required=True)
    p.add_argument('--output',type=Path,required=True)
    p.add_argument('--pairs',type=int,default=32)
    p.add_argument('--variant',choices=('v6','v7'),default='v6')
    p.add_argument('--seed',type=lambda x:int(x,0),default=0x7000000000000000)
    p.add_argument('--sampling-seed',type=int,default=260927)
    args=p.parse_args()
    if args.output.exists() or not 1<=args.pairs<=64: p.error('New output and 1..64 pairs required')
    shadow.configure_cpu(); shadow.configure_variant(args.variant)
    selection,_=shadow.frozen_selections(args.checkpoint,'learner',args.checkpoint,'learner')
    policy=shadow.checkpoints.materialize_policy(selection,'cpu')
    cards=selection.catalog['cards']
    rows=defaultdict(Counter); exposed=defaultdict(set); selected_games=defaultdict(set)
    counts=Counter(); volos=defaultdict(Counter)
    volos_context=heroes.context_bytes('soi.volos')
    original=shadow.ObservedHost.advance_active
    def compute(actor):
        logits,values=actor.compute_logits(); logp=logits.log_softmax(-1); probability=logp.exp()
        actor.diagnostics={'entropy':(-(probability*logp).sum(-1)).numpy().copy(),
            'max_probability':probability.max(-1).values.numpy().copy(),
            'legal_count':actor.mask.sum(-1).numpy().astype(np.int64),
            'probabilities':probability.numpy().copy(),'logits':logits.numpy().copy()}
        return shadow.sample_actions(logits,values)
    def card_id(c):
        i=int(round(float(c[16])*192))-1
        return cards[i] if i>=0 else None
    def observe(host,actions,active):
        for lane in np.flatnonzero(active):
            seat=int(host.seats[lane]); role=0 if seat==host.seat_a else 1
            packet,diag=host.packets[role]
            if int(packet[lane,0])!=int(actions[lane]): raise RuntimeError('Action/probability mismatch')
            o=host.obs[lane]; legal=np.flatnonzero(host.mask[lane]==1); c=host.candidates[lane]
            hero=heroes.HEROES[int(round(float(o[22])*5))-1]
            game=(host.seed+int(lane),host.seat_a,seat)
            mastery=int(round(float(o[17])*30)); counts['decisions']+=1
            is_volos=np.array_equal(o[112:116],volos_context)
            context=','.join(str(int(round(float(x)*255))) for x in o[112:116])
            for index in legal:
                candidate=c[index]; kind=int(candidate[:16].argmax()); card=card_id(candidate)
                if kind in (0,1,2,4,5,6,7): key=shadow.KINDS[kind]+':'+str(card)
                elif kind==12: key='choice:'+context+':'+str(card or int(round(float(candidate[25])*128)))
                elif kind in (9,14,15): key=shadow.KINDS[kind]+':'+hero
                else: continue
                row=rows[key]; row['legal_menus']+=1; row['probability_sum']+=float(diag['probabilities'][lane,index]); exposed[key].add(game)
                chosen=int(index)==int(actions[lane]); row['selections']+=chosen
                if chosen:
                    selected_games[key].add(game)
                    row['mastery_sum']+=mastery
                    for threshold in (10,15,20,30): row['selected_at_mastery_'+str(threshold)]+=int(mastery>=threshold)
                if is_volos:
                    mode=int(round(float(candidate[25])*128)); group='all_four_legal' if len(legal)==4 else 'any_legal'
                    for group in (['any_legal','all_four_legal'] if len(legal)==4 else ['any_legal']):
                        r=volos[group+':'+str(mode)]; r['legal_menus']+=1;r['selections']+=chosen;r['probability_sum']+=float(diag['probabilities'][lane,index])
                        r['logit_sum']+=float(diag['logits'][lane,index])
            if is_volos:
                counts['volos_menus']+=1;counts['volos_legal_count_'+str(len(legal))]+=1
                if len(legal)==4:
                    logits=diag['logits'][lane,legal].astype(float)
                    counts['volos_all4_curvature_abs_sum']+=float(np.abs(np.diff(logits,2)).sum())
                    counts['volos_all4_logit_range_sum']+=float(np.ptp(logits))
        return original(host,actions,active)
    report={'schema':'shards-choice-coverage-probe-v1','policy':selection.metadata,'policy_sha256':shadow.checkpoints.policy_hash(policy),
        'cells':[],'seed_start':args.seed,'optimizer_updates':0,'cuda_initialized':False,
        'scope':'Frozen sampled learner on both seats; ten distinct hero matchups with paired seat swaps. Only setup heroes forced. Legal menus repeat; player-game exposure deduplicated. Acquisitions do not imply plays or condition triggers.'}
    started=time.monotonic()
    with patch.object(shadow.CPUActor,'compute',compute),patch.object(shadow.ObservedHost,'advance_active',observe):
        for number,pair in enumerate(itertools.combinations(heroes.HEROES,2)):
            build_count=0
            def build(n,workers,seed,**kwargs):
                nonlocal build_count
                assigned=pair if build_count%2==0 else pair[::-1];build_count+=1
                host=LearningHost(n,workers,seed,**kwargs)
                try: heroes.force_initial_draft(host,assigned);return host
                except BaseException:host.close();raise
            result=shadow.run_cpu_match(policy,policy,games=2*args.pairs,seed=report['seed_start']+number*128,
                batch=min(32,args.pairs),sampling_seed=args.sampling_seed+number,max_seconds=120,host_factory=build,step_delay_ms=3,telemetry=False)
            result['hero_pair']=pair;report['cells'].append(result)
            report['choices']={key:dict(value,exposed_player_games=len(exposed[key]),selected_player_games=len(selected_games[key]),
                mean_probability=value['probability_sum']/value['legal_menus']) for key,value in sorted(rows.items())}
            report['volos']={key:dict(value,mean_probability=value['probability_sum']/value['legal_menus'],mean_logit=value['logit_sum']/value['legal_menus']) for key,value in sorted(volos.items())}
            report['counts']=dict(counts);report['seconds']=time.monotonic()-started
            report['complete']=len(report['cells'])==10 and all(c['complete'] for c in report['cells'])
            save_json(args.output,report);print(json.dumps({'pair':pair,'complete':result['complete'],'seconds':result['seconds']}),flush=True)
            if not result['complete']:break
    if torch.cuda.is_initialized():raise RuntimeError('CUDA unexpectedly initialized')

if __name__=='__main__':main()
