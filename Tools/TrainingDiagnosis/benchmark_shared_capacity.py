"""Replay identical real search roots through small and large shared buffers."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import time

import numpy as np
import torch
from search_candidate import Inference, client, load_frozen_policy, verify_bundle, select_cpus, sha256_file


class RootLimit(Exception):
    pass


@torch.inference_mode()
def run(a):
    os.sched_setaffinity(0,select_cpus(os.sched_getaffinity(0),6));torch.set_num_threads(1)
    torch.backends.fp32_precision='ieee';torch.backends.cuda.matmul.fp32_precision='ieee'
    source=json.loads((a.source/'plan.json').read_text())
    binary=a.source/'runtime/DepthHost.dll'
    if sha256_file(binary)!=source['binary_sha256']:raise ValueError('Frozen runtime changed')
    client.BINARY=binary
    incumbent=Path(source['incumbent']);verify_bundle(incumbent)
    policy,meta=load_frozen_policy(a.policy,device='cuda',allow_external_calibration=True)
    if meta['policy_file_sha256']!=source['candidate']['policy_file_sha256']:raise ValueError('Policy differs from source experiment')
    a.output.mkdir(exist_ok=False)
    os.environ.update(SHARDS_DEPTH_INCUMBENT=str(incumbent),SHARDS_DEPTH_EVAL='1',SHARDS_DIAG_NO_SEARCH='0',
        SHARDS_DEPTH_POLICY_ONLY='0',SHARDS_DEPTH_LEARNER_HERO_FILTER='',SHARDS_DEPTH_GPU_INCUMBENT='0')
    for name in ('hybrid','drop_encoder_caches','strategic_coverage','rez_coverage','future_scry','market_plans','pooled_inference','shared_inference','greedy_root','greedy_rollout','mastery_finish'):
        os.environ['SHARDS_DEPTH_'+name.upper()]='1' if source.get(name,False) else '0'
    for name,default in [('prior',.015),('prior_cap',6),('margin',.02),('horizon_turns',1),('rollout_styles',4)]:
        os.environ['SHARDS_DEPTH_'+name.upper()]=str(source.get(name,default))
    os.environ['SHARDS_DEPTH_SHARED_INFERENCE']='1'
    infer=Inference(policy,a.output,source.get('scry_finish_bias',0.),source.get('scry_temperature',1.),512)
    reports=[]
    for index,capacity in enumerate((2048,8192,8192,2048)):
        inputs=hashlib.sha256();outputs=hashlib.sha256();roots=[];frames=0;rows=0
        def prediction(packet,meta):
            nonlocal frames,rows
            inputs.update(np.asarray([len(packet)],dtype='<u8'));inputs.update(packet)
            if meta is not None:inputs.update(meta)
            z,v=infer(packet,meta);outputs.update(np.ascontiguousarray(z));outputs.update(np.ascontiguousarray(v))
            frames+=1;rows+=len(packet)
            return z,v
        def real_root(packet,meta,actions,improved):
            roots.append(dict(observation_sha256=hashlib.sha256(packet).hexdigest(),
                meta=meta.tolist(),actions=actions.tolist(),improved=improved.tolist()))
            if len(roots)>=a.roots:raise RootLimit()
        started=time.monotonic()
        with (a.output/f'host-{index}.log').open('w') as log:
            with client.SearchHost(batch=60,depth=source['depth'],width=source['width'],worlds=source['worlds'],log=log,shared_capacity=capacity) as host:
                host.deadline=time.monotonic()+180
                try:host.collect(a.seed,prediction,retain_rows=False,on_root=real_root)
                except RootLimit:pass
                if len(roots)!=a.roots:raise RuntimeError('Did not reach the prescribed real-root count')
                transport=host.transport.copy()
        item=dict(capacity=capacity,seconds=time.monotonic()-started,frames=frames,rows=rows,
            inputs_sha256=inputs.hexdigest(),outputs_sha256=outputs.hexdigest(),roots=roots,transport=transport)
        reports.append(item)
        (a.output/'progress.json').write_text(json.dumps(dict(runs=reports),indent=2)+'\n')
        print(json.dumps({k:v for k,v in item.items() if k!='roots'}),flush=True)
    reference=reports[0]
    for item in reports[1:]:
        for key in ('inputs_sha256','outputs_sha256','roots','frames','rows'):
            if item[key]!=reference[key]:raise RuntimeError('Shared-capacity replay changed '+key)
    if not all(r['transport']['framed_rows']>0 for r in reports if r['capacity']==2048):
        raise RuntimeError('Replay never exercised framed fallback')
    old=np.mean([r['seconds'] for r in reports if r['capacity']==2048])
    new=np.mean([r['seconds'] for r in reports if r['capacity']==8192])
    result=dict(passed=True,seed=a.seed,policy_sha256=meta['policy_file_sha256'],binary_sha256=source['binary_sha256'],
        fixed_gpu_batch=512,source_experiment=str(a.source),real_roots_per_run=60*a.roots,
        runs=reports,mean_2048_seconds=old,mean_8192_seconds=new,speedup=old/new,
        additional_shared_memory_bytes=(8192-2048)*client.ROW*4,
        limitations='Matched-root replay, not complete-game strength evidence. Timings share the same six cores with an ongoing evaluation; whole-job speedup is not established.',
        running_final_modified=False)
    (a.output/'result.json').write_text(json.dumps(result,indent=2)+'\n')
    print(json.dumps({k:v for k,v in result.items() if k!='runs'},indent=2))


if __name__=='__main__':
    p=argparse.ArgumentParser(description=__doc__)
    for name in ('source','policy','output'):p.add_argument('--'+name,type=Path,required=True)
    p.add_argument('--roots',type=int,default=2);p.add_argument('--seed',type=int,required=True)
    a=p.parse_args()
    if not 1<=a.roots<=4:p.error('Bounded to1..4 root batches')
    run(a)
