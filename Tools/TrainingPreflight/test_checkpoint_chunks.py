"""Lossless checkpoint chunks and prepublication failure atomicity, CPU only."""
import hashlib
import io
from pathlib import Path
import tempfile
import unittest
from unittest import mock

import torch
import campaign_state as persistence


class ChunkTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.root = Path(self.temporary.name)

    def tearDown(self):
        self.temporary.cleanup()

    def test_byte_chunks_equal_existing_bytes_path_exactly(self):
        buffer = bytearray(b"\x00payload\xff")
        chunks = (b"header", memoryview(buffer), b"", b"tail")
        persistence._atomic_write(self.root / "chunks", chunks)
        persistence._atomic_write(self.root / "bytes", b"header" + buffer + b"tail")
        self.assertEqual((self.root / "chunks").read_bytes(), (self.root / "bytes").read_bytes())
        self.assertEqual(buffer, bytearray(b"\x00payload\xff"))
        self.assertFalse(list(self.root.glob("*.tmp")))

    def test_save_writes_header_and_memoryview_with_existing_load_format(self):
        observed = []
        original_write = persistence._atomic_write
        def inspect(path, data):
            if Path(path).suffix == ".soicp":
                self.assertIsInstance(data, tuple)
                self.assertIsInstance(data[1], memoryview)
                observed.append(b"".join(data))
            return original_write(path, data)
        with persistence.CampaignBudget(self.root / "budget.json", 100) as budget:
            with mock.patch.object(persistence, "_atomic_write", side_effect=inspect):
                saved = persistence.save_checkpoint_atomic(self.root / "model.soicp", {"policy": torch.tensor([-0., 3.]), "ring": b"nonempty"},
                                                           identity={"scope": "chunks"}, budget=budget, include_cuda_rng=False)
            raw = (self.root / "model.soicp").read_bytes()
            self.assertEqual(raw, observed[0])
            self.assertEqual(saved["bytes"], len(raw))
            magic, length, digest = persistence._HEADER.unpack(raw[:persistence._HEADER.size])
            self.assertEqual(magic, persistence._MAGIC)
            self.assertEqual(length, len(raw) - persistence._HEADER.size)
            self.assertEqual(digest, hashlib.sha256(raw[persistence._HEADER.size:]).digest())
            loaded = persistence.load_checkpoint(self.root / "model.soicp", expected_identity={"scope": "chunks"}, budget=budget)
            self.assertTrue(torch.equal(loaded["state"]["policy"].view(torch.uint8), torch.tensor([-0., 3.]).view(torch.uint8)))

    def test_replace_failure_preserves_old_checkpoint_and_poisons_open_budget(self):
        path = self.root / "model.soicp"
        path.write_bytes(b"previous authoritative checkpoint")
        previous = path.read_bytes()
        real_replace = persistence.os.replace
        def fail_checkpoint(source, destination):
            if Path(destination) == path:
                raise OSError("injected rename failure")
            return real_replace(source, destination)
        with persistence.CampaignBudget(self.root / "budget.json", 100) as budget:
            with mock.patch.object(persistence.os, "replace", side_effect=fail_checkpoint):
                with self.assertRaises(persistence.CheckpointError):
                    persistence.save_checkpoint_atomic(path, {"policy": torch.ones(3)}, identity={"scope": "chunks"},
                                                       budget=budget, include_cuda_rng=False)
            self.assertEqual(path.read_bytes(), previous)
            self.assertTrue(budget._poisoned)
            with self.assertRaises(persistence.PersistenceError):
                budget.snapshot()
        self.assertFalse(list(self.root.glob("*.tmp")))

    def test_second_chunk_write_failure_leaves_previous_file_unchanged(self):
        path = self.root / "data"
        path.write_bytes(b"previous")
        with self.assertRaises(TypeError):
            persistence._atomic_write(path, (b"first chunk", object()))
        self.assertEqual(path.read_bytes(), b"previous")
        self.assertFalse(list(self.root.glob("*.tmp")))


if __name__ == "__main__":
    unittest.main()
