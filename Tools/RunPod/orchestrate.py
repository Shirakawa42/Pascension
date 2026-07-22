#!/usr/bin/env python3
"""Fan-out SoiSim CPU jobs across RunPod pods, collect the results, ALWAYS tear down.

Secrets come from the repo-root .env (RUNPOD_API_KEY, RUNPOD_S3_KEY) — never hardcoded,
never logged. Non-secret config from Tools/RunPod/config.env. The SoiSim linux-x64
binary lives on the network volume at bin/SoiSim (upload once with `python orchestrate.py upload-binary`).

Flow (run): split N games across K pods -> each pod runs a probe slice, writing .soip +
a result json + a live status file to the mounted volume -> we poll the volume over S3 and
rewrite Tools/ShardsData/runpod-status.md for you to watch -> when all slices finish we
TERMINATE every pod (data is safe on the volume), download the .soip + results over S3,
and aggregate the win rate. Cost guards: max pods, per-run wall-clock kill, guaranteed
teardown in a finally block, an active-pods.json breadcrumb, and `teardown-all`.

Usage:
  python orchestrate.py upload-binary
  python orchestrate.py run --name diamond2000 --pods 8 --vcpu 32 --games 2000 \
        --cmd "probe --a strong --truncate-a 2 --net-a 8 --budget 3200 --b strong \
               --truncate-b 2 --net-b 8 --budget-b 800 --earlystop 0" --record --probe-agg
  python orchestrate.py teardown-all          # nuke every pod on the account
  python orchestrate.py status --name NAME     # one-shot status read
"""
import argparse, json, math, os, sys, time, urllib.request, urllib.error

REST = "https://rest.runpod.io/v1"
GQL = "https://api.runpod.io/graphql"
HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.abspath(os.path.join(HERE, "..", ".."))
ACTIVE = os.path.join(HERE, "active-pods.json")
STATUS_MD = os.path.join(REPO, "Tools", "ShardsData", "runpod-status.md")


def load_env():
    for path in (os.path.join(REPO, ".env"), os.path.join(HERE, "config.env"),
                 os.path.join(HERE, ".secrets")):
        if not os.path.exists(path):
            continue
        for line in open(path, encoding="utf-8"):
            line = line.strip()
            if not line or line.startswith("#") or "=" not in line:
                continue
            k, v = line.split("=", 1)
            os.environ.setdefault(k.strip(), v.strip())


def _http(method, url, body=None, headers=None):
    data = json.dumps(body).encode() if body is not None else None
    req = urllib.request.Request(url, data=data, method=method)
    req.add_header("Authorization", "Bearer " + os.environ["RUNPOD_API_KEY"])
    req.add_header("Content-Type", "application/json")
    for k, v in (headers or {}).items():
        req.add_header(k, v)
    try:
        with urllib.request.urlopen(req, timeout=60) as r:
            raw = r.read().decode()
            return json.loads(raw) if raw.strip() else {}
    except urllib.error.HTTPError as e:
        return {"_error": e.code, "_body": e.read().decode()[:400]}


def rest(method, path, body=None):
    return _http(method, REST + path, body)


_USER_ID = None
_S3 = None


def user_id():
    global _USER_ID
    if _USER_ID:
        return _USER_ID
    env = os.environ.get("RUNPOD_S3_ACCESS_KEY_ID")
    if env:
        _USER_ID = env
        return _USER_ID
    r = {}
    for _ in range(5):
        r = _http("POST", GQL, {"query": "query{myself{id}}"})
        if isinstance(r, dict) and r.get("data", {}).get("myself"):
            _USER_ID = r["data"]["myself"]["id"]
            return _USER_ID
        time.sleep(2)
    raise RuntimeError("could not fetch user id: " + json.dumps(r)[:200])


def s3():
    global _S3
    if _S3 is None:
        import boto3
        _S3 = boto3.client(
            "s3", endpoint_url=os.environ["RUNPOD_S3_ENDPOINT"],
            aws_access_key_id=user_id(), aws_secret_access_key=os.environ["RUNPOD_S3_KEY"],
            region_name=os.environ["RUNPOD_DATACENTER"].lower())
    return _S3


VOL = lambda: os.environ["RUNPOD_VOLUME_ID"]


def s3_list(prefix):
    cl = s3()
    keys = []
    tok = None
    while True:
        kw = {"Bucket": VOL(), "Prefix": prefix}
        if tok:
            kw["ContinuationToken"] = tok
        resp = cl.list_objects_v2(**kw)
        keys += [o["Key"] for o in resp.get("Contents", [])]
        tok = resp.get("NextContinuationToken")
        if not tok:
            break
    return keys


def s3_get_text(key):
    try:
        return s3().get_object(Bucket=VOL(), Key=key)["Body"].read().decode("utf-8", "replace")
    except Exception:
        return None


# ---------------------------------------------------------------- commands

def upload_binary(_):
    path = os.path.join(HERE, "publish", "SoiSim")
    if not os.path.exists(path):
        sys.exit("binary not found: " + path + " (publish it first)")
    s3().upload_file(path, VOL(), "bin/SoiSim")
    mb = os.path.getsize(path) / 1e6
    print(f"uploaded bin/SoiSim ({mb:.1f} MB) to volume {VOL()}")


def _pod_cmd(worker, cli, out_root, outflag, record, aggregate):
    w = f"w{worker:02d}"
    ldata = f"/tmp/{w}"  # record to FAST LOCAL disk — network-volume writes can be
                          # lost when the container terminates before they sync.
    data_arg = f" {outflag} {ldata}" if record else ""
    result_arg = f" --result {out_root}/results/{w}.json" if aggregate else ""
    copy = f"cp {ldata}/*.soip {out_root}/data/{w}/ 2>/dev/null || true" if record else "true"
    parts = [
        "set -e",
        "chmod +x /workspace/bin/SoiSim",
        f"mkdir -p {out_root}/status/{w} {out_root}/data/{w} {out_root}/results {ldata}",
        f"export SOISIM_STATUS_DIR={out_root}/status/{w}",
        f"/workspace/bin/SoiSim {cli}{result_arg}{data_arg} > {out_root}/status/{w}/run.log 2>&1",
        copy,     # copy the COMPLETE local .soip to the volume
        "sync",   # force it to the volume backend BEFORE signalling done
        f"touch {out_root}/results/{w}.done",
    ]
    return " && ".join(parts)


def create_pod(name, docker_cmd, vcpu):
    body = {
        "name": name, "computeType": "CPU", "cloudType": "COMMUNITY",
        "cpuFlavorIds": ["cpu5c", "cpu3c"], "vcpuCount": vcpu,
        "dataCenterIds": [os.environ["RUNPOD_DATACENTER"]],
        "networkVolumeId": VOL(), "imageName": "ubuntu:22.04",
        "containerDiskInGb": 15, "volumeMountPath": "/workspace",
        "dockerStartCmd": ["bash", "-lc", docker_cmd], "ports": [],
    }
    return rest("POST", "/pods", body)


def terminate(pid):
    return rest("DELETE", f"/pods/{pid}")


def teardown(pids):
    for pid in pids:
        try:
            terminate(pid)
            print("terminated", pid)
        except Exception as e:
            print("WARN could not terminate", pid, e)
    if os.path.exists(ACTIVE):
        os.remove(ACTIVE)


def teardown_all(_):
    pods = rest("GET", "/pods")
    ids = [p["id"] for p in pods] if isinstance(pods, list) else []
    print("pods on account:", ids or "none")
    teardown(ids)


def _write_status(name, phase, lines):
    os.makedirs(os.path.dirname(STATUS_MD), exist_ok=True)
    body = (f"# RunPod fan-out — {name}\n\nUpdated: **{time.strftime('%H:%M:%S')}**\n\n"
            f"**Phase**: {phase}\n\n" + "\n".join(lines) + "\n")
    try:
        open(STATUS_MD, "w", encoding="utf-8").write(body)
    except OSError:
        pass


def _parse_progress(md):
    # pull "X / Y games" out of a worker's campaign-status.md
    if not md:
        return None
    import re
    m = re.search(r"(\d[\d,]*)\s*/\s*(\d[\d,]*)\s*games", md)
    if not m:
        return None
    done = int(m.group(1).replace(",", ""))
    tot = int(m.group(2).replace(",", ""))
    return done, tot


def run(args):
    pods_cap = int(os.environ.get("RUNPOD_MAX_PODS", "16"))
    kill_min = int(os.environ.get("RUNPOD_MAX_POD_MINUTES", "90"))
    k = min(args.pods, pods_cap)
    per = math.ceil(args.games / k)
    out_root = f"/workspace/runs/{args.name}"
    print(f"batch {args.name}: {args.games} games / {k} pods = {per} each, {args.vcpu} vCPU, kill@{kill_min}min")

    created, workers = [], []
    try:
        for i in range(k):
            seed = args.seed_base + i * 100000
            cli = f"{args.cmd} --games {per} --seed-base {seed}"
            dcmd = _pod_cmd(i, cli, out_root, args.outflag, args.record, not args.no_aggregate)
            r = create_pod(f"{args.name}-w{i:02d}", dcmd, args.vcpu)
            if isinstance(r, dict) and "id" in r:
                created.append(r["id"]); workers.append(i)
                json.dump(created, open(ACTIVE, "w"))
                print(f"  created w{i:02d} -> {r['id']}")
            else:  # partial availability — skip this slice rather than abort the batch
                print(f"  WARN w{i:02d} create failed (skipping): {json.dumps(r)[:200]}")
        if not workers:
            raise RuntimeError("no pods could be created")
        print(f"  {len(workers)}/{k} pods created")

        # monitor (resilient to transient S3/API hiccups)
        start = time.time()
        while True:
            try:
                done_markers = [k2 for k2 in s3_list(f"runs/{args.name}/results/") if k2.endswith(".done")]
                prog, total_done = [], 0
                for i in workers:
                    w = f"w{i:02d}"
                    pr = _parse_progress(s3_get_text(f"runs/{args.name}/status/{w}/campaign-status.md"))
                    if any(m.endswith(f"{w}.done") for m in done_markers):
                        total_done += per
                        prog.append(f"- {w}: done")
                    elif pr:
                        total_done += pr[0]
                        prog.append(f"- {w}: {pr[0]}/{pr[1]} games")
                    else:
                        prog.append(f"- {w}: starting…")
                elapsed = (time.time() - start) / 60
                planned = len(workers) * per
                _write_status(args.name, f"running ({len(done_markers)}/{len(workers)} pods done)",
                              [f"**Games**: ~{total_done}/{planned} · elapsed {elapsed:.0f} min · {len(workers)}×{args.vcpu} vCPU", ""] + prog)
                print(f"  [{elapsed:.0f}min] {len(done_markers)}/{len(workers)} done, ~{total_done}/{planned} games")
                if len(done_markers) >= len(workers):
                    break
            except Exception as e:
                print("  poll hiccup (continuing):", str(e)[:120])
                elapsed = (time.time() - start) / 60
            if elapsed > kill_min:
                print("!! kill-timeout reached — tearing down")
                break
            time.sleep(20)
    finally:
        teardown(created)

    # download + aggregate (data persists on the volume after teardown)
    _download_and_aggregate(args, k)


def _download_and_aggregate(args, k):
    local = os.path.join(REPO, "Tools", "ShardsData", "selfplay", args.name)
    os.makedirs(local, exist_ok=True)
    cl = s3()
    time.sleep(15)  # let the network-volume writes settle into the S3 view
    n_soip = bytes_dl = 0
    for key in s3_list(f"runs/{args.name}/data/"):
        if key.endswith(".soip"):
            dst = os.path.join(local, key.split("/")[-2] + "-" + key.split("/")[-1])
            want = cl.head_object(Bucket=VOL(), Key=key)["ContentLength"]
            for attempt in range(4):  # retry until the download matches the volume size
                cl.download_file(VOL(), key, dst)
                if os.path.getsize(dst) == want:
                    break
                time.sleep(10)
            n_soip += 1
            bytes_dl += want
    positions = max(0, (bytes_dl - 32 * n_soip)) // 3096
    if args.no_aggregate:
        summary = f"selfplay done: {n_soip} .soip files, ~{positions:,} positions -> {local}"
    else:
        score = decisive = games = 0.0
        for key in s3_list(f"runs/{args.name}/results/"):
            if key.endswith(".json"):
                r = json.loads(s3_get_text(key))
                score += r["score"]; decisive += r["decisive"]; games += r["games"]
        wr = score / decisive if decisive else 0
        lo = hi = 0
        if decisive:
            z = 1.959964; p = wr
            den = 1 + z * z / decisive
            ctr = p + z * z / (2 * decisive)
            adj = z * math.sqrt(p * (1 - p) / decisive + z * z / (4 * decisive * decisive))
            lo, hi = (ctr - adj) / den, (ctr + adj) / den
        summary = (f"A win rate: {wr:.1%} [{lo:.1%}-{hi:.1%}] over {int(games)} games · "
                   f"{n_soip} .soip, ~{positions:,} positions -> {local}")
    print("RESULT:", summary)
    _write_status(args.name, "COMPLETE — pods torn down", [summary])
    open(os.path.join(local, "RESULT.txt"), "w").write(summary + "\n")


def status_cmd(args):
    md = open(STATUS_MD).read() if os.path.exists(STATUS_MD) else "(no status yet)"
    print(md)


def main():
    load_env()
    ap = argparse.ArgumentParser()
    sub = ap.add_subparsers(dest="cmd", required=True)
    sub.add_parser("upload-binary").set_defaults(fn=upload_binary)
    sub.add_parser("teardown-all").set_defaults(fn=teardown_all)
    sp = sub.add_parser("status"); sp.add_argument("--name", default=""); sp.set_defaults(fn=status_cmd)
    r = sub.add_parser("run")
    r.add_argument("--name", required=True)
    r.add_argument("--pods", type=int, required=True)
    r.add_argument("--vcpu", type=int, default=32)
    r.add_argument("--games", type=int, required=True)
    r.add_argument("--seed-base", type=int, default=200000)
    r.add_argument("--cmd", required=True, help="SoiSim args WITHOUT --games/--seed-base/--result/output flag")
    r.add_argument("--record", action="store_true", help="capture .soip training data from each pod")
    r.add_argument("--outflag", default="--record",
                   help="the SoiSim flag that names the .soip output dir: --record (probe) or --out (selfplay)")
    r.add_argument("--no-aggregate", action="store_true",
                   help="skip win-rate aggregation (selfplay data-gen has no --result)")
    r.set_defaults(fn=run)
    args = ap.parse_args()
    args.fn(args)


if __name__ == "__main__":
    main()
