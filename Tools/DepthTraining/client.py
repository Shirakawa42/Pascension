"""Framed, bounded search-host transport. Only real root positions are examples."""
from pathlib import Path
import os
import mmap
import select
import struct
import subprocess
import tempfile
import time
import numpy as np

HERE = Path(__file__).resolve().parent
BINARY = HERE / 'Host/bin/Release/net8.0/DepthHost.dll'
DOTNET = '/home/lva/.dotnet/dotnet'
OBS, ACTIONS, FEATURES = 24576, 64, 48
ROW = OBS + ACTIONS * FEATURES + ACTIONS

class SearchHost:
    def __init__(self, *, batch=16, depth=8, width=4, worlds=2, log=None, shared_capacity=2048,
                 workers=6, statistics_directory=None):
        if type(shared_capacity) is not int or not 1<=shared_capacity<=8192:
            raise ValueError('Shared inference capacity must be an integer in1..8192')
        self.batch = batch
        if type(workers) is not int or not 1<=workers<=6:raise ValueError('Worker budget must be in1..6')
        env=os.environ.copy();env['SHARDS_DEPTH_OWNER_PID']=str(os.getpid());env['DOTNET_PROCESSOR_COUNT']=str(workers)
        env['SHARDS_DEPTH_WORKERS']=str(workers)
        if statistics_directory is not None:env['SHARDS_DEPTH_STATISTICS']=str(statistics_directory)
        self.shared_file=None;self.shared_map=None;self.shared_capacity=shared_capacity
        self.transport=dict(shared_requests=0,shared_rows=0,framed_requests=0,framed_rows=0,max_request_rows=0)
        env.pop('SHARDS_DEPTH_SHARED_INPUT_PATH',None)
        try:
            if env.get('SHARDS_DEPTH_SHARED_INFERENCE')=='1':
                self.shared_file=tempfile.NamedTemporaryFile(prefix='shards-depth-',dir='/dev/shm')
                size=self.shared_capacity*ROW*4;self.shared_file.truncate(size)
                self.shared_map=mmap.mmap(self.shared_file.fileno(),size)
                env['SHARDS_DEPTH_SHARED_INPUT_PATH']=self.shared_file.name
            self.process = subprocess.Popen([DOTNET, str(BINARY), 'serve', str(batch), str(depth), str(width), str(worlds)],
                stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=log, bufsize=0, env=env)
        except BaseException:
            self.close_shared();raise
        self.deadline = float('inf')
        self.stop = lambda: False
    def read(self, length):
        parts = bytearray()
        while len(parts) < length:
            if time.monotonic() >= self.deadline or self.stop():
                raise InterruptedError('Search collection stopped at authorization boundary')
            ready, _, _ = select.select([self.process.stdout], [], [], 1)
            if not ready:
                if self.process.poll() is not None: raise RuntimeError('Search host exited')
                continue
            part = os.read(self.process.stdout.fileno(), min(length-len(parts), 1<<20))
            if not part: raise RuntimeError(f'Search protocol EOF, return code {self.process.poll()}')
            parts.extend(part)
        return parts
    def write(self, data):
        view = memoryview(data)
        while view:
            if time.monotonic() >= self.deadline or self.stop(): raise InterruptedError('Search reply stopped')
            _, ready, _ = select.select([], [self.process.stdin], [], 1)
            if ready:
                n = os.write(self.process.stdin.fileno(), view[:65536]); view = view[n:]
    def collect(self, seed, infer, *, progress=None, retain_rows=True, on_root=None, incumbent_infer=None):
        import json
        self.write(struct.pack('<Q', seed))
        rows, actors, lanes, targets, priors, improved, root_values = [], [], [], [], [], [], []
        pending = None
        while True:
            kind, count = struct.unpack('<ii', self.read(8))
            if not 0 < count <= (1<<22): raise RuntimeError('Invalid search frame size')
            if kind in (1, 2, 6, 7):
                shared=kind in (6,7)
                path='shared' if shared else 'framed'
                self.transport[path+'_requests']+=1;self.transport[path+'_rows']+=count
                self.transport['max_request_rows']=max(self.transport['max_request_rows'],count)
                if shared:kind-=5
                meta = np.frombuffer(self.read(count*8), '<i4').reshape(count, 2).copy() if kind == 1 else None
                if shared:
                    if self.shared_map is None or count>self.shared_capacity:raise RuntimeError('Invalid shared inference frame')
                    packet=np.frombuffer(self.shared_map,'<f4',count=count*ROW).reshape(count,ROW)
                    # Real roots outlive many hypothetical batches until kind 3
                    # supplies the selected action; retain their exact inputs.
                    if kind==1:packet=packet.copy()
                else:packet = np.frombuffer(self.read(count*ROW*4), '<f4').reshape(count, ROW).copy()
                if not np.isfinite(packet).all(): raise RuntimeError('Nonfinite search observation')
                logits, values = infer(packet, meta)
                reply = np.concatenate([logits, values[:, None]], 1).astype('<f4')
                if not np.isfinite(reply).all(): raise RuntimeError('Nonfinite search inference')
                self.write(reply.tobytes())
                if kind == 1:
                    pending = (packet, meta, logits, values)
                    if progress: progress(len(rows), count)
            elif kind == 5:
                if incumbent_infer is None:raise RuntimeError('Unexpected incumbent GPU request')
                packet=np.frombuffer(self.read(count*5440*4),'<f4').reshape(count,5440).copy()
                if not np.isfinite(packet).all():raise RuntimeError('Nonfinite incumbent observation')
                prediction=incumbent_infer(packet)
                if prediction.shape!=(count,65) or not np.isfinite(prediction).all():raise RuntimeError('Invalid incumbent GPU response')
                self.write(np.asarray(prediction,dtype='<f4').tobytes())
            elif kind == 3:
                choice = np.frombuffer(self.read(count*8), '<i4').reshape(count,2).copy(); actions=choice[:,0]
                if pending is None or len(pending[0]) != count: raise RuntimeError('Missing real search root')
                packet, meta, logits, values = pending; pending = None
                mask = packet[:, -ACTIONS:]
                if (actions < 0).any() or (actions >= ACTIONS).any() or not mask[np.arange(count), actions].all():
                    raise RuntimeError('Search selected an illegal action')
                if on_root is not None:on_root(packet,meta,actions,choice[:,1])
                if retain_rows:
                    rows.append(packet);lanes.append(meta[:, 0]);actors.append(meta[:, 1]);targets.append(actions);priors.append(logits);improved.append(choice[:,1]);root_values.append(values)
            elif kind == 4:
                report = json.loads(self.read(count))
                if pending is not None: raise RuntimeError('Unlabelled root at cohort boundary')
                if not retain_rows:return dict(report=report)
                return dict(rows=np.concatenate(rows), lanes=np.concatenate(lanes), actors=np.concatenate(actors),
                            targets=np.concatenate(targets), priors=np.concatenate(priors), improved=np.concatenate(improved), values=np.concatenate(root_values), report=report)
            else: raise RuntimeError(f'Unknown depth protocol opcode {kind}')
    def close(self):
        try:
            self.process.terminate()
            try: self.process.wait(timeout=5)
            except subprocess.TimeoutExpired: self.process.kill();self.process.wait()
        finally:
            self.process.stdin.close();self.process.stdout.close()
            self.close_shared()
    def close_shared(self):
        if self.shared_map is not None:
            # An exception traceback may retain the last NumPy view. Unlink the
            # file now; that view releases the mapping when the traceback dies.
            try:self.shared_map.close()
            except BufferError:pass
            self.shared_map=None
        if self.shared_file is not None:self.shared_file.close();self.shared_file=None
    def __enter__(self): return self
    def __exit__(self, *exc): self.close()
