"""CPU-only paired HostV3/V4 wire differential and bounded request timing."""
import argparse
import hashlib
import json
import mmap
import os
from pathlib import Path
import select
import statistics
import struct
import subprocess
import tempfile
import time

import numpy as np

HERE = Path(__file__).resolve().parent
OLD = HERE.parent / "HostV3/bin/Release/net8.0/TrainingHostV3.dll"
NEW = HERE / "bin/Release/net8.0/TrainingHostV4.dll"
DOTNET = os.environ.get("SHARDS_DOTNET", "/home/lva/.dotnet/dotnet")
EXPECTED_OLD = "472f94ef69e265b1ab03249413275ccc088063dfa5f0fc2d9b57e806025a8520"
CAP_TRACE = Path("/home/lva/.local/share/shards-training/2026-09-26/pilot128-v2/censored-episodes/censored-v4-lane137-1790424400651778390.json")


def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def environment(fused, fixed_jit):
    env = dict(os.environ, DOTNET_PROCESSOR_COUNT="1", DOTNET_gcServer="0",
               SHARDS_SPLIT_BRANCHES="8", SHARDS_SHARED_COPY="span",
               SHARDS_FUSED_SERVE=str(int(fused)), SHARDS_PROFILE_PUBLICATION="0")
    if fixed_jit:
        env.update(DOTNET_TieredCompilation="0", DOTNET_TieredPGO="0")
    else:
        env.pop("DOTNET_TieredCompilation", None)
        env.pop("DOTNET_TieredPGO", None)
    env.pop("SHARDS_SHARED_BUFFER", None)
    for key in list(env):
        if key.startswith("SHARDS_STATS_"):
            del env[key]
    return env


class Host:
    def __init__(self, binary, batch, seed, transport, fused=False, fixed_jit=True, statistics_directory=None):
        self.batch, self.size = batch, batch * 4164 * 4
        self.log = tempfile.TemporaryFile()
        self.file = self.mapping = self.mapped = self.process = None
        self.raw = np.empty(self.size, dtype=np.uint8)
        self.obs = self.raw[:batch * 2048 * 4].view("<f4").reshape(batch, 2048)
        self.candidates = self.raw[batch * 2048 * 4:batch * 4096 * 4].view("<f4").reshape(batch, 64, 32)
        self.mask = self.raw[batch * 4096 * 4:batch * 4160 * 4].view("<f4").reshape(batch, 64)
        self.done = self.raw[batch * 4162 * 4:batch * 4163 * 4].view("<i4")
        self.header, self.trailer = bytearray(32), bytearray(64)
        env = environment(fused, fixed_jit)
        if statistics_directory is not None:
            env.update(SHARDS_STATS_DIRECTORY=str(statistics_directory), SHARDS_STATS_PURPOSE="training_pool",
                       SHARDS_STATS_EXPECTED_SEED=str(seed), SHARDS_STATS_EXPECTED_BATCH=str(batch))
        try:
            if transport == "shared":
                self.file = tempfile.NamedTemporaryFile(prefix="shards-host-v4-", dir="/dev/shm")
                self.file.truncate(self.size); self.file.flush()
                self.mapping = mmap.mmap(self.file.fileno(), self.size)
                self.mapped = np.frombuffer(self.mapping, dtype=np.uint8)
                env["SHARDS_SHARED_BUFFER"] = self.file.name
            self.process = subprocess.Popen([DOTNET, str(binary), "serve"], stdin=subprocess.PIPE,
                stdout=subprocess.PIPE, stderr=self.log, bufsize=0, env=env)
            self.call(struct.pack("<IIIQ", 1, batch, 1, seed))
        except BaseException:
            self.close(); raise

    def read(self, target):
        view = memoryview(target).cast("B")
        while view:
            if not select.select([self.process.stdout], [], [], 15)[0]:
                raise TimeoutError("Host response stalled")
            count = self.process.stdout.readinto(view)
            if not count:
                self.log.seek(0)
                raise RuntimeError("Host closed: " + self.log.read().decode(errors="replace")[-4000:])
            view = view[count:]

    def call(self, packet):
        start = time.perf_counter_ns()
        view = memoryview(packet)
        while view:
            count = self.process.stdin.write(view)
            if not count:
                raise RuntimeError("Host command pipe closed")
            view = view[count:]
        self.read(self.header)
        assert struct.unpack("<8I", self.header) == (0x534F4931, 1, self.batch, 2048, 64, 32, self.size, 64)
        if self.mapped is None:
            self.read(self.raw)
        else:
            np.copyto(self.raw, self.mapped)
        self.read(self.trailer)
        self.metrics = struct.unpack("<8d", self.trailer)
        return (time.perf_counter_ns() - start) / 1e9

    def close(self):
        try:
            if self.process is not None:
                if self.process.poll() is None:
                    try:
                        self.process.stdin.write(struct.pack("<I", 3))
                        self.process.stdin.close(); self.process.wait(timeout=3)
                    except (BrokenPipeError, subprocess.TimeoutExpired):
                        self.process.kill(); self.process.wait(timeout=3)
                if not self.process.stdin.closed:
                    self.process.stdin.close()
                self.process.stdout.close()
        finally:
            self.log.close()
            self.mapped = None
            if self.mapping is not None:
                self.mapping.close()
            if self.file is not None:
                self.file.close()


def compare(a, b):
    assert np.array_equal(a.raw, b.raw), "Observation/candidates/mask/reward/done/seat bytes differ"
    assert a.metrics[2:7] == b.metrics[2:7], "Deterministic engine counters differ"


def choices(host, rng, step, active=None, concessions=True):
    action = np.full(host.batch, -1, dtype="<i4")
    for lane in range(host.batch if active is None else active):
        legal = np.flatnonzero(host.mask[lane])
        kind = host.candidates[lane, legal, :16].argmax(1)
        if concessions and step % 128 == 127 and np.any(kind == 11):
            action[lane] = legal[np.flatnonzero(kind == 11)[0]]
        else:
            rank = np.array([6, 2, 2, 3, 5, 3, 5, 5, 1, 4, 0, -90, 1, 1, -50, 1])[kind]
            action[lane] = rng.choice(legal[rank == rank.max()])
    return action


def differential():
    rows = []
    for transport in ("pipe", "shared"):
        for fused in (False, True):
            a = b = c = None
            statistics_directory = tempfile.TemporaryDirectory(prefix="shards-v4-statistics-parity-")
            try:
                a = Host(OLD, 8, 7026, transport, fused)
                b = Host(NEW, 8, 7026, transport, fused)
                c = Host(NEW, 8, 7026, transport, fused, statistics_directory=statistics_directory.name)
                rng = np.random.default_rng(1729)
                terminals = 0
                for step in range(385):
                    compare(a, b); compare(a, c); terminals += np.count_nonzero(a.done == 1)
                    if step == 384:
                        break
                    actions = choices(a, rng, step)
                    if step % 11 == 0:
                        actions[-1] = -1
                    if step == 191:
                        packet = struct.pack("<IIIQ", 1, 8, 1, 8126)
                    elif step % 37 == 1:
                        packet = struct.pack("<I", 4)
                    else:
                        packet = struct.pack("<I", 5) + actions.tobytes()
                    a.call(packet); b.call(packet); c.call(packet)
                assert terminals > 0
                c.close(); c = None
                latest = [json.loads(p.read_text()) for p in Path(statistics_directory.name).glob("session-*.json")]
                assert len(latest) == 1 and latest[0]["schema"] == "shards-training-pool-stats-v1"
                assert latest[0]["totals"]["completed_games"] == int(terminals)
                assert latest[0]["final"] and latest[0]["reset_count"] == 2
                rows.append(dict(transport=transport, fused=fused, response_batches=385, batch=8,
                    terminal_responses=int(terminals), all_payload_bytes_equal=True, counters_equal=True,
                    reset_observe_selective_hold_and_auto_reset=True, v3_v4_off_v4_on_identical=True,
                    statistics_totals=latest[0]["totals"], statistics_rows=len(latest[0]["rows"])))
            finally:
                if c: c.close()
                if b: b.close()
                if a: a.close()
                statistics_directory.cleanup()
    if CAP_TRACE.exists():
        trace = json.loads(CAP_TRACE.read_text()); a = b = None
        statistics_directory = tempfile.TemporaryDirectory(prefix="shards-v4-statistics-cap-")
        try:
            a = Host(OLD, 1, trace["engine_seed"], "shared")
            b = Host(NEW, 1, trace["engine_seed"], "shared", statistics_directory=statistics_directory.name)
            compare(a, b)
            for i, action in enumerate(trace["action_indices"]):
                assert a.mask[0, action] == 1
                packet = struct.pack("<Ii", 5, action)
                a.call(packet); b.call(packet); compare(a, b)
                assert a.done[0] == (2 if i == len(trace["action_indices"]) - 1 else 0)
            before = a.obs.copy()
            a.call(struct.pack("<Ii", 5, -1)); b.call(struct.pack("<Ii", 5, -1)); compare(a, b)
            assert a.done[0] == 0 and np.array_equal(before, a.obs)
            b.close(); b = None
            latest = [json.loads(p.read_text()) for p in Path(statistics_directory.name).glob("session-*.json")]
            assert len(latest) == 1 and latest[0]["totals"]["completed_games"] == 0 and latest[0]["totals"]["censored_games"] == 1
            assert all(r["selected_player_games"] == 0 and r["draws"] == 0 for r in latest[0]["rows"])
            rows.append(dict(trace=str(CAP_TRACE), trace_sha256=digest(CAP_TRACE),
                actual_learned_actions=len(trace["action_indices"]), all_payload_bytes_equal=True,
                cap_identical=True, terminal_reset_hold_identical=True,
                statistics_censor_only=True, statistics_rows=len(latest[0]["rows"])))
        finally:
            if b: b.close()
            if a: a.close()
            statistics_directory.cleanup()
    return rows


def benchmark(repeats, steps, fixed_jit, warmup, workload):
    rows = []
    workloads = {"full": ("shared", 256, 256), "tail": ("shared", 256, 8), "pipe": ("pipe", 64, 64)}
    selected = workloads.values() if workload == "all" else [workloads[workload]]
    for transport, batch, active in selected:
        for repeat in range(repeats):
            a = b = None
            try:
                a = Host(OLD, batch, 9026, transport, fixed_jit=fixed_jit)
                b = Host(NEW, batch, 9026, transport, fixed_jit=fixed_jit)
                rng = np.random.default_rng(9201)
                elapsed = [[], []]; step_time = [[], []]; encode_time = [[], []]
                owned_sizes = []; action_hash = hashlib.sha256()
                for step in range(steps + warmup):
                    compare(a, b)
                    action = choices(a, rng, step, active)
                    packet = struct.pack("<I", 5) + action.tobytes()
                    action_hash.update(packet)
                    for which in ((repeat + step) % 2, 1 - (repeat + step) % 2):
                        host = (a, b)[which]
                        duration = host.call(packet)
                        if step >= warmup:
                            elapsed[which].append(duration)
                            step_time[which].append(host.metrics[0] / 1000)
                            encode_time[which].append(host.metrics[1] / 1000)
                    if step >= warmup:
                        # Permanent collection counts exclude temporary plays; this is a descriptive lower bound.
                        owned_sizes.extend((a.obs[:active, 320:509].sum(1) * 10).tolist())
                compare(a, b)
                old, new = map(sum, elapsed)
                rows.append(dict(transport=transport, batch=batch, active_lanes=active, repeat=repeat,
                    workers=1, fused=False, measured_requests=steps, warmup_requests=warmup,
                    fixed_jit=fixed_jit, original_request_seconds=old, candidate_request_seconds=new,
                    request_speedup=old / new, request_seconds_saved_fraction=1 - new / old,
                    original_step_seconds=sum(step_time[0]), candidate_step_seconds=sum(step_time[1]),
                    original_encode_seconds=sum(encode_time[0]), candidate_encode_seconds=sum(encode_time[1]),
                    median_original_request_ms=statistics.median(elapsed[0]) * 1000,
                    median_candidate_request_ms=statistics.median(elapsed[1]) * 1000,
                    median_permanent_owned_cards=statistics.median(owned_sizes),
                    final_wrapper_steps=int(a.metrics[2]), final_engine_submissions=int(a.metrics[3]),
                    action_stream_sha256=action_hash.hexdigest(), final_payload_sha256=hashlib.sha256(a.raw).hexdigest(),
                    every_response_byte_equal=True))
            finally:
                if b: b.close()
                if a: a.close()
    return rows


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--mode", choices=("check", "benchmark"), default="check")
    parser.add_argument("--steps", type=int, default=192)
    parser.add_argument("--repeats", type=int, default=3)
    parser.add_argument("--warmup", type=int, default=64)
    parser.add_argument("--workload", choices=("all", "full", "tail", "pipe"), default="all")
    parser.add_argument("--default-jit", action="store_true")
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    assert digest(OLD) == EXPECTED_OLD
    catalogs = [json.loads(subprocess.check_output([DOTNET, str(path), "catalog"], env=environment(False, True))) for path in (OLD, NEW)]
    assert catalogs[0] == catalogs[1] and catalogs[0]["observationSchema"] == "shards-observation-v3"
    cases = differential() if args.mode == "check" else benchmark(args.repeats, args.steps, not args.default_jit, args.warmup, args.workload)
    assert digest(OLD) == EXPECTED_OLD
    result = dict(schema="shards-host-v3-v4-wire-probe-v1", passed=True, mode=args.mode,
        host_v3_sha256=digest(OLD), host_v4_sha256=digest(NEW), catalogs_exactly_equal=True,
        scope="CPU-only, one low-priority worker per alternating request; other training may contend. No GPU/optimizer/ledger use or runtime migration.",
        timestamp=time.time(), cases=cases)
    args.output.write_text(json.dumps(result, indent=2, allow_nan=False) + "\n")
    print(json.dumps(dict(passed=True, mode=args.mode, cases=len(cases), output=str(args.output))))


if __name__ == "__main__":
    main()
