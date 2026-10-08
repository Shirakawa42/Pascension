"""At most five measured minutes of frozen-policy validation and memory sampling.

Use an outer process-group timeout to bound setup, pipe reads and cleanup too.
Optional post-run Python GC/cache cleanup is outside the reported measurement.
"""
import argparse
import gc
import os
import time

import numpy as np
import torch

from bench_common import Monitor, metadata, save_json
from pipeline_bench import Actor, Host, LearnerLoad


def rss_bytes(pid):
    with open(f"/proc/{pid}/status") as source:
        for line in source:
            if line.startswith("VmRSS:"):
                return int(line.split()[1])*1024
    return None


def memory_sample(host):
    return {
        "host_rss_bytes":rss_bytes(host.process.pid),
        "python_rss_bytes":rss_bytes(os.getpid()),
        "managed_heap_bytes":float(host.metrics[7]),
        "resident_events":float(host.metrics[6]),
        "torch_gpu_allocated_bytes":torch.cuda.memory_allocated(),
        "torch_gpu_reserved_bytes":torch.cuda.memory_reserved(),
        "torch_gpu_peak_allocated_bytes":torch.cuda.max_memory_allocated(),
        "torch_gpu_peak_reserved_bytes":torch.cuda.max_memory_reserved(),
    }


@torch.no_grad()
def learner_final_finite_checks(learner):
    """Inspect disposable learner state after timing, including Adam buffers."""
    numerical = learner.validate_parameters()
    parameters = list(learner.model.parameters())
    def tensors(value):
        if torch.is_tensor(value):
            yield value
        elif isinstance(value, dict):
            for item in value.values():
                yield from tensors(item)
        elif isinstance(value, (list, tuple)):
            for item in value:
                yield from tensors(item)
    state_tensors = list(tensors(learner.optimizer.state))
    for state in state_tensors:
        if not bool(torch.isfinite(state).all()):
            raise AssertionError("Nonfinite optimizer tensor state after workload")
    return {**numerical, "copied_learner_parameters_all_finite": True, "parameter_tensors_checked": len(parameters),
            "optimizer_tensor_state_all_finite": True, "optimizer_state_tensors_checked": len(state_tensors),
            "timing_scope": "Checks performed after measured interval"}


def main():
    p=argparse.ArgumentParser(description=__doc__)
    p.add_argument("--seconds",type=float,default=60)
    p.add_argument("--batch",type=int,default=256)
    p.add_argument("--workers",type=int,default=4)
    p.add_argument("--split-branches",type=int,default=8)
    p.add_argument("--server-gc",action="store_true")
    p.add_argument("--learner-every",type=int,default=0)
    p.add_argument("--learner-steps",type=int,default=3)
    p.add_argument("--rollout-rows",type=int,default=0,
                   help="Opt-in independent GPU ring capacity, batch..32768; learner load samples stored rows")
    p.add_argument("--memory-after-gc",action="store_true",
                   help="Report separate post-run Python GC/CUDA cache cleanup; does not request C# GC")
    p.add_argument("--output",required=True)
    args=p.parse_args()
    if not 0 < args.seconds <= 300 or not 1 <= args.batch <= 2048:
        p.error("Probe is bounded to 300 measured seconds and 2048 resident games")
    if not 1 <= args.workers <= 16 or args.split_branches not in (2,8,32,64):
        p.error("Require workers 1..16 and split-branches 2, 8, 32 or 64")
    if not 0 <= args.learner_every <= 4096 or not 1 <= args.learner_steps <= 16:
        p.error("Require learner-every 0..4096 and learner-steps 1..16")
    if args.rollout_rows and not args.batch <= args.rollout_rows <= 32768:
        p.error("Require rollout-rows 0 (disabled), or batch<=rollout-rows<=32768")
    torch.set_num_threads(1)
    torch.manual_seed(60925)
    torch.backends.fp32_precision="ieee"
    torch.backends.cuda.matmul.fp32_precision="tf32"
    result={"metadata":metadata(),"configuration":vars(args),
            "kind":"frozen_model_selected_actions_with_optional_disposable_load","windows":[],
            "learner_semantic_version":LearnerLoad.SEMANTIC_VERSION,
            "learner_targets":"Fixed bounded fabricated returns; copied learner's own legal argmax drives synthetic ratios; no outcome-learning claim"}
    if args.rollout_rows:
        result["rollout_scope"]={
            "storage":"Independent FP32 observations/candidates/masks and sampled actor action/logp/value; bounded overwriting GPU ring",
            "append":"Inside Actor.compute before its existing D2H completion wait; sequential collection and learner load",
            "sampling":"Uniform stored rows with replacement into private learner buffers",
            "actor_packet_semantics":"Stored actor actions match executed original candidate slots; packets are preserved for data-path checks but do not drive fabricated learner ratios",
            "limitations":"No trajectory/reward/policy-version retention guarantee; fabricated learner targets, frozen actor, no strength or training claim",
        }
    host=Host(args.batch,args.workers,60925,pinned=True,server_gc=args.server_gc,
              transport="shared",split_branches=args.split_branches,shared_copy="span")
    try:
        actor=Actor(host,"cuda",256,"embedded","tf32","graph",rollout_rows=args.rollout_rows)
        learner=LearnerLoad(actor,2048,"graph") if args.learner_every else None
        for _ in range(32):
            actions,_=actor.act();host.advance(actions)
        initial=host.metrics.copy()
        torch.cuda.synchronize()
        torch.cuda.reset_peak_memory_stats()
        initial_memory=memory_sample(host)
        iterations=0
        completed=truncated=0
        load_passes=0
        start=window_start=time.perf_counter()
        window_metrics=initial.copy()
        def append_window(now):
            nonlocal window_start,window_metrics
            if now <= window_start:
                return
            d=host.metrics-window_metrics
            memory=memory_sample(host)
            result["windows"].append({"elapsed":now-start,"seconds":now-window_start,
                "wrapper_per_second":d[2]/(now-window_start),"engine_per_second":d[3]/(now-window_start),
                "completed":int(d[4]),"truncated":int(d[5]),
                "rss_bytes":memory["host_rss_bytes"],**memory})
            window_start=now;window_metrics=host.metrics.copy()
        with Monitor() as monitor:
            while time.perf_counter()-start < args.seconds:
                actions,_=actor.act()
                if learner and iterations%args.learner_every==0:
                    learner.run(args.learner_steps)
                    load_passes += 2048*args.learner_steps
                host.advance(actions)
                if not np.isin(host.done,[0,1,2]).all() or not np.isin(host.seats,[0,1]).all():
                    raise RuntimeError("Invalid lifecycle fields")
                if not np.isin(host.rewards,[-1,0,1]).all() or np.any(host.rewards.sum(1)):
                    raise RuntimeError("Invalid zero-sum terminal rewards")
                if np.any(host.rewards[host.done!=1]):
                    raise RuntimeError("Reward on nonterminal/truncated transition")
                completed+=int((host.done==1).sum());truncated+=int((host.done==2).sum())
                iterations+=1
                now=time.perf_counter()
                if now-window_start>=5:
                    append_window(now)
            elapsed=time.perf_counter()-start
        delta=host.metrics-initial
        append_window(start+elapsed)
        if int(delta[2])!=iterations*args.batch or int(delta[4])!=completed or int(delta[5])!=truncated:
            raise RuntimeError("Lifecycle response counts disagree with authoritative host counters")
        final_memory=memory_sample(host)
        result.update({"seconds":elapsed,"wrapper_decisions":int(delta[2]),"engine_submissions":int(delta[3]),
            "wrapper_decisions_per_second":delta[2]/elapsed,"engine_submissions_per_second":delta[3]/elapsed,
            "completed_games":completed,"truncated_games":truncated,"optimizer_example_passes":load_passes,
            "rss_initial_bytes":initial_memory["host_rss_bytes"],"rss_final_bytes":final_memory["host_rss_bytes"],
            "memory_initial":initial_memory,"memory_final":final_memory,
            "memory_scope":"Process RSS includes ordinary resident games and allocator caching; short-run growth alone is not proof of a leak",
            "gpu_monitor":monitor.summary()})
        if args.rollout_rows:
            # Synchronizing ring/actor checks occur after the measured interval.
            result["rollout_correctness"]=actor.validate_rollout()
            result["rollout_correctness"].update(warmup_rows=32*args.batch,
                                                   measured_appended_rows=iterations*args.batch)
        if learner is not None:
            result["learner_numerical"]=learner_final_finite_checks(learner)
        if args.memory_after_gc:
            cleanup_start=time.perf_counter()
            gc.collect()
            torch.cuda.synchronize()
            torch.cuda.empty_cache()
            result["memory_after_python_gc"]=memory_sample(host)
            result["post_measurement_gc_seconds"]=time.perf_counter()-cleanup_start
            result["post_measurement_gc_scope"]="Python gc.collect and torch.cuda.empty_cache only; no C# GC requested; excluded from measured seconds"
        host.close();result["host_runtime_diagnostics"]=host.diagnostics
        save_json(args.output,result)
        print({k:result[k] for k in ("seconds","wrapper_decisions_per_second","engine_submissions_per_second","completed_games","truncated_games","rss_final_bytes")})
    finally:
        host.close()


if __name__=="__main__":
    main()
