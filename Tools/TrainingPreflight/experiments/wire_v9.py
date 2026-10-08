"""V9 wire transport, frozen V1 transport with explicit 2816 observation width."""
import mmap, os, struct, tempfile
import numpy as np
import torch
from pipeline_bench import DOTNET, ROOT
import pipeline_bench

class _ProcessProxy:
    def __getattr__(self, name):
        return getattr(pipeline_bench.subprocess, name)
subprocess = _ProcessProxy()

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
            size = batch * (2816 + 64*32 + 64 + 2 + 1 + 1) * 4
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
                0x534F4931, 1, self.batch, 2816, 64, 32, 64):
            raise RuntimeError("Unexpected protocol header: " + repr(tuple(struct.unpack("<8I", header))))
        expected = n * (2816 + 64*32 + 64 + 2 + 1 + 1) * 4
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
            self.upload = self.storage[:n*(2816+64*32+64)*4].view(torch.float32)
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


from learning_rollout import LearningHost as _OriginalLearningHost
class LearningHost(Host):
    advance_active = _OriginalLearningHost.advance_active
