"""Headless GPU-batched policy/value lookahead experiments. Never trains. Optional isolated statistics publication."""
import argparse, hashlib, json, os, struct, subprocess, sys, tempfile, time, shutil
from pathlib import Path
import numpy as np
import torch
from search_distillation import read_native

WIDTH=5440
CAPACITY=8192

def load_policy(path):
    arrays,width,clip,tanh=read_native(path)
    source=Path(__file__).resolve().parent
    sys.path[:0]=[str(source/'experiments'),str(source)]
    import effect_catalog_v10
    effect_catalog_v10.effect_matrix=lambda:arrays['card_effects'].reshape(193,512)
    from rez_policy import ChoicePolicy
    from learning_model import PolicyConfig
    policy=ChoicePolicy(PolicyConfig(width=width,input_clip=clip,value_tanh=tanh))
    state=policy.state_dict()
    policy.load_state_dict({k:torch.as_tensor(arrays[k].reshape(v.shape),dtype=v.dtype) for k,v in state.items()},strict=True)
    return policy.cuda().eval().requires_grad_(False)

class GpuEvaluator:
    def __init__(self,policy,sizes=(32,64,128,256,512,1024)):
        self.entries={}
        for size in sizes:
            cpu=torch.zeros((size,WIDTH),pin_memory=True)
            gpu=torch.zeros_like(cpu,device='cuda');gpu[:,-64:]=1
            out=torch.empty((size,65),device='cuda');host=torch.empty_like(out,device='cpu',pin_memory=True)
            def compute():
                logits,value=policy(gpu[:,:3328].contiguous(),gpu[:,3328:5376].reshape(size,64,32).contiguous(),gpu[:,5376:].contiguous())
                out[:,:64].copy_(logits.softmax(-1));out[:,64].copy_(value)
            stream=torch.cuda.Stream();stream.wait_stream(torch.cuda.current_stream())
            with torch.cuda.stream(stream):
                for _ in range(3):compute()
            torch.cuda.current_stream().wait_stream(stream);torch.cuda.synchronize()
            graph=torch.cuda.CUDAGraph()
            with torch.cuda.graph(graph):compute()
            self.entries[size]=(cpu,gpu,out,host,graph)
    def evaluate(self,rows,destination):
        for begin in range(0,len(rows),max(self.entries)):
            n=min(len(rows)-begin,max(self.entries));size=next(k for k in self.entries if k>=n)
            cpu,gpu,out,host,graph=self.entries[size]
            np.copyto(cpu.numpy()[:n],rows[begin:begin+n])
            # Fixed padded graph; unused rows remain legal and are never consumed.
            cpu.numpy()[n:,-64:]=1
            gpu.copy_(cpu,non_blocking=True);graph.replay();host.copy_(out,non_blocking=True);torch.cuda.synchronize()
            destination[begin:begin+n]=host.numpy()[:n]

@torch.inference_mode()
def main():
    p=argparse.ArgumentParser(description=__doc__)
    p.add_argument('--policy',type=Path,default=Path('Assets/Resources/AI/shards-policy.bytes'))
    p.add_argument('--reference-policy',type=Path)
    p.add_argument('--reference-hybrid',type=int,choices=[0,1],default=0)
    p.add_argument('--reference-candidates',type=int,default=8)
    p.add_argument('--reference-depth',type=int,default=8)
    p.add_argument('--horizon-turns',type=int,choices=[1,2],default=1)
    p.add_argument('--reference-horizon-turns',type=int,choices=[1,2],default=1)
    p.add_argument('--reference-tactical-guards',type=int,choices=[0,1],default=0)
    p.add_argument('--reference-mixed-resource-plans',type=int,choices=[0,1],default=0)
    p.add_argument('--reference-rollout-styles',type=int,choices=[1,2,3,4],default=1)
    p.add_argument('--experience',action='store_true')
    p.add_argument('--policy-label',help='Display label; defaults to the frozen policy SHA-256 prefix')
    p.add_argument('--host',type=Path,default=Path('Tools/GpuSearchHost/bin/Release/net8.0/GpuSearchHost.dll'))
    p.add_argument('--output',type=Path,required=True)
    p.add_argument('--review-depth',type=int,default=0)
    p.add_argument('--review-turn',type=int,choices=[0,1],default=0)
    p.add_argument('--review-hybrid',type=int,choices=[0,1],default=0)
    p.add_argument('--review-worlds',type=int,default=0)
    p.add_argument('--review-candidates',type=int,default=0)
    p.add_argument('--replay',type=Path)
    p.add_argument('--replay-records',type=Path)
    p.add_argument('--reviews',type=Path)
    p.add_argument('--positions',type=Path)
    p.add_argument('--campaign',type=Path)
    p.add_argument('--catalog',type=Path,default=Path('Tools/TrainingPreflight/results/balance-card-catalog-patch-2026-09-27.json'))
    p.add_argument('--games',type=int,default=80);p.add_argument('--batch',type=int,default=80)
    p.add_argument('--mode',choices=['baseline','paired','both','search-paired','tactics','tactics-baseline','greedy-paired','copy-audit','optimization-audit','hybrid-equivalence','shared-rollout-gpu','statistics','replay','position-review-gpu','outcome-review-gpu'],default='paired')
    p.add_argument('--outcome-actions',default='',help='Comma-separated exact legal action names for an offline outcome comparison')
    p.add_argument('--outcome-hybrid',type=int,choices=[0,1],default=0)
    p.add_argument('--outcome-worlds',type=int,default=32)
    p.add_argument('--outcome-seed',type=int,default=913103)
    p.add_argument('--menu-plans',type=int,choices=[0,1],default=0)
    p.add_argument('--menu-depth',type=int,choices=[0,1,2],default=0)
    p.add_argument('--reference-menu-depth',type=int,choices=[0,1,2],default=0)
    p.add_argument('--reference-menu-plans',type=int,choices=[0,1],default=0)
    p.add_argument('--setup-plans',type=int,choices=[0,1],default=0)
    p.add_argument('--reference-setup-plans',type=int,choices=[0,1],default=0)
    p.add_argument('--optional-choices',type=int,choices=[0,1],default=0)
    p.add_argument('--prior-verification-worlds',type=int,default=0)
    p.add_argument('--reference-prior-verification-worlds',type=int,default=0)
    p.add_argument('--action-prior-cap',type=float,default=0)
    p.add_argument('--reference-action-prior-cap',type=float,default=0)
    p.add_argument('--scry-plans',type=int,choices=[0,1],default=0)
    p.add_argument('--future-scry-plans',type=int,choices=[0,1],default=0)
    p.add_argument('--share-rollout-states',type=int,choices=[0,1],default=0)
    p.add_argument('--reference-share-rollout-states',type=int,choices=[0,1],default=0)
    p.add_argument('--future-scry-depth',type=int,choices=range(5),default=4)
    p.add_argument('--reference-future-scry-depth',type=int,choices=range(5),default=4)
    p.add_argument('--reference-future-scry-plans',type=int,choices=[0,1],default=0)
    p.add_argument('--simplify-wins',type=int,choices=[0,1],default=0)
    p.add_argument('--reference-simplify-wins',type=int,choices=[0,1],default=0)
    p.add_argument('--sequence-repairs',type=int,choices=[0,1],default=0)
    p.add_argument('--reference-sequence-repairs',type=int,choices=[0,1],default=0)
    p.add_argument('--prune-no-effect-plans',type=int,choices=[0,1],default=0)
    p.add_argument('--reference-prune-no-effect-plans',type=int,choices=[0,1],default=0)
    p.add_argument('--reference-scry-plans',type=int,choices=[0,1],default=0)
    p.add_argument('--choice-prior-scale',type=float,default=1)
    p.add_argument('--reference-choice-prior-scale',type=float,default=1)
    p.add_argument('--reference-optional-choices',type=int,choices=[0,1],default=0)
    p.add_argument('--hybrid',type=int,choices=[0,1],default=0)
    p.add_argument('--hybrid-finish-turn',type=int,choices=[0,1],default=0)
    p.add_argument('--end-turn-extension',type=int,default=0)
    p.add_argument('--reference-end-turn-extension',type=int,default=0)
    p.add_argument('--hybrid-skip-forced',type=int,choices=[0,1],default=1)
    p.add_argument('--tactical-guards',type=int,choices=[0,1],default=0)
    p.add_argument('--mixed-resource-plans',type=int,choices=[0,1],default=0)
    p.add_argument('--rollout-styles',type=int,choices=[1,2,3,4],default=1)
    p.add_argument('--copy',choices=['reflection','compiled'],default='reflection')
    p.add_argument('--workers',type=int);p.add_argument('--candidates',type=int,default=8)
    p.add_argument('--depth',type=int,default=8);p.add_argument('--worlds',type=int,default=2)
    p.add_argument('--margin',type=float,default=.04);p.add_argument('--prior',type=float,default=.015)
    p.add_argument('--seed',type=int,default=7998392938210000000)
    p.add_argument('--variants',type=int,default=4)
    p.add_argument('--samples',type=int,default=0)
    p.add_argument('--case')
    p.add_argument('--server-gc',action=argparse.BooleanOptionalAction,default=True)
    p.add_argument('--terminal-nodes',type=int,default=512)
    p.add_argument('--opponent',choices=['network','terminal','greedy'],default='network')
    a=p.parse_args()
    if a.workers is None:a.workers=8
    if not 1<=a.workers<=8:p.error('--workers must be 1..8')
    # One logical CPU per physical core when topology is available. The entire
    # process tree (including GC/inference helpers) inherits this eight-CPU cap.
    available=sorted(os.sched_getaffinity(0));cores={}
    for cpu in available:
        topology=Path(f'/sys/devices/system/cpu/cpu{cpu}/topology')
        key=((topology/'physical_package_id').read_text(),(topology/'core_id').read_text())
        cores.setdefault(key,cpu)
    cpus=list(cores.values())[:a.workers]
    os.sched_setaffinity(0,cpus)
    # NumPy may create idle BLAS helpers during import, before main is entered.
    # Pin those existing threads too; subsequent threads inherit this mask.
    for thread in Path('/proc/self/task').iterdir():
        try:os.sched_setaffinity(int(thread.name),cpus)
        except ProcessLookupError:pass
    os.environ['DOTNET_PROCESSOR_COUNT']=str(len(cpus))
    if a.replay:
        if a.mode!='replay':p.error('--replay requires replay mode')
        a.games=len(json.loads(a.replay.read_text()))
    if a.experience and a.mode!='both':p.error('--experience requires both-seat self-play mode')
    if a.reference_policy and a.mode not in ('search-paired','replay'):p.error('--reference-policy requires search-paired or passive replay analysis')
    if a.campaign and a.mode!='statistics':p.error('--campaign requires --mode statistics')
    root=Path(__file__).resolve().parents[2]
    compiled_sources=[]
    for folder in ('Assets/Scripts/Core','Assets/Scripts/Shards/Engine','Assets/Scripts/Shards/Content','Assets/Scripts/Shards/AI','Tools/GpuSearchHost'):
        compiled_sources.extend((root/folder).rglob('*.cs'))
    compiled_sources=[f for f in compiled_sources if not any(part in ('bin','obj') for part in f.relative_to(root).parts)]
    compiled_sources += [root/'Tools/PolicyVerify'/name for name in ('HeroTactics.cs','RezAudit.cs','SearchDiagnostics.cs')]
    compiled_sources += [root/'Tools/FinalEvaluationHost'/name for name in ('TrainingStatistics.cs','StrategyTrace.cs','VictoryEvidence.cs')]
    local_host=a.host.resolve()==root/'Tools/GpuSearchHost/bin/Release/net8.0/GpuSearchHost.dll'
    if local_host:
        newer=[str(f.relative_to(root)) for f in compiled_sources if f.stat().st_mtime_ns>a.host.stat().st_mtime_ns]
        if newer:p.error('Rebuild GpuSearchHost before running: sources newer than the DLL: '+', '.join(newer))
    a.output=a.output.resolve()
    a.output.mkdir(parents=True,exist_ok=False)
    runtime=a.output/'runtime';runtime.mkdir()
    for dependency in a.host.parent.iterdir():
        if dependency.is_file() and dependency.suffix in ('.dll','.json'):
            shutil.copy2(dependency,runtime/dependency.name)
    frozen_host=runtime/a.host.name
    shutil.copy2(a.policy,runtime/'shards-policy.bytes');a.policy=runtime/'shards-policy.bytes'
    if a.reference_policy:
        shutil.copy2(a.reference_policy,runtime/'reference-policy.bytes');a.reference_policy=runtime/'reference-policy.bytes'
    if a.mode in ('statistics','replay'):shutil.copy2(a.catalog,a.output/'card-catalog.json')
    if a.replay:
        shutil.copy2(a.replay,runtime/'replay.json');a.replay=runtime/'replay.json'
    sources=a.output/'sources';sources.mkdir()
    for source_file in list((root/'Assets/Scripts/Shards/AI').glob('*.cs'))+list((root/'Tools/GpuSearchHost').glob('*.cs'))+[root/'Tools/FinalEvaluationHost'/name for name in ('TrainingStatistics.cs','StrategyTrace.cs','VictoryEvidence.cs')]+[Path(__file__).resolve(),root/'Tools/TrainingPreflight/gpu_search_statistics.py']:
        shutil.copy2(source_file,sources/source_file.name)
    source_hashes={}
    for source_file in compiled_sources:
        relative=source_file.relative_to(root);target=a.output/'compiled-sources'/relative
        target.parent.mkdir(parents=True,exist_ok=True);shutil.copy2(source_file,target)
        source_hashes[str(relative)]=hashlib.sha256(target.read_bytes()).hexdigest()
    (a.output/'compiled-source-manifest.json').write_text(json.dumps(dict(local_host=local_host,
        note='Local builds reject sources newer than the DLL; custom hosts may differ from this working-tree source archive.',sha256=source_hashes),indent=2))
    torch.set_num_threads(1);torch.set_num_interop_threads(1)
    torch.backends.cuda.matmul.allow_tf32=False
    setup=time.monotonic();policy=load_policy(a.policy);evaluator=GpuEvaluator(policy)
    old_evaluator=GpuEvaluator(load_policy(a.reference_policy)) if a.reference_policy else evaluator
    if not a.policy_label:a.policy_label='Frozen policy '+hashlib.sha256(a.policy.read_bytes()).hexdigest()[:12]
    manifest={'schema':'gpu-public-rollout-driver-v1','state':'running','args':{k:str(v) if isinstance(v,Path) else v for k,v in vars(a).items()},'policy_sha256':hashlib.sha256(a.policy.read_bytes()).hexdigest(),'reference_policy_sha256':hashlib.sha256(a.reference_policy.read_bytes()).hexdigest() if a.reference_policy else None,'host_sha256':hashlib.sha256(frozen_host.read_bytes()).hexdigest(),'device':torch.cuda.get_device_name(),'torch':torch.__version__,'unity':False,'training_updates':0,'cpu_affinity':cpus,'cuda_graphs':list(evaluator.entries),'setup_seconds':time.monotonic()-setup}
    (a.output/'manifest.json').write_text(json.dumps(manifest,indent=2)+'\n')
    if a.campaign:
        from bench_common import save_json
        save_json(a.campaign/'balance-statistics-final.json',dict(schema='shards-balance-statistics-v1',state='awaiting_samples',snapshot_id='lookahead-start',scope=dict(label='New lookahead evaluation · awaiting first 100 games',final_evaluation=True),totals=dict(attempted_games=0,resolved_games=0,censored_games=0),rankings={}))
        save_json(a.campaign/'final-statistics-mode.json',dict(enabled=True,evaluation_directory=str(a.output),target_games=a.games))
    with tempfile.TemporaryDirectory(prefix='shards-gpu-search-') as temp:
        mmap_path=Path(temp)/'inference.bin'
        with mmap_path.open('wb') as f:f.truncate(CAPACITY*(WIDTH+65)*4)
        mapped=np.memmap(mmap_path,dtype=np.float32,mode='r+',shape=(CAPACITY*(WIDTH+65),))
        inputs=mapped[:CAPACITY*WIDTH].reshape(CAPACITY,WIDTH);outputs=mapped[CAPACITY*WIDTH:].reshape(CAPACITY,65)
        cmd=['/home/lva/.dotnet/dotnet',str(frozen_host),'--map',str(mmap_path),'--output',str(a.output/'games.json'),'--policy',str(a.policy)]
        for key in ('games','batch','mode','workers','candidates','depth','worlds','margin','prior','seed','variants','terminal_nodes','opponent','samples','copy','review_depth','review_hybrid','review_candidates','review_worlds','hybrid','hybrid_skip_forced','hybrid_finish_turn','reference_hybrid','reference_candidates','reference_depth'):cmd += ['--'+key.replace('_','-'),str(getattr(a,key))]
        if a.reference_policy:cmd += ['--reference-policy',str(a.reference_policy)]
        for key in ('tactical_guards','mixed_resource_plans','reference_tactical_guards','reference_mixed_resource_plans','rollout_styles','reference_rollout_styles','outcome_worlds','outcome_seed','outcome_hybrid','outcome_actions','menu_plans','reference_menu_plans','menu_depth','reference_menu_depth','review_turn','setup_plans','reference_setup_plans','optional_choices','reference_optional_choices','choice_prior_scale','reference_choice_prior_scale','end_turn_extension','reference_end_turn_extension','scry_plans','reference_scry_plans','action_prior_cap','reference_action_prior_cap','prior_verification_worlds','reference_prior_verification_worlds','prune_no_effect_plans','reference_prune_no_effect_plans','horizon_turns','reference_horizon_turns','simplify_wins','reference_simplify_wins','sequence_repairs','reference_sequence_repairs','future_scry_plans','reference_future_scry_plans','future_scry_depth','reference_future_scry_depth','share_rollout_states','reference_share_rollout_states'):
            cmd += ['--'+key.replace('_','-'),str(getattr(a,key))]
        if a.experience:cmd += ['--experience',str(a.output/'experience.bin')]
        if a.replay:cmd += ['--replay',str(a.replay)]
        for key in ('replay_records','reviews','positions'):
            if getattr(a,key):cmd += ['--'+key.replace('_','-'),str(getattr(a,key))]
        if a.case:cmd += ['--case',a.case]
        clock=time.monotonic();last_publication=clock;inference_seconds=0;calls=0
        with (a.output/'host.log').open('w') as log:
            env=dict(os.environ);env["DOTNET_gcServer"]="1" if a.server_gc else "0"
            if a.mode in ("statistics","replay"):env["SHARDS_STRATEGY_TRACE"]="1"
            child=subprocess.Popen(cmd,stdin=subprocess.PIPE,stdout=subprocess.PIPE,stderr=log,env=env)
            try:
                while True:
                    header=child.stdout.read(4)
                    if len(header)!=4:raise RuntimeError(f'Host stopped unexpectedly: {(a.output/"host.log").read_text()[-4000:]}')
                    count=struct.unpack('<i',header)[0]
                    if count==0:break
                    old=count<0;count=abs(count)
                    if not 0<count<=CAPACITY:raise RuntimeError(f'Invalid inference batch {count}')
                    start=time.monotonic();(old_evaluator if old else evaluator).evaluate(inputs[:count],outputs[:count]);inference_seconds+=time.monotonic()-start;calls+=1
                    child.stdin.write(b'\x01');child.stdin.flush()
                    if a.mode=='statistics' and time.monotonic()-last_publication>10:
                        from gpu_search_statistics import publish
                        publish(a.output,a.campaign);last_publication=time.monotonic()
                if child.wait(timeout=30)!=0:raise RuntimeError('Host failed')
            finally:
                if child.poll() is None:child.terminate();child.wait(timeout=30)
        manifest.update(seconds=time.monotonic()-clock,inference_seconds=inference_seconds,inference_calls=calls,simulations_completed=True)
        (a.output/'manifest.json').write_text(json.dumps(manifest,indent=2)+'\n')
        if hashlib.sha256(a.policy.read_bytes()).hexdigest()!=manifest['policy_sha256']:raise RuntimeError('Frozen policy changed')
        if a.reference_policy and hashlib.sha256(a.reference_policy.read_bytes()).hexdigest()!=manifest['reference_policy_sha256']:raise RuntimeError('Frozen reference changed')
        if a.mode=='statistics':
            from gpu_search_statistics import publish
            publish(a.output,a.campaign,final=True)
        manifest.update(completed=True,state='completed',frozen_weights_unchanged=True,statistics_verified=a.mode=='statistics')
        (a.output/'manifest.json').write_text(json.dumps(manifest,indent=2)+'\n')
        summary=json.loads((a.output/'games.json').read_text())
        for key in ('rows','evidence'):
            if key in summary:summary[key]='omitted'
        print(json.dumps(summary),flush=True)

if __name__=='__main__':main()
