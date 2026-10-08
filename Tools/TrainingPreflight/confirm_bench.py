"""Interleaved confirmation with a bounded wait for quiet GPU periods.

Heuristics flag likely contention; they cannot prove machine exclusivity. Every
attempt is kept, including slow/contended results. No applications are stopped.
"""
import argparse
import json
import os
from pathlib import Path
import signal
import subprocess
import sys
import time

from bench_common import ROOT, gpu_status, save_json

HERE=Path(__file__).resolve().parent


def command(name,seconds):
    base=[sys.executable,str(HERE/"pipeline_bench.py"),"--batch","256","--workers","4",
          "--transport","shared","--mode","graph","--precision","tf32",
          "--action-source","exercise","--seconds",str(seconds)]
    variants={
        "pipe":["--transport","pipe"],
        "span":[],
        "split8":["--split-branches","8"],
        "batch512":["--batch","512","--split-branches","8"],
        "workers8":["--workers","8","--split-branches","8"],
        "servergc":["--server-gc","--split-branches","8"],
        "half-upload":["--transfer-dtype","fp16","--split-branches","8"],
    }
    if name in variants:
        return base+variants[name]
    if name=="overlap":
        return [sys.executable,str(HERE/"overlap_bench.py"),"--groups","2","--batch-per-group","128",
                "--workers-per-group","2","--transport","shared","--shared-copy","span",
                "--split-branches","8","--action-source","exercise","--seconds",str(seconds)]
    raise ValueError(name)


def quiet_before(status):
    return bool(status) and status["memory_mib"]<8192 and status["utilization_percent"]<=25 and status["power_w"]<180


def quiet_during(result):
    m=result.get("gpu_monitor",{})
    return bool(m) and m["memory_mib"].get("max",1e9)<8192 and m["power_w"].get("max",1e9)<180


def main():
    def interrupted(signum, frame):
        raise KeyboardInterrupt
    signal.signal(signal.SIGTERM,interrupted)
    p=argparse.ArgumentParser(description=__doc__)
    p.add_argument("--cases",default="pipe,span,split8,batch512,workers8,servergc,half-upload,overlap")
    p.add_argument("--repeats",type=int,default=2)
    p.add_argument("--seconds",type=float,default=6)
    p.add_argument("--max-seconds",type=float,default=600)
    p.add_argument("--max-attempts",type=int,default=48)
    p.add_argument("--output-dir",type=Path,required=True)
    a=p.parse_args()
    if not 0<a.max_seconds<=1200 or not 1<=a.repeats<=5 or not 0<a.seconds<=30 or not 1<=a.max_attempts<=100:
        p.error("Use bounded confirmation: total<=1200, repeats<=5, case<=30, attempts<=100")
    names=a.cases.split(",")
    for name in names:command(name,a.seconds)
    a.output_dir.mkdir(parents=True,exist_ok=True)
    manifest={"scope":"contention-screened confirmation, not exclusive-machine proof", "arguments":vars(a)|{"output_dir":str(a.output_dir)},
              "quiet_criteria":"before memory<8GiB,util<=25%,power<180W; during memory<8GiB,power<180W",
              "valid_counts":{name:0 for name in names},"attempts":[]}
    start=time.monotonic();attempt=0;cursor=0
    while attempt<a.max_attempts and time.monotonic()-start<a.max_seconds and min(manifest["valid_counts"].values())<a.repeats:
        name=names[cursor%len(names)];cursor+=1
        if manifest["valid_counts"][name]>=a.repeats:continue
        # Three consecutive observations reduce false quiet detections between
        # dispatches. Wait at most15s, then rotate without launching a case.
        deadline=min(start+a.max_seconds,time.monotonic()+15)
        consecutive=0
        while time.monotonic()<deadline and consecutive<3:
            status=gpu_status();consecutive=consecutive+1 if quiet_before(status) else 0
            if consecutive<3:time.sleep(.5)
        if consecutive<3:continue
        attempt+=1
        output=a.output_dir/f"{name}-attempt{attempt:02}.json"
        cmd=command(name,a.seconds)+["--output",str(output.resolve())]
        print(f"Attempt {attempt}: {name}",flush=True)
        process=subprocess.Popen(cmd,cwd=ROOT,stdout=subprocess.PIPE,stderr=subprocess.PIPE,text=True,start_new_session=True)
        entry={"name":name,"command":cmd,"gpu_before":status}
        try:
            stdout,stderr=process.communicate(timeout=max(.1,min(60,start+a.max_seconds-time.monotonic())))
            if process.returncode==0:
                result=json.loads(output.read_text())
                entry["quiet"]=quiet_during(result)
                entry["status"]="ok"
                entry["wrapper_per_second"]=result["wrapper_decisions_per_second"]
                if entry["quiet"]:manifest["valid_counts"][name]+=1
            else:
                entry["status"]="error"
            if stderr or process.returncode:(a.output_dir/f"{name}-attempt{attempt:02}.log").write_text(stdout+"\n"+stderr)
        except subprocess.TimeoutExpired:
            try:
                os.killpg(process.pid,signal.SIGKILL)
            except ProcessLookupError:
                pass
            stdout,stderr=process.communicate()
            entry["status"]="timeout"
        except BaseException:
            try:
                os.killpg(process.pid,signal.SIGKILL)
            except ProcessLookupError:
                pass
            process.communicate()
            entry["status"]="interrupted"
            manifest["attempts"].append(entry)
            save_json(a.output_dir/"manifest.json",manifest)
            raise
        manifest["attempts"].append(entry)
        save_json(a.output_dir/"manifest.json",manifest)
    manifest["elapsed_seconds"]=time.monotonic()-start
    manifest["all_requested_quiet_repeats_collected"]=min(manifest["valid_counts"].values())>=a.repeats
    save_json(a.output_dir/"manifest.json",manifest)
    print(manifest["valid_counts"],flush=True)


if __name__=="__main__":main()
