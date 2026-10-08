"""Disposable owned CPU processes; never launch the production training stack."""
import hashlib,json,os,signal,subprocess,sys,tempfile,threading,time,unittest
from dataclasses import replace
from pathlib import Path
from unittest.mock import patch
sys.path.insert(0,str(Path(__file__).resolve().parents[1]))
from runner import Runner,Lock,SCHEMA,STATE_SCHEMA,inspect,signal_owned,decode,atomic,failure_reason,completed_evaluation,boot

FAKE = r'''
import argparse,hashlib,json,os,signal,subprocess,sys,time
from pathlib import Path
p=argparse.ArgumentParser();p.add_argument('--campaign');p.add_argument('--run-dir');p.add_argument('--seconds');p.add_argument('--config');p.add_argument('--resume',nargs='?',const='yes');p.add_argument('--port');p.add_argument('--bind')
a=p.parse_args();folder=Path(a.campaign or a.run_dir);role=Path(__file__).stem
def get(name):return json.loads((folder/name).read_text())
def put(name,value):
 t=folder/(name+'.tmp-'+str(os.getpid()));t.write_text(json.dumps(value));os.replace(t,folder/name)
if role=='monitor':
 put('monitor.json',{'pid':os.getpid()})
 while True:time.sleep(.02)
elif role=='host_child':
 (folder/'host.pid').write_text(str(os.getpid()))
 while True:time.sleep(.02)
elif role=='train':
 child=subprocess.Popen([sys.executable,str(Path(__file__).with_name('host_child.py')),'--run-dir',str(folder)])
 data=get('budget.json');mode=get('control.json').get('mode');mono=time.monotonic()
 data['active']={'pid':os.getpid(),'boot_id':Path('/proc/sys/kernel/random/boot_id').read_text().strip(),'last_heartbeat_monotonic':mono-(125 if mode=='stale' else 0),'progress':{'games':1,'generations':1}}
 put('budget.json',data);(folder/'train.pid').write_text(str(os.getpid()));stopped=False
 def stop(*_):
  global stopped;stopped=True
 signal.signal(signal.SIGTERM,stop)
 (folder/'train.ready').write_text('handlers installed')
 while not stopped:
  if mode!='stale':
   data['active']['last_heartbeat_monotonic']=time.monotonic();put('budget.json',data)
  time.sleep(.02)
 (folder/'clean.checkpoint').write_text('complete boundary')
 child.terminate();child.wait(timeout=2)
 data['active']=None;put('budget.json',data);put('status.json',{'state':'complete'})
else:
 count=int((folder/'launch.count').read_text())+1 if (folder/'launch.count').exists() else 1
 (folder/'launch.count').write_text(str(count));control=get('control.json');mode=control.get('mode')
 if mode=='kill':os.kill(os.getpid(),signal.SIGKILL)
 if mode=='error':raise RuntimeError('Nonfinite fixture is never retried')
 requested=False
 if mode in ('wait','stale'):
  child=subprocess.Popen([sys.executable,str(Path(__file__).with_name('train.py')),'--run-dir',str(folder),'--seconds',a.seconds,'--config',str(folder/'config.json'),'--resume',str(folder/'latest.soicp')],start_new_session=True)
  def stop(*_):
   global requested;requested=True;child.terminate()
  signal.signal(signal.SIGTERM,stop)
  put('supervisor.json',{'supervisor_pid':os.getpid(),'trainer_pid':child.pid,'state':'starting'})
  code=child.wait()
 else:code=0
 put('supervisor.json',{'supervisor_pid':os.getpid(),'state':'complete' if code==0 else 'failed','returncode':code,'stop_reason':'requested_stop' if requested else None})
 if code==0 and not requested:
  pairs=get('prepared.json')['post_training_pairs']
  put('post-training-evaluation-fixture.json',{'purpose':'final_strength_evaluation','pairs':pairs,
   'checkpoint_sha256':hashlib.sha256((folder/'latest.soicp').read_bytes()).hexdigest(),
   'incumbent_manifest_sha256':hashlib.sha256((folder/'incumbent/manifest.json').read_bytes()).hexdigest(),
   'summary':{'evaluation_finished':True,'planned_pairs':pairs,'complete_pairs':pairs,'recorded_games':2*pairs}})
'''


class Fixture:
    def __init__(self,folder,mode='complete',retries=2):
        self.root=Path(folder);self.project=self.root/'project';scripts=self.project/'Tools/ZeroDepthTraining';scripts.mkdir(parents=True)
        for role in ('launch','train','monitor','host_child'):(scripts/(role+'.py')).write_text(FAKE)
        self.campaign=self.root/'campaign';self.campaign.mkdir();(self.campaign/'incumbent').mkdir()
        atomic(self.campaign/'control.json',{'mode':mode});atomic(self.campaign/'config.json',{})
        atomic(self.campaign/'budget.json',{'campaign_id':'fixture','limit_seconds':15.,'charged_seconds':0.,'active':None,'collection_accounting':{'censored_games':0}})
        atomic(self.campaign/'prepared.json',{'post_training_pairs':2});atomic(self.campaign/'incumbent/manifest.json',{'frozen':True})
        (self.campaign/'latest.soicp').write_bytes(b'frozen-weight-and-RNG-fixture')
        self.directory=self.root/'watchdog';self.directory.mkdir()
        self.config=dict(schema=SCHEMA,initial_campaign=str(self.campaign),python=sys.executable,seconds=15.,
            hard_deadline_wall=time.time()+15.,port=0,bind='127.0.0.1',max_restarts=retries,poll_seconds=.02,grace_seconds=.4,
            recovery_root=str(self.directory/'recoveries'),project=str(self.project))
        self.checks=0
        def check(campaign):
            self.checks+=1;data=json.loads((Path(campaign)/'budget.json').read_text())
            if data['active'] is not None:raise RuntimeError('fixture refuses live trainer recovery')
            return dict(campaign_id=data['campaign_id'],remaining_seconds=15.,limit_seconds=data['limit_seconds'])
        self.runner=Runner(self.config,self.directory,checker=check)
    def cleanup(self):
        for key in ('launcher','trainer','monitor'):
            owner=decode(self.runner.state.get(key))
            if owner and inspect(owner.pid)==owner:signal_owned(owner,signal.SIGKILL,group=True)
        for owner in self.runner.state.get('owned_descendants',[]):
            owner=decode(owner)
            if inspect(owner.pid)==owner:signal_owned(owner,signal.SIGKILL)
        for process in (self.runner.launcher_process,self.runner.monitor_process):
            if process:
                try:process.wait(timeout=2)
                except subprocess.TimeoutExpired:pass


def await_condition(callback):
    end=time.monotonic()+3
    while time.monotonic()<end:
        if callback():return
        time.sleep(.01)
    raise AssertionError('Disposable process fixture timed out')


class Tests(unittest.TestCase):
    def fake_proc(self,stats,command):
        class File:
            def __init__(self,name):self.name=name
            def read_text(self):return stats.pop(0)
            def read_bytes(self):return command
        class Folder:
            def __truediv__(self,name):return File(name)
        class Proc:
            def __truediv__(self,pid):return Folder()
        return Proc()
    def stat(self,state='R',ticks=100):
        fields=['0']*20;fields[0]=state;fields[2]='200';fields[3]='200';fields[19]=str(ticks)
        return '200 (fixture) '+' '.join(fields)
    def test_exit_between_stat_and_cmdline_is_not_owner_reuse(self):
        fake=self.fake_proc([self.stat()],b'')
        with patch('runner.Path',return_value=fake):self.assertIsNone(inspect(200))
    def test_stat_exit_or_start_change_while_argv_read_is_rejected(self):
        for after in (self.stat('Z'),self.stat('X'),self.stat(ticks=999)):
            fake=self.fake_proc([self.stat(),after],b'python\0fixture.py\0')
            with patch('runner.Path',return_value=fake):self.assertIsNone(inspect(200))
    def test_short_real_completions_never_misreport_exit_as_pid_reuse(self):
        for index in range(12):
            with self.subTest(index=index),tempfile.TemporaryDirectory() as folder:
                fixture=Fixture(folder)
                try:
                    result=fixture.runner.run()
                    self.assertEqual(result['status'],'completed',json.dumps(result))
                    self.assertEqual(result['restarts'],0)
                finally:fixture.cleanup()
    def test_single_owner_lock_rejects_actual_second_process(self):
        with tempfile.TemporaryDirectory() as folder:
            path=Path(folder)/'lock'
            with Lock(path):
                code='import sys;sys.path.insert(0,sys.argv[1]);from runner import Lock;Lock(sys.argv[2]).__enter__()'
                result=subprocess.run([sys.executable,'-c',code,str(Path(__file__).resolve().parents[1]),str(path)],capture_output=True,text=True)
                self.assertNotEqual(result.returncode,0);self.assertIn('Another watchdog',result.stderr)
    def test_pidfd_kills_only_bound_owned_process_not_unrelated(self):
        first=subprocess.Popen([sys.executable,'-c','import time;time.sleep(10)'],start_new_session=True)
        second=subprocess.Popen([sys.executable,'-c','import time;time.sleep(10)'],start_new_session=True)
        try:
            owner=inspect(first.pid)
            with self.assertRaises(RuntimeError):signal_owned(replace(owner,start_ticks=owner.start_ticks+1),signal.SIGKILL)
            self.assertIsNone(first.poll());self.assertIsNone(second.poll())
            signal_owned(owner,signal.SIGKILL);first.wait(timeout=2)
            self.assertIsNone(second.poll())
        finally:
            for process in (first,second):
                if process.poll() is None:process.kill()
                process.wait(timeout=2)
    def test_actual_process_losses_retry_bounded_without_seconds_reset(self):
        with tempfile.TemporaryDirectory() as folder:
            fixture=Fixture(folder,'kill',2)
            try:
                deadline=fixture.config['hard_deadline_wall'];result=fixture.runner.run()
                self.assertEqual(result['status'],'retry_exhausted');self.assertEqual(result['restarts'],2)
                self.assertEqual(int((fixture.campaign/'launch.count').read_text()),3)
                self.assertEqual(fixture.config['hard_deadline_wall'],deadline)
                for log in fixture.directory.glob('launcher-attempt*.log'):self.assertNotIn('Traceback',log.read_text())
                self.assertEqual((fixture.campaign/'latest.soicp').read_bytes(),b'frozen-weight-and-RNG-fixture')
            finally:fixture.cleanup()
    def test_actual_python_nonfinite_error_never_retries(self):
        with tempfile.TemporaryDirectory() as folder:
            fixture=Fixture(folder,'error',3)
            try:
                result=fixture.runner.run();self.assertEqual(result['status'],'blocked')
                self.assertEqual(result['restarts'],0);self.assertEqual(int((fixture.campaign/'launch.count').read_text()),1)
            finally:fixture.cleanup()
    def test_complete_evaluation_never_restarts(self):
        with tempfile.TemporaryDirectory() as folder:
            fixture=Fixture(folder)
            try:
                result=fixture.runner.run();self.assertEqual(result['status'],'completed',json.dumps(result))
                self.assertEqual(result['restarts'],0);self.assertEqual(int((fixture.campaign/'launch.count').read_text()),1)
                second=Runner(fixture.config,fixture.directory,checker=lambda _:self.fail('Completed state must not load/train again'))
                self.assertEqual(second.run()['status'],'completed')
            finally:fixture.cleanup()
    def test_durable_stop_before_launch_never_checks_or_starts(self):
        with tempfile.TemporaryDirectory() as folder:
            fixture=Fixture(folder)
            (fixture.directory/'STOP').write_text('cancel')
            self.assertEqual(fixture.runner.run()['status'],'stopped');self.assertEqual(fixture.checks,0)
            self.assertFalse((fixture.campaign/'launch.count').exists())
    def test_service_stop_checkpoints_trainer_and_joins_host(self):
        with tempfile.TemporaryDirectory() as folder:
            fixture=Fixture(folder,'wait');result={}
            worker=threading.Thread(target=lambda:result.update(fixture.runner.run()))
            try:
                worker.start();await_condition(lambda:(fixture.campaign/'host.pid').exists() and (fixture.campaign/'train.ready').exists())
                await_condition(lambda:bool(fixture.runner.state.get('trainer')))
                fixture.runner.stop_requested=True;worker.join(timeout=3)
                self.assertFalse(worker.is_alive());self.assertEqual(result['status'],'interrupted')
                self.assertTrue((fixture.campaign/'clean.checkpoint').exists())
                self.assertIsNone(inspect(int((fixture.campaign/'train.pid').read_text())))
                self.assertIsNone(inspect(int((fixture.campaign/'host.pid').read_text())))
            finally:fixture.runner.stop_requested=True;fixture.cleanup();worker.join(timeout=2)
    def test_same_boot_service_restart_resumes_but_durable_stop_does_not(self):
        with tempfile.TemporaryDirectory() as folder:
            fixture=Fixture(folder,'wait');result={};worker=threading.Thread(target=lambda:result.update(fixture.runner.run()))
            original_runner=fixture.runner
            resumed=None
            try:
                worker.start();await_condition(lambda:bool(fixture.runner.state.get('trainer')) and (fixture.campaign/'train.ready').exists())
                fixture.runner.stop_requested=True;worker.join(timeout=3)
                self.assertEqual(result['status'],'interrupted')
                atomic(fixture.campaign/'control.json',{'mode':'complete'})
                resumed=Runner(fixture.config,fixture.directory,checker=fixture.runner.checker)
                second=resumed.run();self.assertEqual(second['status'],'completed',json.dumps(second))
                self.assertEqual(second['restarts'],1);self.assertEqual(int((fixture.campaign/'launch.count').read_text()),2)
                fixture.runner=resumed
                (fixture.directory/'STOP').write_text('cancel')
                self.assertEqual(Runner(fixture.config,fixture.directory,checker=lambda _:self.fail()).run()['status'],'stopped')
                self.assertEqual(int((fixture.campaign/'launch.count').read_text()),2)
            finally:
                if resumed:fixture.runner=resumed
                fixture.cleanup();worker.join(timeout=2)
                # The respawned controller adopts the same monitor identity;
                # retain/reap the original Popen handle until its owned process
                # has actually exited, avoiding misleading ResourceWarnings.
                for process in (original_runner.launcher_process,original_runner.monitor_process):
                    if process:process.wait(timeout=2)
    def test_dead_launcher_preserves_graceful_checkpoint_before_retry(self):
        with tempfile.TemporaryDirectory() as folder:
            fixture=Fixture(folder,'wait',0);result={};worker=threading.Thread(target=lambda:result.update(fixture.runner.run()))
            try:
                worker.start();await_condition(lambda:bool(fixture.runner.state.get('trainer')) and (fixture.campaign/'host.pid').exists())
                signal_owned(decode(fixture.runner.state['launcher']),signal.SIGKILL)
                worker.join(timeout=3);self.assertFalse(worker.is_alive())
                self.assertEqual(result['status'],'retry_exhausted');self.assertTrue((fixture.campaign/'clean.checkpoint').exists())
                self.assertIsNone(inspect(int((fixture.campaign/'host.pid').read_text())))
            finally:fixture.runner.stop_requested=True;fixture.cleanup();worker.join(timeout=2)
    def test_actual_monitor_restart_is_owned_and_bounded(self):
        with tempfile.TemporaryDirectory() as folder:
            fixture=Fixture(folder)
            try:
                fixture.runner.spawn_monitor();old=decode(fixture.runner.state['monitor'])
                signal_owned(old,signal.SIGTERM);fixture.runner.monitor_process.wait(timeout=2)
                fixture.runner.spawn_monitor();new=decode(fixture.runner.state['monitor'])
                self.assertNotEqual(old,new);self.assertEqual(fixture.runner.state['monitor_restarts'],2)
                self.assertIsNone(inspect(old.pid));self.assertEqual(inspect(new.pid),new)
            finally:fixture.cleanup()
    def test_error_censor_deadline_and_requested_stop_classification(self):
        self.assertEqual(failure_reason(-9,{'stop_reason':'hard_deadline'},''),'deadline')
        self.assertEqual(failure_reason(-9,{'stop_reason':'requested_stop'},''),'stopped')
        for text in ('Traceback (most recent call last)','Nonfinite model','Four censored games in the recent 4096 attempts; inspect traces before further learning','checksum mismatch','CUDA error: illegal memory access'):
            self.assertEqual(failure_reason(-9,{},text),'unsafe_error')
        self.assertEqual(failure_reason(-9,{},'',censored_increased=True),'unsafe_error')
        self.assertEqual(failure_reason(1,{},''),'unsafe_error')
    def test_partial_or_mismatched_evaluation_is_not_completion(self):
        with tempfile.TemporaryDirectory() as folder:
            fixture=Fixture(folder);path=fixture.campaign/'post-training-evaluation-fixture.json'
            value=dict(purpose='final_strength_evaluation',pairs=2,checkpoint_sha256='wrong',incumbent_manifest_sha256='wrong',
                summary=dict(evaluation_finished=True,planned_pairs=2,complete_pairs=2,recorded_games=4))
            atomic(path,value);self.assertFalse(completed_evaluation(fixture.campaign))
            value['checkpoint_sha256']=hashlib.sha256((fixture.campaign/'latest.soicp').read_bytes()).hexdigest()
            value['incumbent_manifest_sha256']=hashlib.sha256((fixture.campaign/'incumbent/manifest.json').read_bytes()).hexdigest()
            value['summary']['evaluation_finished']=False;atomic(path,value)
            self.assertFalse(completed_evaluation(fixture.campaign))
            value['summary']['evaluation_finished']=True;atomic(path,value)
            self.assertTrue(completed_evaluation(fixture.campaign))


if __name__=='__main__':unittest.main()
