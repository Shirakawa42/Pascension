"""CPU evaluation-only soft activation timing intervention before exploration mixture.
No campaign updates, weight mutation or legal action deletion.
"""
import argparse
from collections import Counter,defaultdict
import hashlib,json,sys,time
from pathlib import Path
from unittest.mock import patch
sys.path.insert(0,str(Path(__file__).resolve().parents[1]))
import cpu_shadow_eval as shadow
from choice_policy_v8 import ChoicePolicy
from strategy_coverage_probe import immediate_gate
from hero_curriculum.balanced_evaluation import run_balanced_matrix,evaluation_plan
import numpy as np
import torch
from bench_common import save_json
from wire_v8 import LearningHost as V8Host

class SoftTimingPolicy(ChoicePolicy):
    def soft_penalty(self,obs,candidates,legal):
        if obs.is_cuda:raise RuntimeError('CPU evaluation only')
        result=torch.zeros_like(candidates[...,0])
        if not self.penalty:return result
        o=obs.detach().numpy();c=candidates.detach().numpy()
        for lane,slot in zip(*np.nonzero(legal.detach().numpy() & (c[...,4]==1))):
            code=round(float(c[lane,slot,16])*192)-1
            if 0<=code<len(self.cards) and immediate_gate(self.cards[code],o[lane]) is False:
                result[lane,slot]=self.penalty
        return result

    def forward(self, obs, candidates, legal_mask):
        legal = legal_mask.bool()
        x, c = self.transform_inputs(obs, candidates)
        identity = (candidates[...,16]*192).round().long().clamp(0,192)
        embeddings = self.core.card_embedding(identity)
        pooled = (embeddings*legal.unsqueeze(-1)).sum(1) / legal.sum(1,keepdim=True).clamp_min(1)
        # Inject before the existing final nonlinearity: visible menu/card facts
        # can interact with board context, rather than only add a fixed bias.
        hidden = self.core.trunk[1](self.core.trunk[0](x[:,:2048]))
        state = self.core.trunk[3](self.core.trunk[2](hidden)
            + self.core.information(x[:,2048:]) + self.core.menu_information(pooled))
        query = self.core.query(state)
        keys = self.core.candidate(c) + embeddings
        scores = torch.bmm(keys,query.unsqueeze(-1)).squeeze(-1)*self.core.scale
        scores = scores + self.core.candidate_bias(c).squeeze(-1)
        scores = scores - self.soft_penalty(obs, candidates, legal)
        values = self.core.value(state).squeeze(-1).float()
        modes = torch.cat((self.core.volos_head(state),self.core.decision_head(torch.cat((state,x[:,2048:]),-1))),-1)
        if obs.is_cuda:
            from choice_logits_v8 import choice_logits
            logits = choice_logits(scores,modes,obs,candidates,legal,self.action_kind_bias,self.volos_context,self.choice_exploration)
        else:
            volos = (obs[:,112:116]==self.volos_context).all(-1,keepdim=True)
            ordinal = (candidates[...,25]*128).round().long()
            mode = (candidates[...,12]==1)&legal
            old_index = ordinal.clamp(0,3)
            logits = scores + modes[:,:4].gather(1,old_index)*(mode&volos)
            typed = ((obs[:,5:6]==3/8)|(obs[:,5:6]==5/8)|volos)
            new_index = ordinal.clamp(0,15)
            logits = logits + modes[:,4:].gather(1,new_index)*(mode&typed&(ordinal>=0)&(ordinal<16))
            logits = logits + candidates[...,:16].float()@self.action_kind_bias
            logits = logits.masked_fill(~legal,-1.e9)
            relic=legal&(candidates[...,7]==1); destiny=legal&(candidates[...,6]==1)
            nr=relic.sum(-1,keepdim=True).clamp_min(1);nd=destiny.sum(-1,keepdim=True).clamp_min(1)
            ar=self.choice_exploration[0]*relic.any(-1,keepdim=True);ad=self.choice_exploration[1]*destiny.any(-1,keepdim=True)
            logp=logits.log_softmax(-1)+torch.log1p(-ar-ad)
            lr=(ar.clamp_min(1.e-30).log()-nr.float().log()).expand_as(logits).masked_fill(~(relic&(ar>0)),-torch.inf)
            ld=(ad.clamp_min(1.e-30).log()-nd.float().log()).expand_as(logits).masked_fill(~(destiny&(ad>0)),-torch.inf)
            logits=torch.where((ar+ad)>0,torch.logaddexp(torch.logaddexp(logp,lr),ld),logits).masked_fill(~legal,-1.e9)
        return logits.float(),values.tanh() if self.config.value_tanh else values


def main():
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--checkpoint',type=Path,required=True)
    parser.add_argument('--output',type=Path,required=True)
    args=parser.parse_args()
    if args.output.exists() or args.output.with_suffix('.plan.json').exists():parser.error('Use fresh output paths')
    shadow.configure_cpu();shadow.configure_variant('v8')
    selection,_=shadow.frozen_selections(args.checkpoint,'learner',args.checkpoint,'learner')
    if selection.metadata['checkpoint_generation']!=8541:raise RuntimeError('Predeclared baseline is8541')
    baseline=shadow.checkpoints.materialize_policy(selection,'cpu')
    policy=SoftTimingPolicy(baseline.config).eval().requires_grad_(False)
    policy.load_state_dict(baseline.state_dict(),strict=True)
    cards=selection.catalog['cards'];policy.cards=cards;policy.penalty=1.5
    seed,sampling=0x8500000000000000,850926
    declaration={'declared_wall':time.time(),'plan':evaluation_plan(pairs_per_cell=16,seed=seed,sampling_seed=sampling),
        'policy':selection.metadata,'intervention':'A subtracts1.5 from pre-mixture kind4 exhaust scores with exact false immediate_gate. B unchanged. Every original legal action remains possible.',
        'screen':{'requires_complete':True,'games':640,'maximum_censors':0,'minimum_point_score':.45,
            'interpretation':'Predeclared coarse rejection screen, NOT formal noninferiority or superiority.'},
        'helper_sha256':hashlib.sha256(Path(__file__).read_bytes()).hexdigest(),
        'predicate_sha256':hashlib.sha256((Path(__file__).parent/'strategy_coverage_probe.py').read_bytes()).hexdigest(),
        'baseline_forward_sha256':hashlib.sha256((Path(__file__).parent/'choice_policy_v8.py').read_bytes()).hexdigest(),
        'optimizer_updates':0,'cuda_initialized':False}
    save_json(args.output.with_suffix('.plan.json'),declaration)
    # Contract includes a false gate and a legal relic; row1 has a true gate.
    ob=torch.zeros(2,2560);ca=torch.zeros(2,64,32);mask=torch.zeros(2,64,dtype=torch.bool)
    mask[:,:3]=True;ca[:,0,4]=1;ca[:,0,16]=(cards.index('paradigm_shift')+1)/192
    ca[:,1,7]=1;ca[:,1,16]=1/192;ca[:,2,10]=1
    ob[1,51]=.05;ob[1,52]=.05
    with torch.inference_mode():
        mix=policy.choice_exploration.clone();policy.penalty=0
        a=policy(ob,ca,mask);b=baseline(ob,ca,mask)
        for x,y in zip(a,b):torch.testing.assert_close(x,y,rtol=0,atol=0)
        policy.choice_exploration.zero_();raw=policy(ob,ca,mask)[0]
        expected_raw=raw.clone();expected_raw[0,0]-=1.5
        expected=(1-float(mix[0]))*expected_raw.softmax(-1);expected[:,1]+=float(mix[0])
        policy.choice_exploration.copy_(mix);policy.penalty=1.5
        actual=policy(ob,ca,mask)[0].softmax(-1)
        torch.testing.assert_close(actual,expected,rtol=1e-6,atol=1e-7)
        assert bool((actual[mask]>0).all())
        torch.testing.assert_close(actual[1],b[0][1].softmax(-1),rtol=0,atol=0)
    before=[shadow.checkpoints.policy_hash(p) for p in (policy,baseline)]
    counts=[Counter(),Counter()];pending=defaultdict(dict);examples=[[],[]]
    original=shadow.ObservedHost.advance_active;parity_rows=0
    def observe(host,actions,active):
        nonlocal parity_rows
        for lane in np.flatnonzero(active):
            seat=int(host.seats[lane]);role=int(seat!=host.seat_a)
            ob=host.obs[lane];cs=host.candidates[lane];legal=np.flatnonzero(host.mask[lane]==1)
            chosen=cs[actions[lane]];kind=int(chosen[:16].argmax())
            def card_id(c):
                code=round(float(c[16])*192)-1
                return cards[code] if 0<=code<len(cards) else None
            key=(host.seed+int(lane),host.seat_a,seat);turn=round(float(ob[2])*100)
            for card,event in list(pending[key].items()):
                if turn!=event['round']:pending[key].pop(card);continue
                if ob[4]==0 and immediate_gate(card,ob) is True:
                    owned=ob[128+3*192+cards.index(card)]>.05
                    available=any(cs[s,4]==1 and card_id(cs[s])==card for s in legal)
                    if owned and not available:
                        counts[role]['false_then_true_before_end:'+card]+=1
                        if len(examples[role])<12:examples[role].append(dict(event,card=card,game=key))
                    pending[key].pop(card)
            counts[role]['acting_decisions']+=1
            for slot in legal:
                if cs[slot,4]!=1:continue
                card=card_id(cs[slot]);gate=immediate_gate(card,ob)
                if gate is not None:counts[role]['offered_gate_'+str(gate).lower()+':'+card]+=1
            if kind==4:
                card=card_id(chosen);gate=immediate_gate(card,ob)
                if gate is not None:counts[role]['selected_gate_'+str(gate).lower()+':'+card]+=1
                if gate is False:pending[key][card]={'round':turn}
            if kind==10:
                counts[role]['end_turns']+=1
                if any(cs[s,0]==1 for s in legal):counts[role]['end_with_legal_hand_play']+=1
                pending[key].clear()
            if parity_rows<64 and parity_rows%2==role:
                with torch.inference_mode():
                    oo=torch.from_numpy(ob.copy())[None];cc=torch.from_numpy(cs.copy())[None];mm=torch.from_numpy(host.mask[lane].copy()).bool()[None]
                    saved=policy.penalty;policy.penalty=0
                    try:
                        aa=policy(oo,cc,mm);bb=baseline(oo,cc,mm)
                        for x,y in zip(aa,bb):torch.testing.assert_close(x,y,rtol=0,atol=0)
                    finally:policy.penalty=saved
                parity_rows+=1
        result=original(host,actions,active)
        for lane in np.flatnonzero(active & (host.done!=0)):
            for seat in (0,1):pending.pop((host.seed+int(lane),host.seat_a,seat),None)
        return result
    with patch.object(shadow.ObservedHost,'advance_active',observe):
        report=run_balanced_matrix(policy,baseline,pairs_per_cell=16,seed=seed,sampling_seed=sampling,batch=16,max_seconds=650,host_factory=V8Host)
    report['intervention']=declaration['intervention']
    report['plan']['intervention']['subsequent_behavior']=declaration['intervention']
    report.update(declaration=declaration,counts={'soft_timing':dict(counts[0]),'unchanged':dict(counts[1])},
        examples={'soft_timing':examples[0],'unchanged':examples[1]},
        count_scope='Actual active acting-policy rows only. Same-turn missed gates require a later normal priority state, owned card and unavailable exhaust; evidence of missed opportunity, not causal win value.',
        frozen_weights_unchanged=before==[shadow.checkpoints.policy_hash(p) for p in (policy,baseline)],
        validation={'synthetic_exact_zero_penalty_parity':True,'synthetic_premixture_contract':True,'all_legal_support_preserved':True,'real_exact_zero_penalty_rows':parity_rows},
        cuda_initialized=torch.cuda.is_initialized())
    assert report['frozen_weights_unchanged'] and not torch.cuda.is_initialized()
    report['passes_predeclared_screen']=bool(report.get('complete') and report.get('censored_games')==0 and report.get('score_a',0)>=.45)
    save_json(args.output,report)
    print(json.dumps({k:report.get(k) for k in ('complete','games','score_a','score_bound_95','censored_games','passes_predeclared_screen','seconds')},indent=2))

if __name__=='__main__':main()
