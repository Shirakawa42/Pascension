"""Bounded CPU prefix/one-action-concession wire fixtures; no policy or training.

Run under an outer process-group timeout. This never simulates full matches.
"""
import json
import mmap
import os
from pathlib import Path
import select
import signal
import struct
import subprocess
import tempfile
import time

import numpy as np

HERE = Path(__file__).resolve().parent
DOTNET = "/home/lva/.dotnet/dotnet"
V4 = HERE.parent / "HostV4/bin/Release/net8.0/TrainingHostV4.dll"
V5 = HERE / "bin/Release/net8.0/TrainingHostV5.dll"


class Wire:
    def __init__(self, binary, *, mode=None, shared=False, fused=False, statistics=False):
        self.folder = tempfile.TemporaryDirectory(prefix="shards-v5-wire-")
        self.path = Path(self.folder.name)
        self.stderr = (self.path / "stderr.txt").open("w+")
        env = {key: value for key, value in os.environ.items()
               if not key.startswith(("SHARDS_STATS_", "SHARDS_SHARED_"))}
        env.update(SHARDS_FUSED_SERVE="1" if fused else "0", SHARDS_SPLIT_BRANCHES="8")
        self.shared = shared
        if shared:
            env.update(SHARDS_SHARED_BUFFER=str(self.path / "mapped.bin"), SHARDS_SHARED_COPY="span")
        if statistics:
            env.update(SHARDS_STATS_DIRECTORY=str(self.path / "statistics"), SHARDS_STATS_PURPOSE="training_pool",
                       SHARDS_STATS_EXPECTED_SEED="0", SHARDS_STATS_EXPECTED_BATCH="32")
        command = [DOTNET, str(binary), "serve"]
        if mode is not None:
            command += ["--hero-setup", mode]
        self.process = subprocess.Popen(command, stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                                        stderr=self.stderr, env=env, start_new_session=True)
        self.mapping = None

    def read(self, length):
        result = bytearray()
        deadline = time.monotonic() + 10
        while len(result) < length:
            remaining = deadline - time.monotonic()
            if remaining <= 0 or not select.select([self.process.stdout], [], [], remaining)[0]:
                raise TimeoutError("Host prefix response exceeded10 seconds")
            part = os.read(self.process.stdout.fileno(), length-len(result))
            if not part:
                raise RuntimeError("Host exited before a complete response")
            result.extend(part)
        return bytes(result)

    def request(self, request):
        self.process.stdin.write(request)
        self.process.stdin.flush()
        header = struct.unpack("<8I", self.read(32))
        assert header[:2] == (0x534f4931, 1) and header[3:6] == (2048, 64, 32)
        self.batch = header[2]
        if self.shared:
            timing = self.read(64)
            if self.mapping is None:
                with (self.path / "mapped.bin").open("r+b") as handle:
                    self.mapping = mmap.mmap(handle.fileno(), header[6])
            data = self.mapping[:]
        else:
            data = self.read(header[6])
            timing = self.read(64)
        self.times = np.frombuffer(timing, dtype="<f8").copy()
        n = self.batch
        self.obs = np.frombuffer(data, dtype="<f4", count=n*2048).reshape(n, 2048).copy()
        self.candidates = np.frombuffer(data, dtype="<f4", count=n*2048, offset=n*2048*4).reshape(n, 64, 32).copy()
        offset = n*4096*4
        self.mask = np.frombuffer(data, dtype="<f4", count=n*64, offset=offset).reshape(n, 64).copy()
        offset += n*64*4
        self.rewards = np.frombuffer(data, dtype="<f4", count=n*2, offset=offset).reshape(n, 2).copy()
        self.done = np.frombuffer(data, dtype="<i4", count=n, offset=offset+n*8).copy()
        self.seats = np.frombuffer(data, dtype="<i4", count=n, offset=offset+n*12).copy()

    def reset(self, batch, seed):
        if self.shared:
            with (self.path / "mapped.bin").open("a+b") as handle:
                handle.truncate(batch * 4164 * 4)
        self.request(struct.pack("<IIIQ", 1, batch, 2, seed))

    def step(self, actions):
        self.request(struct.pack("<I", 5) + np.asarray(actions, dtype="<i4").tobytes())

    def close(self):
        try:
            if self.process.poll() is None:
                self.process.stdin.write(struct.pack("<I", 3))
                self.process.stdin.flush()
                self.process.wait(timeout=5)
        finally:
            if self.process.poll() is None:
                try:
                    os.killpg(self.process.pid, signal.SIGKILL)
                except ProcessLookupError:
                    pass
                self.process.wait(timeout=5)
            if self.mapping is not None:
                self.mapping.close()
            self.process.stdin.close()
            self.process.stdout.close()
            self.stderr.close()

    def cleanup(self):
        self.close()
        self.folder.cleanup()


def same(left, right, *, row=None):
    for field in ("obs", "candidates", "mask", "seats"):
        a, b = getattr(left, field), getattr(right, field)
        if row is not None:
            a, b = a[row], b[0]
        assert np.array_equal(a, b), field


def test_case(shared, fused):
    hosts = []
    def make(*args, **kwargs):
        host = Wire(*args, shared=shared, fused=fused, **kwargs)
        hosts.append(host)
        return host
    try:
        baseline, natural = make(V4), make(V5)
        mixed = make(V5, mode="curriculum-75-25", statistics=True)
        for host in hosts:
            host.reset(32, 0)
        same(baseline, natural)
        forced = mixed.obs[:, 22] != 0
        assert forced.any() and (~forced).any()
        assert np.array_equal(mixed.times[2:6], np.zeros(4)), "Forced setup never increments wire policy counters"
        initial_mixed = tuple(getattr(mixed, name).copy() for name in ("obs", "candidates", "mask", "seats"))
        for seat in (1, 0):
            actions = np.full(32, -1)
            for lane in np.flatnonzero(forced):
                hero = mixed.obs[lane, 22+48*seat]
                slots = np.flatnonzero((baseline.mask[lane] == 1) & (baseline.candidates[lane, :, 12] == 1)
                                       & (baseline.candidates[lane, :, 31] == hero))
                assert len(slots) == 1 and baseline.seats[lane] == seat
                actions[lane] = slots[0]
            baseline.step(actions)
        same(baseline, mixed)
        setup_count = int(2*forced.sum())
        assert baseline.times[2] == setup_count and mixed.times[2] == 0
        # Variable disjoint hold masks exercise both serving loops and owned publication.
        for step in range(4):
            actions = mixed.mask.argmax(1).astype(np.int64)
            actions[np.arange(32) % 3 == step % 3] = -1
            baseline.step(actions)
            mixed.step(actions)
            same(baseline, mixed)
            assert np.array_equal(baseline.done, mixed.done) and not mixed.done.any()
            assert baseline.times[2]-setup_count == mixed.times[2]
        # Reset restores exact setup/first observation, not the suffix state.
        mixed.reset(32, 0)
        for name, expected in zip(("obs", "candidates", "mask", "seats"), initial_mixed):
            assert np.array_equal(getattr(mixed, name), expected)
        # A single legal concession tests terminal response and automatic reset.
        # It is an administrative fixture, not a simulated full match/result sample.
        assert forced[0]
        choices = np.flatnonzero((mixed.mask[0] == 1) & (mixed.candidates[0, :, 11] == 1))
        assert len(choices) == 1
        actions = np.full(32, -1)
        actions[0] = choices[0]
        mixed.step(actions)
        assert mixed.done[0] == 1 and np.count_nonzero(mixed.done) == 1
        assert np.array_equal(mixed.rewards[0], [-1, 1])
        assert np.array_equal(mixed.times[2:6], [1, 1, 1, 0])
        fresh = make(V5, mode="curriculum-75-25")
        fresh.reset(1, 32)
        same(mixed, fresh, row=0)
        mixed.close()
        reports = [json.loads(path.read_text()) for path in (mixed.path/"statistics").glob("*/session-*.json")
                   if ".error." not in path.name and ".history-error." not in path.name]
        assert {report["hero_setup"]["cohort"] for report in reports} == {"forced-random", "natural-draft"}
        assert sum(report["totals"]["completed_games"] for report in reports) == 1
        assert all(report["totals"]["censored_games"] == 0 for report in reports)
        assert next(report for report in reports if report["hero_setup"]["cohort"] == "natural-draft")["totals"]["completed_games"] == 0
        return {"shared": shared, "fused": fused, "passed": True,
                "initial_forced_lanes": int(forced.sum()), "initial_natural_lanes": int((~forced).sum()),
                "suffix_steps": 4, "administrative_concession_fixtures": 1,
                "complete_matches_simulated": 0, "optimizer_updates": 0}
    finally:
        for host in hosts:
            host.cleanup()


def main():
    before = subprocess.check_output([DOTNET, str(V4), "catalog"], timeout=10)
    after = subprocess.check_output([DOTNET, str(V5), "catalog"], timeout=10)
    assert before == after, "Complete catalog output remains byte-identical"
    results = [test_case(shared, fused) for shared in (False, True) for fused in (False, True)]
    print(json.dumps({"passed": True, "catalog_bytes_unchanged": True, "cases": results,
                      "scope": "CPU prefixes, exact wire/manual-setup parity and single-action auto-reset fixtures only"}, indent=2))


if __name__ == "__main__":
    main()
