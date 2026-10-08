"""Small candidate policies for fresh Shards training preflight experiments.

The numeric observation contract is owned by the adapter. This module has no
access to game state. Its benchmark inputs are synthetic, not game experience.
"""

from __future__ import annotations

import math

import torch
from torch import Tensor, nn


class CandidatePolicy(nn.Module):
    """Encode state once and score a variable, externally validated legal menu.

    ``bilinear`` avoids a [batch, actions, width] activation completely.
    ``encoded`` adds a nonlinear shared candidate projection of at most 64 dims.
    ``embedded`` additionally represents categorical card identity explicitly;
    it is the capacity baseline. ``bilinear`` is only a throughput floor.
    Padded/terminal rows must not be submitted: every row needs a legal action.
    The adapter validates that contract before entering the GPU hot path.
    """

    def __init__(
        self,
        obs_dim: int = 2048,
        action_dim: int = 32,
        width: int = 256,
        scorer: str = "embedded",
    ) -> None:
        super().__init__()
        if scorer not in ("bilinear", "encoded", "embedded"):
            raise ValueError(f"Unknown scorer: {scorer}")
        self.obs_dim = obs_dim
        self.action_dim = action_dim
        self.width = width
        self.scorer = scorer
        self.trunk = nn.Sequential(
            nn.Linear(obs_dim, width), nn.SiLU(),
            nn.Linear(width, width), nn.SiLU(),
        )
        key_dim = action_dim if scorer == "bilinear" else min(width, 64)
        self.query = nn.Linear(width, key_dim)
        self.candidate = (
            nn.Identity() if scorer == "bilinear" else
            nn.Sequential(nn.Linear(action_dim, key_dim), nn.SiLU())
        )
        self.candidate_bias = nn.Linear(action_dim, 1, bias=False)
        self.card_embedding = nn.Embedding(193, key_dim) if scorer == "embedded" else None
        self.value = nn.Linear(width, 1)
        self.scale = 1.0 / math.sqrt(key_dim)

    def forward(self, obs: Tensor, candidates: Tensor, legal_mask: Tensor):
        state = self.trunk(obs)
        query = self.query(state)
        keys = self.candidate(candidates)
        if self.card_embedding is not None:
            # Adapter v1: slot 16 = stable categorical card ID / 192; 0 is none.
            identity = (candidates[..., 16] * 192).round().long().clamp(0, 192)
            keys = keys + self.card_embedding(identity)
        # bmm avoids materializing the broadcast product [B, A, key_dim].
        scores = torch.bmm(keys, query.unsqueeze(-1)).squeeze(-1) * self.scale
        scores = scores + self.candidate_bias(candidates).squeeze(-1)
        logits = scores.float().masked_fill(~legal_mask, -1.0e9)
        return logits, self.value(state).squeeze(-1).float()


def validate_legal_mask(legal_mask: Tensor) -> None:
    """Boundary check, outside compiled/captured code; terminal rows are omitted."""
    if legal_mask.dtype != torch.bool or legal_mask.ndim != 2:
        raise ValueError("legal_mask must be a rank-2 bool tensor")
    if not bool(legal_mask.any(dim=1).all()):
        raise ValueError("All-invalid action row: omit terminal/padded requests")


def sample_actions(logits: Tensor, values: Tensor) -> Tensor:
    """Masked categorical draw, selected log-prob, value in one FP32 packet.

    Exponential/Gumbel-max sampling is CUDA-graph compatible and avoids an
    extra softmax+multinomial launch. FP32 action IDs are exact for these menus.
    The output lives only until the next graph replay unless the caller copies it.
    """
    noise = torch.empty_like(logits).exponential_().clamp_min_(1.0e-20)
    actions = (logits - noise.log()).argmax(dim=-1)
    logp = logits.log_softmax(dim=-1).gather(1, actions.unsqueeze(1)).squeeze(1)
    return torch.stack((actions.float(), logp, values), dim=1)


def ppo_loss(
    logits: Tensor,
    values: Tensor,
    actions: Tensor,
    old_logp: Tensor,
    advantages: Tensor,
    returns: Tensor,
    clip_ratio: float = 0.2,
    entropy_coefficient: float = 0.01,
) -> Tensor:
    """PPO-shaped scalar objective; all sensitive arithmetic stays in FP32.

    This is shared math, not a training algorithm: rollout ownership, opponent
    versions, rewards, truncations and advantage construction belong upstream.
    """
    logp_all = logits.float().log_softmax(dim=-1)
    logp = logp_all.gather(1, actions.long().unsqueeze(1)).squeeze(1)
    ratio = (logp - old_logp.float()).exp()
    advantage = advantages.float()
    policy = -torch.minimum(
        ratio * advantage,
        ratio.clamp(1.0 - clip_ratio, 1.0 + clip_ratio) * advantage,
    ).mean()
    value = 0.5 * (values.float() - returns.float()).square().mean()
    entropy = -(logp_all.exp() * logp_all).sum(dim=-1).mean()
    return policy + 0.5 * value - entropy_coefficient * entropy
