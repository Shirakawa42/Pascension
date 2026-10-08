import threading
import unittest
import numpy as np

from inference_pool import InferencePool


class Producer:
    def __init__(self, barrier, fail=False):
        self.barrier, self.fail = barrier, fail
        self.stop = lambda: False

    def collect(self, seed, infer, **kwargs):
        self.barrier.wait(timeout=3)
        if self.fail:raise RuntimeError('transport failure')
        total = 0
        for step in range(3):
            if self.stop():raise InterruptedError('stopped')
            # Reuse the same shared buffer after receiving each reply.
            packet = np.full((seed, 8), seed+step, np.float32)
            logits, values = infer(packet, None)
            np.testing.assert_array_equal(logits, packet[:, :4]*2)
            np.testing.assert_array_equal(values, packet[:, 0]+1)
            packet.fill(-99)
            total += len(logits)
        return total


class InferencePoolTests(unittest.TestCase):
    def test_outputs_stay_with_their_producer_and_buffers_can_be_reused(self):
        pool = InferencePool(lambda rows, _: (rows[:, :4]*2, rows[:, 0]+1))
        barrier = threading.Barrier(2)
        self.assertEqual(pool.collect([Producer(barrier), Producer(barrier)], [2, 5]), [6, 15])
        self.assertEqual(pool.request_count, 6)

    def test_gpu_failure_releases_all_waiting_producers(self):
        def fail(*_):raise RuntimeError('gpu failure')
        barrier = threading.Barrier(2)
        with self.assertRaisesRegex(RuntimeError, 'gpu failure'):
            InferencePool(fail).collect([Producer(barrier), Producer(barrier)], [2, 5])

    def test_transport_failure_releases_other_producers(self):
        barrier = threading.Barrier(2)
        pool = InferencePool(lambda rows, _: (rows[:, :4]*2, rows[:, 0]+1))
        with self.assertRaisesRegex(RuntimeError, 'transport failure'):
            pool.collect([Producer(barrier, fail=True), Producer(barrier)], [2, 5])


if __name__ == '__main__':unittest.main()
