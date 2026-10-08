"""Ordered semantic center memory; old learned channels retained exactly."""
import torch
from torch import nn
from choice_policy_v11 import ChoicePolicy as PreviousPolicy

TOP_ID_SLOTS = (702, 894, 1086, 1278)

class ChoicePolicy(PreviousPolicy):
    def __init__(self, *args, **kwargs):
        super().__init__(*args, **kwargs)
        self.core.effect_state = nn.Linear(15 * 64, self.config.width, bias=False)

    def semantic_context(self, obs, table):
        existing = super().semantic_context(obs, table)
        # Slice/stack avoids constructing a CPU advanced-index tensor during
        # CUDA graph capture.
        ids = (torch.stack([obs[:, i] for i in TOP_ID_SLOTS], dim=1) * 192).round().long().clamp(0, 192)
        top = torch.nn.functional.embedding(ids, table)
        top = top * (ids != 0).unsqueeze(-1)
        return torch.cat((existing, top.flatten(1)), dim=1)
