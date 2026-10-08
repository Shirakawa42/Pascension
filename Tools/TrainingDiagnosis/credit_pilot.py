"""Matched finite PPO arms. Never changes the stopped campaign or its budget."""
import argparse,copy,json,os,signal,sys,time
from dataclasses import asdict
from pathlib import Path
ROOT=Path(__file__).resolve().parents[2]
sys.path[:0]=[str(ROOT/'Tools/ZeroDepthTraining'),str(ROOT/'Tools/TrainingPreflight'),str(ROOT/'Tools/MatchupBenchmark')]
import numpy as np
import torch
import train
from model import Policy,PolicyConfig,cpu_state,legacy_exploration_state
from campaign_state import load_checkpoint,restore_rng
from opponent_archive import OpponentArchive
from combined_actor import CombinedGraphActor
from pipeline_actor import act_pair
from cpu_affinity import select_cpus
from league import sha256_file,load_frozen_policy

def run(work,source,mode,games,exploration=0.):
    os.sched_setaffinity(0,select_cpus(os.sched_getaffinity(0),6))
    torch.set_num_threads(1);torch.backends.fp32_precision='ieee';torch.backends.cuda.matmul.fp32_precision='ieee'
    meta=json.loads((source/'learner/manifest.json').read_text())
    initial=load_checkpoint(source/'snapshot.soicp',expected_identity=meta['identity']);state=initial['state']
    label=mode+('-explore' if exploration else '')
    config=train.TrainConfig(**{**state['configuration'],'trace_mode':mode,'optional_exploration':exploration});config.validate()
    assert config.batch==64 and config.workers==6
    policy=Policy(state['catalog'],PolicyConfig(**state['policy_config'])).to('cuda');policy.load_state_dict(state['policy'])
    policy.optional_exploration.fill_(exploration)
    learner=train.Learner(policy,config);learner.restore_optimizer(copy.deepcopy(state['optimizer']))
    archive=OpponentArchive(config.archive_strategy,limit=config.archive_limit,every=config.archive_every,
        generation=state['generations'],current=cpu_state(policy),archive=[legacy_exploration_state(w) for w in state['archive']],metadata=copy.deepcopy(state['opponent_archive']))
    actor=CombinedGraphActor(policy,64,graph=True,compiled=False,packed=True)
    opponent_policy=copy.deepcopy(policy);opponent=CombinedGraphActor(opponent_policy,64,graph=True,compiled=False,packed=True)
    store=train.Rollout(config.capacity,state['catalog'],device='cuda');restore_rng(initial,include_cuda=True)
    stop=False
    def requested(*_):
        nonlocal stop
        stop=True
    signal.signal(signal.SIGTERM,requested);signal.signal(signal.SIGINT,requested)
    started=time.monotonic()
    def expired():return stop or (work/'STOP').exists() or time.monotonic()-started>240
    generation=state['generations'];steps=decisions=0;cohorts=[];seed=0x6100000000000000
    with train.Host(64,6,seed,automation=True,hero_mode='balanced_random') as host:
        for cohort in range(games//64):
            if expired():raise TimeoutError('Diagnostic time/stop bound reached')
            if cohort:host.reset(seed+cohort*64)
            store.reset();actor.refresh(policy)
            weights,selected=archive.choose(generation);opponent_policy.load_state_dict(weights);opponent.refresh(opponent_policy)
            archived=np.random.random(64)<config.archive_fraction;seats=(np.arange(64)+generation)%2
            while not np.all(host.done):
                if expired():raise TimeoutError('Diagnostic time/stop bound reached')
                active=host.done==0;other=active&archived&(host.actors!=seats);retained=np.flatnonzero(active&~other)
                (actions,packet),(other_actions,_)=act_pair(actor,opponent,host,active,other)
                actions[other]=other_actions[other];actions[~active]=-1
                store.append(host,packet,retained,actor=actor);host.advance(actions)
            if not np.all(host.done==1):raise RuntimeError('Censored diagnostic cohort; no update accepted')
            indices,returns,advantages,excluded=store.seal(host.done,host.rewards,gae_lambda=config.trace_decay,trace_mode=mode)
            result=learner.update(store,indices,returns,advantages,stop_check=expired);learner.verify_finite_state()
            if result.get('deadline_stop'):raise TimeoutError('Diagnostic update interrupted')
            archive.observe_cohort(generation,selected['id'],host.done,host.rewards,archived,seats)
            generation+=1;steps+=result['optimizer_steps'];decisions+=len(indices)
            if generation%config.archive_every==0:archive.add(generation,cpu_state(policy))
            cohorts.append(dict(cohort=cohort,games=(cohort+1)*64,**result))
            (work/f'credit-{label}-progress.json').write_text(json.dumps(dict(games=(cohort+1)*64,seconds=time.monotonic()-started)))
    folder=work/f'credit-{label}';folder.mkdir(exist_ok=False)
    parent=torch.load(source/'learner/policy.pt',map_location='cpu',weights_only=True)
    parent['policy']=cpu_state(policy);parent['identity']['configuration']=asdict(config)
    parent['identity']['source_fingerprint']=train.source_fingerprint()
    parent['training'].update(games=state['games']+games,generations=generation,
        optimizer_steps=state['optimizer_steps']+steps,decisions=state['decisions']+decisions)
    torch.save(parent,folder/'policy.pt')
    meta.update(training=parent['training'],identity=parent['identity'],bytes=(folder/'policy.pt').stat().st_size,
        policy_file_sha256=sha256_file(folder/'policy.pt'),diagnostic_pilot=dict(mode=mode,games=games,parent_checkpoint=str(source/'snapshot.soicp'),deployed=False))
    for key in ('checkpoint','checkpoint_sha256','checkpoint_payload_sha256'):meta.pop(key,None)
    (folder/'manifest.json').write_text(json.dumps(meta,indent=2))
    # Retain all learned state so a selected arm need not lose its optimizer/archive.
    torch.save(dict(policy=parent['policy'],optimizer=learner.optimizer.state_dict(),archive=archive.weights,
        opponent_archive=archive.state_dict(generation),configuration=asdict(config),catalog=state['catalog'],
        policy_config=state['policy_config'],training=parent['training']),folder/'continuation-state.pt')
    check,_=load_frozen_policy(folder,device='cpu');del check
    report=dict(mode=mode,games=games,seconds=time.monotonic()-started,optimizer_steps=steps,decisions=decisions,
        configuration=asdict(config),cohorts=cohorts,all_behavior_checks_passed=True,censored=0,
        peak_allocated_mib=torch.cuda.max_memory_allocated()/1048576)
    (work/f'credit-{label}-results.json').write_text(json.dumps(report,indent=2))
    print(json.dumps({k:v for k,v in report.items() if k!='cohorts'}),flush=True)

if __name__=='__main__':
    p=argparse.ArgumentParser();p.add_argument('--work',type=Path,required=True);p.add_argument('--source',type=Path,required=True)
    p.add_argument('--mode',choices=['decision','round'],required=True);p.add_argument('--games',type=int,default=4096)
    p.add_argument('--exploration',type=float,default=0.)
    a=p.parse_args()
    if not 64<=a.games<=8192 or a.games%64:p.error('Use 64..8192 games in complete 64-game cohorts')
    run(a.work,a.source,a.mode,a.games,a.exploration)
