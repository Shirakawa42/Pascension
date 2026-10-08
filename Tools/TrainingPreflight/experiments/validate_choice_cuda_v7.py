"""Exclusive GPU forward/backward parity against the Torch reference; no updates."""
import sys,json
from pathlib import Path
sys.path.insert(0,str(Path(__file__).resolve().parents[1]))
import torch
from bench_common import save_json
from migrate_checkpoint import _inactive_ledger
from choice_policy_v7 import ChoicePolicy,context_bytes
from choice_logits_v7 import choice_logits

def reference(scores,modes,obs,c,legal,prior,context,epsilon):
    ismode=(obs[:,112:116]==context).all(-1,keepdim=True)&(c[:,:,12]==1)&legal
    ids=(c[:,:,25]*128).round().long().clamp(0,3)
    logits=scores+modes.gather(1,ids)*ismode+c[:,:,:16]@prior
    logits=logits.masked_fill(~legal,-1.e9)
    relic=legal&(c[:,:,7]==1);dest=legal&(c[:,:,6]==1)
    ar=epsilon[0]*relic.any(-1,keepdim=True);ad=epsilon[1]*dest.any(-1,keepdim=True)
    nr=relic.sum(-1,keepdim=True).clamp_min(1);nd=dest.sum(-1,keepdim=True).clamp_min(1)
    base=logits.log_softmax(-1)+torch.log1p(-ar-ad)
    lr=(ar.clamp_min(1.e-30).log()-nr.float().log()).expand_as(logits).masked_fill(~(relic&(ar>0)),-torch.inf)
    ld=(ad.clamp_min(1.e-30).log()-nd.float().log()).expand_as(logits).masked_fill(~(dest&(ad>0)),-torch.inf)
    return torch.where(ar+ad>0,torch.logaddexp(torch.logaddexp(base,lr),ld),logits).masked_fill(~legal,-1.e9)

root=Path('/home/lva/.local/share/shards-training/2026-09-26')
with _inactive_ledger(root/'budget.json'):
    torch.set_num_threads(1);torch.manual_seed(770926)
    torch.backends.fp32_precision='ieee';torch.backends.cuda.matmul.fp32_precision='ieee'
    context=torch.tensor(context_bytes('soi.volos'),device='cuda')
    prior=torch.tensor([1,0,0,.5,0,0,0,0,0,0,-1,-12,0,0,-1,0],device='cuda',dtype=torch.float32)
    rows=[]
    for batch in (1,32,64,128,256,2048):
        for magnitude in (1,25,200):
            for eps in ([0.,0.],[.06,.02],[.06,0.],[0.,.02]):
                s=(torch.randn(batch,64,device='cuda')*magnitude).requires_grad_();m=torch.randn(batch,4,device='cuda',requires_grad=True)
                obs=torch.randn(batch,2048,device='cuda');obs[::3,112:116]=context
                c=torch.zeros(batch,64,32,device='cuda');k=torch.randint(0,16,(batch,64),device='cuda');c.scatter_(2,k[:,:,None],1.)
                c[:,:,25]=torch.randint(0,4,(batch,64),device='cuda')/128
                legal=torch.rand(batch,64,device='cuda')>.4;legal[:,0]=True
                epsilon=torch.tensor(eps,device='cuda')
                actual=choice_logits(s,m,obs,c,legal,prior,context,epsilon)
                expected=reference(s,m,obs,c,legal,prior,context,epsilon)
                error=float((actual-expected).abs().max())
                logp_error=float((actual.log_softmax(-1)-expected.log_softmax(-1)).abs().max())
                weight=torch.randn_like(actual)
                ga=torch.autograd.grad((actual*weight).sum(),(s,m),retain_graph=True)
                ge=torch.autograd.grad((expected*weight).sum(),(s,m))
                grad_error=max(float((a-e).abs().max()) for a,e in zip(ga,ge))
                for a in ga:assert torch.isfinite(a).all()
                torch.testing.assert_close(actual,expected,atol=1.e-4,rtol=1.e-5)
                for a,e in zip(ga,ge):torch.testing.assert_close(a,e,atol=2.e-4,rtol=2.e-4)
                rows.append({'batch':batch,'scale':magnitude,'epsilon':eps,'logit_error':error,'logp_error':logp_error,'gradient_error':grad_error})
    save_json('Tools/TrainingPreflight/results/v7-cuda-choice-parity.json',{'passed':True,'cases':rows,'optimizer_updates':0,
        'scope':'72 random-mask/extreme-logit cases comparing custom FP32 forward/backward against unfused Torch math on GPU'})
    print(json.dumps({'cases':len(rows),'max_logit_error':max(x['logit_error'] for x in rows),'max_logp_error':max(x['logp_error'] for x in rows),'max_gradient_error':max(x['gradient_error'] for x in rows)}))
