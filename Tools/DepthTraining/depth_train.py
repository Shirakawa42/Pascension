"""Bounded search expert iteration, resuming learned 512-width weights."""
from pathlib import Path
import argparse, collections, copy, hashlib, json, os, signal, sys, time
HERE=Path(__file__).resolve().parent
sys.path.insert(0,str(HERE.parent/'ZeroDepthTraining'));sys.path.insert(0,str(HERE))
import numpy as np
import torch
from league import load_frozen_policy, run_neural_arena, sha256_file, POLICY_SCHEMA
from model import Policy,PolicyConfig
from client import SearchHost,BINARY
from learning import inputs,update
from dataclasses import asdict

def atomic(path,value):
    path=Path(path);tmp=path.with_suffix(path.suffix+'.tmp')
    with tmp.open('w') as f:json.dump(value,f,indent=2,allow_nan=False);f.flush();os.fsync(f.fileno())
    os.replace(tmp,path)

def save(path,payload):
    path=Path(path);tmp=path.with_suffix('.tmp')
    with tmp.open('wb') as f:torch.save(payload,f);f.flush();os.fsync(f.fileno())
    os.replace(tmp,path)

def freeze(policy,directory,identity,counters):
    directory=Path(directory);directory.mkdir()
    payload={'schema':POLICY_SCHEMA,'identity':identity,'training':counters,'catalog':policy.catalog,'policy_config':asdict(policy.config),
        'policy':{k:v.detach().cpu().clone() for k,v in policy.state_dict().items()}}
    save(directory/'policy.pt',payload)
    atomic(directory/'manifest.json',{'schema':POLICY_SCHEMA,'file':'policy.pt','identity':identity,'training':counters,
        'bytes':(directory/'policy.pt').stat().st_size,'policy_file_sha256':sha256_file(directory/'policy.pt')})
    return directory

def main():
    parser=argparse.ArgumentParser();parser.add_argument('--config',type=Path,required=True);args=parser.parse_args()
    config=json.loads(args.config.read_text());run=Path(config['run']);run.mkdir(exist_ok=True,parents=True)
    from supervisor import remaining
    start=time.monotonic();wall=time.time();deadline=start+remaining(config)
    stopping=False
    def stop_signal(*_):
        nonlocal stopping
        stopping=True
    signal.signal(signal.SIGTERM,stop_signal);signal.signal(signal.SIGINT,stop_signal)
    def stopped():return stopping or time.monotonic()>=deadline or (args.config.parent/'STOP').exists()
    torch.set_num_threads(1);torch.manual_seed(config['seed']);np.random.seed(config['seed']%(2**32))
    policy,origin=load_frozen_policy(config['initial_policy'],device='cuda');policy.requires_grad_(True)
    optimizer=torch.optim.AdamW(policy.parameters(),lr=config['learning_rate'])
    from host import BINARY as ZERO_BINARY
    from train import source_fingerprint
    identity={'schema':'shards-depth-training-v1','source_fingerprint':source_fingerprint(),'host_sha256':sha256_file(ZERO_BINARY),
        'training_host_sha256':sha256_file(BINARY),'origin':origin,'configuration':config}
    games=generations=optimizer_steps=decisions=censored_total=0;next_seed=config['seed'];history=collections.deque(maxlen=100000);last_eval=0
    checkpoint=run/'latest.pt'
    if checkpoint.exists():
        state=torch.load(checkpoint,map_location='cpu',weights_only=False)
        if state['identity']!=identity:raise RuntimeError('Depth checkpoint identity differs')
        policy.load_state_dict(state['policy'],strict=True);optimizer.load_state_dict(state['optimizer'])
        games=state['games'];generations=state['generations'];optimizer_steps=state['optimizer_steps'];decisions=state['decisions'];next_seed=state['next_seed'];last_eval=state['last_eval']
        censored_total=state.get('censored_total',0);history.extend(state['history']);torch.set_rng_state(state['torch_rng']);np.random.set_state(state['numpy_rng']);torch.cuda.set_rng_state_all(state['cuda_rng'])
    def counts():return dict(games=games,generations=generations,optimizer_steps=optimizer_steps,decisions=decisions)
    if not (run/'initial').exists():freeze(policy,run/'initial',identity,counts())
    if not (run/'champion.json').exists():atomic(run/'champion.json',{'path':str(run/'initial'),'games':games,'trials':0,'promotions':0})
    def checkpoint_now():
        save(checkpoint,dict(identity=identity,policy=policy.state_dict(),optimizer=optimizer.state_dict(),**counts(),next_seed=next_seed,
            history=list(history),last_eval=last_eval,censored_total=censored_total,torch_rng=torch.get_rng_state(),cuda_rng=torch.cuda.get_rng_state_all(),numpy_rng=np.random.get_state()))
    checkpoint_now();base_games=games;last_status=0;last_learning={};nodes_total=0;changed_total=0;decisions_run=0
    def publish(state='training',**extra):
        nonlocal last_status
        elapsed=time.monotonic()-start
        data={'state':state,'mode':'depth expert iteration','parameters':sum(p.numel() for p in policy.parameters()),'depth':config['depth'],'candidates':config['width'],'worlds':config['worlds'],
            **counts(),'censored_games':censored_total,'inherited_training_games':origin['training']['games'],'games_per_second':(games-base_games)/max(elapsed,1),'estimated_games_per_hour':3600*(games-base_games)/max(elapsed,1),
            'rolling_games':len(history),'mean_rounds':float(np.mean([g['rounds'] for g in history])) if history else None,
            'hero_games':dict(collections.Counter(hero for g in history for hero in g['heroes'])),
            'search_nodes':nodes_total,'search_overrides':changed_total,'search_decisions':decisions_run,'learning':last_learning,
            'updated_wall':time.time(),'remaining_seconds':max(0,min(deadline-time.monotonic(),config['hard_deadline_wall']-time.time())),
            'hard_deadline_wall':config['hard_deadline_wall'],'champion':json.loads((run/'champion.json').read_text()),**extra}
        if (run/'strength-latest.json').exists():
            strength=json.loads((run/'strength-latest.json').read_text());data['strength']={k:strength[k] for k in ('summary','rounds','integrity_verified','elapsed_seconds') if k in strength}
        if (run/'incumbent-evaluation.json').exists():data['incumbent_summary']=json.loads((run/'incumbent-evaluation.json').read_text())['summary']
        atomic(run/'status.json',data);last_status=time.monotonic()
    @torch.no_grad()
    def infer(packet,meta):
        policy.eval();logits,value=policy(*inputs(packet,'cuda'))
        return logits.cpu().numpy(),value.cpu().numpy()
    publish()
    try:
        with (run/'host.log').open('ab',buffering=0) as log,SearchHost(batch=config['batch'],depth=config['depth'],width=config['width'],worlds=config['worlds'],log=log) as host:
            host.deadline=deadline;host.stop=stopped
            while not stopped():
                if deadline-time.monotonic()<=config.get('final_eval_seconds',0):break
                # Reserve and persist seeds before collection; an interrupted cohort
                # is discarded and never silently counted/learned a second time.
                seed=next_seed;next_seed+=config['batch'];checkpoint_now()
                def progress(steps,live):
                    if time.monotonic()-last_status>5:publish(cohort_steps=steps,active_lanes=live)
                collection=host.collect(seed,infer,progress=progress)
                censored_total+=sum(not g['completed'] for g in collection['report']['games'])
                if censored_total>=4:
                    atomic(run/'censor-failure.json',collection['report']);(args.config.parent/'STOP').write_text('Four censored games: investigate before resuming.')
                    raise RuntimeError('Censor threshold reached; stopped before learning')
                controls=json.loads((run/'controls.json').read_text()) if (run/'controls.json').exists() else {}
                lr=float(controls.get('learning_rate',config['learning_rate']))
                if not 1e-6<=lr<=5e-5:raise ValueError('Learning rate outside tested control range')
                for group in optimizer.param_groups:group['lr']=lr
                last_learning=update(policy,optimizer,collection,epochs=config['epochs']);last_learning['learning_rate']=lr
                completed=[g for g in collection['report']['games'] if g['completed']]
                history.extend(completed);games+=len(completed);generations+=1;decisions+=len(collection['rows']);optimizer_steps+=last_learning['optimizer_steps']
                nodes_total+=collection['report']['nodes'];changed_total+=collection['report']['overrides'];decisions_run+=collection['report']['decisions']
                with (run/'metrics.jsonl').open('a') as f:f.write(json.dumps({'wall':time.time(),**counts(),**last_learning,**{k:v for k,v in collection['report'].items() if k!='games'}})+'\n')
                del collection
                checkpoint_now();publish()
                # Real strength check: fresh seed-paired games against an evolving
                # retained champion. Round length is displayed, never rewarded.
                slot=int((time.time()-config['start_wall'])//3600)
                if slot>last_eval and deadline-time.monotonic()>360:
                    last_eval=slot;checkpoint_now();publish('evaluating')
                    candidate=freeze(policy,run/f'candidate-{games}',identity,counts())
                    champion=json.loads((run/'champion.json').read_text())
                    result=run_neural_arena(candidate,champion['path'],run/f'arena-{slot}-{games}.json',games=1600,batch=40,workers=8,
                        seed_base=((0xA200000000000000//20)+1)*20+slot*100000,policy_seed=config['seed']+slot,max_seconds=120,stop_check=stopped,deadline_monotonic=deadline)
                    atomic(run/'strength-latest.json',result)
                    champion['trials']+=1
                    summary=result.get('summary',result)
                    # Promotion uses the arena's paired confidence interval only;
                    # absent/incomplete evidence cannot replace the champion.
                    lower=summary.get('paired_hoeffding_score_interval',[-1,1])[0]
                    if lower>.5 and summary.get('evaluation_finished') and result.get('integrity_verified'):
                        champion.update(path=str(candidate),games=games,promotions=champion['promotions']+1,trial_at_promotion=champion['trials'])
                    atomic(run/'champion.json',champion);checkpoint_now();publish()
        if not stopped() and config.get('incumbent_bundle'):
            from evaluate_incumbent import challenge
            checkpoint_now();publish('evaluating_incumbent')
            candidate=freeze(policy,run/f'final-{games}',identity,counts())
            def final_progress(result):
                if time.monotonic()-last_status>5:publish('evaluating_incumbent',incumbent_summary=result['summary'])
            challenge(candidate,config['incumbent_bundle'],run/'incumbent-evaluation.json',deadline=deadline,stop=stopped,progress=final_progress)
    except InterruptedError:
        pass
    except BaseException:
        publish('failed');raise
    checkpoint_now();publish('stopped' if stopped() else 'complete')
    print(json.dumps({'finished':True,**counts()}),flush=True)

if __name__=='__main__':main()
