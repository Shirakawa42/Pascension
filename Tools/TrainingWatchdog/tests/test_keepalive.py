"""Read-only sentinel tests; disposable CPU runners never import training code."""
from __future__ import annotations

import contextlib
from dataclasses import asdict, replace
import hashlib
import json
from pathlib import Path
import subprocess
import sys
import tempfile
import time
import unittest
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import keepalive as k


FAKE = r'''
import fcntl, hashlib, json, os, sys, time
from pathlib import Path
config_path=Path(sys.argv[2]);directory=config_path.parent
config=json.loads(config_path.read_text());boot=Path('/proc/sys/kernel/random/boot_id').read_text().strip()
lock=(directory/'watchdog.lock').open('a');fcntl.flock(lock,fcntl.LOCK_EX)
state=dict(schema='shards-training-watchdog-state-v1',status='training',current_campaign=config['initial_campaign'],
 campaign_id='fixture',watchdog_pid=os.getpid(),watchdog_boot_id=boot,boot_id=boot,
 config_sha256=hashlib.sha256(json.dumps(config,sort_keys=True).encode()).hexdigest())
def put():
 path=directory/'state.json';temporary=directory/('state.tmp-'+str(os.getpid()))
 temporary.write_text(json.dumps(state));os.replace(temporary,path)
put()
while not (directory/'EXIT').exists() and not (directory/'STOP').exists():time.sleep(.01)
if (directory/'STOP').exists():
 time.sleep(.15);state['status']='stopped'
else:state['status']='interrupted'
put()
'''


class Fixture:
    def __init__(self, folder):
        self.folder = Path(folder)
        self.project = self.folder / 'project'
        scripts = self.project / 'Tools/TrainingWatchdog'
        scripts.mkdir(parents=True)
        self.script = scripts / 'runner.py'
        self.script.write_text(FAKE)
        self.campaign = self.folder / 'campaign'
        self.campaign.mkdir()
        self.control = self.folder / 'control'
        self.control.mkdir()
        self.path = self.control / 'config.json'
        self.config = dict(schema=k.CONFIG_SCHEMA, initial_campaign=str(self.campaign), python=sys.executable,
            seconds=100., hard_deadline_wall=time.time()+100., port=8766, bind='127.0.0.1',
            max_restarts=3, poll_seconds=5., grace_seconds=25., recovery_root=str(self.control/'recoveries'),
            project=str(self.project))
        self.path.write_text(json.dumps(self.config))
        self.sha = k.sha_file(self.path)
        self.runner_sha = k.sha_file(self.script)
        self.process = None

    def sentinel(self, **kwargs):
        return k.Sentinel(self.path, self.sha, hard_deadline_wall=self.config['hard_deadline_wall'],
            expected_runner_sha=self.runner_sha, poll_seconds=.01, **kwargs)

    def state(self, **fields):
        value = dict(schema=k.STATE_SCHEMA, status='training', current_campaign=str(self.campaign),
                     campaign_id='fixture', watchdog_pid=987654321, watchdog_boot_id=k.boot_id(), boot_id=k.boot_id(),
                     config_sha256=hashlib.sha256(json.dumps(self.config,sort_keys=True).encode()).hexdigest())
        value.update(fields)
        (self.control/'state.json').write_text(json.dumps(value))
        return value

    def owner(self, pid=12345):
        return k.Process(pid, 100, pid, pid, (sys.executable,str(self.script),'--config',str(self.path)), k.boot_id())

    def start(self):
        self.process = subprocess.Popen([sys.executable,str(self.script),'--config',str(self.path)],
                                        start_new_session=True,stdout=subprocess.DEVNULL,stderr=subprocess.PIPE)
        end=time.monotonic()+3
        while not (self.control/'state.json').exists():
            if self.process.poll() is not None:
                raise AssertionError(self.process.communicate()[1].decode())
            if time.monotonic()>end:raise AssertionError('CPU fixture startup timed out')
            time.sleep(.01)

    def close(self):
        if self.process is not None:
            if self.process.poll() is None:
                (self.control/'EXIT').touch()
                try:self.process.wait(timeout=2)
                except subprocess.TimeoutExpired:self.process.kill();self.process.wait(timeout=2)
            self.process.communicate(timeout=2)


class Tests(unittest.TestCase):
    @contextlib.contextmanager
    def fixture(self):
        with tempfile.TemporaryDirectory() as folder:
            fixture=Fixture(folder)
            try:yield fixture
            finally:fixture.close()

    def test_real_flock_adopts_without_mutation_and_requests_retry_only_after_exit(self):
        with self.fixture() as f:
            f.start();sentinel=f.sentinel()
            before={path.name:path.read_bytes() for path in f.control.iterdir()}
            result=sentinel.poll()
            self.assertEqual(result['disposition'],'holding')
            self.assertEqual(result['runner']['pid'],f.process.pid)
            self.assertEqual(before,{path.name:path.read_bytes() for path in f.control.iterdir()})
            (f.control/'EXIT').touch();f.process.wait(timeout=2)
            result=sentinel.poll()
            self.assertEqual(result['exit_code'],10)
            self.assertEqual(result['config_sha256'],f.sha)
            self.assertEqual(result['hard_deadline_wall'],f.config['hard_deadline_wall'])

    def test_kernel_observation_never_acquires_a_lock_and_does_not_create_a_missing_file(self):
        with self.fixture() as f:
            lock=f.control/'watchdog.lock'
            with patch('fcntl.flock',side_effect=AssertionError('sentinel must not acquire any lock')):
                self.assertFalse(k.lock_busy(lock));self.assertFalse(lock.exists())
            f.start()
            with patch('fcntl.flock',side_effect=AssertionError('sentinel must not acquire any lock')):
                self.assertTrue(k.lock_busy(lock));self.assertEqual(f.sentinel().poll()['disposition'],'holding')
            (f.control/'EXIT').touch();f.process.wait(timeout=2)
            self.assertFalse(k.lock_busy(lock))

    def test_durable_stop_waits_for_actual_busy_controller_cleanup(self):
        with self.fixture() as f:
            f.start();sentinel=f.sentinel();self.assertEqual(sentinel.poll()['disposition'],'holding')
            (f.control/'STOP').touch()
            result=sentinel.poll();self.assertEqual(result['disposition'],'holding')
            self.assertIn('awaiting watchdog cleanup',result['reason'])
            f.process.wait(timeout=2)
            self.assertEqual(sentinel.poll()['exit_code'],0)

    def test_config_runner_and_parent_deadline_pins_refuse_changes(self):
        with self.fixture() as f:
            with self.assertRaises(ValueError):k.Sentinel(f.path,'0'*64)
            with self.assertRaises(ValueError):k.Sentinel(f.path,f.sha,hard_deadline_wall=f.config['hard_deadline_wall']+1)
            with self.assertRaises(ValueError):k.Sentinel(f.path,f.sha,expected_runner_sha='0'*64)
            sentinel=f.sentinel();f.path.write_text(f.path.read_text()+' ')
            self.assertEqual(sentinel.poll()['exit_code'],20)
        with self.fixture() as f:
            sentinel=f.sentinel();f.script.write_text(FAKE+'\n# changed\n')
            self.assertEqual(sentinel.poll()['exit_code'],20)

    def test_terminal_and_unsafe_statuses_never_request_a_runner(self):
        with self.fixture() as f:
            for phase,expected in [('completed',0),('deadline',0),('stopped',0),('blocked',20),('retry_exhausted',20)]:
                with self.subTest(phase=phase):
                    f.state(status=phase);self.assertEqual(f.sentinel().poll()['exit_code'],expected)

    def test_unknown_busy_controller_gets_bounded_grace_and_never_retry(self):
        with self.fixture() as f:
            f.state();sentinel=f.sentinel(startup_grace_seconds=1.)
            with patch.object(k,'lock_busy',return_value=True),patch.object(k.time,'monotonic',side_effect=[100.,100.,102.]):
                self.assertEqual(sentinel.poll()['disposition'],'holding')
                self.assertEqual(sentinel.poll()['exit_code'],20)

    def test_busy_published_pid_reuse_or_unrelated_command_is_unsafe(self):
        with self.fixture() as f:
            f.state(watchdog_pid=12345)
            owner=replace(f.owner(),command=(sys.executable,'unrelated.py'))
            with patch.object(k,'lock_busy',return_value=True),patch.object(k,'inspect',return_value=owner):
                result=f.sentinel().poll();self.assertEqual(result['exit_code'],20)
                self.assertIn('unrelated',result['reason'])

    def test_live_evaluation_remains_attached_after_training_cap(self):
        with self.fixture() as f:
            f.state(status='evaluating',watchdog_pid=12345)
            owner=f.owner()
            with patch.object(k,'lock_busy',return_value=True),patch.object(k,'inspect',return_value=owner),\
                 patch.object(k.time,'time',return_value=f.config['hard_deadline_wall']+30):
                result=f.sentinel().poll();self.assertEqual(result['disposition'],'holding')
                self.assertIsNone(result['exit_code'])

    def test_ownerless_evaluation_and_healthy_deadline_gap_never_restart_learning(self):
        with self.fixture() as f:
            f.state(status='evaluating')
            result=f.sentinel().poll();self.assertEqual(result['exit_code'],20)
            f.state(status='training',launcher={'pid':3456})
            (f.campaign/'supervisor.json').write_text(json.dumps(dict(supervisor_pid=3456,state='complete',returncode=0,stop_reason='hard_deadline')))
            self.assertEqual(f.sentinel().poll()['exit_code'],20)

    def test_ownerless_exact_evaluation_keeps_foreground_after_cap_then_refuses_retry(self):
        with self.fixture() as f:
            launcher=k.Process(3456,200,3456,3456,(sys.executable,'fixture-launch.py'),k.boot_id())
            record=asdict(launcher);record['command']=list(launcher.command)
            f.state(status='evaluating',launcher=record)
            sentinel=f.sentinel()
            with patch.object(k,'inspect',side_effect=lambda pid:launcher if pid==3456 else None),\
                 patch.object(k.time,'time',return_value=f.config['hard_deadline_wall']+10):
                result=sentinel.poll();self.assertEqual(result['disposition'],'holding')
                self.assertTrue(result['unsafe_alert']);self.assertEqual(result['retained_process']['pid'],3456)
                self.assertIsNone(result['exit_code'])
            with patch.object(k,'inspect',return_value=None),\
                 patch.object(k.time,'time',return_value=f.config['hard_deadline_wall']+11):
                self.assertEqual(sentinel.poll()['exit_code'],20)

    def test_stop_with_ownerless_exact_work_keeps_attachment_until_work_exits(self):
        with self.fixture() as f:
            trainer=k.Process(7890,200,7890,7890,(sys.executable,'fixture-train.py'),k.boot_id())
            record=asdict(trainer);record['command']=list(trainer.command)
            f.state(trainer=record);(f.control/'STOP').touch();sentinel=f.sentinel()
            with patch.object(k,'inspect',side_effect=lambda pid:trainer if pid==7890 else None):
                result=sentinel.poll();self.assertEqual(result['disposition'],'holding')
                self.assertTrue(result['unsafe_alert']);self.assertIsNone(result['exit_code'])
            with patch.object(k,'inspect',return_value=None):self.assertEqual(sentinel.poll()['exit_code'],0)

    def test_reused_saved_evaluation_pid_does_not_authorize_retaining_unrelated_work(self):
        with self.fixture() as f:
            original=k.Process(3456,200,3456,3456,(sys.executable,'fixture-launch.py'),k.boot_id())
            record=asdict(original);record['command']=list(original.command)
            f.state(status='evaluating',launcher=record)
            reused=replace(original,start_ticks=900,command=('unrelated',))
            with patch.object(k,'inspect',side_effect=lambda pid:reused if pid==3456 else None):
                self.assertEqual(f.sentinel().poll()['exit_code'],20)

    def test_ownerless_training_before_cap_requests_only_same_runner_even_with_live_trainer_metadata(self):
        with self.fixture() as f:
            f.state(launcher={'pid':4567},trainer={'pid':7890})
            with patch.object(k,'inspect',return_value=None),patch('subprocess.Popen',side_effect=AssertionError('must not launch')):
                result=f.sentinel().poll()
                self.assertEqual(result['exit_code'],10)
                self.assertEqual(result['current_campaign'],str(f.campaign))

    def test_deadline_remaining_backward_clock_and_corrupt_state_fail_closed(self):
        with self.fixture() as f:
            f.state();sentinel=f.sentinel()
            with patch.object(k.time,'time',return_value=f.config['hard_deadline_wall']+1):
                self.assertEqual(sentinel.poll()['exit_code'],20)
            f.state(authorization_remaining_seconds=0)
            self.assertEqual(f.sentinel().poll()['exit_code'],20)
            f.state(observed_wall=time.time()+50)
            self.assertEqual(f.sentinel().poll()['exit_code'],20)
            f.state(config_sha256='0'*64)
            self.assertEqual(f.sentinel().poll()['exit_code'],20)
            (f.control/'state.json').write_text('{bad json')
            self.assertEqual(f.sentinel().poll()['exit_code'],20)

    def test_readonly_observer_never_imports_model_or_signals_and_cli_terminal_protocol(self):
        with self.fixture() as f:
            f.state(status='completed')
            with patch('os.kill',side_effect=AssertionError('no signal')),patch('os.killpg',side_effect=AssertionError('no signal')):
                self.assertEqual(f.sentinel().poll()['exit_code'],0)
            source=Path(k.__file__)
            result=subprocess.run([sys.executable,str(source),'--config',str(f.path),'--expected-sha',f.sha,
                '--hard-deadline-wall',str(f.config['hard_deadline_wall']),'--expected-runner-sha',f.runner_sha],
                capture_output=True,text=True,timeout=2)
            self.assertEqual(result.returncode,0,result.stderr)
            rows=[json.loads(line) for line in result.stdout.splitlines()]
            self.assertEqual(len(rows),1);self.assertEqual(rows[-1]['protocol'],k.PROTOCOL)
            self.assertEqual(rows[-1]['exit_code'],0)
            self.assertEqual(rows[-1]['config_sha256'],f.sha)


if __name__=='__main__':unittest.main()
