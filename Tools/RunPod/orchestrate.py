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


def upload_nets(args):
    cl = s3()
    for net in [n.strip() for n in args.nets.split(",")]:
        d = os.path.join(REPO, "Tools", "ShardsML", "out", net)
        cl.upload_file(os.path.join(d, "weights.bin"), VOL(), f"nets/{net}/weights.bin")
        cl.upload_file(os.path.join(d, "weights.json"), VOL(), f"nets/{net}/weights.json")
        print("uploaded net", net)


def sweep(args):
    """Gate each candidate net on ITS OWN pod, concurrently, then rank by win rate."""
    kill_min = int(os.environ.get("RUNPOD_MAX_POD_MINUTES", "90"))
    nets = [n.strip() for n in args.nets.split(",")]
    out_root = f"/workspace/runs/{args.name}"
    created, mapping = [], []
    try:
        for i, net in enumerate(nets):
            seed = args.seed_base + i * 100000
            cli = (f"/workspace/bin/SoiSim probe --a strong --net-a-file /workspace/nets/{net}/weights.bin "
                   f"{args.gate_cmd} --games {args.games} --seed-base {seed} "
                   f"--result {out_root}/results/{net}.json")
            dcmd = " && ".join([
                "set -e", "chmod +x /workspace/bin/SoiSim",
                f"mkdir -p {out_root}/status/{net} {out_root}/results",
                f"export SOISIM_STATUS_DIR={out_root}/status/{net}",
                f"{cli} > {out_root}/status/{net}/run.log 2>&1",
                f"touch {out_root}/results/{net}.done",
            ])
            r = create_pod(f"{args.name}-{net}", dcmd, args.vcpu)
            if isinstance(r, dict) and "id" in r:
                created.append(r["id"]); mapping.append(net)
                json.dump(created, open(ACTIVE, "w"))
                print(f"  {net} -> {r['id']}")
            else:
                print(f"  WARN {net} create failed: {json.dumps(r)[:200]}")
        if not created:
            raise RuntimeError("no pods could be created")
        start = time.time()
        while True:
            try:
                done = [k for k in s3_list(f"runs/{args.name}/results/") if k.endswith(".done")]
                lines = []
                for net in mapping:
                    pr = _parse_progress(s3_get_text(f"runs/{args.name}/status/{net}/campaign-status.md"))
                    if any(m.endswith(f"{net}.done") for m in done):
                        lines.append(f"- {net}: done")
                    elif pr:
                        lines.append(f"- {net}: {pr[0]}/{pr[1]}")
                    else:
                        lines.append(f"- {net}: starting…")
                elapsed = (time.time() - start) / 60
                _write_status(args.name, f"sweep ({len(done)}/{len(mapping)} done)", [f"elapsed {elapsed:.0f} min"] + lines)
                print(f"  [{elapsed:.0f}min] {len(done)}/{len(mapping)} done")
                if len(done) >= len(mapping):
                    break
            except Exception as e:
                print("  poll hiccup:", str(e)[:120])
                elapsed = (time.time() - start) / 60
            if elapsed > kill_min:
                break
            time.sleep(15)
    finally:
        teardown(created)
    time.sleep(10)
    results = []
    for net in nets:
        txt = s3_get_text(f"runs/{args.name}/results/{net}.json")
        if txt:
            r = json.loads(txt)
            results.append((net, r["wr"], int(r["games"])))
    results.sort(key=lambda x: -x[1])
    print("\n=== SWEEP RESULTS (candidate vs gen-8) ===")
    for net, wr, g in results:
        print(f"  {net}: {wr:.1%}  ({g} games)")
    _write_status(args.name, "SWEEP COMPLETE", [f"- {net}: {wr:.1%} ({g} games)" for net, wr, g in results])


def tournament(args):
    """Round-robin benchmark: many matchups, each fanned across its OWN share of pods,
    all concurrent. Each matchup's slices aggregate to one win rate. Spec is a JSON list
    of {label, cmd, games, pods}. Guaranteed teardown; per-matchup Wilson CI."""
    kill_min = int(os.environ.get("RUNPOD_MAX_POD_MINUTES", "90"))
    pods_cap = int(os.environ.get("RUNPOD_MAX_PODS", "16"))
    spec = json.load(open(args.spec, encoding="utf-8"))
    out_root = f"/workspace/runs/{args.name}"
    total_pods = sum(m["pods"] for m in spec)
    if total_pods > pods_cap:
        sys.exit(f"spec wants {total_pods} pods > cap {pods_cap}")
    print(f"tournament {args.name}: {len(spec)} matchups, {total_pods} pods, kill@{kill_min}min")

    created, slices = [], []  # slices: (label, worker, per_games)
    try:
        for mi, m in enumerate(spec):
            pods = m["pods"]
            per = math.ceil(m["games"] / pods)
            for j in range(pods):
                seed = args.seed_base + mi * 1_000_000 + j * 100_000
                w = f"{m['label']}-w{j}"
                cli = f"/workspace/bin/SoiSim {m['cmd']} --games {per} --seed-base {seed} --result {out_root}/results/{w}.json"
                dcmd = " && ".join([
                    "set -e", "chmod +x /workspace/bin/SoiSim",
                    f"mkdir -p {out_root}/status/{w} {out_root}/results",
                    f"export SOISIM_STATUS_DIR={out_root}/status/{w}",
                    f"{cli} > {out_root}/status/{w}/run.log 2>&1",
                    f"touch {out_root}/results/{w}.done",
                ])
                r = create_pod(f"{args.name}-{w}", dcmd, args.vcpu)
                if isinstance(r, dict) and "id" in r:
                    created.append(r["id"]); slices.append((m["label"], w, per))
                    json.dump(created, open(ACTIVE, "w"))
                    print(f"  {w} -> {r['id']}")
                else:
                    print(f"  WARN {w} create failed: {json.dumps(r)[:160]}")
        if args.require_all and len(created) < total_pods:
            print(f"  require-all: only {len(created)}/{total_pods} pods — tearing down, will retry")
            teardown(created)
            sys.exit(3)
        if not created:
            raise RuntimeError("no pods could be created")
        start = time.time()
        while True:
            try:
                done = [k for k in s3_list(f"runs/{args.name}/results/") if k.endswith(".done")]
                by_label = {}
                for label, w, _ in slices:
                    by_label.setdefault(label, [0, 0])
                    by_label[label][1] += 1
                    if any(m.endswith(f"{w}.done") for m in done):
                        by_label[label][0] += 1
                lines = [f"- {lb}: {d}/{t} pods done" for lb, (d, t) in sorted(by_label.items())]
                elapsed = (time.time() - start) / 60
                _write_status(args.name, f"tournament ({len(done)}/{len(slices)} slices done)",
                              [f"elapsed {elapsed:.0f} min · {len(slices)} slices"] + lines)
                print(f"  [{elapsed:.0f}min] {len(done)}/{len(slices)} slices done")
                if len(done) >= len(slices):
                    break
            except Exception as e:
                print("  poll hiccup:", str(e)[:120])
                elapsed = (time.time() - start) / 60
            if elapsed > kill_min:
                print("!! kill-timeout — tearing down")
                break
            time.sleep(20)
    finally:
        teardown(created)

    time.sleep(12)
    agg = {}  # label -> [score, decisive, games]
    for key in s3_list(f"runs/{args.name}/results/"):
        if not key.endswith(".json"):
            continue
        w = key.split("/")[-1][:-5]
        label = w.rsplit("-w", 1)[0]
        try:
            r = json.loads(s3_get_text(key))
        except Exception:
            continue
        a = agg.setdefault(label, [0.0, 0, 0])
        a[0] += r["score"]; a[1] += r["decisive"]; a[2] += r["games"]
    results = []
    for label, (score, dec, games) in agg.items():
        wr = score / dec if dec else 0
        lo = hi = 0
        if dec:
            z = 1.959964; p = wr; den = 1 + z * z / dec
            ctr = p + z * z / (2 * dec)
            adj = z * math.sqrt(p * (1 - p) / dec + z * z / (4 * dec * dec))
            lo, hi = (ctr - adj) / den, (ctr + adj) / den
        results.append({"label": label, "wr": wr, "lo": lo, "hi": hi, "games": int(games)})
    results.sort(key=lambda x: x["label"])
    print("\n=== TOURNAMENT RESULTS (A win rate) ===")
    for r in results:
        print(f"  {r['label']}: {r['wr']:.1%} [{r['lo']:.1%}-{r['hi']:.1%}] ({r['games']} games)")
    local = os.path.join(REPO, "Tools", "ShardsData", "benchmark", "results")
    os.makedirs(local, exist_ok=True)
    json.dump(results, open(os.path.join(local, f"runpod-{args.name}.json"), "w"), indent=2)
    _write_status(args.name, "TOURNAMENT COMPLETE — pods torn down",
                  [f"- {r['label']}: {r['wr']:.1%} [{r['lo']:.1%}-{r['hi']:.1%}] ({r['games']}g)" for r in results])


def runstats(args):
    """Fan `soisim run` (full GameRecord JSONL) across pods for balance stats. Each pod
    runs all character matchups at a slice of games, writes JSONL to /tmp then copies to
    the volume; we download + merge (one header + all game lines). Guaranteed teardown."""
    kill_min = int(os.environ.get("RUNPOD_MAX_POD_MINUTES", "90"))
    pods_cap = int(os.environ.get("RUNPOD_MAX_PODS", "16"))
    k = min(args.pods, pods_cap)
    matchups = args.matchups
    per = max(1, math.ceil(args.games / (matchups * k)))   # games-per-matchup per pod
    planned = per * matchups * k
    out_root = f"/workspace/runs/{args.name}"
    print(f"runstats {args.name}: ~{planned} games ({k} pods × {matchups} matchups × {per}), bots={args.bots}")
    created, workers = [], []
    try:
        for i in range(k):
            seed = args.seed_base + i * 100000
            w = f"w{i:02d}"
            ld = f"/tmp/{w}"
            cli = (f"/workspace/bin/SoiSim run --bots {args.bots} --games-per-matchup {per} "
                   f"--seed-base {seed} --threads {args.vcpu} --out {ld}/games.jsonl --tag {args.name}")
            # Copy the growing JSONL to the volume every 25s so a killed pod (timeout /
            # balance) still leaves its games-so-far behind — the run() copied only at the
            # end, which lost everything on an early kill.
            dcmd = f"""chmod +x /workspace/bin/SoiSim
mkdir -p {out_root}/status/{w} {out_root}/data {out_root}/results {ld}
export SOISIM_STATUS_DIR={out_root}/status/{w}
( while true; do cp {ld}/games.jsonl {out_root}/data/{w}.jsonl 2>/dev/null && sync; sleep 25; done ) &
CL=$!
{cli} > {out_root}/status/{w}/run.log 2>&1
kill $CL 2>/dev/null || true
cp {ld}/games.jsonl {out_root}/data/{w}.jsonl 2>/dev/null && sync
touch {out_root}/results/{w}.done"""
            r = create_pod(f"{args.name}-{w}", dcmd, args.vcpu)
            if isinstance(r, dict) and "id" in r:
                created.append(r["id"]); workers.append(i)
                json.dump(created, open(ACTIVE, "w"))
                print(f"  {w} -> {r['id']}")
            else:
                print(f"  WARN {w} create failed: {json.dumps(r)[:160]}")
        if args.require_all and len(created) < k:
            print(f"  require-all: only {len(created)}/{k} pods — tearing down, will retry")
            teardown(created); sys.exit(3)
        if not created:
            raise RuntimeError("no pods could be created")
        start = time.time()
        while True:
            try:
                done = [x for x in s3_list(f"runs/{args.name}/results/") if x.endswith(".done")]
                tot = 0
                for i in workers:
                    pr = _parse_progress(s3_get_text(f"runs/{args.name}/status/w{i:02d}/campaign-status.md"))
                    if any(m.endswith(f"w{i:02d}.done") for m in done):
                        tot += per * matchups
                    elif pr:
                        tot += pr[0]
                elapsed = (time.time() - start) / 60
                _write_status(args.name, f"runstats ({len(done)}/{len(workers)} pods done)",
                              [f"~{tot}/{planned} games · {elapsed:.0f} min · {len(workers)}×{args.vcpu} vCPU"])
                print(f"  [{elapsed:.0f}min] {len(done)}/{len(workers)} pods, ~{tot}/{planned} games")
                if len(done) >= len(workers):
                    break
            except Exception as e:
                print("  poll hiccup:", str(e)[:120]); elapsed = (time.time() - start) / 60
            if elapsed > kill_min:
                print("!! kill-timeout — tearing down"); break
            time.sleep(20)
    finally:
        teardown(created)

    time.sleep(12)
    local = os.path.join(REPO, "Tools", "ShardsData", "sim", args.name)
    os.makedirs(local, exist_ok=True)
    merged = os.path.join(local, "games.jsonl")
    cl = s3()
    n_files = n_games = 0
    with open(merged, "w", encoding="utf-8") as out:
        wrote_header = False
        for key in sorted(s3_list(f"runs/{args.name}/data/")):
            if not key.endswith(".jsonl"):
                continue
            txt = s3_get_text(key)
            if not txt:
                continue
            n_files += 1
            for line in txt.splitlines():
                if not line.strip():
                    continue
                if '"type":"header"' in line or '"type": "header"' in line:
                    if wrote_header:
                        continue
                    wrote_header = True
                else:
                    n_games += 1
                out.write(line + "\n")
    summary = f"runstats done: {n_games} games from {n_files} pods -> {merged}"
    print("RESULT:", summary)
    _write_status(args.name, "RUNSTATS COMPLETE — pods torn down", [summary])


def status_cmd(args):
    md = open(STATUS_MD).read() if os.path.exists(STATUS_MD) else "(no status yet)"
    print(md)


def main():
    load_env()
    ap = argparse.ArgumentParser()
    sub = ap.add_subparsers(dest="cmd", required=True)
    sub.add_parser("upload-binary").set_defaults(fn=upload_binary)
    sub.add_parser("teardown-all").set_defaults(fn=teardown_all)
    un = sub.add_parser("upload-nets"); un.add_argument("--nets", required=True); un.set_defaults(fn=upload_nets)
    sw = sub.add_parser("sweep")
    sw.add_argument("--name", required=True)
    sw.add_argument("--nets", required=True, help="comma list of out/<name> dirs (each has weights.bin/json)")
    sw.add_argument("--vcpu", type=int, default=32)
    sw.add_argument("--games", type=int, default=200)
    sw.add_argument("--seed-base", type=int, default=160000)
    sw.add_argument("--gate-cmd", required=True, help="probe args for the opponent + budgets (no --a/--net-a-file/--games/--seed-base/--result)")
    sw.set_defaults(fn=sweep)
    tn = sub.add_parser("tournament")
    tn.add_argument("--name", required=True)
    tn.add_argument("--spec", required=True, help="JSON list of {label,cmd,games,pods}")
    tn.add_argument("--vcpu", type=int, default=32)
    tn.add_argument("--seed-base", type=int, default=700000)
    tn.set_defaults(fn=tournament)
    rs = sub.add_parser("runstats")
    rs.add_argument("--name", required=True)
    rs.add_argument("--pods", type=int, required=True)
    rs.add_argument("--vcpu", type=int, default=32)
    rs.add_argument("--games", type=int, required=True)
    rs.add_argument("--bots", default="rank:diamond")
    rs.add_argument("--matchups", type=int, default=15)
    rs.add_argument("--seed-base", type=int, default=900000)
    rs.add_argument("--require-all", action="store_true")
    rs.set_defaults(fn=runstats)
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
