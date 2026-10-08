#!/usr/bin/env python3
"""Bounded opcode-5 parity and terminal-hold checks; never runs learning."""
import json
import os
from pathlib import Path
import struct
import subprocess
import tempfile


COUNT = 8
WORDS = COUNT * (2048 + 64 * 32 + 64 + 4)
BODY_BYTES = WORDS * 4


def read_exact(stream, length):
    chunks = []
    while length:
        chunk = stream.read(length)
        if not chunk:
            raise RuntimeError("Short host response")
        chunks.append(chunk)
        length -= len(chunk)
    return b"".join(chunks)


def lane(body, index):
    # Observation, candidates, mask and actor are unchanged on hold; rewards
    # and done deliberately refer only to the latest command response.
    obs = index * 2048 * 4
    candidates = COUNT * 2048 * 4 + index * 64 * 32 * 4
    mask = COUNT * (2048 + 64 * 32) * 4 + index * 64 * 4
    actor = COUNT * (2048 + 64 * 32 + 64 + 3) * 4 + index * 4
    return (body[obs:obs + 2048 * 4], body[candidates:candidates + 64 * 32 * 4],
            body[mask:mask + 64 * 4], body[actor:actor + 4])


def main():
    directory = Path(__file__).resolve().parent
    dotnet = os.environ.get("SHARDS_DOTNET", str(Path.home() / ".dotnet/dotnet"))
    command = [dotnet, str(directory / "bin/Release/net8.0/TrainingHost.dll"), "serve"]
    results = []
    for workers in (1, 4):
        processes, paths = [], []
        try:
            for mapped, fused in ((False, False), (False, True), (True, False), (True, True)):
                env = dict(os.environ, SHARDS_FUSED_SERVE=str(int(fused)), SHARDS_SHARED_COPY="span",
                           SHARDS_PROFILE_PUBLICATION="0", DOTNET_gcServer="0")
                env.pop("SHARDS_SHARED_BUFFER", None)
                path = None
                if mapped:
                    fd, path = tempfile.mkstemp(prefix="shards-selective-check-", dir="/dev/shm")
                    os.ftruncate(fd, BODY_BYTES)
                    os.close(fd)
                    env["SHARDS_SHARED_BUFFER"] = path
                paths.append(path)
                processes.append(subprocess.Popen(command, stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                                                  stderr=subprocess.PIPE, env=env))

            def request(opcode, actions=None):
                reference = reference_stats = None
                for index, (process, path) in enumerate(zip(processes, paths)):
                    # Compare old opcode 2 with selective opcode 5 whenever every
                    # lane is active; no observation/counter differences allowed.
                    actual_opcode = 2 if opcode == 5 and min(actions) >= 0 and index == 0 else opcode
                    packet = struct.pack("<I", actual_opcode)
                    if opcode == 1:
                        packet += struct.pack("<IIQ", COUNT, workers, 417)
                    elif actions is not None:
                        packet += struct.pack("<" + "i" * COUNT, *actions)
                    process.stdin.write(packet)
                    process.stdin.flush()
                    header = struct.unpack("<8I", read_exact(process.stdout, 32))
                    assert header == (0x534F4931, 1, COUNT, 2048, 64, 32, BODY_BYTES, 64), header
                    body = Path(path).read_bytes() if path else read_exact(process.stdout, BODY_BYTES)
                    stats = struct.unpack("<8d", read_exact(process.stdout, 64))
                    if reference is None:
                        reference, reference_stats = body, stats
                    else:
                        assert body == reference, ("Payload differs", workers, opcode, index)
                        assert stats[2:7] == reference_stats[2:7], ("Counters differ", workers, opcode, index)
                    if index % 2 and opcode == 5:
                        assert stats[1] == 0, "Fused opcode 5 encodes inside the step pass"
                return reference, reference_stats

            before, stats = request(1)
            held_until = [0] * COUNT
            terminal_holds = partial_holds = all_active = 0
            for step in range(96):
                mask_offset = COUNT * (2048 + 64 * 32) * 4
                mask = struct.unpack_from("<" + "f" * (COUNT * 64), before, mask_offset)
                candidates = memoryview(before)[COUNT * 2048 * 4:mask_offset].cast("f")
                actions = []
                for index in range(COUNT):
                    if step < held_until[index] or (step > 8 and step % 11 == index):
                        actions.append(-1)
                        continue
                    valid = [slot for slot in range(64) if mask[index * 64 + slot]]
                    # Explicit concede ensures real terminal + auto-reset coverage.
                    concede = next((slot for slot in valid if candidates[(index * 64 + slot) * 32 + 11]), None)
                    actions.append(concede if concede is not None and step % 7 == index % 7
                                   else valid[(step * 3 + index) % len(valid)])
                after, next_stats = request(5, actions)
                active = sum(action >= 0 for action in actions)
                assert next_stats[2] - stats[2] == active, "Held lanes must not count as wrapper steps"
                all_active += active == COUNT
                partial_holds += 0 < active < COUNT
                reward_offset = COUNT * (2048 + 64 * 32 + 64) * 4
                done_offset = reward_offset + COUNT * 2 * 4
                for index, action in enumerate(actions):
                    done = struct.unpack_from("<i", after, done_offset + index * 4)[0]
                    if action == -1:
                        assert lane(before, index) == lane(after, index), "Held lane observation or seat changed"
                        assert done == 0 and after[reward_offset + index * 8:reward_offset + index * 8 + 8] == bytes(8)
                        terminal_holds += step < held_until[index]
                    elif done:
                        assert done == 1, "No administrative cap expected in bounded fixture"
                        held_until[index] = step + 4
                before, stats = after, next_stats
            # A completely held command is equivalent to observe: no counter or
            # state changes, including fresh lanes whose prior response was terminal.
            held, held_stats = request(5, [-1] * COUNT)
            observed, observed_stats = request(4)
            assert held == observed and held_stats[2:7] == observed_stats[2:7]
            assert held_stats[2:7] == stats[2:7]
            assert terminal_holds and partial_holds and all_active
            diagnostics = []
            for process in processes:
                process.stdin.write(struct.pack("<I", 3)); process.stdin.flush()
                assert process.wait(timeout=5) == 0
                diagnostics.append(json.loads(process.stderr.read()))
            results.append({"workers": workers, "response_batches": 99, "terminal_hold_checks": terminal_holds,
                            "mixed_active_hold_batches": partial_holds, "opcode2_opcode5_parity_batches": all_active,
                            "all_four_payloads_identical": True, "host_diagnostics": diagnostics})
        finally:
            for process in processes:
                if process.poll() is None:
                    process.kill(); process.wait(timeout=5)
                for stream in (process.stdin, process.stdout, process.stderr):
                    stream.close()
            for path in paths:
                if path:
                    os.unlink(path)
    result = {"passed": True, "results": results}
    (directory / "selective-step-selftest.json").write_text(json.dumps(result, indent=2) + "\n")
    print(json.dumps(result, indent=2))


if __name__ == "__main__":
    main()
