"""Bounded on-policy PPO against the pinned, searched in-game opponent.

Every behavior action is sampled without search or action overrides. Only
complete actual games supply terminal returns. Training scores are descriptive;
checkpoint selection and the fresh final strength evaluation are separate.
"""
import argparse
import copy
from dataclasses import asdict
import json
import os
from pathlib import Path
import shutil
import signal
import sys
import time
from types import SimpleNamespace

from search_candidate import (ROOT, Inference, SearchHost, client, load_frozen_policy,
                              sha256_file, verify_bundle, select_cpus, heroes_for_seed)
import numpy as np
import torch
from train import Learner, Rollout, TrainConfig
from model import cpu_state
from client import OBS, ACTIONS, FEATURES, ROW
from scry_policy import ScryPolicy,unwrap,require_calibration


def configure_learning_scope(policy, scope):
    """Actor-only updates cannot alter any input to the search critic.

    Freezing just the value head is insufficient: shared embeddings and the
    state trunk also determine its output. Keep their exact tensors as a guard.
    """
    base = unwrap(policy)
    if scope not in ('full', 'actor-head', 'actor'):
        raise ValueError('Unknown learning scope')
    base.requires_grad_(scope == 'full')
    base.cached_table = None
    expected = None
    if scope != 'full':
        expected = {'query.weight', 'query.bias', 'candidate_bias.weight'}
        base.query.requires_grad_(True)
        base.candidate_bias.requires_grad_(True)
        if scope == 'actor':
            base.candidate.requires_grad_(True)
            expected |= {'candidate.0.weight', 'candidate.0.bias'}
    names = {name for name, parameter in base.named_parameters() if parameter.requires_grad}
    if expected is not None and names != expected:
        raise ValueError('Unexpected actor-only parameter layout')
    frozen = {name: tensor.detach().cpu().clone() for name, tensor in base.state_dict().items()
              if name not in names} if expected is not None else {}
    metadata = dict(scope=scope, trainable_parameters=sorted(names),
        trainable_parameter_count=sum(p.numel() for p in base.parameters() if p.requires_grad),
        critic_and_representation_frozen=expected is not None,
        frozen_tensor_count=len(frozen))
    return metadata, frozen


def verify_frozen_state(policy, frozen):
    state = unwrap(policy).state_dict()
    for name, expected in frozen.items():
        if name not in state or not torch.equal(state[name].detach().cpu(), expected):
            raise RuntimeError('Actor-only training changed frozen tensor: ' + name)


def owned_rollout(collection):
    """Keep exact owned inputs and the actor's actual log probability/value."""
    rows = collection['rows']
    n = len(rows)
    games = collection['report']['games']
    if not games or not all(g['completed'] for g in games):
        raise ValueError('Unfinished games cannot supply terminal training credit')
    if np.any(collection['improved']):
        raise ValueError('Searched or overridden actions are not on-policy samples')
    lanes = collection['lanes']; seats = collection['actors']
    if not np.array_equal(seats, lanes % 2):
        raise ValueError('Opponent decisions entered the learner rollout')
    if rows.shape != (n, ROW) or not np.isfinite(rows).all():
        raise ValueError('Invalid owned observations')
    actions = collection['targets']
    if np.any(actions < 0) or np.any(actions >= ACTIONS) or not rows[np.arange(n), ROW-ACTIONS+actions].all():
        raise ValueError('Illegal sampled action')
    priors = torch.from_numpy(collection['priors'])
    logp = priors.log_softmax(-1).gather(1, torch.from_numpy(actions).long()[:, None]).numpy()[:, 0]
    packet = np.column_stack((actions, logp, collection['values'])).astype(np.float32)
    if not np.isfinite(packet).all():
        raise ValueError('Nonfinite behavior likelihood or value')
    store = SimpleNamespace(gpu=False, device=torch.device('cpu'), rows=n,
        obs=rows[:, :OBS], candidates=rows[:, OBS:OBS+ACTIONS*FEATURES].reshape(n, ACTIONS, FEATURES),
        mask=rows[:, -ACTIONS:].astype(bool), packet=packet, lanes=lanes, seats=seats,
        rounds=np.rint(rows[:, 2]*100).astype(np.int32))
    rewards = np.zeros((len(games), 2), np.float32)
    if sorted(g['lane'] for g in games) != list(range(len(games))):
        raise ValueError('Invalid lane report')
    for g in games:
        if g['winner'] not in (-1, 0, 1): raise ValueError('Invalid terminal winner')
        if g['winner'] >= 0:
            rewards[g['lane'], g['winner']] = 1
            rewards[g['lane'], 1-g['winner']] = -1
    indices, returns, advantages, excluded = Rollout.seal(store, np.ones(len(games), np.int32), rewards,
                                                        gae_lambda=1.)
    if excluded: raise ValueError('Unexpected excluded complete trajectory')
    return store, indices, returns, advantages


def atomic_json(path, data):
    temp=path.with_suffix(path.suffix+'.tmp');temp.write_text(json.dumps(data, indent=2));temp.replace(path)


def export(folder, policy, learner, base, manifest, provenance):
    folder.mkdir(exist_ok=False)
    payload=copy.deepcopy(base);payload['policy']=cpu_state(unwrap(policy))
    payload['identity']['searched_opponent_training']=provenance
    payload['training']={**base['training'], 'searched_opponent_games':provenance['games'],
                         'searched_opponent_updates':provenance['updates']}
    temp=folder/'policy.tmp';torch.save(payload,temp);temp.replace(folder/'policy.pt')
    meta=copy.deepcopy(manifest)
    for key in ('checkpoint','checkpoint_sha256','checkpoint_payload_sha256'):meta.pop(key,None)
    meta.update(identity=payload['identity'], training=payload['training'],
                bytes=(folder/'policy.pt').stat().st_size, policy_file_sha256=sha256_file(folder/'policy.pt'))
    if isinstance(policy,ScryPolicy):meta['required_inference_calibration']=policy.calibration
    atomic_json(folder/'manifest.json',meta)
    # Retain optimizer and RNG state for a reviewed continuation of this trial.
    temp=folder/'optimizer.tmp'
    torch.save(dict(optimizer=learner.optimizer.state_dict(), torch_rng=torch.get_rng_state(),
                    cuda_rng=torch.cuda.get_rng_state_all(), numpy_rng=np.random.get_state()),temp)
    temp.replace(folder/'optimizer.pt')
    restored,restored_meta=load_frozen_policy(folder,allow_external_calibration=True)
    if isinstance(policy,ScryPolicy):require_calibration(restored_meta,policy.temperature,policy.finish_bias)
    if any(not torch.equal(v,restored.state_dict()[k]) for k,v in payload['policy'].items()):
        raise RuntimeError('Export round-trip changed learned weights')
    return meta['policy_file_sha256']


def run(a):
    os.environ.pop('SHARDS_DEPTH_LEARNER_HERO_FILTER',None)
    os.sched_setaffinity(0,select_cpus(os.sched_getaffinity(0),6));torch.set_num_threads(1)
    torch.backends.fp32_precision='ieee';torch.backends.cuda.matmul.fp32_precision='ieee'
    np.random.seed(202610071);torch.manual_seed(202610071)
    verify_bundle(a.incumbent,repo_root=ROOT)
    goal=json.loads(a.goal_plan.read_text());started=time.monotonic();started_wall=time.time()
    deadline=min(started+a.seconds,started+max(0,goal['deadline_wall']-time.time()))
    if deadline-started<30:raise ValueError('Insufficient authorized trial time')
    a.output.mkdir(parents=True,exist_ok=False);runtime=a.output/'runtime';runtime.mkdir()
    for source in client.BINARY.parent.iterdir():
        if source.suffix in ('.dll','.json'):shutil.copy2(source,runtime/source.name)
    client.BINARY=runtime/'DepthHost.dll'
    for key in ('GREEDY_ROOT','GREEDY_ROLLOUT','MASTERY_FINISH','HYBRID','GPU_INCUMBENT',
                'STRATEGIC_COVERAGE','REZ_COVERAGE','FUTURE_SCRY'):
        os.environ['SHARDS_DEPTH_'+key]='0'
    for key in ('POLICY_ONLY','POOLED_INFERENCE','SHARED_INFERENCE','DROP_ENCODER_CACHES'):
        os.environ['SHARDS_DEPTH_'+key]='1'
    os.environ['SHARDS_DEPTH_INCUMBENT']=str(a.incumbent.resolve())
    os.environ['SHARDS_DEPTH_EVAL']='1';os.environ['SHARDS_DIAG_NO_SEARCH']='0'
    policy,manifest=load_frozen_policy(a.policy,device='cuda',allow_external_calibration=True)
    calibration=require_calibration(manifest,a.scry_temperature,a.scry_finish_bias)
    base=torch.load(a.policy/'policy.pt',map_location='cpu',weights_only=True)
    learning_scope,frozen_state=configure_learning_scope(policy,a.learning_scope)
    if a.scry_temperature!=1. or a.scry_finish_bias!=0.:
        policy=ScryPolicy(policy,temperature=a.scry_temperature,finish_bias=a.scry_finish_bias)
    config=TrainConfig(batch=a.batch,workers=6,width=512,capacity=32768,minibatch=256,epochs=2,
        learning_rate=a.learning_rate,entropy=.002,target_kl=.01,trace_decay=1.,graph=False,
        hero_mode='balanced_random',engine_seed=a.seed)
    config.validate();learner=Learner(policy,config)
    with torch.inference_mode():
        actor=copy.deepcopy(policy).eval().requires_grad_(False);inference=Inference(actor,a.output)
    @torch.inference_mode()
    def infer(packet,meta):return inference(packet,meta)
    sources=[Path(__file__),ROOT/'Tools/DepthTraining/client.py',ROOT/'Tools/ZeroDepthTraining/train.py',
             ROOT/'Tools/ZeroDepthTraining/model.py',ROOT/'Tools/ZeroDepthTraining/league.py',ROOT/'Tools/TrainingDiagnosis/search_candidate.py',Path(__file__).with_name('scry_policy.py')]
    plan=dict(schema='searched-opponent-ppo-v1',source_policy=manifest,started_wall=started_wall,training=asdict(config),
        games=a.games,batch=a.batch,seed=a.seed,seconds=a.seconds,deadline_wall=goal['deadline_wall'],
        incumbent_manifest_sha256=sha256_file(a.incumbent/'manifest.json'),
        binary_sha256=sha256_file(client.BINARY),sources={str(p.relative_to(ROOT)):sha256_file(p) for p in sources},
        learner_search=False,opponent_search=True,terminal_returns=True,policy_overrides=False,
        checkpoint_games=a.checkpoint_games,learning_updates=not a.validate_only,
        validation_only=a.validate_only,strength_proven=False)
    plan['inference_calibration']=calibration
    plan['learning_scope']=learning_scope
    atomic_json(a.output/'plan.json',plan)
    stopped=False
    def stop(*_):
        nonlocal stopped
        stopped=True
    signal.signal(signal.SIGTERM,stop);signal.signal(signal.SIGINT,stop)
    def stop_check():return stopped or (a.output/'STOP').exists() or time.monotonic()>=deadline or time.time()>=goal['deadline_wall']
    raw=[];updates=[];saved=[];decisions=0;last_update=0.;phase='collecting';reason=None
    def publish():
        wins=sum(g['winner']==g['learner_seat'] for g in raw)
        losses=sum(g['winner']>=0 and g['winner']!=g['learner_seat'] for g in raw)
        data=dict(state=reason or phase,completed=len(raw),planned=a.games,training_only=True,
            elapsed_seconds=time.monotonic()-started,elapsed_wall_seconds=time.time()-started_wall,
            decisions=decisions,updates=updates,
            checkpoints=saved,completed_games=raw,strength_proven=False,
            summary=dict(wins=wins,losses=losses,draws=len(raw)-wins-losses,
                         resolved_game_score=None if not raw else (wins+.5*(len(raw)-wins-losses))/len(raw)),
            wins_by_hero={hero:dict(games=sum(g['heroes'][g['learner_seat']]==hero for g in raw),
                wins=sum(g['heroes'][g['learner_seat']]==hero and g['winner']==g['learner_seat'] for g in raw))
                for hero in ('decima','tetra','volos','kosynwu','rez')})
        atomic_json(a.output/'progress.json',data);return data
    def heartbeat(*_):
        nonlocal last_update
        if time.monotonic()-last_update>3:publish();last_update=time.monotonic()
    def save_completed_training():
        if a.validate_only or not raw or (saved and saved[-1]['games']==len(raw)):return
        provenance=dict(parent_policy_sha256=manifest['policy_file_sha256'],
            plan_sha256=sha256_file(a.output/'plan.json'),games=len(raw),updates=len(updates),
            training_seed=a.seed,source=str(a.output.resolve()),strength_proven=False)
        provenance['inference_calibration']=calibration
        provenance['learning_scope']=learning_scope
        verify_frozen_state(policy,frozen_state)
        folder=a.output/f'checkpoint-{len(raw):06d}'
        digest=export(folder,policy,learner,base,manifest,provenance)
        saved.append(dict(games=len(raw),path=str(folder.resolve()),sha256=digest))
    try:
        with (a.output/'host.log').open('a') as log, SearchHost(batch=a.batch,depth=1,width=1,worlds=1,log=log) as host:
            host.deadline=deadline;host.stop=stop_check
            for offset in range(0,a.games,a.batch):
                if stop_check():raise InterruptedError('Stopped before next complete cohort')
                phase='collecting on-policy games';publish()
                with torch.inference_mode():
                    actor.load_state_dict(policy.state_dict(),strict=True);actor.cache_frozen_table()
                collection=host.collect(a.seed+offset//2,infer,progress=heartbeat)
                for g in collection['report']['games']:
                    if tuple(g['heroes'])!=heroes_for_seed(g['seed']):raise RuntimeError('Hero balance mismatch')
                    g['learner_seat']=g['lane']%2
                store,indices,returns,advantages=owned_rollout(collection)
                decisions+=len(indices);phase='verifying and learning';publish()
                policy.train();unwrap(policy).cached_table=None
                if a.validate_only:
                    stats=learner.verify_behavior(store,indices,stop_check=stop_check,heartbeat=heartbeat)
                else:
                    stats=learner.update(store,indices,returns,advantages,stop_check=stop_check,heartbeat=heartbeat)
                    learner.verify_finite_state()
                verify_frozen_state(policy,frozen_state)
                stats['frozen_tensor_check_passed']=bool(frozen_state)
                contexts=policy.catalog['contexts']
                scry=(store.obs[:,144]>.5)&(np.rint(store.obs[:,157]*len(contexts))==contexts.index('soi.scry'))
                stats['scry_decisions']=int(scry.sum())
                stats['scry_finish_choices']=int((scry&(store.candidates[np.arange(store.rows),store.packet[:,0].astype(np.int64),13]>.5)).sum())
                policy.eval();updates.append(dict(games=offset+a.batch,rows=len(indices),**stats))
                raw.extend(collection['report']['games'])
                if not a.validate_only and (len(raw)%a.checkpoint_games==0 or len(raw)==a.games or stop_check()):
                    save_completed_training()
                publish();print(json.dumps(dict(games=len(raw),seconds=time.monotonic()-started,update=stats)),flush=True)
                del collection,store,indices,returns,advantages
        reason='complete';data=publish();atomic_json(a.output/'result.json',data)
    except InterruptedError as error:
        reason='stopped: '+str(error);save_completed_training();publish()
    except BaseException as error:
        reason='failed: '+str(error);publish();raise


if __name__=='__main__':
    p=argparse.ArgumentParser(description=__doc__)
    for name in ('output','policy','incumbent','goal-plan'):p.add_argument('--'+name,type=Path,required=True)
    p.add_argument('--games',type=int,default=800);p.add_argument('--batch',type=int,default=40)
    p.add_argument('--seconds',type=float,default=3600);p.add_argument('--learning-rate',type=float,default=1e-5)
    p.add_argument('--checkpoint-games',type=int,default=200)
    p.add_argument('--seed',type=int,default=0x6B70000000000000//20*20)
    p.add_argument('--scry-temperature',type=float,default=1.)
    p.add_argument('--scry-finish-bias',type=float,default=0.)
    p.add_argument('--learning-scope',choices=('full','actor-head','actor'),default='full',
                   help='Actor scopes preserve the complete critic and its shared representation')
    p.add_argument('--validate-only',action='store_true');a=p.parse_args()
    if not 2<=a.batch<=64 or a.batch%2 or a.games%a.batch or a.games<1 or a.games>4000:p.error('Invalid bounded cohort size')
    if not a.validate_only and (a.batch!=40 or a.games%40):p.error('Training uses complete balanced 40-game cohorts')
    if a.checkpoint_games<a.batch or a.checkpoint_games%a.batch:p.error('Checkpoints require complete cohorts')
    if not 30<=a.seconds<=7200 or not 0<a.learning_rate<=3e-5:p.error('Invalid bounded trial configuration')
    if not 0<=a.seed<2**63-a.games or a.seed%20:p.error('Training must use aligned low-bit seeds')
    run(a)
