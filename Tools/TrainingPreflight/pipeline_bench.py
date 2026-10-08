"""Bounded real-engine frozen-policy pipeline; optional disposable optimizer load.

This deliberately does not launch training or save a trained checkpoint. Its
learner load uses fabricated advantages on actual observations, to measure
scheduling cost separately from any claim about playing strength.
The optional CPU ragged actor remains synchronous; concurrent CPU acting and
GPU learning is a separate, unmeasured scheduling experiment.
Optional GPU rollout storage owns observed inputs and actor packets in a bounded
ring. This measures a data path with fabricated learner targets, not complete
trajectories, rewards, policy-version lifetimes or an outcome-learning run.
"""
from __future__ import annotations

import argparse
import copy
import mmap
import os
from pathlib import Path
import struct
import subprocess
import tempfile
import time

import numpy as np
import torch

from bench_common import DOTNET, ROOT, Monitor, distribution, metadata, save_json
from model import CandidatePolicy, ppo_loss, sample_actions


class Host:
    def __init__(self, batch, workers, seed=17, pinned=False, server_gc=False, transport="pipe", split_branches=2, shared_copy="span"):
        self.batch = batch
        self.pinned = pinned
        self.storage = None
        self.closed = False
        self.mapping = self.mapped_array = self.shared_file = None
        self.log = tempfile.TemporaryFile(mode="w+b")
        env = os.environ.copy()
        env["DOTNET_gcServer"] = "1" if server_gc else "0"
        env["SHARDS_SPLIT_BRANCHES"] = str(split_branches)
        env["SHARDS_SHARED_COPY"] = shared_copy
        if transport == "shared":
            size = batch * (2048 + 64*32 + 64 + 2 + 1 + 1) * 4
            self.shared_file = tempfile.NamedTemporaryFile(prefix="shards-preflight-", dir="/dev/shm")
            self.shared_file.truncate(size)
            self.shared_file.flush()
            self.mapping = mmap.mmap(self.shared_file.fileno(), size)
            self.mapped_array = np.frombuffer(self.mapping, dtype=np.uint8)
            env["SHARDS_SHARED_BUFFER"] = self.shared_file.name
        else:
            env.pop("SHARDS_SHARED_BUFFER", None)
        self.process = subprocess.Popen(
            [DOTNET, str(ROOT / "Tools/TrainingPreflight/Host/bin/Release/net8.0/TrainingHost.dll"), "serve"],
            stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=self.log, bufsize=0, env=env)
        self.process.stdin.write(struct.pack("<IIIQ", 1, batch, workers, seed))
        try:
            self.receive()
        except BaseException:
            self.close()
            raise

    def read_exact(self, destination):
        view = memoryview(destination).cast("B")
        pos = 0
        while pos < len(view):
            got = self.process.stdout.readinto(view[pos:])
            if not got:
                self.log.seek(0)
                raise RuntimeError("Host stopped: " + self.log.read().decode(errors="replace")[-8000:])
            pos += got

    def receive(self):
        header = bytearray(32)
        self.read_exact(header)
        magic, version, n, obs, actions, features, size, trailer = struct.unpack("<8I", header)
        if (magic, version, n, obs, actions, features, trailer) != (
                0x534F4931, 1, self.batch, 2048, 64, 32, 64):
            raise RuntimeError("Unexpected protocol header: " + repr(tuple(struct.unpack("<8I", header))))
        expected = n * (2048 + 64*32 + 64 + 2 + 1 + 1) * 4
        if size != expected:
            raise RuntimeError(f"Payload size {size}, expected {expected}")
        if self.storage is None:
            self.storage = torch.empty(size, dtype=torch.uint8, pin_memory=self.pinned)
            self.raw = self.storage.numpy()
            floats = self.raw.view(np.float32)
            cursor = 0
            def take(shape):
                nonlocal cursor
                count = int(np.prod(shape))
                result = floats[cursor:cursor+count].reshape(shape)
                cursor += count
                return result
            self.obs = take((n, obs))
            self.candidates = take((n, actions, features))
            self.mask = take((n, actions))
            self.rewards = take((n, 2))
            self.done = take((n,)).view(np.int32)
            self.seats = take((n,)).view(np.int32)
            self.upload = self.storage[:n*(2048+64*32+64)*4].view(torch.float32)
        if self.mapping is None:
            self.read_exact(self.raw)
        else:
            np.copyto(self.raw, self.mapped_array)
        tail = bytearray(64)
        self.read_exact(tail)
        self.metrics = np.asarray(struct.unpack("<8d", tail))
        if not np.all(self.mask.sum(axis=1) >= 1):
            raise RuntimeError("An observation has no legal action")

    def advance(self, indices=None):
        if indices is None:
            self.process.stdin.write(struct.pack("<I", 4))
        else:
            indices = np.asarray(indices, dtype="<i4")
            if indices.shape != (self.batch,) or np.any(indices < 0) or np.any(indices >= 64) or not np.all(
                    self.mask[np.arange(self.batch), indices] == 1):
                raise ValueError("Invalid policy action")
            packet = struct.pack("<I", 2) + indices.tobytes()
            # Raw pipe writes may be short. Keep framing exact at large batches.
            view = memoryview(packet)
            while view:
                written = self.process.stdin.write(view)
                if not written:
                    raise RuntimeError("Host command pipe closed")
                view = view[written:]
        self.receive()

    def close(self):
        if self.closed:
            return
        self.closed = True
        try:
            if self.process.poll() is None:
                try:
                    self.process.stdin.write(struct.pack("<I", 3))
                    self.process.stdin.close()
                    self.process.wait(timeout=5)
                except (BrokenPipeError, subprocess.TimeoutExpired):
                    try:
                        self.process.terminate()
                    except ProcessLookupError:
                        pass
                    try:
                        self.process.wait(timeout=2)
                    except subprocess.TimeoutExpired:
                        self.process.kill()
                        self.process.wait(timeout=2)
        finally:
            if not self.process.stdin.closed:
                self.process.stdin.close()
            self.process.stdout.close()
            self.log.seek(0)
            self.diagnostics = self.log.read().decode(errors="replace")
            self.log.close()
            if self.mapping is not None:
                self.mapped_array = None
                self.mapping.close()
                self.shared_file.close()


class ActorRollout:
    """Single-stream owned GPU ring; append participates in the actor graph.

    Overwrites are intentional once capacity is reached. Collection and learner
    updates remain sequential. Count/cursor are GPU tensors; the CPU mirror is
    updated only after Actor.act's existing completion wait, never during capture.
    """

    def __init__(self, rows, batch):
        if not 1 <= batch <= rows <= 32768:
            raise ValueError("Rollout requires batch <= rows <=32768")
        self.rows, self.batch = rows, batch
        self.obs = torch.empty(rows, 2048, dtype=torch.float32, device="cuda")
        self.candidates = torch.empty(rows, 64, 32, dtype=torch.float32, device="cuda")
        self.mask = torch.empty(rows, 64, dtype=torch.float32, device="cuda")
        self.packet = torch.empty(rows, 3, dtype=torch.float32, device="cuda")
        self.cursor = torch.zeros((), dtype=torch.int64, device="cuda")
        self.count = torch.zeros((), dtype=torch.int64, device="cuda")
        self.total = torch.zeros((), dtype=torch.int64, device="cuda")
        self.offsets = torch.arange(batch, device="cuda", dtype=torch.int64)
        self.indices = torch.empty(batch, device="cuda", dtype=torch.int64)
        self.completed_rows = 0

    def append(self, obs, candidates, mask, packet):
        torch.add(self.offsets, self.cursor, out=self.indices)
        self.indices.remainder_(self.rows)
        self.obs.index_copy_(0, self.indices, obs)
        self.candidates.index_copy_(0, self.indices, candidates)
        self.mask.index_copy_(0, self.indices, mask)
        self.packet.index_copy_(0, self.indices, packet)
        self.cursor.add_(self.batch).remainder_(self.rows)
        self.count.add_(self.batch).clamp_max_(self.rows)
        self.total.add_(self.batch)

    def reset(self):
        # Stable tensor addresses survive reset and remain valid for graph replay.
        self.cursor.zero_()
        self.count.zero_()
        self.total.zero_()
        self.completed_rows = 0

    @property
    def valid_rows(self):
        return min(self.rows, self.completed_rows)

    @property
    def array_bytes(self):
        return sum(value.numel() * value.element_size()
                   for value in (self.obs, self.candidates, self.mask, self.packet))


class Actor:
    def __init__(self, host, device, width, scorer, precision, mode, transfer_dtype="fp32", cpu_ragged=False, rollout_rows=0):
        self.host, self.device, self.precision, self.mode = host, device, precision, mode
        self.cuda = device == "cuda"
        self.transfer_dtype = transfer_dtype
        self.cpu_ragged = cpu_ragged
        if rollout_rows and (device != "cuda" or mode not in ("eager", "graph") or transfer_dtype != "fp32"):
            raise ValueError("Owned rollout currently requires CUDA/eager or graph with FP32 transfer")
        if cpu_ragged and (device, mode, scorer, precision, transfer_dtype) != ("cpu", "eager", "embedded", "fp32", "fp32"):
            raise ValueError("CPU ragged inference requires CPU/eager/embedded/FP32 and FP32 transfer")
        self.model = CandidatePolicy(obs_dim=2048, width=width, scorer=scorer).to(device).eval()
        if self.cpu_ragged:
            from sparse_cpu_bench import SparseFirstLayerPolicy, pack_observations, pack_legal_candidates
            self.sparse_model = SparseFirstLayerPolicy(self.model)
            self.pack_observations = pack_observations
            self.pack_legal_candidates = pack_legal_candidates
        # The CPU ragged path completes inference before Host.advance mutates
        # this buffer. No async work retains an input across that ownership handoff.
        self.input = host.upload if self.cpu_ragged else torch.empty_like(host.upload, device=device)
        if transfer_dtype == "fp16":
            if not self.cuda:
                raise ValueError("Half-transfer probe requires CUDA")
            self.half_host = torch.empty(host.upload.shape,dtype=torch.float16,pin_memory=host.pinned)
            self.half_device = torch.empty(host.upload.shape,dtype=torch.float16,device="cuda")
        n = host.batch
        self.obs = self.input[:n*2048].view(n, 2048)
        self.candidates = self.input[n*2048:n*(2048+64*32)].view(n,64,32)
        self.mask = self.input[n*(2048+64*32):].view(n,64)
        self.rollout = ActorRollout(rollout_rows, n) if rollout_rows else None
        self.rollout_actor_reference = tuple(parameter.detach().clone() for parameter in self.model.parameters()) if self.rollout else None
        self.output_host = torch.empty(n,3,dtype=torch.float32, pin_memory=self.cuda)
        # A fixed exercise distribution visits longer games. This is not a
        # trained policy, heuristic strength baseline, or a restricted mask.
        self.bias = torch.tensor([4,2,2,1,3,2,1,1,0,1,-4,-20,0,0,0,0], device=device,dtype=torch.float32)
        self.graph = None
        if not self.cpu_ragged:
            self.input.copy_(host.upload, non_blocking=host.pinned)
        if self.cuda:
            torch.cuda.synchronize()
        self.forward = self.compute
        self.cold_start = 0.
        if mode == "compile":
            self.forward = torch.compile(self.compute, mode="reduce-overhead", fullgraph=True)
        start = time.perf_counter()
        with torch.inference_mode():
            if mode == "graph":
                stream = torch.cuda.Stream()
                stream.wait_stream(torch.cuda.current_stream())
                with torch.cuda.stream(stream):
                    for _ in range(4):
                        self.output = self.compute()
                torch.cuda.current_stream().wait_stream(stream)
                torch.cuda.synchronize()
                self.graph = torch.cuda.CUDAGraph()
                with torch.cuda.graph(self.graph):
                    self.output = self.compute()
            else:
                for _ in range(4):
                    self.output = self.forward()
            if self.cuda:
                torch.cuda.synchronize()
        self.cold_start = time.perf_counter() - start
        if self.rollout is not None:
            # Discard append side effects from eager warmup or graph capture.
            # Same-stream ordering makes this visible before the first real act.
            self.rollout.reset()
        self.events = [torch.cuda.Event(enable_timing=True) for _ in range(4)] if self.cuda else []
        self.cpu_ragged_checks = None
        if self.cpu_ragged:
            # Validation must not shift the actual actor's sampling sequence.
            with torch.random.fork_rng(devices=[]):
                self.cpu_ragged_checks = self.validate_cpu_ragged()
            self.cold_start = time.perf_counter() - start

    def compute(self):
        if self.cpu_ragged:
            legal_mask = self.mask.bool()
            logits, values = self.sparse_model.forward_ragged(
                self.pack_observations(self.obs), self.pack_legal_candidates(self.candidates, legal_mask),
                *legal_mask.shape,
            )
        else:
            with torch.autocast(self.device, dtype=torch.bfloat16, enabled=self.precision == "bf16"):
                logits, values = self.model(self.obs, self.candidates, self.mask.bool())
        logits = logits * .02 + (self.candidates[:,:,:16] @ self.bias)
        packet = sample_actions(logits, values)
        if self.rollout is not None:
            self.rollout.append(self.obs, self.candidates, self.mask, packet)
        return packet

    @torch.inference_mode()
    def validate_cpu_ragged(self):
        from sparse_cpu_bench import verify_ragged
        legal_mask = self.mask.bool()
        checks = verify_ragged(
            self.model, self.sparse_model, self.obs, self.candidates, legal_mask,
            self.pack_observations(self.obs), self.pack_legal_candidates(self.candidates, legal_mask),
        )
        # Verify the integrated category bias and sampled-packet contract too.
        packet = self.compute()
        logits, values = self.model(self.obs, self.candidates, legal_mask)
        logits = logits * .02 + (self.candidates[:,:,:16] @ self.bias)
        actions = packet[:, 0].long()
        expected_logp = logits.log_softmax(-1).gather(1, actions[:, None]).squeeze(1)
        torch.testing.assert_close(packet[:, 1], expected_logp, rtol=2e-5, atol=2e-5)
        torch.testing.assert_close(packet[:, 2], values, rtol=2e-5, atol=2e-5)
        if not bool(torch.isfinite(packet).all()) or not bool(legal_mask.gather(1, actions[:, None]).all()):
            raise AssertionError("CPU ragged integrated packet is nonfinite or illegal")
        checks.update(category_biased_packet_logp_max_abs_error=(packet[:, 1] - expected_logp).abs().max().item(),
                      category_biased_packet_value_max_abs_error=(packet[:, 2] - values).abs().max().item(),
                      host_upload_storage_aliased=self.input.data_ptr() == self.host.upload.data_ptr())
        return checks

    @torch.inference_mode()
    def act(self):
        if self.cuda:
            self.events[0].record()
        start = time.perf_counter()
        if self.transfer_dtype == "fp16":
            # Include CPU conversion and GPU expansion in measured service time;
            # reduced PCIe bytes alone do not establish a useful optimization.
            self.half_host.copy_(self.host.upload)
            self.half_device.copy_(self.half_host,non_blocking=self.host.pinned)
            self.input.copy_(self.half_device)
        elif not self.cpu_ragged:
            self.input.copy_(self.host.upload, non_blocking=self.host.pinned)
        if self.cuda:
            self.events[1].record()
        if self.graph:
            self.graph.replay()
            output = self.output
        else:
            if self.mode == "compile":
                torch.compiler.cudagraph_mark_step_begin()
            output = self.forward()
        if self.cuda:
            self.events[2].record()
        self.output_host.copy_(output, non_blocking=self.cuda)
        if self.cuda:
            self.events[3].record()
            self.events[3].synchronize()
            stages = [self.events[i].elapsed_time(self.events[i+1]) for i in range(3)]
        else:
            stages = [0., (time.perf_counter()-start)*1000, 0.]
        packet = self.output_host.numpy()
        if not np.isfinite(packet).all():
            raise RuntimeError("Nonfinite actor output")
        if self.rollout is not None:
            self.rollout.completed_rows += self.host.batch
        return packet[:,0].astype(np.int32), stages

    @torch.inference_mode()
    def validate_rollout(self):
        """Outside-timing reconciliation and last-batch/frozen-weight checks."""
        ring = self.rollout
        if ring is None:
            raise ValueError("No owned rollout configured")
        expected_total = ring.completed_rows
        if (int(ring.cursor.item()), int(ring.count.item()), int(ring.total.item())) != (
                expected_total % ring.rows, min(ring.rows, expected_total), expected_total):
            raise AssertionError("Owned rollout cursor/count/total disagree with completed actor calls")
        if expected_total:
            positions = (torch.arange(self.host.batch, device="cuda") + expected_total - self.host.batch) % ring.rows
            for stored, source in ((ring.obs, self.obs), (ring.candidates, self.candidates), (ring.mask, self.mask)):
                if stored.untyped_storage().data_ptr() == source.untyped_storage().data_ptr():
                    raise AssertionError("Owned rollout aliases actor input storage")
                if not torch.equal(stored.index_select(0, positions), source):
                    raise AssertionError("Owned rollout last inputs differ from the completed actor batch")
            if not torch.equal(ring.packet.index_select(0, positions), self.output_host.to("cuda")):
                raise AssertionError("Owned rollout packet differs from the completed actor output")
            packets = ring.packet[:ring.valid_rows]
            actions = packets[:, 0]
            if not bool(torch.isfinite(packets).all()) or not torch.equal(actions, actions.round()):
                raise AssertionError("Stored packet is nonfinite or action is not an integer")
            if not bool(((actions >= 0) & (actions < 64)).all()):
                raise AssertionError("Stored action outside candidate slots")
            if not bool(ring.mask[:ring.valid_rows].bool().gather(1, actions.long()[:, None]).all()):
                raise AssertionError("Stored actor action is illegal in its stored mask")
        if not all(torch.equal(parameter, reference)
                   for parameter, reference in zip(self.model.parameters(), self.rollout_actor_reference)):
            raise AssertionError("Frozen actor weights changed during the workload")
        return {"cursor_count_total_match_completed_calls": True,
                "latest_inputs_and_packet_exact": bool(expected_total), "independent_input_storage": True,
                "all_valid_stored_packets_finite_and_actions_legal": True, "actor_weights_unchanged": True,
                "completed_rows": expected_total, "valid_rows": ring.valid_rows,
                "cursor": expected_total % ring.rows, "capacity_rows": ring.rows, "array_bytes": ring.array_bytes}


class LearnerLoad:
    """Disposable compute load with anchored targets; not an outcome trainer.

    Actions are this copy's own legal argmax when a minibatch is prepared. Thus
    its selected old probability is at least 1/64 for a valid masked softmax,
    bounding subsequent ratios by 64 without changing/clamping the PPO loss.
    Actor packets remain independent transport evidence, not behavior data for
    this deliberately synthetic objective. Parameters never return to the actor.
    """
    SEMANTIC_VERSION = "synthetic-legal-argmax-fixed-returns-v2"

    def __init__(self, actor, batch, mode):
        self.actor, self.batch, self.mode = actor, batch, mode
        self.model = copy.deepcopy(actor.model).train()
        self.index = torch.arange(batch,device="cuda") % actor.host.batch
        self.obs = actor.obs[self.index].clone()
        self.candidates = actor.candidates[self.index].clone()
        self.mask = actor.mask[self.index].bool()
        self.selected = self.mask.long().argmax(-1)
        if actor.rollout is not None:
            self.random_rows = torch.empty(batch,device="cuda")
            self.mask_float = torch.empty(batch,64,dtype=torch.float32,device="cuda")
            self.behavior_packet = torch.empty(batch,3,dtype=torch.float32,device="cuda")
        self.old_logp = torch.zeros(batch,device="cuda")
        self.advantages = torch.linspace(-1,1,batch,device="cuda")
        self.returns = self.advantages.clone()
        self.old_logp_floor = -float(np.log(self.mask.shape[-1]))
        self.health = torch.zeros((),dtype=torch.int32,device="cuda")
        self.failed = False
        self.last_grad_norm = None
        self.prepare_targets()
        self.optimizer = torch.optim.AdamW(self.model.parameters(),lr=3e-4,fused=True,capturable=mode=="graph")
        self.step = self.eager_step
        self.owner = None
        if mode == "graph":
            from gpu_bench import capture
            self.step,self.owner = capture(self.eager_step)
        else:
            # Pay lazy optimizer/kernel setup before the measured interval,
            # just as the graph capture path does with its warm-up updates.
            for _ in range(4):
                self.eager_step()
        torch.cuda.synchronize()
        self.check_health("warmup")
        self.health.zero_()

    def prepare_targets(self):
        with torch.no_grad(),torch.autocast("cuda",dtype=torch.bfloat16,enabled=self.actor.precision=="bf16"):
            logits,_ = self.model(self.obs,self.candidates,self.mask)
            # Explicit masking keeps argmax legal even if a finite mask sentinel
            # were to exceed an unusually low legal logit. The lower-bound check
            # below also rejects a softmax dominated by such invalid sentinels.
            self.selected.copy_(logits.masked_fill(~self.mask,-torch.inf).argmax(-1))
            self.old_logp.copy_(logits.log_softmax(-1).gather(1,self.selected[:,None]).squeeze(1))
            self.returns.copy_(self.advantages)
            invalid = (~torch.isfinite(self.old_logp)).any() | (self.old_logp < self.old_logp_floor-1e-5).any()
            invalid = invalid | (~self.mask.gather(1,self.selected[:,None])).any()
            self.health.bitwise_or_(invalid.to(torch.int32)*4)

    def record_health(self, loss, grad_norm):
        # Device-only sticky flags participate in captured execution. An earlier
        # bad update cannot disappear behind a finite final loss. No per-update
        # CPU read/synchronization or policy-ratio clamp is introduced.
        with torch.no_grad():
            self.health.bitwise_or_((~torch.isfinite(loss)).to(torch.int32))
            self.health.bitwise_or_((~torch.isfinite(grad_norm)).to(torch.int32)*2)

    def check_health(self, phase="updates"):
        flags = int(self.health.item())
        if flags:
            self.failed = True
            reasons = [name for bit,name in ((1,"nonfinite loss"),(2,"nonfinite pre-clip gradient norm"),
                                             (4,"invalid legal-argmax probability bound")) if flags & bit]
            raise RuntimeError(f"Invalid synthetic optimizer load during {phase}: {', '.join(reasons)}; discard this disposable learner")

    def validate_parameters(self):
        """One final parameter check, outside the measured workload interval."""
        self.check_health("final validation")
        if not bool(torch.stack([torch.isfinite(parameter).all() for parameter in self.model.parameters()]).all()):
            self.failed = True
            raise RuntimeError("Nonfinite final disposable learner parameters")
        return {"all_parameters_finite": True, "sticky_loss_and_gradient_checks_passed": True,
                "legal_argmax_old_logp_bound_passed": True, "semantic_version": self.SEMANTIC_VERSION}

    def eager_step(self):
        self.optimizer.zero_grad(set_to_none=True)
        with torch.autocast("cuda",dtype=torch.bfloat16,enabled=self.actor.precision=="bf16"):
            logits,values = self.model(self.obs,self.candidates,self.mask)
        loss = ppo_loss(logits,values,self.selected,self.old_logp,self.advantages,self.returns)
        loss.backward()
        self.last_grad_norm = torch.nn.utils.clip_grad_norm_(self.model.parameters(),.5,foreach=True)
        self.record_health(loss,self.last_grad_norm)
        self.optimizer.step()
        return loss

    def run(self, steps):
        if self.failed:
            raise RuntimeError("A failed disposable learner cannot be reused")
        if not 1 <= steps <= 16:
            raise ValueError("Synthetic optimizer load requires 1..16 updates per call")
        self.health.zero_()
        ring = self.actor.rollout
        if ring is None:
            self.obs.copy_(self.actor.obs[self.index])
            self.candidates.copy_(self.actor.candidates[self.index])
            self.mask.copy_(self.actor.mask[self.index].bool())
        else:
            if ring.valid_rows == 0:
                raise RuntimeError("Cannot learn from an empty owned rollout")
            # GPU-only random indices; the count is exact up to the bounded
            # 32768 rows, and no per-minibatch count .item() forces a host wait.
            self.random_rows.uniform_()
            self.random_rows.mul_(ring.count)
            self.index.copy_(self.random_rows.long())
            torch.index_select(ring.obs,0,self.index,out=self.obs)
            torch.index_select(ring.candidates,0,self.index,out=self.candidates)
            torch.index_select(ring.mask,0,self.index,out=self.mask_float)
            self.mask.copy_(self.mask_float)
            torch.index_select(ring.packet,0,self.index,out=self.behavior_packet)
            # Keep the original packet for storage/gather validation. Its actor
            # actions and likelihoods intentionally do not drive this toy loss.
        self.prepare_targets()
        for _ in range(steps):
            self.step()
        torch.cuda.synchronize()
        # Group-boundary rejection detects every captured loss/gradient failure.
        # This is not transactional rollback: discard the disposable copy on a
        # failure; the separately frozen actor remains untouched.
        self.check_health()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--batch", type=int, default=256)
    parser.add_argument("--workers", type=int, default=4)
    parser.add_argument("--seed", type=int, default=17)
    parser.add_argument("--device", choices=("transport", "cpu", "cuda"), default="cuda")
    parser.add_argument("--width", type=int, default=256)
    parser.add_argument("--scorer", default="embedded")
    parser.add_argument("--precision", choices=("fp32","tf32","bf16"), default="bf16")
    parser.add_argument("--mode", choices=("eager","graph","compile"), default="eager")
    parser.add_argument("--cpu-ragged", action="store_true", help="CPU/eager/embedded/FP32 sparse observations and all original legal candidate slots")
    parser.add_argument("--rollout-rows", type=int, default=0,
                        help="Opt-in owned GPU ring capacity, batch..32768; append is inside the actor's existing completion boundary")
    parser.add_argument("--pageable", action="store_true")
    parser.add_argument("--transfer-dtype",choices=("fp32","fp16"),default="fp32")
    parser.add_argument("--server-gc", action="store_true")
    parser.add_argument("--transport", choices=("pipe","shared"), default="pipe")
    parser.add_argument("--shared-copy", choices=("accessor","span"), default="span")
    parser.add_argument("--split-branches", type=int, choices=(2,8,32,64), default=2)
    parser.add_argument("--seconds", type=float, default=10)
    parser.add_argument("--warmup", type=float, default=2)
    parser.add_argument("--warmup-steps", type=int, default=32)
    parser.add_argument("--action-source", choices=("model","exercise"), default="model",
                        help="exercise selects a deterministic probe stream after still running inference")
    parser.add_argument("--learner-every", type=int, default=0)
    parser.add_argument("--learner-steps", type=int, default=1)
    parser.add_argument("--learner-batch", type=int, default=2048)
    parser.add_argument("--learner-mode", choices=("eager","graph"), default="eager")
    parser.add_argument("--output", required=True)
    args = parser.parse_args()
    if not 1 <= args.batch <= 4096 or not 0 < args.seconds <= 60 or not 0 <= args.warmup <= 10:
        parser.error("Bounded benchmark requires batch1..4096, seconds<=60, warmup<=10")
    if not 1<=args.workers<=16 or not 0<=args.learner_every<=4096 or not 1<=args.learner_steps<=16 or not 1<=args.learner_batch<=8192 or not 0<=args.warmup_steps<=4096:
        parser.error("Require workers1..16, learner-every0..4096, learner-steps1..16, learner-batch1..8192, warmup-steps0..4096")
    if args.device != "cuda" and (args.mode != "eager" or args.learner_every):
        parser.error("Graph/compile/learner load require CUDA in this preflight")
    if args.cpu_ragged and (args.device, args.mode, args.scorer, args.precision, args.transfer_dtype) != ("cpu", "eager", "embedded", "fp32", "fp32"):
        parser.error("--cpu-ragged requires --device cpu --mode eager --scorer embedded --precision fp32 --transfer-dtype fp32")
    if args.rollout_rows and (not args.batch <= args.rollout_rows <= 32768 or args.device != "cuda"
                             or args.mode not in ("eager", "graph") or args.transfer_dtype != "fp32"):
        parser.error("--rollout-rows requires batch<=rows<=32768, CUDA/eager or graph, and FP32 transfer")
    torch.set_num_threads(1)
    torch.set_num_interop_threads(1)
    torch.manual_seed(args.seed)
    torch.backends.fp32_precision = "ieee"
    torch.backends.cuda.matmul.fp32_precision = "tf32" if args.precision == "tf32" else "ieee"
    result = {"metadata": metadata(), "configuration": vars(args),
              "kind": "frozen_exercise_policy_real_engine_pipeline",
              "inference_executed":args.device!="transport",
              "upload_timing_scope":"CPU conversion plus H2D plus GPU expansion" if args.transfer_dtype=="fp16" else "upload dispatch and H2D interval",
              "learner_targets": "own-learner legal argmax and fixed fabricated returns in [-1,1]; no training claim",
              "learner_load_semantics": LearnerLoad.SEMANTIC_VERSION if args.learner_every else None}
    if args.cpu_ragged:
        result["upload_timing_scope"] = "CPU aliases Host upload storage; synchronous inference completes before Host.advance"
        result["cpu_ragged_scheduling_scope"] = "One-thread synchronous CPU actor; concurrent CPU acting/GPU learning is not implemented or measured"
    if args.rollout_rows:
        result["rollout_scope"] = {
            "storage": "Independent FP32 observations/candidates/masks and sampled actor action/logp/value; bounded overwriting GPU ring",
            "append": "Within Actor.compute before its existing D2H completion wait; sequential collection and learner load",
            "learner_sampling": "Uniform stored rows with replacement into private learner buffers; original actor packets retained for gather checks, not used as synthetic loss actions",
            "target_semantics": "Own-learner legal argmax, its current selected old logp, fixed fabricated advantages/returns in [-1,1]; no outcome or trajectory training claim",
            "executed_action_matches_stored_actor_action": args.action_source == "model",
            "exercise_action_note": "Exercise mode executes its separate seeded action stream; stored actor samples remain legal but are not executed trajectories",
        }
    host = Host(args.batch,args.workers,args.seed,pinned=not args.pageable and args.device=="cuda",
                server_gc=args.server_gc,transport=args.transport,split_branches=args.split_branches,shared_copy=args.shared_copy)
    try:
        actor = None if args.device == "transport" else Actor(host,args.device,args.width,args.scorer,args.precision,args.mode,args.transfer_dtype,args.cpu_ragged,args.rollout_rows)
        if args.cpu_ragged:
            result["cpu_ragged_correctness"] = actor.cpu_ragged_checks
        if args.learner_every:
            learner = LearnerLoad(actor,args.learner_batch,args.learner_mode)
        with Monitor() as monitor:
            # Fixed step count avoids warming different game populations merely
            # because one implementation is faster. --warmup remains an upper
            # bound; workloads that reach it report the actual warmup count.
            rng = np.random.default_rng(args.seed)
            def choose_exercise():
                weights = np.exp(host.candidates[:,:,:16] @ np.asarray(
                    [4,2,2,1,3,2,1,1,0,1,-4,-20,0,0,0,0],dtype=np.float32)) * host.mask
                cumulative = weights.cumsum(axis=1)
                draws = rng.random(args.batch) * cumulative[:,-1]
                return (cumulative < draws[:,None]).sum(axis=1).astype(np.int32)
            deadline = time.perf_counter() + args.warmup
            warmed = 0
            while warmed < args.warmup_steps and time.perf_counter() < deadline:
                action, _ = (None, None) if actor is None else actor.act()
                if args.action_source == "exercise":
                    action = choose_exercise()
                host.advance(action)
                warmed += 1
            initial = host.metrics.copy()
            iterations, example_passes, learner_seconds = 0,0,0.
            service, host_times, stage_times, host_step, host_encode = [],[],[],[],[]
            legal_count, finished, truncated = [],0,0
            start = time.perf_counter()
            while time.perf_counter() - start < args.seconds:
                tick = time.perf_counter()
                action, stages = (None,[0,0,0]) if actor is None else actor.act()
                if args.action_source == "exercise":
                    action = choose_exercise()
                service.append(time.perf_counter()-tick)
                stage_times.append(stages)
                legal_count.extend(host.mask.sum(axis=1).tolist())
                if args.learner_every and iterations % args.learner_every == 0:
                    tick = time.perf_counter()
                    learner.run(args.learner_steps)
                    learner_seconds += time.perf_counter()-tick
                    example_passes += args.learner_batch*args.learner_steps
                tick = time.perf_counter()
                host.advance(action)
                host_times.append(time.perf_counter()-tick)
                host_step.append(host.metrics[0])
                host_encode.append(host.metrics[1])
                finished += int((host.done==1).sum())
                truncated += int((host.done==2).sum())
                iterations += 1
            elapsed = time.perf_counter()-start
        delta = host.metrics-initial
        result.update({"seconds":elapsed,"iterations":iterations,"warmup_steps":warmed,
                       "wrapper_decisions":int(delta[2]),"engine_submissions":int(delta[3]),
                       "completed_games":finished,"truncated_games":truncated,
                       "wrapper_decisions_per_second":delta[2]/elapsed,
                       "engine_submissions_per_second":delta[3]/elapsed,
                       "response_rows_per_second":iterations*args.batch/elapsed,
                       "games_per_second":finished/elapsed,
                       "optimizer_example_passes":example_passes,
                       "optimizer_seconds":learner_seconds,
                       "actor_service_seconds":sum(service),"host_roundtrip_seconds":sum(host_times),
                       "host_step_seconds":sum(host_step)/1000,"host_encode_seconds":sum(host_encode)/1000,
                       "host_transport_and_bookkeeping_seconds":sum(host_times)-(sum(host_step)+sum(host_encode))/1000,
                       "actor_service_ms":distribution([x*1000 for x in service]),
                       "host_roundtrip_ms":distribution([x*1000 for x in host_times]),
                       "host_step_ms":distribution(host_step),"host_encode_ms":distribution(host_encode),
                       "gpu_stage_mean_ms":dict(zip(("upload","forward_sample","download"),np.mean(stage_times,axis=0).tolist())),
                       "legal_candidates":distribution(legal_count),
                       "single_legal_action_fraction":sum(x==1 for x in legal_count)/max(1,len(legal_count)),
                       "mean_legal_candidate_occupancy":sum(legal_count)/max(1,len(legal_count))/64,
                       "resident_logged_events_end":host.metrics[6],"managed_heap_bytes_end":host.metrics[7],
                       "cold_actor_seconds":actor.cold_start if actor else 0,
                       "gpu_monitor":monitor.summary()})
        if args.rollout_rows:
            # These synchronizing checks are outside the measured interval.
            result["rollout_correctness"] = actor.validate_rollout()
            result["rollout_correctness"]["warmup_rows"] = warmed * args.batch
            result["rollout_correctness"]["measured_appended_rows"] = iterations * args.batch
        if args.learner_every:
            result["learner_numerical"] = learner.validate_parameters()
        host.close()
        result["host_runtime_diagnostics"] = host.diagnostics
        save_json(args.output,result)
        print({k:result[k] for k in ("wrapper_decisions_per_second","engine_submissions_per_second","completed_games","truncated_games","actor_service_seconds","host_roundtrip_seconds","optimizer_seconds")})
    finally:
        host.close()


if __name__ == "__main__":
    main()
