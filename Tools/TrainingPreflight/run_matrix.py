"""Sequential bounded preflight suites. No command launches a training campaign."""
from __future__ import annotations

import argparse
import json
import os
from pathlib import Path
import signal
import subprocess
import sys
import time

from bench_common import DOTNET, ROOT, save_json, source_fingerprint

HERE = Path(__file__).resolve().parent


def suites(stage, repeats, seconds):
    cases = []
    if stage == "cpu":
        for repeat in range(repeats):
            # Interleave modes/worker counts rather than running all replicates
            # of one variant while clock/thermal state may drift.
            for workers in (1,4,8,12):
                for mode in ("engine","encode"):
                    name = f"cpu-{mode}-w{workers}-r{repeat}"
                    cases.append((name,[DOTNET,str(HERE/"Host/bin/Release/net8.0/TrainingHost.dll"),
                                        "benchmark","5000",str(workers),mode,"17000"],{},True))
            for mode in ("engine","encode","snapshot"):
                name = f"snapshot-comparison-{mode}-r{repeat}"
                cases.append((name,[DOTNET,str(HERE/"Host/bin/Release/net8.0/TrainingHost.dll"),
                                    "benchmark","300","4",mode,"17000"],{},True))
    elif stage == "pipeline":
        variants = [
            ("pipe-eager-bf16", ["--mode","eager","--precision","bf16"]),
            ("pipe-graph-tf32", []),
            ("shared-accessor", ["--transport","shared","--shared-copy","accessor"]),
            ("shared-graph-tf32", ["--transport","shared"]),
            ("shared-b64", ["--transport","shared","--batch","64"]),
            ("shared-b1024", ["--transport","shared","--batch","1024"]),
            ("shared-w1", ["--transport","shared","--workers","1"]),
            ("shared-w8", ["--transport","shared","--workers","8"]),
            ("shared-w12", ["--transport","shared","--workers","12"]),
            ("shared-split8", ["--transport","shared","--split-branches","8"]),
            ("shared-split32", ["--transport","shared","--split-branches","32"]),
            ("shared-split64", ["--transport","shared","--split-branches","64"]),
            ("shared-servergc", ["--transport","shared","--server-gc"]),
        ]
        for repeat in range(repeats):
            for name,extra in variants if repeat % 2 == 0 else reversed(variants):
                cases.append((f"{name}-r{repeat}",[
                    sys.executable,str(HERE/"pipeline_bench.py"),"--batch","256","--workers","4",
                    "--width","256","--mode","graph","--precision","tf32","--action-source","exercise",
                    "--seconds",str(seconds),*extra],{},False))
    elif stage == "validation":
        cases.append(("learner-health",[sys.executable,str(HERE/"check_learner_health.py")],{},False))
        cases.append(("ring-correctness",[sys.executable,str(HERE/"pipeline_rollout_check.py"),
                                         "--with-learner"],{},False))
        for name,extra in (("learner",[]),("ring-learner",["--rollout-rows","16384"])):
            cases.append((f"soak-{name}",[sys.executable,str(HERE/"soak_bench.py"),
                "--seconds",str(seconds),"--batch","256","--workers","4","--split-branches","8",
                "--learner-every","8","--learner-steps","3","--memory-after-gc",*extra],{},False))
    elif stage == "compile":
        for kind, batches in (("actor","256,1024"),("learner","1024,2048")):
            cases.append((f"compile-{kind}",[
                sys.executable,str(HERE/"gpu_bench.py"),"--kinds",kind,"--widths","256",
                "--batches",batches,"--scorers","embedded","--precisions","tf32,bf16",
                "--modes","compile","--seconds",".5","--repeats","3","--max-cases","4",
                "--max-total-seconds","500"],{},False))
    elif stage == "crossover":
        cases.append(("cpu-inference",[
            sys.executable,str(HERE/"gpu_bench.py"),"--kinds","actor","--widths","128,256",
            "--batches","1,8,32,128","--scorers","embedded","--precisions","fp32",
            "--modes","eager","--device","cpu","--seconds",".3","--repeats","3",
            "--max-cases","8","--max-total-seconds","200"],{},False))
        cases.append(("gpu-small-inference",[
            sys.executable,str(HERE/"gpu_bench.py"),"--kinds","actor","--widths","128,256",
            "--batches","1,8,32,128","--scorers","embedded","--precisions","tf32",
            "--modes","graph","--seconds",".3","--repeats","3",
            "--max-cases","8","--max-total-seconds","200"],{},False))
    elif stage in ("load", "rollout", "fused", "fused-gpu", "cpu-ragged"):
        base=[sys.executable,str(HERE/"pipeline_bench.py"),"--batch","256","--workers","4",
              "--width","256","--transport","shared","--shared-copy","span",
              "--split-branches","8","--mode","graph","--precision","tf32",
              "--action-source","exercise","--seconds",str(seconds)]
        overlap=[sys.executable,str(HERE/"overlap_bench.py"),"--groups","2",
                 "--batch-per-group","128","--workers-per-group","2",
                 "--split-branches","8","--actor-mode","graph","--precision","tf32",
                 "--transport","shared","--shared-copy","span","--action-source","exercise",
                 "--seconds",str(seconds)]
        load=["--learner-every","8","--learner-steps","3","--learner-batch","2048"]
        if stage=="load":
            variants=[("actor",base,{}),
                      ("learner-eager",base+load+["--learner-mode","eager"],{}),
                      ("learner-graph",base+load+["--learner-mode","graph"],{}),
                      ("overlap-actor",overlap,{}),
                      ("overlap-learner",overlap+["--learner-every","16","--learner-steps","3",
                                                "--learner-batch","2048","--learner-mode","graph"],{}),
                      ("half-upload",base+["--transfer-dtype","fp16"],{})]
        elif stage=="rollout":
            ring=["--rollout-rows","16384"]
            variants=[("actor",base,{}),
                      ("actor-ring",base+ring,{}),
                      ("learner-eager",base+load+["--learner-mode","eager"],{}),
                      ("learner-graph",base+load+["--learner-mode","graph"],{}),
                      ("learner-ring",base+load+ring+["--learner-mode","graph"],{}),
                      ("overlap-learner",overlap+["--learner-every","16","--learner-steps","3",
                                                "--learner-batch","2048","--learner-mode","graph"],{})]
        elif stage=="cpu-ragged":
            cpu=base+["--device","cpu","--mode","eager","--precision","fp32"]
            variants=[("dense-cpu-128",cpu+["--width","128"],{}),
                      ("ragged-cpu-128",cpu+["--width","128","--cpu-ragged"],{}),
                      ("ragged-cpu-256",cpu+["--width","256","--cpu-ragged"],{}),
                      ("graph-gpu-256",base,{})]
        else:
            if stage=="fused":
                base += ["--device","transport","--mode","eager"]
            variants=[(f"{name}-w{workers}",base+["--workers",str(workers)],{"SHARDS_FUSED_SERVE":flag})
                      for workers in (4,8) for name,flag in (("baseline","0"),("fused","1"))]
        for repeat in range(repeats):
            for name,command,env in variants if repeat%2==0 else reversed(variants):
                cases.append((f"{name}-r{repeat}",command.copy(),env,False))
    return cases


def main():
    def interrupted(signum, frame):
        raise KeyboardInterrupt
    signal.signal(signal.SIGTERM,interrupted)
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument("--stage",choices=("cpu","pipeline","compile","crossover","load","rollout","validation","fused","fused-gpu","cpu-ragged"),required=True)
    p.add_argument("--repeats",type=int,default=3)
    p.add_argument("--seconds",type=float,default=8)
    p.add_argument("--max-seconds",type=float,default=1200)
    p.add_argument("--output-dir",type=Path,required=True)
    args = p.parse_args()
    if not 1 <= args.repeats <= 5 or not 0 < args.seconds <= 60 or not 0 < args.max_seconds <= 1800:
        p.error("Preflight bounds: repeats<=5, seconds<=60, total<=1800")
    args.output_dir.mkdir(parents=True,exist_ok=True)
    manifest = {"stage":args.stage,"source_sha256":source_fingerprint(),
                "campaign_training_started":False,"jobs":[]}
    start = time.monotonic()
    for name,command,extra_env,capture_json in suites(args.stage,args.repeats,args.seconds):
        remaining = args.max_seconds-(time.monotonic()-start)
        if remaining < 5:
            manifest["stopped_at_budget"] = True
            break
        output = args.output_dir/f"{name}.json"
        if not capture_json:
            command.extend(["--output",str(output.resolve())])
        print(name,flush=True)
        env = os.environ.copy()
        env.update(extra_env)
        env.setdefault("TORCHINDUCTOR_CACHE_DIR","/home/lva/.cache/shards-preflight/inductor")
        tick = time.monotonic()
        process = subprocess.Popen(command,cwd=ROOT,env=env,stdout=subprocess.PIPE,stderr=subprocess.PIPE,
                                   text=True,start_new_session=True)
        job = {"name":name,"command":command,"environment_overrides":extra_env}
        try:
            stdout,stderr = process.communicate(timeout=min(600 if args.stage=="compile" else 240,remaining))
            job.update({"returncode":process.returncode,"status":"ok" if process.returncode==0 else "error"})
            if capture_json and process.returncode==0:
                save_json(output,json.loads(stdout))
            if stderr or process.returncode:
                (args.output_dir/f"{name}.log").write_text(stdout+"\n"+stderr)
        except subprocess.TimeoutExpired:
            try:
                os.killpg(process.pid,signal.SIGKILL)
            except ProcessLookupError:
                pass
            stdout,stderr = process.communicate()
            job["status"]="timeout"
            (args.output_dir/f"{name}.log").write_text(stdout+"\n"+stderr)
        except BaseException:
            try:
                os.killpg(process.pid,signal.SIGKILL)
            except ProcessLookupError:
                pass
            process.communicate()
            job["status"]="interrupted"
            manifest["jobs"].append(job)
            save_json(args.output_dir/"manifest.json",manifest)
            raise
        job["elapsed_seconds"]=time.monotonic()-tick
        manifest["jobs"].append(job)
        save_json(args.output_dir/"manifest.json",manifest)
        if job["status"] != "ok":
            print(f"{name}: {job['status']}; inspect saved log",flush=True)
    manifest["elapsed_seconds"]=time.monotonic()-start
    manifest["requested_jobs"]=len(suites(args.stage,args.repeats,args.seconds))
    save_json(args.output_dir/"manifest.json",manifest)
    if any(job["status"] != "ok" for job in manifest["jobs"]) or len(manifest["jobs"]) != manifest["requested_jobs"]:
        raise SystemExit(1)


if __name__ == "__main__":
    main()
