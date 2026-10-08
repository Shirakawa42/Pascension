"""Frozen learner versus deployed AI: 16 declared hero matchups, both seats."""
from pathlib import Path
import argparse,json,os,signal,sys,time
from dataclasses import asdict
ROOT=Path(__file__).resolve().parents[2]
sys.path[:0]=[str(ROOT/'Tools/ZeroDepthTraining'),str(ROOT/'Tools/TrainingPreflight')]
import numpy as np
import torch
from host import Host,catalog
from catalog_check import compatible_catalog
from cpu_affinity import select_cpus
from model import Actor
from league import load_frozen_policy,verify_frozen_policy
from evaluate import GameResult,summarize_pairs,verify_bundle,save_report,sha256_file
HEROES=('decima','tetra','volos','kosynwu','rez')
MATCHUPS=tuple((a,b) for a in HEROES[:4] for b in HEROES if a!=b)
SEED=12682136550676000000

def matchup(seed):
    if not SEED<=seed<SEED+2400:raise ValueError('Unplanned benchmark seed')
    return MATCHUPS[(seed-SEED)%16]

def summary(records):
    games=[GameResult(**{k:r[k] for k in ('seed','learner_seat','winner','censored')}) for r in records]
    total=summarize_pairs(games,planned_pairs=2400)
    rows=[]
    for a,b in MATCHUPS:
        chosen=[g for g in games if matchup(g.seed)==(a,b)]
        value=summarize_pairs(chosen,planned_pairs=150,alpha=.05/16)
        rows.append(dict(learner=a,opponent=b,**value))
    return total,rows

def run(work):
    work=Path(work);plan=json.loads((work/'plan.json').read_text());binary=Path(plan['binary'])
    os.sched_setaffinity(0, select_cpus(os.sched_getaffinity(0),6))
    torch.set_num_threads(1);torch.backends.fp32_precision='ieee';torch.backends.cuda.matmul.fp32_precision='ieee'
    assert plan['games']==4800 and plan['workers']==6 and plan['batch']==32
    assert sha256_file(binary)==plan['binary_sha256']
    verify_bundle(work/'incumbent',repo_root=ROOT)
    assert sha256_file(work/'incumbent/manifest.json')==plan['incumbent_manifest_sha256']
    policy,meta=load_frozen_policy(work/'learner',device='cuda',manifest_sha256=plan['learner_manifest_sha256'])
    compatible_catalog(policy.catalog,catalog(binary))
    torch.manual_seed(plan['policy_seed'])
    actor=Actor(policy,32,graph=True,packed=True)
    records=[];started=time.time();stopping=False;choices=0;last_report=0;active=[]
    def stop(*_):
        nonlocal stopping
        stopping=True
    signal.signal(signal.SIGTERM,stop);signal.signal(signal.SIGINT,stop)
    def cancelled():return stopping or (work/'STOP').exists() or Path(plan['training_control'],'STOP').exists() or Path(plan['training_campaign'],'STOP').exists()
    def publish(state='running',error=None,force=False):
        nonlocal last_report
        now=time.time()
        if not force and now-last_report<1:return
        overall,rows=summary(records);elapsed=now-started;rate=len(records)/elapsed if elapsed else 0
        value=dict(schema='shards-fixed-hero-live-benchmark-v1',state=state,updated_wall=now,started_wall=started,elapsed_seconds=elapsed,planned_games=4800,completed_games=len(records),games_per_second=rate,estimated_remaining_seconds=(4800-len(records))/rate if len(records)>=32 and rate else None,checkpoint_games=meta['training']['games'],workers=6,batch=32,learner_excludes_rez=True,incumbent_search_enabled=True,summary=overall,matchups=rows,active_games=active,learner_decisions=choices,training_deadline_wall=plan['training_deadline_wall'],training_paused=True,scope='Four learner heroes; each faces all four different opponents, including Rez. Not a five-hero overall strength claim.',error=error)
        save_report(work/'status.json',value)
        if force:save_report(work/'results.json',dict(plan=plan,report=value,results=records))
        last_report=now
    publish(force=True)
    try:
        with (work/'games.jsonl').open('x',buffering=1) as journal:
            for first in range(0,2400,16):
                if cancelled():raise InterruptedError('Benchmark stop requested')
                with Host(binary=binary,batch=32,workers=6,seed=SEED+first,opponent_bundle=work/'incumbent',paired=True,automation=True,timeout=300) as host:
                    seen=np.zeros(32,dtype=bool)
                    while not seen.all():
                        if cancelled():raise InterruptedError('Benchmark stop requested')
                        completed=np.flatnonzero((host.done!=0)&~seen)
                        for lane in completed:
                            seed=SEED+first+int(lane)//2;seat=int(lane)%2;pair=matchup(seed);reward=host.rewards[lane]
                            capped=int(host.done[lane])==2
                            if capped:winner=None
                            elif np.array_equal(reward,[0,0]):winner=-1
                            elif np.array_equal(reward,[1,-1]):winner=0
                            elif np.array_equal(reward,[-1,1]):winner=1
                            else:raise RuntimeError('Invalid terminal utilities')
                            result=dict(seed=seed,learner_seat=seat,winner=winner,censored=capped,learner_hero=pair[0],opponent_hero=pair[1],rounds=round(float(host.obs[lane,2])*100),finished_wall=time.time())
                            records.append(result);journal.write(json.dumps(result)+'\n');seen[lane]=True
                        live=~seen
                        active=[dict(learner=matchup(SEED+first+int(i)//2)[0],opponent=matchup(SEED+first+int(i)//2)[1],seat=int(i)%2,round=round(float(host.obs[i,2])*100)) for i in np.flatnonzero(live)]
                        publish(force=bool(completed.size))
                        if seen.all():break
                        if not np.all(host.actors[live]==np.arange(32)[live]%2):raise RuntimeError('Opponent decision delivered to learner')
                        # Validate actual assigned heroes directly in the learner's public observation.
                        for lane in np.flatnonzero(live):
                            pair=matchup(SEED+first+int(lane)//2)
                            actual=(HEROES[int(round(float(host.obs[lane,22])*5))-1],HEROES[int(round(float(host.obs[lane,86])*5))-1])
                            if actual!=pair:raise RuntimeError('Actual hero observation differs from planned matchup')
                        actions,_=actor.act(host);actions[seen]=-1;choices+=int(live.sum());host.advance(actions)
            verify_frozen_policy(work/'learner',manifest_sha256=plan['learner_manifest_sha256']);verify_bundle(work/'incumbent',repo_root=ROOT)
            assert len(records)==4800
            publish('complete',force=True)
    except BaseException as error:
        publish('stopped' if isinstance(error,InterruptedError) else 'failed',f'{type(error).__name__}: {error}',force=True)
        raise

if __name__=='__main__':
    p=argparse.ArgumentParser();p.add_argument('--work',type=Path,required=True);run(p.parse_args().work)
