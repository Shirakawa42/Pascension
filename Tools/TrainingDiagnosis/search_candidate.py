"""Evaluate the full-information learner with public-world search, no learning."""
import argparse
from dataclasses import asdict
import json
import os
from pathlib import Path
import shutil
import signal
import sys
import time

ROOT=Path(__file__).resolve().parents[2]
sys.path[:0]=[str(ROOT/'Tools/DepthTraining'),str(ROOT/'Tools/ZeroDepthTraining'),str(ROOT/'Tools/MatchupBenchmark')]
import numpy as np
import torch
import client
from client import SearchHost,OBS,ACTIONS,FEATURES,ROW
from league import load_frozen_policy,sha256_file,heroes_for_seed
from evaluate import verify_bundle,GameResult,summarize_pairs
from cpu_affinity import select_cpus
from search_parity import metrics,acceptable
from search_experience import SearchExperience
from scry_policy import calibrate,require_calibration,validate_calibration


class Inference:
    def __init__(self,policy,diagnostics=None,scry_finish_bias=0.,scry_temperature=1.,fixed_batch=0,active_transfer=False):
        self.active_transfer=active_transfer
        self.diagnostics=diagnostics
        if fixed_batch not in (0,512):raise ValueError('Fixed inference batch must be disabled or512')
        self.fixed_batch=fixed_batch
        self.scry_finish_bias=float(scry_finish_bias)
        self.scry_temperature=float(scry_temperature)
        validate_calibration(self.scry_temperature,self.scry_finish_bias)
        self.context_count=len(policy.catalog['contexts']);self.scry_context=policy.catalog['contexts'].index('soi.scry')
        self.policy=policy;self.entries={};self.calls=0;self.rows=0;self.contexts=set();self.max_logit_error=0.;self.max_value_error=0.
        self.parity_max={}
        self.seconds=0.;self.value_bins=np.zeros(8,dtype=np.int64)
        policy.cache_frozen_table()
        for size in (1,2,4,8,16,32,64,128,256,512):
            cpu=torch.zeros((size,ROW),pin_memory=True);gpu=cpu.to('cuda');gpu[:,-ACTIONS:]=1
            result=torch.empty((size,65),device='cuda');host=torch.empty_like(result,device='cpu',pin_memory=True)
            def compute():
                logits,values=self.forward(gpu);result[:,:64].copy_(logits);result[:,64].copy_(values)
            stream=torch.cuda.Stream();stream.wait_stream(torch.cuda.current_stream())
            with torch.cuda.stream(stream):
                for _ in range(3):compute()
            torch.cuda.current_stream().wait_stream(stream);torch.cuda.synchronize()
            graph=torch.cuda.CUDAGraph()
            with torch.cuda.graph(graph):compute()
            self.entries[size]=(cpu,gpu,result,host,graph)

    def forward(self,x):
        candidates=x[:,OBS:OBS+ACTIONS*FEATURES].reshape(-1,ACTIONS,FEATURES)
        logits,values=self.policy(x[:,:OBS],candidates,x[:,-ACTIONS:])
        logits=calibrate(logits,x[:,:OBS],candidates,x[:,-ACTIONS:],context_count=self.context_count,
                         scry_context=self.scry_context,temperature=self.scry_temperature,finish_bias=self.scry_finish_bias)
        return logits,values

    def __call__(self,packet,meta):
        started=time.perf_counter()
        output=np.empty((len(packet),65),np.float32)
        for begin in range(0,len(packet),512):
            n=min(512,len(packet)-begin);size=self.fixed_batch or next(k for k in self.entries if k>=n)
            cpu,gpu,result,host,graph=self.entries[size]
            np.copyto(cpu.numpy()[:n],packet[begin:begin+n]);cpu.numpy()[n:,-ACTIONS:]=1
            # Unused rows do not interact with consumed rows. Retain their valid
            # previous inputs, preserving the fixed GEMM shape without sending
            # tens of megabytes of padding for tiny search requests.
            if self.active_transfer:gpu[:n].copy_(cpu[:n],non_blocking=True)
            else:gpu.copy_(cpu,non_blocking=True)
            graph.replay();host[:n].copy_(result[:n],non_blocking=True)
            torch.cuda.synchronize()
            output[begin:begin+n]=host.numpy()[:n]
        context_ids=np.array([int(round(float(r[157])*26)) if r[144] else 0 for r in packet])
        contexts=set(context_ids.tolist())
        if self.calls<3 or self.calls%256==0 or contexts-self.contexts:
            indices=set(np.linspace(0,len(packet)-1,min(64,len(packet)),dtype=int).tolist())
            indices.update(int(np.flatnonzero(context_ids==context)[0]) for context in contexts-self.contexts)
            indices=np.array(sorted(indices))
            eager=self.forward(torch.as_tensor(packet[indices],device='cuda'))
            reference=torch.cat((eager[0],eager[1][:,None]),1).cpu().numpy()
            report=metrics(reference,output[indices],packet[indices,-ACTIONS:])
            for key,value in report.items():self.parity_max[key]=max(self.parity_max.get(key,0),value)
            self.max_logit_error=self.parity_max['raw_logit_error'];self.max_value_error=self.parity_max['value_error']
            if not acceptable(report):
                if self.diagnostics is not None:
                    np.savez_compressed(self.diagnostics/'parity-failure.npz',packet=packet,output=output,indices=indices,eager=reference)
                raise RuntimeError(f'Graph/eager parity failed: {report}')
            self.contexts.update(context_ids[indices].tolist())
        self.calls+=1;self.rows+=len(packet)
        self.value_bins+=np.histogram(output[:,64],bins=[-1.00001,-.99,-.9,-.5,0,.5,.9,.99,1.00001])[0]
        self.seconds+=time.perf_counter()-started
        return output[:,:64],output[:,64]


class IncumbentInference:
    def __init__(self,bundle):
        sys.path.insert(0,str(ROOT/'Tools/TrainingPreflight'))
        from gpu_search import load_policy,GpuEvaluator
        # The legacy trainer also names its module "model". Import its frozen
        # architecture in a private module, then restore the learner binding.
        import importlib.util
        previous=sys.modules.get('model')
        spec=importlib.util.spec_from_file_location('_shards_incumbent_model',ROOT/'Tools/TrainingPreflight/model.py')
        legacy=importlib.util.module_from_spec(spec);sys.modules[spec.name]=legacy
        try:
            sys.modules['model']=legacy;spec.loader.exec_module(legacy)
            self.policy=load_policy(bundle/'shards-policy.bytes')
        finally:
            if previous is None:sys.modules.pop('model',None)
            else:sys.modules['model']=previous
        self.evaluator=GpuEvaluator(self.policy,sizes=(1,2,4,8,16,32,64,128,256,512))
        self.calls=0;self.rows=0;self.seconds=0.

    def __call__(self,packet):
        started=time.perf_counter();result=np.empty((len(packet),65),np.float32)
        self.evaluator.evaluate(packet,result)
        self.calls+=1;self.rows+=len(packet);self.seconds+=time.perf_counter()-started
        return result


@torch.inference_mode()
def run(args):
    os.sched_setaffinity(0,select_cpus(os.sched_getaffinity(0),6));torch.set_num_threads(1)
    torch.backends.fp32_precision='ieee';torch.backends.cuda.matmul.fp32_precision='ieee'
    verify_bundle(args.incumbent,repo_root=ROOT)
    runtime_origin=None
    if args.runtime is not None:
        runtime_origin=json.loads((args.runtime.parent/'plan.json').read_text())
        if sha256_file(args.runtime/'DepthHost.dll')!=runtime_origin['binary_sha256']:
            raise RuntimeError('Frozen replay runtime does not match its original plan')
        defaults=dict(greedy_root=False,greedy_rollout=False,mastery_finish=False,hybrid=False,
                      drop_encoder_caches=False,strategic_coverage=False,rez_coverage=False,future_scry=False,market_plans=False,pooled_inference=False,shared_inference=False,shared_capacity=2048,horizon_turns=1,rollout_styles=4,scry_finish_bias=0.,scry_temperature=1.,fixed_inference_batch=0)
        for name in ('depth','width','worlds','prior','prior_cap','margin',*defaults):
            if getattr(args,name)!=runtime_origin.get(name,defaults.get(name)):
                raise ValueError('Frozen replay setting differs from its original plan: '+name)
        if args.gpu_incumbent!=(runtime_origin.get('incumbent_inference_backend','native')!='native'):
            raise ValueError('Frozen replay incumbent backend differs')
        client.BINARY=args.runtime/'DepthHost.dll'
    args.output.mkdir(parents=True,exist_ok=False);runtime=args.output/'runtime';runtime.mkdir()
    for source in client.BINARY.parent.iterdir():
        if source.suffix in ('.dll','.json'):shutil.copy2(source,runtime/source.name)
    client.BINARY=runtime/'DepthHost.dll'
    os.environ['SHARDS_DEPTH_INCUMBENT']=str(args.incumbent.resolve());os.environ['SHARDS_DEPTH_EVAL']='1'
    os.environ['SHARDS_DIAG_NO_SEARCH']='0'
    os.environ['SHARDS_DEPTH_POLICY_ONLY']='0'
    os.environ['SHARDS_DEPTH_LEARNER_HERO_FILTER']=','.join(args.hero_filter or [])
    os.environ['SHARDS_DEPTH_PRIOR']=str(args.prior);os.environ['SHARDS_DEPTH_PRIOR_CAP']=str(args.prior_cap);os.environ['SHARDS_DEPTH_MARGIN']=str(args.margin)
    os.environ['SHARDS_DEPTH_HORIZON_TURNS']=str(args.horizon_turns)
    os.environ['SHARDS_DEPTH_ROLLOUT_STYLES']=str(args.rollout_styles)
    for name in ('greedy_root','greedy_rollout','mastery_finish','hybrid','drop_encoder_caches','gpu_incumbent','strategic_coverage','rez_coverage','future_scry','market_plans','pooled_inference','shared_inference'):
        os.environ['SHARDS_DEPTH_'+name.upper()]='1' if getattr(args,name) else '0'
    policy,meta=load_frozen_policy(args.policy,device='cuda',allow_external_calibration=True)
    require_calibration(meta,args.scry_temperature,args.scry_finish_bias)
    started=time.monotonic();started_wall=time.time();deadline=started+args.seconds
    goal=json.loads(args.goal_plan.read_text());deadline=min(deadline,started+max(0,goal['deadline_wall']-time.time()))
    infer=Inference(policy,args.output,args.scry_finish_bias,args.scry_temperature,args.fixed_inference_batch);results=[];raw=[];cohorts=[];experience_files=[];stopped=False;transport_stats=None
    incumbent_infer=IncumbentInference(args.incumbent) if args.gpu_incumbent else None
    def stop(*_):
        nonlocal stopped
        stopped=True
    signal.signal(signal.SIGTERM,stop);signal.signal(signal.SIGINT,stop)
    plan=dict(candidate=meta,started_wall=started_wall,incumbent=str(args.incumbent.resolve()),incumbent_manifest_sha256=sha256_file(args.incumbent/'manifest.json'),
        binary_sha256=sha256_file(client.BINARY),games=args.games,depth=args.depth,width=args.width,worlds=args.worlds,
        batch=args.batch,workers=6,seed=args.seed,start_pair=args.start_pair,seconds=args.seconds,smoke=args.smoke,prior=args.prior,prior_cap=args.prior_cap,margin=args.margin,
        hero_filter=args.hero_filter,
        fixed_inference_batch=args.fixed_inference_batch,
        greedy_root=args.greedy_root,greedy_rollout=args.greedy_rollout,mastery_finish=args.mastery_finish,
        hybrid=args.hybrid,rules_unchanged=True,learning_updates=False,
        runtime_origin_plan=runtime_origin,
        capture_roots=args.capture_roots,
        drop_encoder_caches=args.drop_encoder_caches,
        incumbent_inference_backend='gpu_with_native_parity_checks' if args.gpu_incumbent else 'native',
        strategic_coverage=args.strategic_coverage,
        rez_coverage=args.rez_coverage,
        future_scry=args.future_scry,
        market_plans=args.market_plans,
        pooled_inference=args.pooled_inference,
        shared_inference=args.shared_inference,
        shared_capacity=args.shared_capacity,
        horizon_turns=args.horizon_turns,
        rollout_styles=args.rollout_styles,
        scry_finish_bias=args.scry_finish_bias,
        scry_temperature=args.scry_temperature,
        search='ported deployed macro/menu planner with full-information model' if args.hybrid else 'sampled public-world continuations with terminal/critic evaluation',
        source_files={str(p.relative_to(ROOT)):sha256_file(p) for p in [Path(__file__).resolve(),ROOT/'Tools/DepthTraining/client.py',
            *sorted(p for p in (ROOT/'Tools/DepthTraining/Host').rglob('*.cs') if 'obj' not in p.parts and 'bin' not in p.parts),ROOT/'Tools/ZeroDepthTraining/Host/Encoder.cs',ROOT/'Tools/ZeroDepthTraining/model.py',ROOT/'Tools/ZeroDepthTraining/known_top.py',ROOT/'Tools/ZeroDepthTraining/league.py',ROOT/'Tools/FinalEvaluationHost/VictoryEvidence.cs',Path(__file__).with_name('search_parity.py'),Path(__file__).with_name('search_experience.py'),Path(__file__).with_name('audit_goal_evaluation.py'),Path(__file__).with_name('scry_policy.py')]})
    if args.final_protocol is not None:
        from audit_goal_evaluation import verify_declaration
        verify_declaration(json.loads(args.final_protocol.read_text()),plan)
        plan['final_protocol_sha256']=sha256_file(args.final_protocol)
    if args.routed_protocol is not None:
        from audit_routed_evaluation import verify_component
        verify_component(json.loads(args.routed_protocol.read_text()),args.component_name,plan)
        plan['routed_protocol_sha256']=sha256_file(args.routed_protocol)
        plan['component_name']=args.component_name
    (args.output/'plan.json').write_text(json.dumps(plan,indent=2))
    expected_games=sum(heroes_for_seed(args.seed+args.start_pair+i//2)[i%2] in args.hero_filter for i in range(args.games)) if args.hero_filter else args.games
    def publish(reason=None,root_choices=None):
        summary=summarize_pairs(results,planned_pairs=args.games//2)
        if args.hero_filter:
            summary.update(stronger_than_current=False,verdict='Component only; strength is assessed on the complete routed benchmark')
        data=dict(state=reason or 'running',completed=len(results),planned=expected_games,summary=summary,
            source_seed_range_games=args.games,hero_filter=args.hero_filter,
            elapsed_seconds=time.monotonic()-started,elapsed_wall_seconds=time.time()-started_wall,
            inference_calls=infer.calls,inference_rows=infer.rows,
            graph_logit_error=infer.max_logit_error,graph_value_error=infer.max_value_error,
            numerical_parity=infer.parity_max,
            inference_seconds=infer.seconds,value_histogram=infer.value_bins.tolist(),
            value_histogram_edges=[-1,-.99,-.9,-.5,0,.5,.9,.99,1],
            torch_peak_reserved_bytes=torch.cuda.max_memory_reserved(),
            learner_transport=transport_stats,
            incumbent_gpu_inference=None if incumbent_infer is None else dict(calls=incumbent_infer.calls,rows=incumbent_infer.rows,seconds=incumbent_infer.seconds),
            completed_games=raw,cohorts=cohorts,root_choices_in_current_cohort=root_choices,
            experience_files=experience_files,
            victory_causes={role:{cause:sum(g.get('victoryCause')==cause and g['winner']>=0 and
                (g['winner']==g['learner_seat'])==(role=='learner') for g in raw)
                for cause in ('mastery','normal_damage','comet','other_health_loss','concession','unknown')}
                for role in ('learner','incumbent')},
            wins_by_hero={hero:dict(games=sum(g['heroes'][g['learner_seat']]==hero for g in raw),
                wins=sum(g['heroes'][g['learner_seat']]==hero and g['winner']==g['learner_seat'] for g in raw))
                for hero in ('decima','tetra','volos','kosynwu','rez')})
        temporary=args.output/'progress.tmp';temporary.write_text(json.dumps(data,indent=2));temporary.replace(args.output/'progress.json')
        return data
    try:
        with (args.output/'host.log').open('a') as log, SearchHost(batch=args.batch,depth=args.depth,width=args.width,worlds=args.worlds,log=log,shared_capacity=args.shared_capacity) as host:
            transport_stats=host.transport
            host.deadline=deadline;host.stop=lambda:stopped or (args.output/'STOP').exists() or time.time()>=goal['deadline_wall']
            last_update=0;root_rows=0
            def progress(_,count):
                nonlocal last_update,root_rows
                root_rows+=count
                if time.monotonic()-last_update>3:publish(root_choices=root_rows);last_update=time.monotonic()
            for offset in range(0,args.games,args.batch):
                root_rows=0
                experience=SearchExperience(ROW) if args.capture_roots else None
                collection=host.collect(args.seed+args.start_pair+offset//2,infer,progress=progress,retain_rows=False,
                    on_root=experience.append if experience else None,incumbent_infer=incumbent_infer)
                if experience is not None and collection['report']['games']:
                    arrays=experience.arrays(collection['report']['games']);name=f'experience-{offset:06d}.npz'
                    temporary=args.output/(name+'.tmp')
                    with temporary.open('wb') as file:np.savez_compressed(file,**arrays)
                    temporary.replace(args.output/name)
                    experience_files.append(dict(file=name,sha256=sha256_file(args.output/name),rows=len(arrays['actions']),bytes=(args.output/name).stat().st_size))
                cohorts.append({key:value for key,value in collection['report'].items() if key!='games'})
                for game in collection['report']['games']:
                    seat=game['lane']%2;game['learner_seat']=seat
                    if tuple(game['heroes'])!=heroes_for_seed(game['seed']):raise RuntimeError('Hero balance mismatch')
                    results.append(GameResult(game['seed'],seat,game['winner'] if game['completed'] else None,not game['completed']))
                    raw.append(game)
                publish();print(json.dumps(dict(completed=len(results),seconds=time.monotonic()-started)),flush=True)
        data=publish('complete');(args.output/'result.json').write_text(json.dumps(data,indent=2))
    except BaseException as error:
        publish('stopped: '+str(error));raise


if __name__=='__main__':
    p=argparse.ArgumentParser(description=__doc__)
    p.add_argument('--output',type=Path,required=True);p.add_argument('--policy',type=Path,required=True)
    p.add_argument('--incumbent',type=Path,required=True);p.add_argument('--goal-plan',type=Path,required=True)
    p.add_argument('--final-protocol',type=Path,help='Pin and validate a predeclared final gate before collecting any games')
    p.add_argument('--routed-protocol',type=Path,help='Pin a component of a hero-routed final benchmark')
    p.add_argument('--component-name')
    p.add_argument('--hero-filter',nargs='+',choices=('decima','tetra','volos','kosynwu','rez'),help='Evaluate only games assigned to these public learner heroes')
    p.add_argument('--fixed-inference-batch',type=int,choices=(0,512),default=0,help='Keep GEMM shapes identical when filtering public hero lanes')
    p.add_argument('--games',type=int,default=80);p.add_argument('--batch',type=int,default=8)
    p.add_argument('--depth',type=int,default=24);p.add_argument('--width',type=int,default=4);p.add_argument('--worlds',type=int,default=2)
    p.add_argument('--prior',type=float,default=.015);p.add_argument('--prior-cap',type=float,default=6);p.add_argument('--margin',type=float,default=.02)
    p.add_argument('--seconds',type=float,default=900);p.add_argument('--seed',type=int,default=0xA300000000000000//20*20)
    p.add_argument('--start-pair',type=int,default=0,help='Replay offset for a smoke-only diagnostic cohort')
    p.add_argument('--greedy-root',action='store_true');p.add_argument('--greedy-rollout',action='store_true');p.add_argument('--mastery-finish',action='store_true')
    p.add_argument('--hybrid',action='store_true')
    p.add_argument('--runtime',type=Path,help='Verified frozen runtime for reproducing an interrupted cohort')
    p.add_argument('--capture-roots',action='store_true',help='Save lossless real-decision inputs with terminal outcomes for later diagnostics')
    p.add_argument('--drop-encoder-caches',action='store_true',help='Rebuild disposable encoder caches instead of copying them into search worlds')
    p.add_argument('--gpu-incumbent',action='store_true',help='Development-only incumbent inference acceleration with native parity checks')
    p.add_argument('--strategic-coverage',action='store_true',help='Always propose legal Focus and hero abilities in hybrid search')
    p.add_argument('--rez-coverage',action='store_true',help='Always propose Rez power while preserving other heroes\' shortlists')
    p.add_argument('--future-scry',action='store_true',help='Evaluate complete Scry choices immediately following a proposed root action')
    p.add_argument('--market-plans',action='store_true',help='Propose free reroll followed by acquiring an exactly known center top card')
    p.add_argument('--pooled-inference',action='store_true',help='Reuse fully cleared encoder buffers to reduce large-object allocation')
    p.add_argument('--shared-inference',action='store_true',help='Pass full observations through a private shared-memory buffer')
    p.add_argument('--shared-capacity',type=int,choices=(2048,4096,8192),default=2048,help='Shared input rows; larger buffers avoid framed fallback at the cost of CPU RAM')
    p.add_argument('--horizon-turns',type=int,choices=(1,2),default=1,help='Settle only the current turn, or also a simulated opponent reply')
    p.add_argument('--rollout-styles',type=int,choices=(1,2,3,4),default=4,help='One uses only the learned continuation; four also try the legacy heuristic styles')
    p.add_argument('--scry-finish-bias',type=float,default=0.,help='Teacher-calibrated additive logit correction, restricted to legal Scry Finish')
    p.add_argument('--scry-temperature',type=float,default=1.,help='Differentiable softening of legal Scry logits; other contexts and critic unchanged')
    p.add_argument('--smoke',action='store_true');a=p.parse_args()
    if a.hero_filter:a.hero_filter=sorted(set(a.hero_filter))
    if a.final_protocol is not None and (a.hero_filter or a.routed_protocol is not None):p.error('Use a routed protocol for hero-filtered final components')
    if bool(a.routed_protocol)!=bool(a.component_name):p.error('Routed protocol and component name must be supplied together')
    if not 2<=a.batch<=64 or a.batch%2 or a.games%a.batch or (not a.smoke and a.games%40):p.error('Even batch 2..64; complete batches; non-smoke games multiple of 40')
    if not 1<=a.depth<=64 or not 1<=a.width<=64 or not 1<=a.worlds<=16 or not 10<=a.seconds<=10800:p.error('Invalid bounded search configuration')
    if not 0xA000000000000000<=a.seed<0xB000000000000000-a.games or a.seed%20:p.error('Use an aligned held-out seed namespace')
    if a.start_pair<0 or (a.start_pair and not a.smoke):p.error('A replay offset requires --smoke')
    if a.hybrid and a.greedy_rollout:p.error('Hybrid planning supplies its own continuation styles')
    if not np.isfinite(a.scry_finish_bias) or abs(a.scry_finish_bias)>80:p.error('Scry Finish correction must be finite and within 80 logits')
    if not np.isfinite(a.scry_temperature) or not 1<=a.scry_temperature<=64:p.error('Scry temperature must be in [1,64]')
    if (a.strategic_coverage or a.rez_coverage or a.future_scry or a.horizon_turns!=1 or a.rollout_styles!=4) and not a.hybrid:p.error('Strategic coverage, future Scry and rollout configuration require --hybrid')
    run(a)
