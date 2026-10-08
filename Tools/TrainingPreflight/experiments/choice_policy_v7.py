"""Categorical Volos scores and an explicit on-policy legal-choice mixture.

No rules/host changes. Every actor and PPO forward returns the same mixture.
Old policies migrate with a zero residual head and zero exploration, preserving
exact old logits. New learning uses 6% relic and 2% destiny mixture mass only
when the corresponding normal choice is legal. These are menu probabilities,
not percentages of games or externally forced curriculum actions.
"""
import torch
from torch import nn
import variant_runtime as v3

EXPLORATION = (0.06, 0.02)  # relic kind7, destiny kind6

def context_bytes(value):
    hashed = 2166136261
    for char in value:
        hashed = ((hashed ^ ord(char)) * 16777619) & 0xffffffff
    return [((hashed >> (8*i)) & 255)/255 for i in range(4)]


class ChoiceCore(nn.Module):
    def __init__(self, base):
        super().__init__()
        # Preserve all existing names, parameter order, tensors and arithmetic.
        for name in ('trunk', 'query', 'candidate', 'candidate_bias', 'card_embedding', 'value'):
            setattr(self, name, getattr(base, name))
        self.scale = base.scale
        self.volos_head = nn.Linear(base.width, 4)
        nn.init.zeros_(self.volos_head.weight)
        nn.init.zeros_(self.volos_head.bias)

    def scores_and_values(self, obs, candidates):
        state = self.trunk(obs)
        query = self.query(state)
        keys = self.candidate(candidates)
        identity = (candidates[..., 16] * 192).round().long().clamp(0, 192)
        keys = keys + self.card_embedding(identity)
        scores = torch.bmm(keys, query.unsqueeze(-1)).squeeze(-1) * self.scale
        scores = scores + self.candidate_bias(candidates).squeeze(-1)
        return scores.float(), self.value(state).squeeze(-1).float(), self.volos_head(state)

    def forward(self, obs, candidates, legal_mask, mode_ids, mode_mask):
        scores, values, modes = self.scores_and_values(obs, candidates)
        scores = scores + modes.gather(1, mode_ids) * mode_mask
        return scores.masked_fill(~legal_mask, -1.e9), values


class ChoicePolicy(v3.IeeeLearningPolicy):
    def __init__(self, *args, **kwargs):
        super().__init__(*args, **kwargs)
        self.core = ChoiceCore(self.core)
        self.register_buffer('volos_context', torch.tensor(context_bytes('soi.volos'), dtype=torch.float32))
        self.register_buffer('choice_exploration', torch.tensor(EXPLORATION, dtype=torch.float32))

    def forward(self, obs, candidates, legal_mask):
        legal = legal_mask.bool()
        transformed_obs, transformed_candidates = self.transform_inputs(obs, candidates)
        if obs.is_cuda:
            from choice_logits_v7 import choice_logits
            scores, values, modes = self.core.scores_and_values(transformed_obs, transformed_candidates)
            logits = choice_logits(scores, modes, obs, candidates, legal, self.action_kind_bias,
                self.volos_context, self.choice_exploration)
            return logits, values.tanh() if self.config.value_tanh else values
        volos = (obs[:, 112:116] == self.volos_context).all(-1, keepdim=True)
        modes = (candidates[..., 25] * 128).round().long().clamp(0, 3)
        mode_mask = volos & (candidates[..., 12] == 1) & legal
        logits, values = self.core(transformed_obs, transformed_candidates, legal, modes, mode_mask)
        logits = logits + candidates[..., :16].float() @ self.action_kind_bias
        logits = logits.masked_fill(~legal, -1.e9)
        if self.config.value_tanh:
            values = values.tanh()

        # One categorical distribution: alpha_r U(relics) + alpha_d U(destinies)
        # + (1-alpha_r-alpha_d) pi. Absent categories have zero weight. Log-space
        # combination retains legal support even for very small base probabilities.
        relic = legal & (candidates[..., 7] == 1)
        destiny = legal & (candidates[..., 6] == 1)
        nr = relic.sum(-1, keepdim=True).clamp_min(1)
        nd = destiny.sum(-1, keepdim=True).clamp_min(1)
        ar = self.choice_exploration[0] * relic.any(-1, keepdim=True)
        ad = self.choice_exploration[1] * destiny.any(-1, keepdim=True)
        logp = logits.log_softmax(-1) + torch.log1p(-ar-ad)
        # Clamp before log avoids log(0) in autograd, while the explicit mask
        # gives unavailable/zero-mass components exact zero probability.
        lr = (ar.clamp_min(1.e-30).log() - nr.float().log()).expand_as(logits)
        ld = (ad.clamp_min(1.e-30).log() - nd.float().log()).expand_as(logits)
        lr = lr.masked_fill(~(relic & (ar > 0)), -torch.inf)
        ld = ld.masked_fill(~(destiny & (ad > 0)), -torch.inf)
        mixture = torch.logaddexp(torch.logaddexp(logp, lr), ld)
        logits = torch.where((ar+ad) > 0, mixture, logits).masked_fill(~legal, -1.e9)
        return logits.float(), values.float()
