"""One current-information policy forward pass; no search or simulated branches."""
from __future__ import annotations

import copy
from dataclasses import asdict, dataclass
import math

import numpy as np
import torch
from torch import nn


def dimensions(catalog):
    def field(snake, pascal):
        return int(catalog.get(snake, catalog.get(pascal)))
    return field("obs_dim", "ObsDim"), field("max_actions", "MaxActions"), field("action_dim", "ActionDim")


@dataclass
class PolicyConfig:
    width: int = 512
    key_width: int = 64


def optional_menu_logits(logits, candidates, mask, epsilon):
    """A probability floor only where selecting and declining are both legal.

    The returned distribution is used by both sampling and PPO likelihoods.
    A zero coefficient selects the original logits bit-for-bit, including for
    legacy archived opponents. Priority actions (including concession) are unchanged.
    """
    legal = mask.bool()
    eligible = ((candidates[..., 12] > .5) & legal).any(-1) & ((candidates[..., 13] > .5) & legal).any(-1)
    logp = logits.log_softmax(-1)
    uniform = -legal.sum(-1, keepdim=True).clamp_min(1).float().log()
    mixed = torch.logaddexp(logp + torch.log1p(-epsilon), uniform + epsilon.log())
    mixed = mixed.masked_fill(~legal, -1.e9)
    return torch.where((eligible & (epsilon > 0)).unsqueeze(-1), mixed, logits)


def legacy_exploration_state(state):
    """Add a disabled buffer to old snapshots without changing their behavior."""
    return state if "optional_exploration" in state else {**state, "optional_exploration": torch.zeros(())}


class Policy(nn.Module):
    def __init__(self, catalog, config=None):
        super().__init__()
        self.catalog = copy.deepcopy(catalog)
        self.config = config or PolicyConfig()
        self.register_buffer("optional_exploration", torch.zeros(()))
        obs_dim, _, action_dim = dimensions(catalog)
        width, key = self.config.width, self.config.key_width
        cards = catalog.get("card_ids", catalog.get("cards", []))
        self.card_count = len(cards)
        self.card_column = int(catalog.get("candidate_card_column", 16))
        self.card_scale = float(catalog.get("candidate_card_scale", 192))
        self.histograms = catalog.get("histograms", catalog.get("histogram_descriptors", []))
        self.bag_layout = None
        if self.histograms:
            first = self.histograms[0]
            stride = (self.histograms[1]["offset"] - first["offset"]) if len(self.histograms) > 1 else first["length"]
            if all(zone["offset"] == first["offset"] + index * stride
                   and zone["length"] == self.card_count and zone.get("scale", 1) == first.get("scale", 1)
                   for index, zone in enumerate(self.histograms)):
                self.bag_layout = (int(first["offset"]), int(stride), len(self.histograms), float(first.get("scale", 1)))
        self.card_embedding = nn.Embedding(self.card_count + 1, key, padding_idx=0)
        descriptors = catalog.get("card_features")
        self.effect_projection = None
        if descriptors is not None:
            table = torch.as_tensor(descriptors, dtype=torch.float32)
            if table.ndim != 2 or table.shape[0] != self.card_count or not bool(torch.isfinite(table).all()):
                raise ValueError("Static card effect catalog has invalid dimensions/values")
            self.register_buffer("effect_table", torch.cat((torch.zeros_like(table[:1]), table)))
            self.effect_projection = nn.Linear(table.shape[1], key, bias=False)
        self.trunk = nn.Sequential(nn.Linear(obs_dim, width), nn.SiLU(), nn.Linear(width, width), nn.SiLU())
        self.known_top_supported = catalog.get("observation_schema") == "shards-zero-depth-observation-v2"
        self.known_key_width = min(26, key)
        if self.known_top_supported:
            # Schema v2 reserves inputs 178:256 as zero. Reuse their weights,
            # preserving parameter count/order and every existing Adam slot.
            self.register_buffer("known_top_anchor", self.trunk[0].weight[:, 178:178 + 3*self.known_key_width].detach().clone())
            self.register_buffer("known_top_enabled", torch.zeros(()))
        self.query = nn.Linear(width, key)
        self.candidate = nn.Sequential(nn.Linear(action_dim, key), nn.SiLU())
        self.candidate_bias = nn.Linear(action_dim, 1, bias=False)
        self.value = nn.Linear(width, 1)
        self.zone_projection = nn.Linear(len(self.histograms) * key, width, bias=False) if self.histograms else None
        self.menu_projection = nn.Linear(key, width, bias=False)
        # A mild starting preference; every legal action remains available.
        self.register_buffer("kind_prior", torch.tensor([.8, 0, 0, .3, 0, 0, 0, 0, 0, 0, -.8, -14, 0, 0, -1, 0], dtype=torch.float32))
        nn.init.orthogonal_(self.query.weight, gain=.01)
        nn.init.zeros_(self.query.bias)
        nn.init.zeros_(self.candidate_bias.weight)
        nn.init.zeros_(self.value.weight)
        nn.init.zeros_(self.value.bias)
        self.register_buffer("cached_table", None, persistent=False)

    def _load_from_state_dict(self, state_dict, prefix, *args, **kwargs):
        key = prefix + "optional_exploration"
        if key not in state_dict:
            state_dict[key] = torch.zeros_like(self.optional_exploration)
        coefficient = float(state_dict[key])
        if not math.isfinite(coefficient) or not 0 <= coefficient <= .1:
            raise ValueError("Optional exploration must be finite and in [0,.1]")
        if self.known_top_supported:
            anchor, enabled = prefix + "known_top_anchor", prefix + "known_top_enabled"
            if anchor not in state_dict and enabled not in state_dict:
                # Legacy opponents retain exactly their original behavior.
                state_dict[anchor] = state_dict[prefix + "trunk.0.weight"][:, 178:178 + 3*self.known_key_width].detach().clone()
                state_dict[enabled] = torch.zeros_like(self.known_top_enabled)
        super()._load_from_state_dict(state_dict, prefix, *args, **kwargs)

    def embedding_table(self):
        if self.cached_table is not None:
            return self.cached_table
        table = self.card_embedding.weight
        if self.effect_projection is not None:
            table = table + self.effect_projection(self.numeric(self.effect_table))
        return table

    @torch.no_grad()
    def cache_frozen_table(self):
        # Actors refresh in place: graph replay keeps these addresses stable.
        previous = self.cached_table
        self.cached_table = None
        table = self.embedding_table()
        if previous is None:
            self.cached_table = table.detach().clone()
        else:
            previous.copy_(table)
            self.cached_table = previous

    @staticmethod
    def numeric(x):
        # No clipping: arbitrarily large public counts remain distinguishable.
        return x.float().sign() * x.float().abs().log1p()

    def forward(self, obs, candidates, mask):
        ids = (candidates[..., self.card_column] * self.card_scale).round().long()
        embedding_table = self.embedding_table()
        identities = torch.nn.functional.embedding(ids, embedding_table)
        hidden = self.trunk(self.numeric(obs))
        if self.zone_projection is not None:
            if self.bag_layout is not None:
                start, stride, zones, scale = self.bag_layout
                counts = obs[:, start:start + zones * stride].reshape(obs.shape[0] * zones, stride)[:, :self.card_count]
                pooled_zones = (counts @ embedding_table[1:]) * scale
                pooled_zones = pooled_zones.reshape(obs.shape[0], -1)
            else:
                pooled_zones = torch.cat([
                    (obs[:, int(zone["offset"]):int(zone["offset"]) + int(zone["length"])] * float(zone.get("scale", 1)))
                    @ embedding_table[1:1 + int(zone["length"])] for zone in self.histograms], -1)
            hidden = hidden + self.zone_projection(pooled_zones)
        pooled = (identities * mask.unsqueeze(-1)).sum(1) / mask.sum(1, keepdim=True).clamp_min(1)
        hidden = hidden + self.menu_projection(pooled)
        if self.known_top_supported:
            from known_top import center_top_ids
            known_ids = center_top_ids(obs, self.card_count, self.card_scale)
            known = torch.nn.functional.embedding(known_ids, embedding_table)[:, :, :self.known_key_width]
            known = (known * (known_ids != 0).unsqueeze(-1)).reshape(obs.shape[0], -1)
            projection = torch.nn.functional.linear(
                known, self.trunk[0].weight[:, 178:178 + 3*self.known_key_width] - self.known_top_anchor)
            hidden = hidden + projection * self.known_top_enabled
        hidden = torch.nn.functional.silu(hidden)
        keys = self.candidate(self.numeric(candidates)) + identities
        logits = torch.bmm(keys, self.query(hidden).unsqueeze(-1)).squeeze(-1) / math.sqrt(self.config.key_width)
        logits = logits + self.candidate_bias(self.numeric(candidates)).squeeze(-1)
        logits = logits + candidates[..., :16] @ self.kind_prior
        logits = logits.float().masked_fill(~mask.bool(), -1.e9)
        return optional_menu_logits(logits, candidates, mask, self.optional_exploration), self.value(hidden).squeeze(-1).tanh().float()


class Actor:
    """Fixed-batch frozen GPU actor. Returned packets own their CPU storage."""
    def __init__(self, policy, batch, *, graph=True, device=None, compiled=False, packed=False):
        self.device = torch.device(device or next(policy.parameters()).device)
        self.cuda = self.device.type == "cuda"
        self.policy = copy.deepcopy(policy).to(self.device).eval().requires_grad_(False)
        self.policy.cache_frozen_table()
        self.batch = batch
        obs_dim, actions, action_dim = dimensions(policy.catalog)
        self.obs_host = torch.empty(batch, obs_dim, pin_memory=self.cuda)
        self.candidates_host = torch.empty(batch, actions, action_dim, pin_memory=self.cuda)
        self.mask_host = torch.empty(batch, actions, pin_memory=self.cuda)
        self.obs = torch.zeros_like(self.obs_host, device=self.device)
        self.candidates = torch.zeros_like(self.candidates_host, device=self.device)
        self.mask = torch.ones_like(self.mask_host, device=self.device)
        self.output_host = torch.empty(batch, 3, pin_memory=self.cuda)
        self.compiled_enabled = bool(compiled and self.cuda)
        self.packed_enabled = bool(packed and self.cuda)
        self.forward = (torch.compile(self.policy, fullgraph=True, dynamic=False,
                                      options={"triton.cudagraphs": False})
                        if self.compiled_enabled else self.policy)
        self.packed_transport = None
        if self.packed_enabled:
            from packed_transport import PackedTransport
            self.packed_transport = PackedTransport(policy.catalog, batch, self.device, graph=graph)
        self.all_rows = np.arange(batch, dtype=np.int64)
        self.graph = None
        self.packet = None
        if graph and not self.cuda:
            raise ValueError("CUDA graphs require a CUDA device")
        if graph or self.compiled_enabled:
            stream = torch.cuda.Stream(device=self.device)
            stream.wait_stream(torch.cuda.current_stream(self.device))
            with torch.cuda.stream(stream), torch.inference_mode():
                for _ in range(3):
                    self.packet = self.compute()
            torch.cuda.current_stream(self.device).wait_stream(stream)
            torch.cuda.synchronize(self.device)
            if graph:
                self.graph = torch.cuda.CUDAGraph()
                with torch.inference_mode(), torch.cuda.graph(self.graph):
                    self.packet = self.compute()

    def compute(self):
        logits, values = self.forward(self.obs, self.candidates, self.mask)
        noise = torch.empty_like(logits).exponential_().clamp_min_(1.e-20)
        actions = (logits - noise.log()).argmax(-1)
        logp = logits.log_softmax(-1).gather(1, actions[:, None]).squeeze(1)
        return torch.stack((actions.float(), logp, values), -1)

    @torch.inference_mode()
    def refresh(self, policy):
        self.policy.load_state_dict(policy.state_dict(), strict=True)
        self.policy.cache_frozen_table()

    @torch.inference_mode()
    def act(self, host):
        if self.packed_transport is not None:
            if host.obs.shape[0] != self.batch:
                raise ValueError("Host feature batch differs from the fixed actor")
            return self.act_from_rows(host, self.all_rows)
        self.obs_host.numpy()[:] = host.obs
        self.candidates_host.numpy()[:] = host.candidates
        self.mask_host.numpy()[:] = host.mask
        return self.act_staged()

    @torch.inference_mode()
    def act_staged(self):
        """Infer after the caller fills this actor's pinned staging buffers."""
        # Held/terminal rows are inert graph padding and never enter experience.
        inactive = ~self.mask_host.numpy().any(axis=1)
        self.mask_host.numpy()[inactive, 0] = 1
        if self.packed_transport is None:
            self.obs.copy_(self.obs_host, non_blocking=self.cuda)
            self.candidates.copy_(self.candidates_host, non_blocking=self.cuda)
            self.mask.copy_(self.mask_host, non_blocking=self.cuda)
        else:
            from types import SimpleNamespace
            host = SimpleNamespace(obs=self.obs_host.numpy(), candidates=self.candidates_host.numpy(),
                                   mask=self.mask_host.numpy())
            self.packed_transport.stage(host, self.all_rows)
            self.packed_transport.upload_into(self.obs, self.candidates, self.mask)
        return self.infer_uploaded()

    @torch.inference_mode()
    def act_from_rows(self, host, lanes):
        """Own selected host rows directly; reconstruct full inputs on CUDA."""
        if self.packed_transport is None:
            raise RuntimeError("Selected packed staging requires packed CUDA transport")
        self.packed_transport.stage(host, lanes)
        self.packed_transport.upload_into(self.obs, self.candidates, self.mask)
        return self.infer_uploaded()

    @torch.inference_mode()
    def infer_uploaded(self):
        """Sample only after the current raw device inputs have been uploaded."""
        if self.graph is None:
            self.packet = self.compute()
        else:
            self.graph.replay()
        self.output_host.copy_(self.packet, non_blocking=self.cuda)
        if self.cuda:
            torch.cuda.current_stream(self.device).synchronize()
        packet = self.output_host.numpy().copy()
        return packet[:, 0].astype(np.int32), packet


def cpu_state(model):
    return {key: value.detach().cpu().clone() for key, value in model.state_dict().items()}


def load_checkpoint(path, *, device="cuda"):
    # The shared persistence format verifies length/checksum/finite tensors.
    import json
    from pathlib import Path
    import sys
    sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "TrainingPreflight"))
    from campaign_state import load_checkpoint as checked_load
    path = Path(path)
    identity = json.loads((path.parent / "identity.json").read_text())
    payload = checked_load(path, expected_identity=identity)
    state = payload["state"]
    model = Policy(state["catalog"], PolicyConfig(**state["policy_config"])).to(device)
    model.load_state_dict(state["policy"], strict=True)
    model.eval()
    return model, payload
