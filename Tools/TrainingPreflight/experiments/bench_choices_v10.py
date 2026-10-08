"""Exclusive, frozen GPU microbenchmark; no optimizer or campaign mutation."""
import copy,json,sys,time
from pathlib import Path
sys.path.insert(0,str(Path(__file__).resolve().parents[1]))
import torch
from bench_common import save_json
from learning_model import LearningPolicy,PolicyConfig
from choice_policy_v10 import ChoicePolicy
from choice_policy_v9 import ChoicePolicy as OldPolicy
from choice_policy_v7 import context_bytes
from migrate_runtime_v10 import prepare, PADDED
from migrate_checkpoint import _inactive_ledger

root=Path('/home/lva/.local/share/shards-training/2026-09-26')
from extended_budget_v10 import install as install_budget
install_budget()
with _inactive_ledger(root/'budget.json'):
    torch.set_num_threads(1)
    payload,report=prepare(root/'main-v9/latest.soicp')
    newer=ChoicePolicy(PolicyConfig(**payload['state']['policy_config']))
    newer.load_state_dict(payload['state']['learner']['policy'])
    older=OldPolicy(newer.config)
    old_state={k:v for k,v in newer.state_dict().items() if k in older.state_dict()}
    for key,columns in PADDED.items():old_state[key]=old_state[key][:,:columns].contiguous()
    older.load_state_dict(old_state)
    models=[older.cuda().eval().requires_grad_(False),newer.cuda().eval().requires_grad_(False)]
    rows=[]
    for batch in (32,64,128,256,2048):
        obs=torch.zeros(batch,3328,device='cuda');c=torch.zeros(batch,64,32,device='cuda');mask=torch.zeros(batch,64,dtype=torch.bool,device='cuda')
        mask[:,:20]=True;c[:,:20,0]=1;c[:,1:4,0]=0;c[:,1:4,7]=1;c[:,4:8,0]=0;c[:,4:8,6]=1
        obs[::2,112:116]=torch.tensor(context_bytes('soi.volos'),device='cuda')
        graphs=[]
        for index,model in enumerate(models):
            x=obs[:,:2816].contiguous() if index==0 else obs
            with torch.inference_mode():
                stream=torch.cuda.Stream();stream.wait_stream(torch.cuda.current_stream())
                with torch.cuda.stream(stream):
                    for _ in range(5):out=model(x,c,mask)
                torch.cuda.current_stream().wait_stream(stream);torch.cuda.synchronize()
                graph=torch.cuda.CUDAGraph()
                with torch.cuda.graph(graph):out=model(x,c,mask)
                graphs.append(graph)
        measurements=[[],[]]
        for repetition in range(6):
            for index in (repetition%2,1-repetition%2):
                start,end=torch.cuda.Event(enable_timing=True),torch.cuda.Event(enable_timing=True)
                start.record()
                for _ in range(100):graphs[index].replay()
                end.record();end.synchronize();measurements[index].append(start.elapsed_time(end)/100)
        import statistics
        base,new=map(statistics.median,measurements)
        rows.append({'batch':batch,'v9_ms':base,'v10_ms':new,'ratio':new/base})
    save_json('Tools/TrainingPreflight/results/v10-effects-gpu-microbench.json',{'scope':'Interleaved captured FP32 forward only, synthetic fixed-shape legal menus; not end-to-end training throughput','rows':rows,'optimizer_updates':0})
    print(json.dumps(rows))
