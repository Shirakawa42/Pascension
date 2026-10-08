#!/usr/bin/env python3
"""Bounded baseline/fused scheduling parity across pipe and shared transports."""
import json
import os
from pathlib import Path
import struct
import subprocess
import tempfile


def main():
    host_dir = Path(__file__).resolve().parent
    dotnet = os.environ.get("SHARDS_DOTNET", str(Path.home() / ".dotnet/dotnet"))
    command = [dotnet, str(host_dir / "bin/Release/net8.0/TrainingHost.dll"), "serve"]
    count = 8
    payload_bytes = count * (2048 + 64 * 32 + 64 + 2 + 2) * 4
    results = []

    def read_exact(stream, size):
        chunks = []
        while size:
            chunk = stream.read(size)
            if not chunk:
                raise RuntimeError("Short host response")
            chunks.append(chunk)
            size -= len(chunk)
        return b"".join(chunks)

    for workers in (1, 4):
        processes, paths, modes = [], [], []
        try:
            for mapped, fused in ((False, False), (False, True), (True, False), (True, True)):
                env = dict(os.environ, SHARDS_FUSED_SERVE=str(int(fused)), SHARDS_SHARED_COPY="span",
                    SHARDS_PROFILE_PUBLICATION="0", DOTNET_gcServer="0")
                env.pop("SHARDS_SHARED_BUFFER", None)
                path = None
                if mapped:
                    fd, path = tempfile.mkstemp(prefix="shards-fused-check-", dir="/dev/shm")
                    os.ftruncate(fd, payload_bytes)
                    os.close(fd)
                    env["SHARDS_SHARED_BUFFER"] = path
                paths.append(path)
                modes.append((mapped, fused))
                processes.append(subprocess.Popen(command, stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                    stderr=subprocess.PIPE, env=env))
            request = struct.pack("<IIIQ", 1, count, workers, 1234 + workers)
            max_completed = 0
            for step in range(129):
                reference = reference_stats = None
                for process, path, (_, fused) in zip(processes, paths, modes):
                    process.stdin.write(request)
                    process.stdin.flush()
                    header = struct.unpack("<8I", read_exact(process.stdout, 32))
                    assert header == (0x534F4931, 1, count, 2048, 64, 32, payload_bytes, 64), header
                    body = Path(path).read_bytes() if path else read_exact(process.stdout, payload_bytes)
                    stats = struct.unpack("<8d", read_exact(process.stdout, 64))
                    if reference is None:
                        reference, reference_stats = body, stats
                    else:
                        assert body == reference, ("Payload mismatch", workers, step, path, fused)
                        assert stats[2:7] == reference_stats[2:7], ("Counter mismatch", workers, step, fused)
                    opcode = struct.unpack_from("<I", request)[0]
                    if fused and opcode == 2:
                        assert stats[1] == 0, "Fused encoding time must be included in step time"
                    if opcode != 2:
                        assert stats[0] == 0, "Reset/observe must report no step time"
                    max_completed = max(max_completed, stats[4])
                offset = count * (2048 + 64 * 32) * 4
                mask = struct.unpack_from("<" + str(count * 64) + "f", reference, offset)
                choices = []
                for i in range(count):
                    valid = [j for j in range(64) if mask[i * 64 + j]]
                    choices.append(valid[(step * 7 + i) % len(valid)])
                if step == 63:
                    request = struct.pack("<IIIQ", 1, count, workers, 4321 + workers)
                elif step % 17 == 0:
                    request = struct.pack("<I", 4)
                else:
                    request = struct.pack("<I", 2) + struct.pack("<" + str(count) + "i", *choices)
            assert max_completed > 0, "Parity fixture must cover completed games and auto-reset"
            diagnostics = []
            for process, (_, fused) in zip(processes, modes):
                process.stdin.write(struct.pack("<I", 3))
                process.stdin.flush()
                assert process.wait(timeout=5) == 0
                diagnostic = json.loads(process.stderr.read())
                assert diagnostic["fusedServe"] is fused
                diagnostics.append(diagnostic)
            results.append({"workers": workers, "response_batches": 129, "resident_games": count,
                "all_four_payloads_identical": True, "max_completed_in_epoch": max_completed,
                "actual_host_modes": diagnostics})
        finally:
            for process in processes:
                if process.poll() is None:
                    process.kill()
                    process.wait()
            for path in paths:
                if path:
                    os.unlink(path)
    result = {"passed": True, "results": results}
    print(json.dumps(result, indent=2))
    (host_dir / "fused-serve-selftest.json").write_text(json.dumps(result, indent=2) + "\n")


if __name__ == "__main__":
    main()
