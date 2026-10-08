"""Real behavior policy and guarded PPO updates for outcome-based training.

The caller owns episode collection, opponent versions, returns, advantage
normalization and checkpoint files. This module never invents learner targets.
Actor and learner use exactly the same policy, transform and fixed action prior.
All arithmetic is FP32 (the caller may enable TF32 consistently for matmuls).
"""
from __future__ import annotations

import copy
from dataclasses import asdict, dataclass
import math
from typing import Any

import numpy as np
import torch
from torch import Tensor, nn

from model import CandidatePolicy, sample_actions


OBS_DIM, MAX_ACTIONS, ACTION_DIM = 2048, 64, 32
UPLOAD_FLOATS_PER_ROW = OBS_DIM + MAX_ACTIONS * ACTION_DIM + MAX_ACTIONS
DEFAULT_ACTION_PRIOR = (1., 0., 0., .5, 0., 0., 0., 0., 0., 0., -1., -12., 0., 0., -1., 0.)


class InvalidLearningBatch(ValueError):
    """Invalid experience: caller must fix collection instead of retrying it."""


class LearningNumericalError(RuntimeError):
    """Numerical failure: stop updates and restore a known-good checkpoint."""


@dataclass(frozen=True)
class PolicyConfig:
    width: int = 128
    action_kind_bias: tuple[float, ...] = DEFAULT_ACTION_PRIOR
    input_clip: float = 1000.
    value_tanh: bool = True

    def __post_init__(self):
        object.__setattr__(self, "action_kind_bias", tuple(self.action_kind_bias))
        if self.width not in (128, 256):
            raise ValueError("Learning policy width must be 128 or 256")
        if len(self.action_kind_bias) != 16 or not all(math.isfinite(x) for x in self.action_kind_bias):
            raise ValueError("Expected sixteen finite action-kind prior logits")
        if not math.isfinite(self.input_clip) or not 1 <= self.input_clip <= 1e6:
            raise ValueError("input_clip must be finite in [1, 1e6]")

    def to_dict(self):
        return asdict(self)


class LearningPolicy(nn.Module):
    """Raw adapter-v1/v2 inputs -> shared behavior/learner logits and value.

    Signed log1p compresses already-scaled numeric features, preserving their
    small-value scale approximately. Clipping at input_clip is an explicit
    observation ablation. Candidate kind bits 0..15 and card identity 16 remain
    exactly unchanged, so the categorical embedding always sees the original ID.
    The value's default tanh bound matches terminal zero-sum outcomes [-1, 1].
    All-invalid rows must be omitted; Actor and PPOLearner enforce that boundary.
    """

    FORMAT = "shards-learning-policy-v1"

    def __init__(self, config: PolicyConfig | None = None):
        super().__init__()
        self.config = config or PolicyConfig()
        self.core = CandidatePolicy(obs_dim=OBS_DIM, action_dim=ACTION_DIM,
                                    width=self.config.width, scorer="embedded")
        self.register_buffer("action_kind_bias", torch.tensor(self.config.action_kind_bias, dtype=torch.float32))
        # Start near the mild, fully supported prior without a large random
        # contextual score. These are initializations, not logit scaling at use.
        nn.init.orthogonal_(self.core.query.weight, gain=.01)
        nn.init.zeros_(self.core.query.bias)
        nn.init.zeros_(self.core.candidate_bias.weight)
        nn.init.zeros_(self.core.value.weight)
        nn.init.zeros_(self.core.value.bias)

    def _numeric(self, value: Tensor) -> Tensor:
        clipped = value.float().clamp(-self.config.input_clip, self.config.input_clip)
        return clipped.sign() * clipped.abs().log1p()

    def transform_inputs(self, obs: Tensor, candidates: Tensor):
        transformed_candidates = torch.cat((candidates[..., :17].float(), self._numeric(candidates[..., 17:])), dim=-1)
        return self._numeric(obs), transformed_candidates

    def forward(self, obs: Tensor, candidates: Tensor, legal_mask: Tensor):
        transformed_obs, transformed_candidates = self.transform_inputs(obs, candidates)
        logits, values = self.core(transformed_obs, transformed_candidates, legal_mask.bool())
        logits = logits + candidates[..., :16].float() @ self.action_kind_bias
        logits = logits.masked_fill(~legal_mask.bool(), -1.e9)
        if self.config.value_tanh:
            values = values.tanh()
        return logits.float(), values.float()


def _cpu_snapshot(value: Any):
    if torch.is_tensor(value):
        return value.detach().cpu().clone()
    if isinstance(value, dict):
        return {key: _cpu_snapshot(item) for key, item in value.items()}
    if isinstance(value, list):
        return [_cpu_snapshot(item) for item in value]
    if isinstance(value, tuple):
        return tuple(_cpu_snapshot(item) for item in value)
    return copy.deepcopy(value)


def _tensors(value: Any):
    if torch.is_tensor(value):
        yield value
    elif isinstance(value, dict):
        for item in value.values():
            yield from _tensors(item)
    elif isinstance(value, (tuple, list)):
        for item in value:
            yield from _tensors(item)


class LearningActor:
    """Fixed-batch frozen policy with owned raw device input and pinned output.

    act(host) accepts the preflight Host's flat CPU float32 ``host.upload``.
    Already-pinned inputs upload directly; otherwise an owned pinned staging
    buffer is used. ``act(None)`` consumes this actor's ``upload`` buffer.
    Returned NumPy actions and [action, logp, value] packet own their storage.
    Device obs/candidates/mask/packet are borrowed until the next act; collection
    must copy them into independently owned rollout storage before reusing them.
    refresh() copies weights in place, preserving graph addresses.
    """

    def __init__(self, policy: LearningPolicy, batch: int, graph: bool = True,
                 device: str | torch.device | None = None, validate_inputs: bool = True):
        if not 1 <= batch <= 4096:
            raise ValueError("Actor batch must be in [1,4096]")
        self.batch = batch
        self.device = torch.device(device if device is not None else next(policy.parameters()).device)
        self.cuda = self.device.type == "cuda"
        if graph and not self.cuda:
            raise ValueError("Captured actor requires CUDA; use graph=False for CPU")
        self.validate_inputs = validate_inputs
        self.policy = copy.deepcopy(policy).to(device=self.device, dtype=torch.float32).eval().requires_grad_(False)
        self.model = self.policy
        self.version = 0
        self.upload = torch.zeros(batch * UPLOAD_FLOATS_PER_ROW, dtype=torch.float32, pin_memory=self.cuda)
        self.input = torch.empty_like(self.upload, device=self.device)
        self.obs = self.input[:batch * OBS_DIM].view(batch, OBS_DIM)
        self.candidates = self.input[batch * OBS_DIM:batch * (OBS_DIM + MAX_ACTIONS * ACTION_DIM)].view(batch, MAX_ACTIONS, ACTION_DIM)
        self.mask = self.input[batch * (OBS_DIM + MAX_ACTIONS * ACTION_DIM):].view(batch, MAX_ACTIONS)
        self.input.zero_()
        self.mask.fill_(1.)
        self.output_host = torch.empty(batch, 3, dtype=torch.float32, pin_memory=self.cuda)
        self.graph = None
        self.completion = torch.cuda.Event() if self.cuda else None
        if graph:
            with torch.inference_mode():
                stream = torch.cuda.Stream(device=self.device)
                stream.wait_stream(torch.cuda.current_stream(self.device))
                with torch.cuda.stream(stream):
                    for _ in range(3):
                        self.packet = self.compute()
                torch.cuda.current_stream(self.device).wait_stream(stream)
                torch.cuda.synchronize(self.device)
                self.graph = torch.cuda.CUDAGraph()
                with torch.cuda.graph(self.graph):
                    self.packet = self.compute()

    def compute_logits(self):
        return self.policy(self.obs, self.candidates, self.mask.bool())

    def compute(self):
        return sample_actions(*self.compute_logits())

    def _validate_host(self, source: Tensor):
        if source.device.type != "cpu" or source.dtype != torch.float32 or source.shape != self.upload.shape or not source.is_contiguous():
            raise InvalidLearningBatch("Actor host upload must be contiguous flat CPU float32 with the fixed schema")
        arrays = source.numpy()
        masks = arrays[self.batch * (OBS_DIM + MAX_ACTIONS * ACTION_DIM):].reshape(self.batch, MAX_ACTIONS)
        if not np.isin(masks, (0., 1.)).all() or not masks.any(axis=1).all():
            raise InvalidLearningBatch("Actor mask must be binary with at least one legal action per row")
        if self.validate_inputs:
            if not np.isfinite(arrays).all():
                raise InvalidLearningBatch("Actor input contains a nonfinite feature")
            candidates = arrays[self.batch * OBS_DIM:self.batch * (OBS_DIM + MAX_ACTIONS * ACTION_DIM)].reshape(self.batch, MAX_ACTIONS, ACTION_DIM)
            ids = candidates[..., 16] * 192
            if not ((ids >= 0) & (ids <= 192)).all() or not np.allclose(ids, np.rint(ids), rtol=0, atol=2e-4):
                raise InvalidLearningBatch("Actor candidate card IDs violate the categorical schema")
        return masks

    @torch.inference_mode()
    def act(self, host=None):
        source = self.upload if host is None else host.upload
        masks = self._validate_host(source)
        if self.cuda and not source.is_pinned():
            self.upload.copy_(source)
            source = self.upload
        self.input.copy_(source, non_blocking=self.cuda)
        if self.graph is None:
            self.packet = self.compute()
        else:
            self.graph.replay()
        self.output_host.copy_(self.packet, non_blocking=self.cuda)
        if self.cuda:
            self.completion.record()
            self.completion.synchronize()
        packet = self.output_host.numpy().copy()
        if not np.isfinite(packet).all() or not np.equal(packet[:, 0], np.rint(packet[:, 0])).all():
            raise LearningNumericalError("Nonfinite actor packet or nonintegral sampled action")
        actions = packet[:, 0].astype(np.int64)
        if np.any(actions < 0) or np.any(actions >= MAX_ACTIONS) or not masks[np.arange(self.batch), actions].all():
            raise LearningNumericalError("Actor sampled an illegal candidate slot")
        if np.any(packet[:, 1] > 1e-5):
            raise LearningNumericalError("Actor log-probability exceeds zero")
        return actions, packet

    @torch.no_grad()
    def refresh(self, policy: LearningPolicy, version: int | None = None):
        if policy.config != self.policy.config:
            raise ValueError("Actor refresh cannot change policy configuration")
        if not all(bool(torch.isfinite(value).all()) for value in policy.state_dict().values()):
            raise LearningNumericalError("Refusing to copy nonfinite learner weights to frozen actor")
        self.policy.load_state_dict(policy.state_dict(), strict=True)
        self.version = self.version + 1 if version is None else int(version)

    def state_dict(self):
        return {"format": LearningPolicy.FORMAT, "config": self.policy.config.to_dict(),
                "policy": _cpu_snapshot(self.policy.state_dict()), "version": self.version}


@dataclass(frozen=True)
class PPOConfig:
    learning_rate: float = 3e-4
    clip_ratio: float = .2
    value_clip: float = .2
    value_coefficient: float = .5
    entropy_coefficient: float = .01
    max_grad_norm: float = .5
    target_kl: float = .03
    max_abs_log_ratio: float = 10.
    weight_decay: float = 0.

    def __post_init__(self):
        if not all(math.isfinite(value) for value in asdict(self).values()):
            raise ValueError("PPO hyperparameters must be finite")
        if not 0 < self.learning_rate <= .1 or not 0 < self.clip_ratio < 1 or not 0 < self.max_grad_norm <= 100:
            raise ValueError("Invalid learning rate, clipping ratio or gradient norm limit")
        if min(self.value_clip, self.value_coefficient, self.entropy_coefficient, self.weight_decay) < 0:
            raise ValueError("Loss coefficients, value clipping and decay must be nonnegative")
        if not 0 < self.target_kl <= 10 or not 0 < self.max_abs_log_ratio <= 20:
            raise ValueError("Require target_kl in (0,10], max_abs_log_ratio in (0,20]")


class _CapturedBackward:
    """Speculative derivatives only: this graph never mutates model or Adam.

    The exponential uses clamp(raw_log_ratio, -20, 20). Acceptance requires
    |raw_log_ratio| <= configured limit <=20, so the clamp is exactly the identity
    on every accepted forward value and derivative. Rejected gradients are never
    passed to Adam and are zeroed by the next replay. Raw validity is reported
    separately; safe indices only prevent an invalid input causing a CUDA assert.
    """

    METRICS = ("loss", "policy_loss", "value_loss", "entropy", "approx_kl", "clip_fraction",
               "value_mean", "value_abs_max", "logit_min", "logit_max", "max_abs_log_ratio", "grad_norm")

    def __init__(self, learner, sources):
        self.learner, self.config = learner, learner.config
        self.buffers = tuple(value.detach().to(dtype=torch.float32).clone() for value in sources)
        self.graph = torch.cuda.CUDAGraph()
        stream = torch.cuda.Stream(device=learner.device)
        stream.wait_stream(torch.cuda.current_stream(learner.device))
        with torch.cuda.stream(stream):
            for _ in range(3):
                self.report = self.compute()
        torch.cuda.current_stream(learner.device).wait_stream(stream)
        torch.cuda.synchronize(learner.device)
        with torch.cuda.graph(self.graph):
            self.report = self.compute()

    def compute(self):
        learner, config = self.learner, self.config
        obs, candidates, mask_raw, actions_raw, old_logp, old_values, returns, advantages = self.buffers
        mask = mask_raw.bool()
        actions = actions_raw.long().clamp(0, MAX_ACTIONS-1)
        ids = candidates[..., 16] * 192
        input_checks = [torch.isfinite(value).all() for value in self.buffers]
        input_checks += [((mask_raw == 0) | (mask_raw == 1)).all(), mask.any(-1).all(),
                         ((actions_raw >= 0) & (actions_raw < MAX_ACTIONS)).all(),
                         (actions_raw == actions_raw.long()).all(), (old_logp <= 1e-5).all(),
                         mask.gather(1, actions[:, None]).all(),
                         ((ids >= 0) & (ids <= 192) & ((ids-ids.round()).abs() <= 2e-4)).all()]
        valid_inputs = torch.stack(input_checks).all()
        learner.optimizer.zero_grad(set_to_none=False)
        logits, values = learner.policy(obs, candidates, mask)
        logp_all = logits.float().log_softmax(-1)
        selected_logp = logp_all.gather(1, actions[:, None]).squeeze(1)
        raw_log_ratio = selected_logp-old_logp
        valid_outputs = torch.isfinite(logits).all() & torch.isfinite(values).all() & torch.isfinite(raw_log_ratio).all()
        ratio = raw_log_ratio.clamp(-20., 20.).exp()
        policy_loss = -torch.minimum(ratio*advantages, ratio.clamp(1-config.clip_ratio, 1+config.clip_ratio)*advantages).mean()
        value_errors = (values-returns).square()
        if config.value_clip:
            clipped = old_values+(values-old_values).clamp(-config.value_clip, config.value_clip)
            value_errors = torch.maximum(value_errors, (clipped-returns).square())
        value_loss = .5*value_errors.mean()
        entropy = -(logp_all.exp()*logp_all).sum(-1).mean()
        # Accepted rows have an inactive clamp, including this KL estimator.
        approx_kl = ((ratio-1)-raw_log_ratio).mean()
        clip_fraction = ((ratio-1).abs() > config.clip_ratio).float().mean()
        loss = policy_loss+config.value_coefficient*value_loss-config.entropy_coefficient*entropy
        loss.backward()
        norm = torch.nn.utils.clip_grad_norm_(learner.policy.parameters(), config.max_grad_norm, foreach=True)
        return torch.stack((loss.detach(), policy_loss.detach(), value_loss.detach(), entropy.detach(),
                            approx_kl.detach(), clip_fraction.detach(), values.detach().mean(), values.detach().abs().max(),
                            logits.detach().masked_fill(~mask, torch.inf).min(),
                            logits.detach().masked_fill(~mask, -torch.inf).max(), raw_log_ratio.detach().abs().max(),
                            norm.detach(), valid_inputs.float(), valid_outputs.float()))

    def run(self, sources):
        for target, source in zip(self.buffers, sources):
            target.copy_(source)
        self.graph.replay()
        return self.report.detach().cpu().tolist()


class _CapturedFiniteState:
    """Read-only graph over stable parameter/Adam buffers, built after Adam init."""

    def __init__(self, learner):
        self.values = list(learner.policy.state_dict().values()) + list(_tensors(learner.optimizer.state))
        if any(value.device.type != "cuda" for value in self.values):
            raise ValueError("Captured finite-state check requires device-resident optimizer state")
        self.graph = torch.cuda.CUDAGraph()
        stream = torch.cuda.Stream(device=learner.device)
        stream.wait_stream(torch.cuda.current_stream(learner.device))
        with torch.cuda.stream(stream):
            for _ in range(2):
                self.result = self.compute()
        torch.cuda.current_stream(learner.device).wait_stream(stream)
        torch.cuda.synchronize(learner.device)
        with torch.cuda.graph(self.graph):
            self.result = self.compute()

    def compute(self):
        return torch.stack([torch.isfinite(value).all() for value in self.values]).all()

    def valid(self):
        self.graph.replay()
        return bool(self.result)


class PPOLearner:
    """Genuine clipped PPO using caller-owned behavior records and targets.

    No hidden advantage normalization or invented return occurs. Finite stale
    ratio/KL guards return accepted=False before mutation; stop the remaining
    epoch in that case. Invalid data raises InvalidLearningBatch. Numerical
    failures raise LearningNumericalError and require checkpoint recovery.
    Eager AdamW is intentional: acceptance must be decided before the update.
    Optional mode="captured_backward", batch=N captures speculative derivatives
    and read-only state validation; partial batches fall back to eager semantics.
    """

    FORMAT = "shards-ppo-learner-v1"

    def __init__(self, policy: LearningPolicy, config: PPOConfig | None = None,
                 mode: str = "eager", batch: int | None = None):
        if not all(parameter.dtype == torch.float32 for parameter in policy.parameters()):
            raise ValueError("Learning policy parameters must be FP32")
        self.policy, self.config = policy.train(), config or PPOConfig()
        self.device = next(policy.parameters()).device
        if mode not in ("eager", "captured_backward"):
            raise ValueError("Unknown learner mode")
        if mode == "captured_backward" and (self.device.type != "cuda" or batch is None or not 1 <= batch <= 8192):
            raise ValueError("Captured backward requires CUDA and a fixed batch in [1,8192]")
        self.mode, self.capture_batch = mode, batch
        self._captured_backward = self._captured_finite = None
        self.optimizer = torch.optim.AdamW(policy.parameters(), lr=self.config.learning_rate,
                                          weight_decay=self.config.weight_decay, fused=self.device.type == "cuda")
        self.updates = self.rejected = 0
        self.failed = False

    def _batch_metadata(self, obs, candidates, mask, actions, old_logp, old_values, returns, advantages):
        batch = obs.shape[0] if obs.ndim == 2 else 0
        if batch < 1 or obs.shape != (batch, OBS_DIM) or candidates.shape != (batch, MAX_ACTIONS, ACTION_DIM) or mask.shape != (batch, MAX_ACTIONS):
            raise InvalidLearningBatch("PPO observation/candidate/mask shapes violate the adapter contract")
        values = (obs, candidates, mask, actions, old_logp, old_values, returns, advantages)
        if any(value.device != self.device for value in values):
            raise InvalidLearningBatch("All minibatch tensors must be on the learner device")
        if obs.dtype != torch.float32 or candidates.dtype != torch.float32:
            raise InvalidLearningBatch("PPO raw observations and candidates must be FP32")
        if any(value.shape != (batch,) for value in values[3:]):
            raise InvalidLearningBatch("Actions and stored likelihood/value/target vectors must be [batch]")
        return values

    def _batch(self, obs, candidates, mask, actions, old_logp, old_values, returns, advantages):
        values = self._batch_metadata(obs, candidates, mask, actions, old_logp, old_values, returns, advantages)
        # Check action bounds before any gather, avoiding a CUDA device assertion.
        checks = [torch.isfinite(value).all() for value in values]
        checks += [((mask == 0) | (mask == 1)).all(), mask.bool().any(-1).all(),
                   ((actions >= 0) & (actions < MAX_ACTIONS)).all(), (actions == actions.long()).all(),
                   (old_logp <= 1e-5).all()]
        if not bool(torch.stack(checks).all()):
            raise InvalidLearningBatch("PPO batch has nonfinite data, invalid masks/actions or positive old log-probabilities")
        mask, actions = mask.bool(), actions.long()
        if not bool(mask.gather(1, actions[:, None]).all()):
            raise InvalidLearningBatch("Stored behavior action is illegal in its stored mask")
        ids = candidates[..., 16] * 192
        if not bool(((ids >= 0) & (ids <= 192) & ((ids - ids.round()).abs() <= 2e-4)).all()):
            raise InvalidLearningBatch("Stored categorical card IDs violate the adapter contract")
        return mask, actions

    def _numerical_failure(self, message):
        self.failed = True
        self.optimizer.zero_grad(set_to_none=self.mode == "eager")
        raise LearningNumericalError(message)

    def step(self, obs: Tensor, candidates: Tensor, mask: Tensor, actions: Tensor,
             old_logp: Tensor, old_values: Tensor, returns: Tensor, advantages: Tensor):
        if self.failed:
            raise LearningNumericalError("Learner requires a known-good checkpoint recovery before further updates")
        sources = (obs, candidates, mask, actions, old_logp, old_values, returns, advantages)
        capture_dtypes = (mask.dtype in (torch.bool, torch.float32)
                          and actions.dtype in (torch.int32, torch.int64, torch.float32)
                          and all(value.dtype == torch.float32 for value in sources[4:]))
        if self.mode == "captured_backward" and obs.ndim == 2 and obs.shape[0] == self.capture_batch and capture_dtypes:
            return self._step_captured(sources)
        # Eager zero_grad may replace gradient allocations. Discard any captured
        # backward before a partial batch so its next use rebuilds safe pointers.
        if self._captured_backward is not None:
            self._captured_backward = None
        return self._step_eager(*sources)

    def _step_captured(self, sources):
        self._batch_metadata(*sources)
        if self._captured_backward is None or self._captured_backward.config != self.config:
            self._captured_backward = _CapturedBackward(self, sources)
        numbers = self._captured_backward.run(sources)
        if not numbers[-2]:
            raise InvalidLearningBatch("Captured PPO batch has invalid/nonfinite inputs, masks, action slots or card IDs")
        if not numbers[-1]:
            self._numerical_failure("Nonfinite policy output or PPO log-ratio before update")
        metrics = dict(zip(_CapturedBackward.METRICS, numbers[:-2]))
        metrics["updates"] = self.updates
        if metrics["max_abs_log_ratio"] > self.config.max_abs_log_ratio:
            self.rejected += 1
            return {"accepted": False, "reason": "log_ratio_guard", "max_abs_log_ratio": metrics["max_abs_log_ratio"],
                    "updates": self.updates, "approx_kl": None, "grad_norm": None}
        if not all(math.isfinite(value) for key, value in metrics.items() if key != "grad_norm"):
            self._numerical_failure("Nonfinite PPO objective before optimizer update")
        if metrics["approx_kl"] > self.config.target_kl:
            self.rejected += 1
            return {**metrics, "accepted": False, "reason": "target_kl", "grad_norm": None}
        if not math.isfinite(metrics["grad_norm"]):
            self._numerical_failure("Nonfinite pre-clip gradient norm; optimizer update was not executed")
        self.optimizer.step()
        if self._captured_finite is None:
            # This capture only reads already-created state. No dummy Adam step
            # or warmup mutation needs undoing, and optimizer addresses persist.
            self._captured_finite = _CapturedFiniteState(self)
        if not self._captured_finite.valid():
            self._numerical_failure("Nonfinite learner parameters/optimizer state; recover a known-good checkpoint")
        self.updates += 1
        return {**metrics, "accepted": True, "reason": "updated", "updates": self.updates}

    def _step_eager(self, obs: Tensor, candidates: Tensor, mask: Tensor, actions: Tensor,
                    old_logp: Tensor, old_values: Tensor, returns: Tensor, advantages: Tensor):
        mask, actions = self._batch(obs, candidates, mask, actions, old_logp, old_values, returns, advantages)
        self.optimizer.zero_grad(set_to_none=True)
        logits, values = self.policy(obs, candidates, mask)
        logp_all = logits.float().log_softmax(-1)
        logp = logp_all.gather(1, actions[:, None]).squeeze(1)
        log_ratio = logp - old_logp.float().detach()
        preflight = torch.stack((torch.isfinite(logits).all().float(), torch.isfinite(values).all().float(),
                                 torch.isfinite(log_ratio).all().float(), log_ratio.abs().max())).detach().cpu().tolist()
        if not all(preflight[:3]):
            self._numerical_failure("Nonfinite policy output or PPO log-ratio before update")
        if preflight[3] > self.config.max_abs_log_ratio:
            self.rejected += 1
            return {"accepted": False, "reason": "log_ratio_guard", "max_abs_log_ratio": preflight[3],
                    "updates": self.updates, "approx_kl": None, "grad_norm": None}
        # No exp is evaluated until a finite bounded log-ratio is established.
        ratio = log_ratio.exp()
        advantage = advantages.float().detach()
        policy_loss = -torch.minimum(ratio * advantage, ratio.clamp(1-self.config.clip_ratio, 1+self.config.clip_ratio) * advantage).mean()
        value_errors = (values - returns.float().detach()).square()
        if self.config.value_clip:
            clipped = old_values.float().detach() + (values-old_values.float().detach()).clamp(-self.config.value_clip, self.config.value_clip)
            value_errors = torch.maximum(value_errors, (clipped-returns.float().detach()).square())
        value_loss = .5 * value_errors.mean()
        entropy = -(logp_all.exp() * logp_all).sum(-1).mean()
        approx_kl = ((ratio-1)-log_ratio).mean()
        clip_fraction = ((ratio-1).abs() > self.config.clip_ratio).float().mean()
        loss = policy_loss + self.config.value_coefficient * value_loss - self.config.entropy_coefficient * entropy
        legal_logits = logits.masked_select(mask)
        metric_names = ("loss", "policy_loss", "value_loss", "entropy", "approx_kl", "clip_fraction", "value_mean", "value_abs_max", "logit_min", "logit_max")
        metric_tensor = torch.stack((loss, policy_loss, value_loss, entropy, approx_kl, clip_fraction,
                                    values.mean(), values.abs().max(), legal_logits.min(), legal_logits.max()))
        numbers = metric_tensor.detach().cpu().tolist()
        if not all(math.isfinite(value) for value in numbers):
            self._numerical_failure("Nonfinite PPO objective before backward")
        metrics = dict(zip(metric_names, numbers))
        metrics.update(max_abs_log_ratio=preflight[3], grad_norm=None, updates=self.updates)
        if metrics["approx_kl"] > self.config.target_kl:
            self.rejected += 1
            return {**metrics, "accepted": False, "reason": "target_kl"}
        loss.backward()
        norm = torch.nn.utils.clip_grad_norm_(self.policy.parameters(), self.config.max_grad_norm,
                                             foreach=self.device.type == "cuda")
        metrics["grad_norm"] = float(norm.detach().cpu())
        if not math.isfinite(metrics["grad_norm"]):
            self._numerical_failure("Nonfinite pre-clip gradient norm; optimizer update was not executed")
        self.optimizer.step()
        # Initially favor recovery correctness over shaving guard overhead.
        # A failure here marks the mutated copy unusable until restored.
        self.validate_state()
        self.updates += 1
        return {**metrics, "accepted": True, "reason": "updated", "updates": self.updates}

    @torch.no_grad()
    def validate_state(self):
        parameters = list(self.policy.state_dict().values())
        states = list(_tensors(self.optimizer.state))
        # AdamW's step/moment tensors may be CPU or CUDA, so group by device.
        grouped = {}
        for value in parameters + states:
            grouped.setdefault(value.device, []).append(torch.isfinite(value).all())
        if any(not bool(torch.stack(checks).all()) for checks in grouped.values()):
            self._numerical_failure("Nonfinite learner parameters/optimizer state; recover a known-good checkpoint")
        return {"parameters_finite": True, "optimizer_state_finite": True,
                "parameter_and_buffer_tensors": len(parameters), "optimizer_state_tensors": len(states)}

    def state_dict(self):
        if self.failed:
            raise LearningNumericalError("Refusing to checkpoint a failed learner")
        self.validate_state()
        return {"format": self.FORMAT, "policy_config": self.policy.config.to_dict(),
                "ppo_config": asdict(self.config), "policy": _cpu_snapshot(self.policy.state_dict()),
                "optimizer": _cpu_snapshot(self.optimizer.state_dict()), "updates": self.updates,
                "rejected": self.rejected, "torch_rng_cpu": torch.get_rng_state().clone(),
                "torch_rng_device": torch.cuda.get_rng_state(self.device).cpu().clone() if self.device.type == "cuda" else None}

    def load_state_dict(self, state, restore_rng: bool = True):
        if state.get("format") != self.FORMAT or PolicyConfig(**state["policy_config"]) != self.policy.config:
            raise ValueError("Checkpoint format or policy configuration mismatch")
        if any(not bool(torch.isfinite(value).all()) for value in _tensors((state["policy"], state["optimizer"]))):
            raise LearningNumericalError("Refusing nonfinite recovery checkpoint")
        self.config = PPOConfig(**state["ppo_config"])
        self.policy.load_state_dict(state["policy"], strict=True)
        self.optimizer.load_state_dict(state["optimizer"])
        self._captured_backward = self._captured_finite = None
        self.updates, self.rejected = int(state["updates"]), int(state.get("rejected", 0))
        self.failed = False
        self.optimizer.zero_grad(set_to_none=True)
        self.validate_state()
        if restore_rng:
            torch.set_rng_state(state["torch_rng_cpu"])
            if self.device.type == "cuda" and state.get("torch_rng_device") is not None:
                torch.cuda.set_rng_state(state["torch_rng_device"], self.device)
