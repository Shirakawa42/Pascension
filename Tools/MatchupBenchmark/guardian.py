"""Independent continuation, armed before the benchmark stops any training."""
from pathlib import Path
from dataclasses import asdict
import argparse,json,os,signal,subprocess,sys,time
ROOT=Path(__file__).resolve().parents[2];sys.path.insert(0,str(ROOT/'Tools/TrainingWatchdog'))
from runner import inspect,decode,signal_owned,atomic,Runner,boot

def read(p):return json.loads(Path(p).read_text())
def wait_gone(owner,seconds=30):
 end=time.monotonic()+seconds
 while inspect(owner.pid)==owner:
  if time.monotonic()>end:raise RuntimeError('Owned process did not exit')
  time.sleep(.2)

def main(work):
 work=Path(work);receipt=read(work/'ownership.json');control=Path(receipt['control']);campaign=Path(receipt['campaign']);owner=decode(receipt['coordinator']);watchdog=decode(receipt['watchdog']);marker=control.parent/'root-intervention.json'
 def record(state,**fields):atomic(work/'lifecycle.json',dict(state=state,wall=time.time(),**fields))
 record('guardian_armed',owner=asdict(inspect(os.getpid())))
 while inspect(owner.pid)==owner:time.sleep(2)
 # Cleanup is normally completed by the coordinator. Its crash must not leave
 # a benchmark host running alongside the resumed trainer.
 sys.path.insert(0,str(ROOT/'Tools/ZeroDepthTraining'))
 from launch import _service_identity,_service_members,_join_service_descendants
 child_path=work/'benchmark-owner.json'
 if child_path.exists():
  child=read(child_path)
  unpack=lambda x:tuple(x[:4])+ (bytes.fromhex(x[4]),x[5])
  _join_service_descendants(unpack(child['owner']),[unpack(m) for m in child.get('members',[])])
 if inspect(watchdog.pid)!=watchdog:
  record('resume_requires_review',error='Original watchdog owner changed');return
 state=read(control/'state.json');trainer=decode(state.get('trainer'));launcher=decode(state.get('launcher'))
 if trainer and inspect(trainer.pid)==trainer:
  signal_owned(watchdog,signal.SIGCONT);record('original_training_continues');return
 if launcher and inspect(launcher.pid)==launcher:wait_gone(launcher)
 config=read(control/'config.json');controller=Runner(config,control)
 stops=any(p.exists() for p in [control/'STOP',campaign/'STOP'])
 if stops or controller.remaining_cap()<=30:
  state.update(status='stopped' if stops else 'deadline',launcher=None,trainer=None,owned_descendants=[],stop_reason='durable_STOP' if stops else 'original_deadline',updated_wall=time.time())
  atomic(control/'state.json',state);signal_owned(watchdog,signal.SIGKILL);wait_gone(watchdog,5)
  record('durable_stop_honored' if stops else 'deadline_reached')
 else:
  checked=controller.cpu_check(campaign)
  if checked['campaign_id']!=receipt['campaign_id']:raise RuntimeError('Campaign identity changed')
  # Keep the SAME deadline; never add the benchmark duration to authorization.
  remaining=controller.remaining_cap()
  if remaining<=30:
   signal_owned(watchdog,signal.SIGCONT);record('deadline_reached');return
  state.update(status='starting',launcher=None,trainer=None,owned_descendants=[],attempt_has_active=False,authorization_remaining_seconds=remaining,observed_wall=time.time(),observed_monotonic=time.monotonic(),observed_boot_id=boot())
  for key in ('error','cleanup_error','stall_reason','stop_reason'):state.pop(key,None)
  atomic(control/'state.json',state);signal_owned(watchdog,signal.SIGKILL);wait_gone(watchdog,5)
  with (work/'resumed-watchdog.log').open('a') as log:
   process=subprocess.Popen(list(watchdog.command),stdin=subprocess.DEVNULL,stdout=log,stderr=subprocess.STDOUT,start_new_session=True,cwd=ROOT)
  until=time.monotonic()+180
  while time.monotonic()<until:
   state=read(control/'state.json')
   if state.get('watchdog_pid')==process.pid and state['status']=='training':break
   if process.poll() is not None:raise RuntimeError('Replacement watchdog exited')
   time.sleep(1)
  else:raise RuntimeError('Replacement training startup timed out')
  record('training_resumed',watchdog_pid=process.pid,saved_games=checked['games'],original_deadline_wall=config['hard_deadline_wall'])
  if not (Path(receipt['reviews'])/'STOP').exists():subprocess.run(['systemctl','--user','start',receipt['review_service']],check=True)
 current=read(marker)
 if current.get('work_directory')==str(work):
  current.update(status='complete',phase=read(work/'lifecycle.json')['state'],root_owns_live_migration=False,updated_wall=time.time());atomic(marker,current)

if __name__=='__main__':
 p=argparse.ArgumentParser();p.add_argument('--work',type=Path,required=True);args=p.parse_args()
 try:main(args.work)
 except BaseException as error:
  atomic(args.work/'lifecycle.json',dict(state='resume_requires_review',wall=time.time(),error=str(error)))
  # Never leave a held watchdog forever after a failed continuation.
  receipt=read(args.work/'ownership.json');w=decode(receipt['watchdog'])
  if inspect(w.pid)==w:signal_owned(w,signal.SIGCONT)
  raise
