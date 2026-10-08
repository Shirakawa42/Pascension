"""Bounded natural-game strategy measurement using event-level telemetry."""
import argparse
import collections
import json
import os
from pathlib import Path
import signal
import sys
import time

ROOT = Path(__file__).resolve().parents[2]
sys.path[:0] = [str(ROOT/'Tools/ZeroDepthTraining'), str(ROOT/'Tools/MatchupBenchmark')]


def run(output, policy_path, mode, incumbent, games, seconds):
    import numpy as np
    import torch
    from host import Host
    from model import Actor
    from league import load_frozen_policy, sha256_file
    from cpu_affinity import select_cpus
    os.sched_setaffinity(0, select_cpus(os.sched_getaffinity(0), 6))
    torch.set_num_threads(1)
    torch.backends.fp32_precision='ieee';torch.backends.cuda.matmul.fp32_precision='ieee'
    output.mkdir(parents=True, exist_ok=False)
    telemetry=output/'games';os.environ['SHARDS_DIAG_TELEMETRY']=str(telemetry)
    os.environ['SHARDS_DIAG_BALANCED_EVAL']='1'
    os.environ['SHARDS_DIAG_NO_SEARCH']='1' if mode=='incumbent-raw' else '0'
    binary=ROOT/'Tools/TrainingDiagnosis/Host/bin/Release/net8.0/TrainingDiagnosisHost.dll'
    policy, manifest=load_frozen_policy(policy_path, device='cuda')
    batch=40;actor=Actor(policy,batch,graph=True,packed=True);torch.manual_seed(716927)
    paired=mode!='selfplay';seed=0x6400000000000000//20*20
    started=time.monotonic();deadline=started+seconds;stopped=False
    def stop(*_):
        nonlocal stopped
        stopped=True
    signal.signal(signal.SIGTERM,stop);signal.signal(signal.SIGINT,stop)
    plan=dict(mode=mode,games=games,seconds=seconds,policy_sha256=manifest['policy_file_sha256'],
        binary_sha256=sha256_file(binary),batch=batch,workers=6,seed=seed,paired=paired,
        training_updates=False,automatic_singletons=True,telemetry=str(telemetry))
    (output/'plan.json').write_text(json.dumps(plan,indent=2))
    with Host(binary=binary,batch=batch,workers=6,seed=seed,paired=paired,
        opponent_bundle=incumbent if paired else None,automation=True,
        hero_mode='policy' if paired else 'balanced_random',timeout=min(120,seconds)) as host:
        for cohort in range(games//batch):
            if cohort:host.reset(seed+cohort*(batch//2 if paired else batch))
            while not np.all(host.done):
                if stopped or time.monotonic()>=deadline:raise TimeoutError('Strategy audit reached its bound')
                actions,_=actor.act(host);actions[host.done!=0]=-1
                host.timeout=max(.1,min(120,deadline-time.monotonic()));host.advance(actions)
            if not np.all(host.done==1):raise RuntimeError('Censored strategy cohort')
            (output/'progress.json').write_text(json.dumps(dict(completed=(cohort+1)*batch,planned=games,seconds=time.monotonic()-started)))
    records=[json.loads(p.read_text()) for p in telemetry.glob('game-*.json')]
    if len(records)!=games:raise RuntimeError('Telemetry does not cover all games')
    report=dict(**plan,completed=len(records),elapsed_seconds=time.monotonic()-started,
        victory_causes=dict(collections.Counter(r['victoryCause'] for r in records)),
        focus=sum(sum(r['focus']) for r in records),unused_focus=sum(sum(r['unusedFocus']) for r in records),
        shard_at_29_with_focus=sum(sum(r['shardAt29WithFocus']) for r in records),
        mean_rounds=sum(r['rounds'] for r in records)/len(records),
        winrate=None if not paired else sum(r['winner']==r['learner'] for r in records)/len(records),
        heroes={hero:dict(games=sum(hero in r['heroes'] for r in records),
            wins=sum(r['winner']>=0 and r['heroes'][r['winner']]==hero for r in records),
            mastery_wins=sum(r['winner']>=0 and r['heroes'][r['winner']]==hero and r['victoryCause']=='mastery' for r in records))
            for hero in ('decima','tetra','volos','kosynwu','rez')})
    (output/'report.json').write_text(json.dumps(report,indent=2));print(json.dumps(report),flush=True)


if __name__=='__main__':
    p=argparse.ArgumentParser(description=__doc__)
    p.add_argument('--output',type=Path,required=True);p.add_argument('--policy',type=Path,required=True)
    p.add_argument('--incumbent',type=Path);p.add_argument('--games',type=int,default=1000)
    p.add_argument('--seconds',type=float,default=300)
    p.add_argument('--mode',choices=['selfplay','incumbent-raw','incumbent-search'],default='selfplay')
    a=p.parse_args()
    if a.games<40 or a.games>4000 or a.games%40 or not 10<=a.seconds<=900:p.error('Use 40..4000 games, multiple of 40; 10..900 seconds')
    if a.mode!='selfplay' and a.incumbent is None:p.error('--incumbent is required for paired evaluation')
    run(a.output,a.policy,a.mode,a.incumbent,a.games,a.seconds)
