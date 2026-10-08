"""CPU-only correctness of the source-keyed optional native staging backend."""
import copy
import importlib
import json
from pathlib import Path
import sys
import tempfile
from types import SimpleNamespace
import unittest
from unittest import mock

import numpy as np
import torch

helper_directory = Path(__file__).resolve().parent
if not (helper_directory/'native_packing.py').exists():
    helper_directory = helper_directory.parent
sys.path.insert(0, str(helper_directory))
import native_packing


class NativeHelperTests(unittest.TestCase):
    def test_import_has_no_build_or_self_check_side_effects(self):
        with mock.patch('subprocess.run', side_effect=AssertionError('import compiled')):
            importlib.reload(native_packing)
        self.assertEqual(native_packing._BACKENDS, {})

    def test_unavailable_compiler_and_platform_fall_back(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)/'new'
            self.assertIsNone(native_packing.get_backend(cache_dir=root, compiler='shards-deliberately-missing-gcc'))
            self.assertFalse(root.exists())
            with mock.patch.object(native_packing.sys, 'platform', 'win32'):
                self.assertIsNone(native_packing.get_backend(cache_dir=root))
            self.assertFalse(root.exists())

    def test_build_failure_falls_back_and_cleans_incomplete_artifacts(self):
        with tempfile.TemporaryDirectory() as directory:
            executable = Path(directory)/'compiler'
            executable.write_text('#!/bin/sh\nif [ "$1" = "--version" ]; then echo test-compiler; exit 0; fi\nexit 2\n')
            executable.chmod(0o700)
            root = Path(directory)/'cache'
            self.assertIsNone(native_packing.get_backend(cache_dir=root, compiler=str(executable)))
            self.assertEqual(list(root.iterdir()), [])

    def test_successful_build_and_cached_reload_verify_source_and_binary(self):
        with tempfile.TemporaryDirectory() as directory:
            backend = native_packing.get_backend(cache_dir=directory)
            self.assertIsNotNone(backend)
            entries = list(Path(directory).iterdir())
            self.assertEqual(len(entries), 1)
            source = entries[0]/'stage.c'
            self.assertEqual(source.read_text(), native_packing.C_SOURCE)
            manifest = json.loads((entries[0]/'manifest.json').read_text())
            self.assertEqual(manifest['binary_sha256'], native_packing._sha256(entries[0]/'stage.so'))
            native_packing._BACKENDS.clear()
            with mock.patch('subprocess.run', wraps=native_packing.subprocess.run) as run:
                cached = native_packing.get_backend(cache_dir=directory)
            self.assertIsNotNone(cached)
            self.assertEqual(run.call_count, 1)  # Compiler identity, no compile.
            self.assertEqual(cached.manifest['binary_sha256'], backend.manifest['binary_sha256'])

    def test_corrupt_source_manifest_or_binary_never_loads_cache(self):
        for name in ('stage.c', 'manifest.json', 'stage.so'):
            with self.subTest(name=name), tempfile.TemporaryDirectory() as directory:
                backend = native_packing.get_backend(cache_dir=directory)
                self.assertIsNotNone(backend)
                entry = next(Path(directory).iterdir())
                # Each fresh subprocess would reload a cache; clear the
                # in-process handle table to exercise that same boundary.
                native_packing._BACKENDS.clear()
                path = entry/name
                # Do not overwrite bytes mapped by the live CDLL. Atomically
                # replace its path so the previously loaded inode stays safe.
                replacement = entry/'corrupt.tmp'
                replacement.write_bytes(b'corrupt')
                replacement.replace(path)
                with mock.patch.object(native_packing, 'NativeBackend', side_effect=AssertionError('loaded corruption')):
                    self.assertIsNone(native_packing.get_backend(cache_dir=directory))

    def test_native_state_guards_raw_bits_ownership_and_strided_fallback(self):
        import sys
        sys.path.insert(0, '/mnt/f/Unity/projects/pascension/Tools/ZeroDepthTraining')
        from packed_transport import PackedTransport
        with tempfile.TemporaryDirectory() as directory:
            backend = native_packing.get_backend(cache_dir=directory)
            self.assertIsNotNone(backend)
            transport = PackedTransport.__new__(PackedTransport)
            transport.batch, transport.obs_dim, transport.actions, transport.action_dim = 4, 12, 4, 3
            transport.regions = [(0,4,None,None,None,'fixed'), (4,4,2,2,(0,2.),'table:public'),
                                 (8,4,None,None,None,'fixed'), (12,12,None,None,None,'candidates'),
                                 (24,4,None,None,None,'mask')]
            transport.segment_count, transport.dense_width = 5, 28
            transport.packed_host = torch.empty(4*28, dtype=torch.float32)
            transport.metadata_host = torch.zeros(16, dtype=torch.int32)
            transport.metadata_host.numpy()[:5] = [region[0] for region in transport.regions]
            transport.used_elements = transport.packed_width = transport.retained_count = 0
            reference = copy.copy(transport)
            reference.packed_host = torch.empty_like(transport.packed_host)
            reference.metadata_host = transport.metadata_host.clone()
            obs = np.arange(6*12, dtype=np.float32).reshape(6,12)
            obs[:,0] = np.arange(6)%3/2
            obs[:,1] = np.float32(-0.)
            # Unoccupied table tails are genuinely zero in encoder inputs.
            for row, count in enumerate(np.arange(6)%3): obs[row,4+count*2:8] = 0
            candidates = np.arange(6*4*3, dtype=np.float32).reshape(6,4,3)
            candidates[:,:,1] = np.float32(-0.)
            mask = np.zeros((6,4), np.float32)
            for row in range(6): mask[row,:row%4+1] = 1
            host = SimpleNamespace(obs=obs, candidates=candidates, mask=mask)
            for selected in ([0], [5,2,5], [], [3,2,1,0], [1]):
                lanes = np.asarray(selected, np.int64)
                PackedTransport.stage(reference, host, lanes)
                self.assertEqual(backend.stage(transport, host, lanes), len(lanes))
                self.assertTrue(np.array_equal(reference.metadata_host.numpy(), transport.metadata_host.numpy()))
                self.assertTrue(np.array_equal(reference.packed_host.numpy()[:reference.used_elements].view(np.uint32),
                                               transport.packed_host.numpy()[:transport.used_elements].view(np.uint32)))
            before = transport.packed_host.numpy().view(np.uint32).copy()
            metadata = transport.metadata_host.numpy().copy()
            bad = SimpleNamespace(obs=obs.copy(), candidates=candidates, mask=mask)
            bad.obs[1,0] = np.nan
            with self.assertRaisesRegex(ValueError, 'Invalid encoded table extent: table:public'):
                backend.stage(transport, bad, np.asarray([1]))
            self.assertTrue(np.array_equal(before, transport.packed_host.numpy().view(np.uint32)))
            self.assertTrue(np.array_equal(metadata, transport.metadata_host.numpy()))
            strided = SimpleNamespace(obs=obs[::-1], candidates=candidates[::-1], mask=mask[::-1])
            self.assertIsNone(backend.stage(transport, strided, np.asarray([0])))
            self.assertTrue(np.array_equal(metadata, transport.metadata_host.numpy()))
            # Contiguous input fields can alias the owned output slab. Returning
            # None must leave it untouched so the caller can use NumPy safely.
            payload = transport.packed_host.numpy()
            alias = SimpleNamespace(obs=payload[:48].reshape(4,12),
                                    candidates=payload[48:96].reshape(4,4,3),
                                    mask=payload[96:112].reshape(4,4))
            before = payload.view(np.uint32).copy()
            self.assertIsNone(backend.stage(transport, alias, np.asarray([0])))
            self.assertTrue(np.array_equal(before, payload.view(np.uint32)))
            self.assertTrue(np.array_equal(metadata, transport.metadata_host.numpy()))
            lane_alias = payload.view(np.int64)[:2]
            lane_alias.fill(0)
            before = payload.view(np.uint32).copy()
            self.assertIsNone(backend.stage(transport, host, lane_alias))
            self.assertTrue(np.array_equal(before, payload.view(np.uint32)))
            self.assertTrue(np.array_equal(metadata, transport.metadata_host.numpy()))


if __name__ == '__main__':
    unittest.main()
