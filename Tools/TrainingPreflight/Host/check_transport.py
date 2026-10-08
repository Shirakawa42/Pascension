#!/usr/bin/env python3
"""Bounded byte-for-byte pipe/shared-memory equivalence check, standard library only."""
import json
import os
from pathlib import Path
import struct
import subprocess
import tempfile


def main():
    host_dir = Path(__file__).resolve().parent
    dotnet = os.environ.get("SHARDS_DOTNET", os.environ.get("DOTNET", str(Path.home() / ".dotnet/dotnet")))
    command = [dotnet, str(host_dir / "bin/Release/net8.0/TrainingHost.dll"), "serve"]
    count = 8
    payload_bytes = count * (2048 + 64 * 32 + 64 + 2 + 2) * 4
    fd, path = tempfile.mkstemp(prefix="shards-preflight-", dir="/dev/shm")
    os.ftruncate(fd, payload_bytes)
    os.close(fd)
    processes = []

    def read_exact(stream, size):
        chunks = []
        while size:
            chunk = stream.read(size)
            if not chunk:
                raise RuntimeError("Short host response")
            chunks.append(chunk)
            size -= len(chunk)
        return b"".join(chunks)

    def call(process, request, mapped):
        process.stdin.write(request)
        process.stdin.flush()
        header = struct.unpack("<8I", read_exact(process.stdout, 32))
        assert header == (0x534F4931, 1, count, 2048, 64, 32, payload_bytes, 64), header
        body = Path(path).read_bytes() if mapped else read_exact(process.stdout, payload_bytes)
        trailer = struct.unpack("<8d", read_exact(process.stdout, 64))
        return body, trailer

    try:
        for mapped in (False, True):
            env = dict(os.environ)
            env.pop("SHARDS_SHARED_BUFFER", None)
            if mapped:
                env["SHARDS_SHARED_BUFFER"] = path
            processes.append(subprocess.Popen(command, stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                stderr=subprocess.PIPE, env=env))
        request = struct.pack("<IIIQ", 1, count, 2, 992)
        for step in range(65):
            plain, plain_stats = call(processes[0], request, False)
            shared, shared_stats = call(processes[1], request, True)
            assert plain == shared, ("Different payload", step)
            assert plain_stats[2:7] == shared_stats[2:7], ("Different counters", step)
            offset = count * (2048 + 64 * 32) * 4
            mask = struct.unpack_from("<" + str(count * 64) + "f", plain, offset)
            choices = []
            for i in range(count):
                valid = [j for j in range(64) if mask[i * 64 + j]]
                choices.append(valid[(step * 7 + i) % len(valid)])
            request = struct.pack("<I", 2) + struct.pack("<" + str(count) + "i", *choices)
        for process in processes:
            process.stdin.write(struct.pack("<I", 3))
            process.stdin.flush()
            assert process.wait(timeout=5) == 0
        result = {"passed": True, "batches": 65, "residentGames": count,
            "binaryPayloadBytes": payload_bytes, "pipeSharedIdentical": True,
            "actualHostModes": [json.loads(p.stderr.read()) for p in processes]}
        print(json.dumps(result, indent=2))
        (host_dir / "transport-selftest.json").write_text(json.dumps(result, indent=2) + "\n")
    finally:
        for process in processes:
            if process.poll() is None:
                process.kill()
                process.wait()
        os.unlink(path)


if __name__ == "__main__":
    main()
