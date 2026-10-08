"""CPU-only byte differential against pinned v2; no GPU imports or learner."""
import argparse
import hashlib
import json
import mmap
import os
from pathlib import Path
import struct
import subprocess
import tempfile

import numpy as np

HERE = Path(__file__).resolve().parent
PREFLIGHT = HERE.parents[1]
SLOTS = np.array([317, 318, 319, 509, 510, 511, 701])
DOTNET = os.environ.get('SHARDS_DOTNET', '/home/lva/.dotnet/dotnet')


def exact(stream, count):
    result = bytearray(count)
    position = 0
    while position < count:
        got = stream.readinto(memoryview(result)[position:])
        if not got:
            raise RuntimeError('Host closed before complete response')
        position += got
    return result


class RawHost:
    def __init__(self, binary, batch, seed, transport):
        self.batch = batch
        self.log = tempfile.TemporaryFile()
        self.file = self.mapping = None
        env = os.environ.copy()
        env.update(DOTNET_PROCESSOR_COUNT='1', DOTNET_gcServer='0', SHARDS_SPLIT_BRANCHES='8', SHARDS_SHARED_COPY='span')
        self.size = batch * 4164 * 4
        if transport == 'shared':
            self.file = tempfile.NamedTemporaryFile(prefix='shards-v3-check-', dir='/dev/shm')
            self.file.truncate(self.size)
            self.file.flush()
            self.mapping = mmap.mmap(self.file.fileno(), self.size)
            env['SHARDS_SHARED_BUFFER'] = self.file.name
        else:
            env.pop('SHARDS_SHARED_BUFFER', None)
        self.process = subprocess.Popen([DOTNET, str(binary), 'serve'], stdin=subprocess.PIPE,
            stdout=subprocess.PIPE, stderr=self.log, bufsize=0, env=env)
        self.process.stdin.write(struct.pack('<IIIQ', 1, batch, 1, seed))
        self.receive()

    def receive(self):
        header = struct.unpack('<8I', exact(self.process.stdout, 32))
        assert header == (0x534F4931, 1, self.batch, 2048, 64, 32, self.size, 64), header
        self.body = bytearray(self.mapping[:]) if self.mapping is not None else exact(self.process.stdout, self.size)
        values = np.frombuffer(self.body, dtype='<f4')
        n = self.batch
        self.obs = values[:n*2048].reshape(n, 2048)
        self.candidates = values[n*2048:n*4096].reshape(n, 64, 32)
        self.mask = values[n*4096:n*4160].reshape(n, 64)
        self.lifecycle = values[n*4160:]
        self.metrics = np.array(struct.unpack('<8d', exact(self.process.stdout, 64)))

    def step(self, actions):
        packet = memoryview(struct.pack('<I', 5) + np.asarray(actions, dtype='<i4').tobytes())
        while packet:
            written = self.process.stdin.write(packet)
            if not written:
                raise RuntimeError('Short action write')
            packet = packet[written:]
        self.receive()

    def close(self):
        try:
            if self.process.poll() is None:
                self.process.stdin.write(struct.pack('<I', 3))
                self.process.stdin.close()
                try:
                    self.process.wait(timeout=5)
                except subprocess.TimeoutExpired:
                    self.process.kill(); self.process.wait(timeout=5)
        finally:
            self.process.stdout.close()
            if not self.process.stdin.closed:
                self.process.stdin.close()
            self.log.close()
            if self.mapping is not None:
                self.mapping.close(); self.file.close()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--steps', type=int, default=384)
    parser.add_argument('--batch', type=int, default=4)
    parser.add_argument('--output', type=Path, default=PREFLIGHT/'results/host-v3-differential.json')
    args = parser.parse_args()
    old = PREFLIGHT/'Host/bin/Release/net8.0/TrainingHost.dll'
    new = HERE/'bin/Release/net8.0/TrainingHostV3.dll'
    catalogs = [json.loads(subprocess.check_output([DOTNET, str(path), 'catalog'], text=True)) for path in (old, new)]
    assert catalogs[0]['observationSchema'] == 'shards-observation-v2'
    assert catalogs[1]['observationSchema'] == 'shards-observation-v3'
    assert len(catalogs[0]['cards']) == 189
    assert {k:v for k,v in catalogs[0].items() if k!='observationSchema'} == {
        k:v for k,v in catalogs[1].items() if k not in ('observationSchema','extraObservationFeatures','maxCardDefinitions')}
    assert [x['slot'] for x in catalogs[1]['extraObservationFeatures']] == SLOTS.tolist()
    rows = []
    for transport in ('pipe', 'shared'):
        a = b = None
        try:
            a = RawHost(old, args.batch, 7026, transport)
            b = RawHost(new, args.batch, 7026, transport)
            rng = np.random.default_rng(1729)
            changed = 0
            kinds_seen = set()
            for step in range(args.steps+1):
                assert np.all(a.obs[:, SLOTS] == 0), 'Reserved columns were not all zero in v2'
                assert np.array_equal(np.delete(a.obs.view('<u4'), SLOTS, axis=1),
                                      np.delete(b.obs.view('<u4'), SLOTS, axis=1)), ('legacy observation differs', transport, step)
                assert a.body[args.batch*2048*4:] == b.body[args.batch*2048*4:], ('candidate/mask/reward/done/seat bytes differ', transport, step)
                assert np.array_equal(a.metrics[2:7], b.metrics[2:7]), ('engine counters differ', step)
                assert np.isfinite(b.obs).all()
                assert np.all(b.obs[:, SLOTS] >= 0)
                changed += int(np.count_nonzero(b.obs[:, SLOTS]))
                if step == args.steps:
                    break
                actions = np.zeros(args.batch, dtype=np.int32)
                for lane in range(args.batch):
                    legal = np.flatnonzero(a.mask[lane])
                    kinds = a.candidates[lane, legal, :16].argmax(1)
                    # Exercise ordinary play/buy decisions; explicitly include
                    # terminal concessions at boundaries to check auto-reset.
                    if step >= 127 and step % 128 == 127 and np.any(kinds == 11):
                        choice = int(legal[np.flatnonzero(kinds == 11)[0]])
                    else:
                        ranks = np.array([6,2,2,3,5,3,5,5,1,4,0,-90,1,1,-50,1])[kinds]
                        choice = int(rng.choice(legal[ranks == ranks.max()]))
                    kinds_seen.add(int(a.candidates[lane, choice, :16].argmax()))
                    actions[lane] = choice
                if step % 11 == 0:
                    actions[-1] = -1
                a.step(actions); b.step(actions)
            rows.append({'transport':transport, 'response_batches':args.steps+1, 'lanes':args.batch,
                'nonzero_new_feature_values':changed,'action_kinds_seen':sorted(kinds_seen),
                'wrapper_steps':int(a.metrics[2]),'engine_submissions':int(a.metrics[3]),
                'terminal_games':int(a.metrics[4]),'censored_games':int(a.metrics[5]),
                'all_legacy_observation_bytes_equal':True,'all_candidate_mask_lifecycle_bytes_equal':True,
                'all_engine_counters_equal':True,'v2_reserved_columns_always_zero':True})
        finally:
            if b is not None: b.close()
            if a is not None: a.close()
    result={'passed':True,'schema':'shards-v2-v3-host-differential-v1','feature_slots':SLOTS.tolist(),
        'host_v2_sha256':hashlib.sha256(old.read_bytes()).hexdigest(),
        'host_v3_sha256':hashlib.sha256(new.read_bytes()).hexdigest(),'cases':rows,
        'catalog_v3':catalogs[1], 'scope':'Deterministic paired engine actions; no GPU, policy inference, optimizer, or timing claim'}
    args.output.parent.mkdir(parents=True,exist_ok=True)
    args.output.write_text(json.dumps(result,indent=2)+'\n')
    print(json.dumps({k:result[k] for k in ('passed','feature_slots','cases')}))


if __name__ == '__main__':
    main()
