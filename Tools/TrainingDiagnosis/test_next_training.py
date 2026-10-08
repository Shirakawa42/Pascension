import json
from pathlib import Path
import subprocess
import sys
import tempfile
import time
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parent))
from next_training import alpha_for_review, child, verdict, review_transition


def result(low=.4, high=.6, *, complete=True, games=2000, censored=0):
    return {'summary': dict(evaluation_finished=complete, recorded_games=games,
        censored=censored, missing_games=0, paired_hoeffding_score_interval=[low, high])}


class ControllerTests(unittest.TestCase):
    def test_no_progress_from_point_estimate_or_partial_result(self):
        self.assertEqual(verdict([result(.49, .65)] * 3, 2000), 'not_demonstrated')
        for invalid in (result(complete=False), result(games=1999), result(censored=1)):
            self.assertEqual(verdict([result(.55, .7), invalid, result(.55, .7)], 2000), 'incomplete')

    def test_all_comparisons_must_pass_and_regression_stops(self):
        self.assertEqual(verdict([result(.51, .7)] * 3, 2000), 'progress')
        self.assertEqual(verdict([result(.51, .7), result(.3, .49), result()], 2000), 'regression')
        self.assertEqual(verdict([result(.5, .7)] * 3, 2000), 'not_demonstrated')
        self.assertEqual(verdict([], 2000), 'incomplete')
        self.assertEqual(verdict([result(float('nan'), .8)] * 3, 2000), 'incomplete')

    def test_stall_counter_requires_actual_progress(self):
        failures = 0
        for expected in (1, 2, 3):
            failures, stopped = review_transition('not_demonstrated', failures, 3)
            self.assertEqual(failures, expected)
            self.assertEqual(stopped, expected == 3)
        self.assertEqual(review_transition('progress', 2, 3), (0, False))
        self.assertEqual(review_transition('incomplete', 0, 3), (1, True))
        self.assertEqual(review_transition('regression', 0, 3), (1, True))

    def test_repeated_testing_total_error_budget(self):
        self.assertLessEqual(sum(3 * alpha_for_review(i) for i in range(1, 10000)), .05)
        self.assertGreater(alpha_for_review(1), alpha_for_review(2))
        with self.assertRaises(ValueError): alpha_for_review(0)

    def test_stopped_job_never_starts(self):
        with tempfile.TemporaryDirectory() as folder:
            marker = Path(folder) / 'started'
            with self.assertRaises(TimeoutError):
                child([sys.executable, '-c', f'open({str(marker)!r}, "w").close()'],
                    Path(folder) / 'log', deadline=time.monotonic() + 10, stop_check=lambda: True)
            self.assertFalse(marker.exists())

    def test_expired_job_never_starts(self):
        with tempfile.TemporaryDirectory() as folder:
            with self.assertRaises(TimeoutError):
                child([sys.executable, '-c', 'raise Exception("must not start")'],
                    Path(folder) / 'log', deadline=time.monotonic() - 1, stop_check=lambda: False)

    def test_child_failure_stops_and_timeout_joins_descendant(self):
        with tempfile.TemporaryDirectory() as folder:
            folder = Path(folder)
            with self.assertRaises(RuntimeError):
                child([sys.executable, '-c', 'raise SystemExit(7)'], folder / 'failure.log',
                    deadline=time.monotonic() + 10, stop_check=lambda: False)
            marker = folder / 'pid'
            code = ('import subprocess,sys,time,pathlib; '
                'p=subprocess.Popen([sys.executable,"-c","import time;time.sleep(60)"]); '
                f'pathlib.Path({str(marker)!r}).write_text(str(p.pid)); time.sleep(60)')
            with self.assertRaises(TimeoutError):
                child([sys.executable, '-c', code], folder / 'timeout.log',
                    deadline=time.monotonic() + 1, stop_check=lambda: False)
            from launch import _service_identity
            self.assertTrue(marker.exists())
            self.assertIsNone(_service_identity(int(marker.read_text())))


if __name__ == '__main__':
    unittest.main()
