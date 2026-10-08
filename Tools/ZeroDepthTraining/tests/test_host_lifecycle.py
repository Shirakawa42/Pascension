"""Host process failures release shared memory and obey protocol deadlines."""
import fcntl
import os
from pathlib import Path
import shutil
import sys
import tempfile
import threading
import time
import unittest
from unittest import mock

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

import host
import numpy as np


CATALOG = {"obs_dim": 32, "max_actions": 64, "action_dim": 48}


class HostLifecycleTests(unittest.TestCase):
    def test_failed_shared_mapping_closes_descriptor_and_removes_file(self):
        with tempfile.TemporaryDirectory() as temporary:
            directory = Path(temporary)
            create = tempfile.mkstemp
            descriptors = []

            def local_buffer(**kwargs):
                result = create(prefix=kwargs["prefix"], dir=directory)
                descriptors.append(result[0])
                return result

            with mock.patch.object(host, "catalog", return_value=CATALOG), \
                    mock.patch.object(host.tempfile, "mkstemp", side_effect=local_buffer), \
                    mock.patch.object(host.mmap, "mmap", side_effect=OSError("cannot map shared buffer")):
                with self.assertRaisesRegex(OSError, "cannot map shared buffer"):
                    host.Host(batch=2, workers=1)
            try:
                self.assertEqual(list(directory.iterdir()), [])
                with self.assertRaises(OSError):
                    os.fstat(descriptors[0])
            finally:
                try:
                    os.close(descriptors[0])
                except OSError:
                    pass

    def test_failed_process_start_releases_shared_buffer(self):
        with tempfile.TemporaryDirectory() as temporary:
            directory = Path(temporary)
            create = tempfile.mkstemp

            def local_buffer(**kwargs):
                return create(prefix=kwargs["prefix"], dir=directory)

            with mock.patch.object(host, "catalog", return_value=CATALOG), \
                    mock.patch.object(host.tempfile, "mkstemp", side_effect=local_buffer), \
                    mock.patch.object(host.subprocess, "Popen", side_effect=OSError("cannot start host")):
                with self.assertRaisesRegex(OSError, "cannot start host"):
                    host.Host(batch=2, workers=1)
            self.assertEqual(list(directory.iterdir()), [])

    def test_alive_host_that_stops_reading_commands_times_out(self):
        # Real OS pipes: the peer publishes a valid handshake then deliberately
        # stops consuming stdin. A 4096-lane packet exceeds the pipe capacity.
        small = {"obs_dim": 1, "max_actions": 1, "action_dim": 1}
        peer = ("import os,struct,time; "
                "os.read(0,20); "
                "os.write(1,struct.pack('<8I',0x534f4931,1,4096,1,1,1,114688,64)+bytes(64)); "
                "time.sleep(60)")
        launch = host.subprocess.Popen

        def stalled_peer(*_args, **kwargs):
            process = launch([sys.executable, "-c", peer], **kwargs)
            fcntl.fcntl(process.stdin.fileno(), fcntl.F_SETPIPE_SZ, 4096)
            return process

        errors = []
        with mock.patch.object(host, "catalog", return_value=small), \
                mock.patch.object(host.subprocess, "Popen", side_effect=stalled_peer):
            connection = host.Host(batch=4096, workers=1, timeout=.1)
        connection.mask[:] = 1

        def submit():
            try:
                connection.advance(np.zeros(4096, np.int32))
            except Exception as error:
                errors.append(error)

        sender = threading.Thread(target=submit, daemon=True)
        started = time.monotonic()
        sender.start()
        sender.join(timeout=.6)
        blocked = sender.is_alive()
        try:
            self.assertFalse(blocked, "Command writes must obey the host deadline even while its process is alive")
            self.assertLess(time.monotonic() - started, .6)
            self.assertEqual(len(errors), 1)
            self.assertIsInstance(errors[0], TimeoutError)
        finally:
            connection.process.kill()
            connection.process.wait(timeout=2)
            sender.join(timeout=2)
            connection.close()

    def test_close_does_not_wait_for_a_long_command_timeout_on_a_full_pipe(self):
        small = {"obs_dim": 1, "max_actions": 1, "action_dim": 1}
        peer = ("import os,struct,time; os.read(0,20); "
                "os.write(1,struct.pack('<8I',0x534f4931,1,2,1,1,1,56,64)+bytes(64)); "
                "time.sleep(60)")
        launch = host.subprocess.Popen

        def stalled_peer(*_args, **kwargs):
            process = launch([sys.executable, "-c", peer], **kwargs)
            fcntl.fcntl(process.stdin.fileno(), fcntl.F_SETPIPE_SZ, 4096)
            return process

        with mock.patch.object(host, "catalog", return_value=small), \
                mock.patch.object(host.subprocess, "Popen", side_effect=stalled_peer):
            connection = host.Host(batch=2, workers=1, timeout=60)
        os.write(connection.process.stdin.fileno(), bytes(4096))
        closer = threading.Thread(target=connection.close, daemon=True)
        closer.start()
        closer.join(timeout=1.5)
        blocked = closer.is_alive()
        try:
            self.assertFalse(blocked, "Cleanup must not wait for the full command timeout")
            self.assertIsNotNone(connection.process.poll())
            self.assertFalse(Path(connection.filename).exists())
        finally:
            if connection.process.poll() is None:
                connection.process.kill()
            connection.process.wait(timeout=2)
            closer.join(timeout=2)
            connection.close()


@unittest.skipUnless(host.BINARY.exists() and host.DOTNET.exists(), "Build the headless host before protocol integration")
class FrozenHostProtocolTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.temporary = tempfile.TemporaryDirectory()
        cls.release = Path(cls.temporary.name) / "host"
        shutil.copytree(host.BINARY.parent, cls.release)
        cls.binary = cls.release / host.BINARY.name

    @classmethod
    def tearDownClass(cls):
        cls.temporary.cleanup()

    def test_stale_shell_opponent_setting_does_not_change_a_training_host(self):
        with mock.patch.dict(os.environ, {"SHARDS_ZERO_OPPONENT": "/missing-stale-incumbent-bundle"}):
            with host.Host(batch=2, workers=1, seed=0x2100000000000800,
                           binary=self.binary, opponent_bundle=None, paired=False) as connection:
                self.assertTrue(connection.mask.any(axis=1).all())
                self.assertTrue((connection.done == 0).all())

    def test_held_lanes_are_unchanged_and_seed_reset_reproduces_every_raw_input(self):
        seed = 0x2100000000000900
        with host.Host(batch=4, workers=2, seed=seed, binary=self.binary, automation=True) as connection:
            original = {name: getattr(connection, name).copy()
                        for name in ("obs", "candidates", "mask", "rewards", "done", "actors")}
            filename, process = connection.filename, connection.process
            self.assertTrue(Path(filename).exists())
            self.assertTrue(connection.mask.any(axis=1).all())
            for _ in range(20):
                before = {name: getattr(connection, name)[[1, 3]].copy() for name in original}
                actions = np.full(4, -1, np.int32)
                live = np.flatnonzero((connection.done == 0) & np.array([True, False, True, False]))
                if not live.size:
                    break
                actions[live] = connection.mask[live].argmax(axis=1)
                connection.advance(actions)
                for name, expected in before.items():
                    actual = getattr(connection, name)[[1, 3]]
                    np.testing.assert_array_equal(actual.view(np.uint32), expected.view(np.uint32), err_msg=name)
            connection.reset(seed)
            for name, expected in original.items():
                np.testing.assert_array_equal(getattr(connection, name).view(np.uint32), expected.view(np.uint32), err_msg=name)
            with self.assertRaisesRegex(ValueError, "Action index"):
                connection.advance(np.array([-2, -1, -1, -1]))
            with self.assertRaisesRegex(ValueError, "differs"):
                connection.advance(np.zeros(3, np.int32))
            connection.advance(np.full(4, -1, np.int32))
            for name, expected in original.items():
                np.testing.assert_array_equal(getattr(connection, name).view(np.uint32), expected.view(np.uint32), err_msg=name)
        self.assertIsNotNone(process.poll())
        self.assertFalse(Path(filename).exists())
        connection.close()


if __name__ == "__main__":
    unittest.main()
