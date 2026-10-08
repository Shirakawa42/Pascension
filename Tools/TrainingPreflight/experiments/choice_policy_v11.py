"""V11: identical V10 policy math; use embedding backward for repeated card lookups."""
import torch
from choice_policy_v10 import ChoicePolicy as V10Policy

class ChoicePolicy(V10Policy):
    def semantic_context(self, obs, table):
        # Ignore legacy channel padding: three non-card features occupy each192-slot channel.
        zones = obs[:,128:1856].reshape(-1,9,192)[:,:,:189]
        known = torch.matmul(zones, table[1:190]).flatten(1)
        enemy = torch.matmul(obs[:,2560:2749], table[1:190])
        relic_ids = (obs[:,3005:3008]*192).round().long().clamp(0,192)
        relics = torch.nn.functional.embedding(relic_ids,table).sum(1)
        return torch.cat((known,enemy,relics),dim=1)

    def forward(self, obs, candidates, legal_mask):
        legal = legal_mask.bool()
        x, c = self.transform_inputs(obs, candidates)
        identity = (candidates[...,16]*192).round().long().clamp(0,192)
        table = self.semantic_table()
        embeddings = (self._embedding_cache[identity] if not self.training and not self.core.effect_encoder[0].weight.requires_grad and self._embedding_cache is not None
                      else self.core.card_embedding(identity) + torch.nn.functional.embedding(identity,table))
        pooled = (embeddings*legal.unsqueeze(-1)).sum(1) / legal.sum(1,keepdim=True).clamp_min(1)
        # Inject before the existing final nonlinearity: visible menu/card facts
        # can interact with board context, rather than only add a fixed bias.
        hidden = self.core.trunk[1](self.core.trunk[0](x[:,:2048]))
        state = self.core.trunk[3](self.core.trunk[2](hidden)
            + self.core.information(x[:,2048:]) + self.core.menu_information(pooled)
            + self.core.effect_state(self.semantic_context(obs,table)))
        query = self.core.query(state)
        keys = self.core.candidate(c) + embeddings
        scores = torch.bmm(keys,query.unsqueeze(-1)).squeeze(-1)*self.core.scale
        scores = scores + self.core.candidate_bias(c).squeeze(-1)
        values = self.core.value(state).squeeze(-1).float()
        modes = torch.cat((self.core.volos_head(state),self.core.decision_head(torch.cat((state,x[:,2048:]),-1))),-1)
        if obs.is_cuda:
            from choice_logits_v10 import choice_logits
            logits = choice_logits(scores,modes,obs,candidates,legal,self.action_kind_bias,self.volos_context,self.choice_exploration,self.readiness_index,self.readiness_strength)
        else:
            slots = self.readiness_index[identity]
            ready = obs.gather(1, slots.clamp_min(0))
            false_gate = (slots >= 0) & (ready == 0) & (candidates[...,4] == 1) & legal
            scores = scores - self.readiness_strength * false_gate
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
