"""Short diagnostic trace, separate from unprofiled throughput measurements."""
import argparse
from pathlib import Path

import torch

from bench_common import gpu_status, save_json, source_fingerprint
from pipeline_bench import Actor, Host


def main():
    p=argparse.ArgumentParser(description=__doc__)
    p.add_argument("--batch",type=int,default=256)
    p.add_argument("--workers",type=int,default=4)
    p.add_argument("--seed",type=int,default=17)
    p.add_argument("--split-branches",type=int,choices=(2,8,32,64),default=8)
    p.add_argument("--warmup-batches",type=int,default=512)
    p.add_argument("--profile-batches",type=int,default=20)
    p.add_argument("--transport",choices=("pipe","shared"),default="shared")
    p.add_argument("--precision",choices=("fp32","tf32","bf16"),default="tf32")
    p.add_argument("--mode",choices=("eager","graph"),default="graph")
    p.add_argument("--output",type=Path,required=True)
    args=p.parse_args()
    if not 1<=args.batch<=2048 or not 1<=args.workers<=16 or not 0<=args.warmup_batches<=2048 or not 1<=args.profile_batches<=100:
        p.error("Bounded diagnostic: batch<=2048, workers<=16, warmup<=2048, profile<=100")
    torch.set_num_threads(1)
    torch.manual_seed(args.seed)
    torch.backends.fp32_precision="ieee"
    torch.backends.cuda.matmul.fp32_precision="tf32" if args.precision=="tf32" else "ieee"
    before=gpu_status()
    host=Host(args.batch,args.workers,args.seed,pinned=True,transport=args.transport,
              split_branches=args.split_branches,shared_copy="span")
    try:
        actor=Actor(host,"cuda",256,"embedded",args.precision,args.mode)
        for _ in range(args.warmup_batches):
            actions,_=actor.act()
            host.advance(actions)
        initial=host.metrics.copy()
        stages=[]
        with torch.profiler.profile(activities=[torch.profiler.ProfilerActivity.CPU,
                                                torch.profiler.ProfilerActivity.CUDA],
                                    record_shapes=True) as profiler:
            for _ in range(args.profile_batches):
                with torch.profiler.record_function("actor_upload_forward_sample_download"):
                    actions,_=actor.act()
                with torch.profiler.record_function("engine_encode_transport"):
                    host.advance(actions)
                    stages.append(host.metrics[:2].tolist())
        args.output.parent.mkdir(parents=True,exist_ok=True)
        trace=Path("/home/lva/.cache/shards-preflight")/(args.output.stem+".trace.json")
        trace.parent.mkdir(parents=True,exist_ok=True)
        profiler.export_chrome_trace(str(trace))
        summary={"trace":str(trace),"configuration":vars(args)|{"output":str(args.output)},
                 "source_sha256":source_fingerprint(),"gpu_before":before,"gpu_after":gpu_status(),
                 "cpu_table":profiler.key_averages().table(sort_by="self_cpu_time_total",row_limit=20),
                 "device_table":profiler.key_averages().table(sort_by="self_device_time_total",row_limit=20),
                 "host_step_encode_ms":stages,
                 "warmup_completed_games":int(initial[4]),
                 "profile_completed_games":int(host.metrics[4]-initial[4]),
                 "scope":"Short profiled batches after fixed warmup; diagnostic, not a throughput result"}
        host.close()
        summary["host_runtime_diagnostics"]=host.diagnostics
        save_json(args.output,summary)
        print(summary["cpu_table"])
    finally:
        host.close()


if __name__=="__main__":
    main()
