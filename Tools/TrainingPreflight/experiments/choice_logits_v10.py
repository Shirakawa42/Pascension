"""Fused FP32 choice logits and exact mixture-chain-rule backward.

One program per 64-candidate menu. Inputs are authorized host observations only.
Non-choice menus avoid softmax work. No matrix multiplication or low-precision
arithmetic is introduced. CPU reference lives in choice_policy_v8.py.
"""
import torch
import triton
import triton.language as tl


@triton.jit
def _forward(S, M, O, C, L, B, H, E, RI, RS, Y, P, R, A, I, V):
    row=tl.program_id(0)
    j=tl.arange(0,64)
    legal=tl.load(L+row*64+j)!=0
    k=tl.arange(0,16)
    kinds=tl.load(C+row*2048+j[:,None]*32+k[None,:])
    prior=tl.sum(kinds*tl.load(B+k)[None,:],1)
    h=tl.arange(0,4)
    is_volos=tl.sum((tl.load(O+row*3328+112+h)==tl.load(H+h)).to(tl.int32),0)==4
    mode=tl.minimum(3,tl.maximum(0,tl.floor(tl.load(C+row*2048+j*32+25)*128+0.5).to(tl.int32)))
    is_mode=is_volos & legal & (tl.load(C+row*2048+j*32+12)==1)
    mode_score=tl.load(M+row*20+mode)
    ordinal=tl.floor(tl.load(C+row*2048+j*32+25)*128+0.5).to(tl.int32)
    decision_kind=tl.load(O+row*3328+5)
    typed=(decision_kind==0.375)|(decision_kind==0.625)|is_volos
    generic_valid=typed & legal & (tl.load(C+row*2048+j*32+12)==1) & (ordinal>=0) & (ordinal<16)
    generic_score=tl.load(M+row*20+4+tl.minimum(15,tl.maximum(0,ordinal)))
    score=tl.load(S+row*64+j)+tl.where(is_mode,mode_score,0.)+tl.where(generic_valid,generic_score,0.)+prior
    card=tl.minimum(192,tl.maximum(0,tl.floor(tl.load(C+row*2048+j*32+16)*192+0.5).to(tl.int32)))
    slot=tl.load(RI+card)
    ready=tl.load(O+row*3328+tl.maximum(0,slot))
    false_gate=legal & (slot>=0) & (ready==0.) & (tl.load(C+row*2048+j*32+4)==1.)
    score=score-tl.where(false_gate,tl.load(RS),0.)
    relic=legal & (tl.load(C+row*2048+j*32+7)==1)
    destiny=legal & (tl.load(C+row*2048+j*32+6)==1)
    nr=tl.sum(relic.to(tl.float32),0)
    nd=tl.sum(destiny.to(tl.float32),0)
    ar=tl.where(nr>0,tl.load(E),0.)
    ad=tl.where(nd>0,tl.load(E+1),0.)
    active=ar+ad>0
    if active:
        z=tl.where(legal,score,-float('inf'))
        maximum=tl.max(z,0)
        logp=z-maximum-tl.log(tl.sum(tl.exp(z-maximum),0))
        base=logp+tl.log(1.-ar-ad)
        u=tl.where(relic,ar/tl.maximum(nr,1.),0.)+tl.where(destiny,ad/tl.maximum(nd,1.),0.)
        logu=tl.log(u)
        m=tl.maximum(base,logu)
        logq=m+tl.log(tl.exp(base-m)+tl.exp(logu-m))
        result=tl.where(legal,logq,-1.e9)
        probability=tl.where(legal,tl.exp(logp),0.)
        responsibility=tl.where(legal,tl.exp(base-logq),0.)
    else:
        result=tl.where(legal,score,-1.e9)
        probability=tl.full((64,),0.,tl.float32)
        responsibility=tl.full((64,),0.,tl.float32)
    tl.store(Y+row*64+j,result)
    tl.store(P+row*64+j,probability)
    tl.store(R+row*64+j,responsibility)
    tl.store(A+row,active)
    tl.store(I+row*64+j,ordinal)
    tl.store(V+row*64+j,is_mode.to(tl.uint8)+2*generic_valid.to(tl.uint8))


@triton.jit
def _backward(G, P, R, A, I, V, L, DS, DM):
    row=tl.program_id(0)
    j=tl.arange(0,64)
    legal=tl.load(L+row*64+j)!=0
    g=tl.where(legal,tl.load(G+row*64+j),0.)
    active=tl.load(A+row)!=0
    if active:
        weighted=g*tl.load(R+row*64+j)
        ds=weighted-tl.load(P+row*64+j)*tl.sum(weighted,0)
    else:
        ds=g
    ds=tl.where(legal,ds,0.)
    tl.store(DS+row*64+j,ds)
    mode=tl.load(I+row*64+j)
    valid=tl.load(V+row*64+j)
    for index in tl.static_range(4):
        tl.store(DM+row*20+index,tl.sum(tl.where(((valid&1)!=0) & (tl.minimum(3,tl.maximum(0,mode))==index),ds,0.),0))
    for index in tl.static_range(16):
        tl.store(DM+row*20+4+index,tl.sum(tl.where(((valid&2)!=0) & (mode==index),ds,0.),0))


class ChoiceLogits(torch.autograd.Function):
    @staticmethod
    def forward(ctx,scores,modes,obs,candidates,legal,prior,context,exploration,readiness_index,readiness_strength):
        batch=scores.shape[0]
        output=torch.empty_like(scores)
        probability=torch.empty_like(scores)
        responsibility=torch.empty_like(scores)
        active=torch.empty(batch,device=scores.device,dtype=torch.uint8)
        ids=torch.empty_like(scores,dtype=torch.int32)
        valid=torch.empty_like(legal,dtype=torch.uint8)
        _forward[(batch,)](scores,modes,obs,candidates,legal,prior,context,exploration,readiness_index,readiness_strength,
            output,probability,responsibility,active,ids,valid,num_warps=4,enable_fp_fusion=False)
        ctx.save_for_backward(probability,responsibility,active,ids,valid,legal)
        return output

    @staticmethod
    def backward(ctx,gradient):
        p,r,a,ids,valid,legal=ctx.saved_tensors
        gradient=gradient.contiguous()
        ds=torch.empty_like(gradient)
        dm=torch.empty((gradient.shape[0],20),device=gradient.device,dtype=torch.float32)
        _backward[(gradient.shape[0],)](gradient,p,r,a,ids,valid,legal,ds,dm,num_warps=4,enable_fp_fusion=False)
        return ds,dm,None,None,None,None,None,None,None,None


def choice_logits(scores,modes,obs,candidates,legal,prior,context,exploration,readiness_index,readiness_strength):
    return ChoiceLogits.apply(scores,modes,obs,candidates,legal,prior,context,exploration,readiness_index,readiness_strength)
