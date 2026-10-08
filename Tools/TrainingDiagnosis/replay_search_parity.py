"""Replay a captured real search packet across GPU batch shapes and padding."""
import argparse
import json
from pathlib import Path

import numpy as np
import torch
from search_candidate import Inference, load_frozen_policy, ACTIONS


from search_parity import metrics


@torch.inference_mode()
def run(a):
    torch.set_num_threads(1)
    torch.backends.fp32_precision='ieee';torch.backends.cuda.matmul.fp32_precision='ieee'
    saved=np.load(a.capture);packet=saved['packet'];old=saved['output']
    policy,_=load_frozen_policy(a.policy,device='cuda');infer=Inference(policy)
    reports=[]
    for chunk in (1,16,64,128,512):
        output=[]
        for start in range(0,len(packet),chunk):
            logits,values=infer.forward(torch.as_tensor(packet[start:start+chunk],device='cuda'))
            output.append(torch.cat((logits,values[:,None]),1).cpu().numpy())
        reports.append(dict(kind='eager',chunk=chunk,**metrics(old,np.concatenate(output),packet[:,-ACTIONS:])))
    # Poisoning unused rows must not change consumed results in row-independent layers.
    for poison in ('zero','first_live','last_live'):
        output=np.empty_like(old)
        for start in range(0,len(packet),512):
            n=min(512,len(packet)-start);size=next(k for k in infer.entries if k>=n)
            cpu,gpu,result,host,graph=infer.entries[size]
            if poison=='zero':cpu.zero_()
            else:np.copyto(cpu.numpy(),packet[0 if poison=='first_live' else -1])
            np.copyto(cpu.numpy()[:n],packet[start:start+n]);cpu.numpy()[n:,-ACTIONS:]=1
            gpu.copy_(cpu,non_blocking=True);graph.replay();host.copy_(result,non_blocking=True);torch.cuda.synchronize()
            output[start:start+n]=host.numpy()[:n]
        reports.append(dict(kind='graph',padding=poison,**metrics(old,output,packet[:,-ACTIONS:])))
    result=dict(capture=str(a.capture),reports=reports)
    a.output.write_text(json.dumps(result,indent=2));print(json.dumps(result,indent=2))


if __name__=='__main__':
    p=argparse.ArgumentParser(description=__doc__)
    p.add_argument('--capture',type=Path,required=True);p.add_argument('--policy',type=Path,required=True)
    p.add_argument('--output',type=Path,required=True);run(p.parse_args())
