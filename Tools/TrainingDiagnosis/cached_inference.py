"""Bounded exact-input prediction reuse for a frozen, row-independent policy."""
from collections import OrderedDict
import ctypes
import ctypes.util
import hashlib
from pathlib import Path
import subprocess
import numpy as np


class CachedInference:
    def __init__(self, infer, capacity=1024, audit_every=0):
        self.infer = infer
        self.capacity = capacity
        self.audit_every = audit_every
        self.cache = OrderedDict()
        library = ctypes.util.find_library('xxhash')
        if not library:
            raise RuntimeError('Exact prediction cache requires libxxhash')
        self.library = ctypes.CDLL(library)
        self.hash = self.library.XXH3_64bits
        self.hash.argtypes = [ctypes.c_void_p, ctypes.c_size_t]
        self.hash.restype = ctypes.c_uint64
        self.requests = self.rows = self.reused_rows = self.audit_rows = 0

    def __call__(self, packet, meta):
        if packet.dtype != np.float32 or not packet.flags.c_contiguous:
            raise ValueError('Prediction cache requires contiguous float32 inputs')
        self.requests += 1
        self.rows += len(packet)
        output = np.empty((len(packet), 65), dtype=np.float32)
        pending = {}
        misses = []
        aliases = []
        keys = []
        size = packet.shape[1]*4
        for i, row in enumerate(packet):
            key = self.hash(packet.ctypes.data+i*size, size)
            keys.append(key)
            entry = self.cache.get(key)
            # Hashes only locate candidates. Full equality is mandatory: a
            # collision can never substitute a different public observation.
            if entry is not None and np.array_equal(entry[0], row):
                output[i] = entry[1]
                self.cache.move_to_end(key)
                self.reused_rows += 1
            elif key in pending and np.array_equal(packet[pending[key]], row):
                aliases.append((i, pending[key]))
                self.reused_rows += 1
            else:
                pending[key] = i
                misses.append(i)
        if misses:
            logits, values = self.infer(np.ascontiguousarray(packet[misses]), None)
            for j, i in enumerate(misses):
                output[i, :64] = logits[j]
                output[i, 64] = values[j]
                self.cache[keys[i]] = (packet[i].copy(), output[i].copy())
                self.cache.move_to_end(keys[i])
                if len(self.cache) > self.capacity:
                    self.cache.popitem(last=False)
        for i, source in aliases:
            output[i] = output[source]
        if self.audit_every and self.requests % self.audit_every == 0:
            logits, values = self.infer(packet, meta)
            expected = np.column_stack((logits, values))
            if not np.array_equal(expected, output):
                raise RuntimeError('Cached/uncached prediction values differ')
            self.audit_rows += len(packet)
        return output[:, :64], output[:, 64]

    def diagnostics(self):
        return dict(requests=self.requests, input_rows=self.rows, reused_rows=self.reused_rows,
            reused_fraction=self.reused_rows/self.rows if self.rows else 0,
            retained_rows=len(self.cache), exact_audit_rows=self.audit_rows)


class NativeCachedInference:
    """Same exact cache contract, with comparisons and LRU copies in C++."""
    def __init__(self, infer, capacity=1024, audit_every=0, hash_mask=(1<<64)-1):
        self.infer, self.capacity, self.audit_every = infer, capacity, audit_every
        source = Path(__file__).with_name('prediction_cache.cpp')
        digest = hashlib.sha256(source.read_bytes()).hexdigest()[:20]
        directory = Path.home()/'.cache/shards-prediction-cache'/digest
        directory.mkdir(parents=True, exist_ok=True)
        binary = directory/'cache.so'
        if not binary.exists():
            import os
            temporary = directory/f'cache-{os.getpid()}.so'
            subprocess.run(['g++', '-std=c++17', '-O3', '-shared', '-fPIC', str(source),
                '-l:libxxhash.so.0', '-o', str(temporary)], check=True, capture_output=True)
            temporary.replace(binary)
        self.library = ctypes.CDLL(str(binary))
        self.library.cache_create.argtypes = [ctypes.c_size_t, ctypes.c_size_t, ctypes.c_uint64]
        self.library.cache_create.restype = ctypes.c_void_p
        self.library.cache_destroy.argtypes = [ctypes.c_void_p]
        ptr = ctypes.c_void_p
        self.library.cache_lookup.argtypes = [ptr, ptr, ctypes.c_int, ptr, ptr, ptr]
        self.library.cache_lookup.restype = ctypes.c_int
        self.library.cache_commit.argtypes = [ptr, ptr, ctypes.c_int, ptr, ptr]
        self.library.cache_commit.restype = ctypes.c_int
        self.handle = None
        self.width = None
        self.hash_mask = hash_mask
        self.requests = self.rows = self.reused_rows = self.audit_rows = 0

    def __del__(self):
        if getattr(self, 'handle', None):self.library.cache_destroy(self.handle)

    def __call__(self, packet, meta):
        if packet.dtype != np.float32 or packet.ndim != 2 or not packet.flags.c_contiguous:
            raise ValueError('Prediction cache requires contiguous float32 inputs')
        if self.handle is None:
            self.width = packet.shape[1]
            self.handle = self.library.cache_create(self.capacity, self.width, self.hash_mask)
            if not self.handle:raise RuntimeError('Cannot allocate native prediction cache')
        if packet.shape[1] != self.width:raise ValueError('Prediction input width changed')
        output = np.empty((len(packet), 65), dtype=np.float32)
        sources = np.empty(len(packet), np.int32)
        missing = np.empty(len(packet), np.int32)
        count = self.library.cache_lookup(self.handle, packet.ctypes.data, len(packet),
            output.ctypes.data, sources.ctypes.data, missing.ctypes.data)
        if count < 0:raise RuntimeError('Native cache lookup failed')
        if count:
            indices = missing[:count]
            logits, values = self.infer(packet[indices], None)
            output[indices, :64] = logits
            output[indices, 64] = values
        if self.library.cache_commit(self.handle, packet.ctypes.data, len(packet),
                output.ctypes.data, sources.ctypes.data):raise RuntimeError('Native cache commit failed')
        self.requests += 1;self.rows += len(packet);self.reused_rows += len(packet)-count
        if self.audit_every and self.requests % self.audit_every == 0:
            logits, values = self.infer(packet, meta)
            if not np.array_equal(np.column_stack((logits, values)), output):
                raise RuntimeError('Cached/uncached prediction values differ')
            self.audit_rows += len(packet)
        return output[:, :64], output[:, 64]

    def diagnostics(self):
        return dict(backend='native_exact', requests=self.requests, input_rows=self.rows,
            reused_rows=self.reused_rows, reused_fraction=self.reused_rows/self.rows if self.rows else 0,
            capacity=self.capacity, exact_audit_rows=self.audit_rows)
