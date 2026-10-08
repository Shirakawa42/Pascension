"""Optional lossless CPU staging, compiled lazily from fingerprinted source.

Import never loads/builds a binary. PackedTransport may request a backend once
at construction, before actor graph captures and checkpoint RNG restoration.
The established NumPy stage is the fallback for unavailable builds or strides.
"""
from __future__ import annotations

import ctypes
import hashlib
import json
import os
from pathlib import Path
import platform
import shutil
import subprocess
import sys
import tempfile
import threading

import numpy as np

C_SOURCE = r'''#include <math.h>
#include <stdint.h>
#include <string.h>

/* No fast-math: encoded float32 counts are promoted to double and validated
   with the same rounding/tolerance as the Python transport. All payload bytes
   are copied directly, including signed zeros and arbitrary fixed fields. */
int stage_native(const float *obs,const float *candidates,const float *mask,
    int host_rows,int obs_dim,int actions,int action_dim,const int64_t *lanes,
    int selected,int bucket,const int32_t *starts,const int32_t *maxima,
    const int32_t *capacities,const int32_t *strides,const int32_t *columns,
    const double *scales,const int32_t *kinds,int segments,int dense_width,
    float *destination,int32_t *metadata,int *error_region) {
    for(int row=0;row<selected;row++)
        if(lanes[row]<0 || lanes[row]>=host_rows)return 5;
    int candidate_rows=0;
    for(int row=0;row<selected;row++) {
        const float *source=mask+lanes[row]*actions;
        for(int col=0;col<actions;col++)
            if(source[col]!=0.f && source[col]!=1.f)return 1;
    }
    for(int row=0;row<selected;row++) {
        const float *source=mask+lanes[row]*actions;int count=0,zero_seen=0;
        for(int col=0;col<actions;col++) {
            if(source[col]==0.f)zero_seen=1;
            else {if(zero_seen)return 2;count++;}
        }
        if(!count)return 2;
        if(count>candidate_rows)candidate_rows=count;
    }
    int width=0;
    /* Publish a new plan only after every guard passes. An invalid extent
       must leave the preceding staged ownership/metadata untouched. */
    int lengths[segments], sources[segments];
    for(int region=0;region<segments;region++) {
        int length=maxima[region];
        if(kinds[region]==1)length=candidate_rows*action_dim;
        else if(columns[region]>=0) {
            double maximum=0.;
            for(int row=0;row<selected;row++) {
                double raw=(double)obs[lanes[row]*obs_dim+columns[region]]*scales[region];
                double rounded=nearbyint(raw);
                if(!isfinite(raw) || raw<0. || rounded>capacities[region] ||
                   fabs(raw-rounded)>fmax(1.e-4,fabs(rounded)*1.e-6)) {
                    *error_region=region;return 3;
                }
                if(rounded>maximum)maximum=rounded;
            }
            length=(int)maximum*strides[region];
        }
        lengths[region]=length;sources[region]=width;width+=length;
    }
    if(width<=0 || width>dense_width)return 4;
    for(int region=0;region<segments;region++) {
        metadata[segments+region]=lengths[region];
        metadata[segments*2+region]=sources[region];
    }
    metadata[segments*3]=width;
    int mask_column=0;
    for(int region=0;region<segments;region++)
        if(kinds[region]==2)mask_column=metadata[segments*2+region];
    for(int row=0;row<bucket;row++) {
        float *target=destination+(int64_t)row*width;
        if(row>=selected) {
            memset(target,0,width*sizeof(float));target[mask_column]=1.f;continue;
        }
        int64_t lane=lanes[row];
        for(int region=0;region<segments;region++) {
            int length=metadata[segments+region];if(!length)continue;
            const float *source;
            if(kinds[region]==1)source=candidates+lane*actions*action_dim;
            else if(kinds[region]==2)source=mask+lane*actions;
            else source=obs+lane*obs_dim+starts[region];
            memcpy(target+metadata[segments*2+region],source,length*sizeof(float));
        }
    }
    return 0;
}
'''
FLAGS = ('-O3', '-std=c11', '-fPIC', '-shared')
_LOCK = threading.Lock()
_BACKENDS = {}


def _sha256(path):
    digest = hashlib.sha256()
    with Path(path).open('rb') as source:
        for chunk in iter(lambda: source.read(1024*1024), b''):
            digest.update(chunk)
    return digest.hexdigest()


def _fsync_file(path):
    with Path(path).open('rb') as source:
        os.fsync(source.fileno())


def _fsync_dir(path):
    descriptor = os.open(path, os.O_RDONLY | os.O_DIRECTORY)
    try:
        os.fsync(descriptor)
    finally:
        os.close(descriptor)


class NativeBackend:
    def __init__(self, library, manifest):
        self.library = ctypes.CDLL(str(library))
        self.manifest = dict(manifest)
        self.function = self.library.stage_native
        self.function.restype = ctypes.c_int
        self.function.argtypes = ([ctypes.c_void_p]*3 + [ctypes.c_int]*4 + [ctypes.c_void_p]
                                  + [ctypes.c_int]*2 + [ctypes.c_void_p]*7 + [ctypes.c_int]*2
                                  + [ctypes.c_void_p]*3)

    def stage(self, transport, host, lanes):
        observations, candidates, masks = [np.asarray(getattr(host, name))
                                            for name in ('obs', 'candidates', 'mask')]
        if observations.ndim != 2:
            raise ValueError('Host observation must be a matrix')
        host_batch = observations.shape[0]
        expected = ((host_batch, transport.obs_dim), (host_batch, transport.actions, transport.action_dim),
                    (host_batch, transport.actions))
        if any(value.shape != shape or value.dtype != np.dtype(np.float32)
               for value, shape in zip((observations, candidates, masks), expected)):
            raise ValueError('Host float32 input schema differs from packed transport')
        lanes = np.asarray(lanes)
        if lanes.ndim != 1 or lanes.size and lanes.dtype.kind not in 'iu':
            raise ValueError('Selected lanes must be a one-dimensional integer vector')
        if lanes.size > transport.batch:
            raise ValueError('Selected lane outside transport/host batch')
        def fallback():
            if np.any(lanes < 0) or np.any(lanes >= host_batch):
                raise ValueError('Selected lane outside transport/host batch')
            return None
        if any(not value.flags.c_contiguous for value in (observations, candidates, masks)):
            return fallback()
        # memcpy requires disjoint ranges. Unusual callers may deliberately
        # expose the owned output tensors as input views; preserve the existing
        # NumPy path's semantics rather than invoking overlapping native copies.
        source_addresses = [value.ctypes.data for value in (observations, candidates, masks)]
        destination_ranges = []
        for tensor in (transport.packed_host, transport.metadata_host):
            address = tensor.data_ptr()
            destination_ranges.append((address,address+tensor.numel()*tensor.element_size()))
        if any(address < end and start < address+value.nbytes
               for value, address in zip((observations, candidates, masks), source_addresses)
               for start, end in destination_ranges):
            return fallback()
        if any(value > np.iinfo(np.int32).max for value in (host_batch, transport.obs_dim, transport.actions,
                transport.action_dim, transport.batch, transport.segment_count, transport.dense_width)):
            return fallback()
        lanes = np.ascontiguousarray(lanes, dtype=np.intp)
        if any(lanes.ctypes.data < end and start < lanes.ctypes.data+lanes.nbytes
               for start, end in destination_ranges):
            return fallback()
        if not hasattr(transport, '_native_plan'):
            regions = transport.regions
            arrays = [np.asarray([r[0] for r in regions], np.int32),
                      np.asarray([r[1] for r in regions], np.int32),
                      np.asarray([r[2] if r[2] is not None else -1 for r in regions], np.int32),
                      np.asarray([r[3] if r[3] is not None else -1 for r in regions], np.int32),
                      np.asarray([r[4][0] if r[4] is not None else -1 for r in regions], np.int32),
                      np.asarray([r[4][1] if r[4] is not None else 0. for r in regions], np.float64),
                      np.asarray([1 if r[5] == 'candidates' else 2 if r[5] == 'mask' else 0 for r in regions], np.int32)]
            transport._native_plan = arrays
            transport._native_plan_pointers = [array.ctypes.data for array in arrays]
            transport._native_metadata_numpy = transport.metadata_host.numpy()
        cached = getattr(transport,'_native_metadata_numpy',None)
        if cached is None or cached.ctypes.data != destination_ranges[1][0]:
            transport._native_metadata_numpy = transport.metadata_host.numpy()
        error = ctypes.c_int(-1)
        code = self.function(*source_addresses,
            host_batch, transport.obs_dim, transport.actions, transport.action_dim, lanes.ctypes.data,
            lanes.size, transport.batch, *transport._native_plan_pointers, transport.segment_count,
            transport.dense_width, destination_ranges[0][0], destination_ranges[1][0],
            ctypes.byref(error))
        if code == 5:
            raise ValueError('Selected lane outside transport/host batch')
        if code == 1:
            raise ValueError('Action masks must contain only zero/one')
        if code == 2:
            raise ValueError('Packed transport requires a nonempty contiguous legal candidate prefix')
        if code == 3:
            raise ValueError(f'Invalid encoded table extent: {transport.regions[error.value][5]}')
        if code:
            raise RuntimeError('Packed extent exceeded preallocated schema')
        transport.packed_width = int(transport._native_metadata_numpy[-1])
        transport.used_elements = transport.batch * transport.packed_width
        transport.retained_count = int(lanes.size)
        return transport.retained_count

    def self_check(self):
        """Check ABI, dynamic prefix plans, signed zeros, and failure atomicity."""
        def require(condition, message):
            if not condition:
                raise RuntimeError('Native staging self-check failed: '+message)
        obs = np.arange(16, dtype=np.float32).reshape(2, 8)
        obs[:, 0] = [.5, 1.]
        obs[:, 1] = np.float32(-0.)
        candidates = np.arange(16, dtype=np.float32).reshape(2, 2, 4)
        candidates[:, 0, 1] = np.float32(-0.)
        masks = np.asarray([[1., -0.], [1., 1.]], np.float32)
        plan = [np.asarray(values, dtype=dtype) for values, dtype in (
            ([0, 4, 8, 16], np.int32), ([4, 4, 8, 2], np.int32),
            ([-1, 2, -1, -1], np.int32), ([-1, 2, -1, -1], np.int32),
            ([-1, 0, -1, -1], np.int32), ([0., 2., 0., 0.], np.float64),
            ([0, 0, 1, 2], np.int32))]
        destination = np.empty(3*18, np.float32)
        metadata = np.zeros(13, np.int32)
        metadata[:4] = [0, 4, 8, 16]

        def call(lanes):
            lanes = np.asarray(lanes, np.int64)
            error = ctypes.c_int(-1)
            code = self.function(*(array.ctypes.data for array in (obs, candidates, masks)), 2, 8, 2, 4,
                lanes.ctypes.data, len(lanes), 3, *(array.ctypes.data for array in plan),
                4, 18, destination.ctypes.data, metadata.ctypes.data, ctypes.byref(error))
            return code

        for lanes in ([0], [1, 0, 1], []):
            require(call(lanes) == 0, 'valid stage rejected')
            maximum = max((int(round(float(obs[lane, 0])*2)) for lane in lanes), default=0)
            actions = max((int(masks[lane].sum()) for lane in lanes), default=0)
            lengths = [4, maximum*2, actions*4, 2]
            sources = np.cumsum([0, *lengths[:-1]], dtype=np.int32)
            width = sum(lengths)
            require(np.array_equal(metadata[4:8], lengths), 'dynamic lengths')
            require(np.array_equal(metadata[8:12], sources) and metadata[-1] == width, 'dynamic sources/width')
            expected = np.zeros((3, width), np.float32)
            for row, lane in enumerate(lanes):
                expected[row] = np.concatenate((obs[lane, :4], obs[lane, 4:4+maximum*2],
                                                candidates[lane].reshape(-1)[:actions*4], masks[lane]))
            expected[len(lanes):, sources[-1]] = 1.
            require(np.array_equal(destination[:3*width].view(np.uint32), expected.reshape(-1).view(np.uint32)), 'payload bits')
        previous_metadata, previous_destination = metadata.copy(), destination.copy()
        obs[0, 0] = np.nan
        require(call([0]) == 3, 'invalid extent accepted')
        require(np.array_equal(previous_metadata, metadata), 'invalid stage changed metadata')
        require(np.array_equal(previous_destination.view(np.uint32), destination.view(np.uint32)), 'invalid stage changed payload')


def _load_cached(directory, expected):
    manifest = json.loads((directory/'manifest.json').read_text())
    if any(manifest.get(key) != value for key, value in expected.items()):
        raise RuntimeError('Native staging cache manifest differs from keyed source/compiler/ABI')
    if _sha256(directory/'stage.c') != expected['source_sha256']:
        raise RuntimeError('Native staging cached source differs')
    if _sha256(directory/'stage.so') != manifest.get('binary_sha256'):
        raise RuntimeError('Native staging binary checksum differs')
    backend = NativeBackend(directory/'stage.so', manifest)
    backend.self_check()
    return backend


def get_backend(*, cache_dir=None, compiler=None):
    """Return a verified cached backend, or None for the normal NumPy stage.

An unavailable compiler/platform, failed build, invalid cache, or self-check
selects the fallback. Runtime state guard failures still raise from ``stage``.
Cache construction never changes Torch/NumPy RNG or starts a GPU context.
    """
    if (not sys.platform.startswith('linux') or platform.machine() not in ('x86_64', 'AMD64')
            or sys.byteorder != 'little' or np.dtype(np.intp).itemsize != 8
            or ctypes.sizeof(ctypes.c_float) != 4 or ctypes.sizeof(ctypes.c_double) != 8
            or ctypes.sizeof(ctypes.c_int) != 4):
        return None
    root = Path(cache_dir) if cache_dir is not None else Path(os.environ.get('XDG_CACHE_HOME', Path.home()/'.cache'))/'shards-zero-depth'/'native-stage'
    executable = shutil.which(compiler or 'gcc')
    cache_id = (str(root.resolve()), executable)
    with _LOCK:
        if cache_id in _BACKENDS:
            return _BACKENDS[cache_id]
        temporary = None
        try:
            if executable is None:
                _BACKENDS[cache_id] = None
                return None
            version = subprocess.run([executable, '--version'], check=True, capture_output=True, text=True, timeout=10).stdout.splitlines()[0]
            compiler_path = str(Path(executable).resolve())
            expected = {'schema': 'shards-native-stage-v1',
                        'source_sha256': hashlib.sha256(C_SOURCE.encode()).hexdigest(),
                        'compiler_path': compiler_path, 'compiler_sha256': _sha256(compiler_path),
                        'compiler_version': version, 'flags': list(FLAGS),
                        'machine': platform.machine(), 'pointer_bytes': ctypes.sizeof(ctypes.c_void_p)}
            key = hashlib.sha256(json.dumps(expected, sort_keys=True).encode()).hexdigest()
            root.mkdir(parents=True, exist_ok=True)
            directory = root/key
            if directory.exists():
                backend = _load_cached(directory, expected)
            else:
                temporary = Path(tempfile.mkdtemp(prefix='.build-', dir=root))
                source, library = temporary/'stage.c', temporary/'stage.so'
                source.write_text(C_SOURCE)
                _fsync_file(source)
                subprocess.run([executable, *FLAGS, str(source), '-o', str(library), '-lm'],
                               check=True, capture_output=True, timeout=30)
                _fsync_file(library)
                manifest = dict(expected, binary_sha256=_sha256(library))
                backend = NativeBackend(library, manifest)
                backend.self_check()
                (temporary/'manifest.json').write_text(json.dumps(manifest, sort_keys=True, indent=2)+'\n')
                _fsync_file(temporary/'manifest.json')
                _fsync_dir(temporary)
                try:
                    os.rename(temporary, directory)
                    temporary = None
                    _fsync_dir(root)
                except OSError:
                    # POSIX may report EEXIST or ENOTEMPTY when another process
                    # publishes the same exact keyed build. Verify that winner.
                    if not directory.exists():
                        raise
                    backend = _load_cached(directory, expected)
                else:
                    # The already loaded inode survives directory publication.
                    backend.manifest['cache_directory'] = str(directory)
            _BACKENDS[cache_id] = backend
            return backend
        except (OSError, RuntimeError, ValueError, AssertionError, KeyError, IndexError,
                AttributeError, subprocess.SubprocessError):
            _BACKENDS[cache_id] = None
            return None
        finally:
            if temporary is not None:
                shutil.rmtree(temporary, ignore_errors=True)
