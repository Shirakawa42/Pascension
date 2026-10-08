"""V8 adds visible state, menu-aware value and categorical pending choices.

All new projections start at zero: checkpoint migration preserves the old
function on its2048-prefix, all learned tensors and every Adam moment.
"""
import torch
from torch import nn
from learning_model import PolicyConfig
from choice_policy_v7 import ChoicePolicy as V7Policy

OBS_DIM = 2560

class ChoicePolicy(V7Policy):
    def __init__(self, *args, **kwargs):
        super().__init__(*args, **kwargs)
        # Runtime actor validation uses2560, but the old learned trunk remains2048.
        if self.core.trunk[0].in_features != 2048:
            self.core.trunk[0] = nn.Linear(2048, self.config.width)
        self.core.information = nn.Linear(512, self.config.width, bias=False)
        self.core.menu_information = nn.Linear(64, self.config.width, bias=False)
        self.core.decision_head = nn.Linear(self.config.width + 512, 16)
        for module in (self.core.information, self.core.menu_information, self.core.decision_head):
            nn.init.zeros_(module.weight)
            if module.bias is not None: nn.init.zeros_(module.bias)

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
