"""Own an exclusive, frozen benchmark and prearm training continuation."""
from pathlib import Path
from dataclasses import asdict
import argparse,json,os,signal,subprocess,sys,time
ROOT=Path(__file__).resolve().parents[2];HERE=Path(__file__).resolve().parent
sys.path[:0]=[str(ROOT/'Tools/TrainingWatchdog'),str(ROOT/'Tools/ZeroDepthTraining'),str(ROOT/'Tools/TrainingPreflight')]
from runner import inspect,decode,signal_owned,atomic
from launch import _service_identity,_service_members,_join_service_descendants
from performance_upgrade import sha256_file,source_inventory
from league import export_frozen_policy
from evaluate import freeze_incumbent
from host import catalog
from catalog_check import compatible_catalog
PYTHON='/home/lva/.venvs/shards-preflight/bin/python'

def read(p):return json.loads(Path(p).read_text())
def spawn(command,log):
 with Path(log).open('a') as output:return subprocess.Popen(command,stdin=subprocess.DEVNULL,stdout=output,stderr=subprocess.STDOUT,start_new_session=True,cwd=ROOT)
def wait_gone(owner,seconds=90):
 until=time.monotonic()+seconds
 while inspect(owner.pid)==owner:
  if time.monotonic()>until:raise RuntimeError('Training exceeded graceful stop allowance')
  time.sleep(.2)

def main(work,control):
 work,control=Path(work).resolve(),Path(control).resolve();state=read(control/'state.json');campaign=Path(state['current_campaign']);identity=read(campaign/'identity.json');config=read(control/'config.json')
 assert state['status']=='training' and not (control/'STOP').exists()
 assert source_inventory(ROOT)['source_fingerprint']==identity['source_fingerprint']
 watchdog=inspect(state['watchdog_pid']);launcher=decode(state['launcher']);trainer=decode(state['trainer'])
 assert watchdog and inspect(launcher.pid)==launcher and inspect(trainer.pid)==trainer
 reviews=control.parent/'halfhour-reviews-8h-known-top';review_service='shards-halfhour-review-'+state['campaign_id']+'.service';subprocess.run(['systemctl','--user','stop',review_service],check=True)
 receipt=dict(coordinator=asdict(inspect(os.getpid())),watchdog=asdict(watchdog),control=str(control),campaign=str(campaign),campaign_id=state['campaign_id'],reviews=str(reviews),review_service=review_service,deadline_wall=config['hard_deadline_wall'])
 atomic(work/'ownership.json',receipt)
 guardian=spawn([PYTHON,str(HERE/'guardian.py'),'--work',str(work)],work/'guardian.log')
 until=time.monotonic()+15
 while not (work/'lifecycle.json').exists():
  if guardian.poll() is not None or time.monotonic()>until:raise RuntimeError('Continuation did not arm')
  time.sleep(.1)
 assert read(work/'lifecycle.json')['state']=='guardian_armed'
 marker=control.parent/'root-intervention.json';atomic(marker,dict(status='running',phase='user_requested_fixed_matchup_benchmark',pilot_owner=receipt['coordinator'],root_owns_live_migration=True,work_directory=str(work),updated_wall=time.time(),description='User requested4800games; learner excludesRez;6workers; training checkpointed and paused; original deadline unchanged. Independent continuation armed.'))
 signal_owned(watchdog,signal.SIGSTOP);signal_owned(launcher,signal.SIGTERM);wait_gone(launcher);wait_gone(trainer)
 assert read(campaign/'budget.json')['active'] is None
 # Atomic checkpoint inode snapshot; latest continues to belong to training.
 os.link(campaign/'latest.soicp',work/'snapshot.soicp');atomic(work/'identity.json',identity)
 import torch
 torch.set_num_threads(1)
 exported=export_frozen_policy(work/'snapshot.soicp',work/'learner',expected_identity=identity)
 freeze_incumbent(work/'incumbent',repo_root=ROOT)
 binary=HERE/'Host/bin/Release/net8.0/MatchupBenchmarkHost.dll'
 catalog_proof=compatible_catalog(read(campaign/'catalog.json'),catalog(binary))
 # The host may differ only in fixed initial hero assignment and its test command.
 original=ROOT/'Tools/ZeroDepthTraining/Host';copied=HERE/'Host'
 for source in original.glob('*.cs'):
  if source.name!='Program.cs':assert sha256_file(source)==sha256_file(copied/source.name),source.name
 old=(original/'Program.cs').read_text();new=(copied/'Program.cs').read_text()
 expected=old.replace('case "serve":Serve();break;','case "serve":Serve();break;\n                case "fixed-matchups-selftest":Print(FixedMatchups.SelfTest());break;').replace('AdvanceOpponent(game,opponent,i%2);','FixedMatchups.Apply(game,gameSeed,i%2);\n                                AdvanceOpponent(game,opponent,i%2);',1)
 assert new==expected
 plan={**read(work/'request.json'),'schema':'shards-fixed-hero-incumbent-benchmark-v1','catalog_proof':catalog_proof,'created_wall':time.time(),'checkpoint_games':exported['training']['games'],'checkpoint_sha256':sha256_file(work/'snapshot.soicp'),'learner_manifest_sha256':sha256_file(work/'learner/manifest.json'),'incumbent_manifest_sha256':sha256_file(work/'incumbent/manifest.json'),'binary':str(binary),'binary_sha256':sha256_file(binary),'training_source_fingerprint':identity['source_fingerprint'],'host_source_sha256':{p.name:sha256_file(p) for p in copied.glob('*.cs')},'batch':32,'policy_seed':6102026,'training_control':str(control),'training_campaign':str(campaign),'training_deadline_wall':config['hard_deadline_wall'],'learner_search_depth':0,'incumbent_search_enabled':True,'setup':'Actual legal hero-draft submissions; learner hero remains fixed across the paired seat swap; engine RNG untouched by assignment.','promotion_allowed':False}
 atomic(work/'plan.json',plan)
 child=spawn([PYTHON,str(HERE/'run.py'),'--work',str(work)],work/'benchmark.log');owner=_service_identity(child.pid);known=[];stopping=False
 def stop(*_):
  nonlocal stopping
  stopping=True
 signal.signal(signal.SIGTERM,stop);signal.signal(signal.SIGINT,stop)
 try:
  while True:
   known=_service_members(owner,known);atomic(work/'benchmark-owner.json',dict(owner=list(owner[:4])+[owner[4].hex(),owner[5]],members=[list(m[:4])+[m[4].hex(),m[5]] for m in known],wall=time.time()))
   code=child.poll()
   if code is not None:break
   if stopping or any(p.exists() for p in [work/'STOP',control/'STOP',campaign/'STOP']):
    child.terminate()
    try:child.wait(timeout=15)
    except subprocess.TimeoutExpired:pass
    break
   time.sleep(2)
 finally:
  _join_service_descendants(owner,known);child.wait(timeout=10)
 atomic(work/'benchmark-finished.json',dict(returncode=child.returncode,wall=time.time()))
 if child.returncode:raise RuntimeError('Benchmark stopped or failed; see saved live results')

if __name__=='__main__':
 p=argparse.ArgumentParser();p.add_argument('--work',type=Path,required=True);p.add_argument('--control',type=Path,required=True);a=p.parse_args();main(a.work,a.control)
