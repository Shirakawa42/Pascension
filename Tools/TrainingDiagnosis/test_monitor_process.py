import json
import os
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

from monitor_experiment import job_snapshot, job_timing, process_alive


class ProcessMonitoringTests(unittest.TestCase):
    def test_benchmark_rates_use_wall_time_despite_a_smaller_elapsed_timer(self):
        with tempfile.TemporaryDirectory() as temporary:
            directory = Path(temporary)
            (directory/'plan.json').write_text('{}')
            (directory/'progress.json').write_text('{}')
            os.utime(directory/'plan.json', (100, 100))
            os.utime(directory/'progress.json', (300, 300))
            progress = dict(completed=20, elapsed_seconds=100)
            live = job_timing(directory, progress, {}, True, now=400)
            self.assertEqual(live['elapsed_seconds'], 300)
            self.assertAlmostEqual(live['games_per_second'], 20/300)
            stopped = job_timing(directory, progress, {}, False, now=900)
            self.assertEqual(stopped['elapsed_seconds'], 200)
            self.assertAlmostEqual(stopped['games_per_second'], .1)

    def test_recorded_wall_start_and_terminal_duration_survive_file_copy_times(self):
        with tempfile.TemporaryDirectory() as temporary:
            directory = Path(temporary)
            progress = dict(completed=60, elapsed_seconds=90, elapsed_wall_seconds=120)
            plan = dict(started_wall=100)
            self.assertEqual(job_timing(directory, progress, plan, False)['elapsed_seconds'], 120)
            self.assertEqual(job_timing(directory, progress, plan, True, now=250)['elapsed_seconds'], 150)
            self.assertIsNone(job_timing(directory, progress, plan, True, now=50)['elapsed_seconds'])

    def test_existing_pages_receive_corrected_time_without_rewriting_evidence(self):
        with tempfile.TemporaryDirectory() as temporary:
            directory = Path(temporary)
            saved = json.dumps(dict(state='complete', completed=60, elapsed_seconds=90,
                elapsed_wall_seconds=120))
            (directory/'progress.json').write_text(saved)
            (directory/'plan.json').write_text(json.dumps(dict(started_wall=100)))
            snapshot = job_snapshot(directory)
            self.assertEqual(snapshot['progress']['elapsed_seconds'], 120)
            self.assertEqual(snapshot['progress']['reported_elapsed_seconds'], 90)
            self.assertEqual(snapshot['timing']['games_per_second'], .5)
            self.assertEqual((directory/'progress.json').read_text(), saved)

    def test_reused_pid_other_boot_zombie_and_wrong_command_are_not_alive(self):
        with tempfile.TemporaryDirectory() as temporary:
            proc = Path(temporary)
            (proc/'sys/kernel/random').mkdir(parents=True)
            (proc/'sys/kernel/random/boot_id').write_text('boot-one\n')
            (proc/'42').mkdir()
            fields = ['S'] + ['0'] * 18 + ['1234']
            (proc/'42/stat').write_text('42 (worker (name)) ' + ' '.join(fields))
            (proc/'42/cmdline').write_bytes(b'python\0train.py\0')
            owner = dict(pid=42, startticks=1234, command=['python', 'train.py'], boot_id='boot-one')
            self.assertTrue(process_alive(owner, proc))
            self.assertTrue(process_alive(dict(owner=owner), proc))
            for change in (dict(startticks=1235), dict(boot_id='other-boot'), dict(command=['python', 'other.py'])):
                with self.subTest(change=change):
                    self.assertFalse(process_alive(dict(owner, **change), proc))
            fields[0] = 'Z'
            (proc/'42/stat').write_text('42 (worker) ' + ' '.join(fields))
            self.assertFalse(process_alive(owner, proc))

    def test_stale_running_status_is_overridden_but_completed_results_remain(self):
        with tempfile.TemporaryDirectory() as temporary:
            directory = Path(temporary)
            (directory/'owner.json').write_text(json.dumps(dict(pid=42)))
            with patch('monitor_experiment.process_alive', return_value=False):
                for state in ('running', 'complete'):
                    (directory/'progress.json').write_text(json.dumps(dict(state=state, completed=80)))
                    snapshot = job_snapshot(directory)
                    self.assertEqual(snapshot['progress']['completed'], 80)
                    if state == 'running':
                        self.assertEqual(snapshot['progress']['saved_state'], 'running')
                        self.assertIn('stopped', snapshot['progress']['state'])
                    else:
                        self.assertEqual(snapshot['progress']['state'], 'complete')
                (directory/'plan.json').write_text(json.dumps(dict(final_protocol_sha256='fixed')))
                self.assertEqual(job_snapshot(directory)['stage'], 'final benchmark')


if __name__ == '__main__':
    unittest.main()
