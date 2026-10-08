"""Exactly N completed games, one frozen policy, balanced randomized heroes. No training."""
import importlib
import argparse,hashlib,json,sys,time,os
from pathlib import Path
import numpy as np
import torch
p=argparse.ArgumentParser();p.add_argument('--runtime',type=Path,required=True);p.add_argument('--checkpoint',type=Path,required=True);p.add_argument('--host',type=Path,required=True);p.add_argument('--output',type=Path,required=True);p.add_argument('--campaign',type=Path);p.add_argument('--games',type=int,default=50000);p.add_argument('--batch',type=int,default=250);p.add_argument('--seed',type=lambda v:int(v,0),default=0x7500000000000000);p.add_argument('--runtime-module',default='variant_v12_runtime');p.add_argument('--catalog',type=Path);a=p.parse_args()
assert a.games%a.batch==0 and a.games%20==0
if a.output.exists():raise RuntimeError('Refusing to mix with an existing evaluation directory')
a.output.mkdir(parents=True)
source=Path(__file__).resolve().parent
sys.path[:0]=[str(a.runtime/'Tools/TrainingPreflight/experiments'),str(a.runtime/'Tools/TrainingPreflight')]
runtime_module=importlib.import_module(a.runtime_module)
install,variant_identity=runtime_module.install,runtime_module.variant_identity
install()
import evaluate_checkpoints as e,learning_model,pipeline_bench
from evaluate_checkpoints import policy_hash
from wire_v10 import LearningHost
from balance_host_statistics import combine,delta
from bench_common import save_json
from variant_runtime import _original_subprocess

e.identity=variant_identity;e.LearningPolicy=learning_model.LearningPolicy
selection=e.load_selection(a.checkpoint,'learner')
torch.set_num_threads(1);torch.set_num_interop_threads(1);torch.manual_seed(9275037)
policy=e.materialize_policy(selection,'cuda');before=policy_hash(policy)
actor=learning_model.LearningActor(policy,a.batch,graph=True,device='cuda')
ledger=a.campaign/'budget.json' if a.campaign else None
ledger_hash=hashlib.sha256(ledger.read_bytes()).hexdigest() if ledger else None
host_hash=hashlib.sha256(a.host.read_bytes()).hexdigest()
class Factory:
 def __getattr__(self,name):return getattr(_original_subprocess,name)
 def Popen(self,args,*pos,**kw):
  env=dict(kw.get('env') or os.environ)
  env.update(SHARDS_STATS_DIRECTORY=str(a.output/'raw'),SHARDS_STATS_PURPOSE='final_evaluation',SHARDS_STATS_EXPECTED_SEED=str(a.seed),SHARDS_STATS_EXPECTED_BATCH=str(a.batch),SHARDS_FUSED_SERVE='1',SHARDS_HERO_FIX_SEATS='3')
  kw['env']=env
  return _original_subprocess.Popen([args[0],str(a.host),'serve','--hero-setup','curriculum-75-25'],*pos,**kw)
pipeline_bench.subprocess=Factory()
report=dict(schema='shards-final-frozen-evaluation-v1',state='running',evaluation_only=True,optimizer_updates=0,games_requested=a.games,completed_games=0,checkpoint=str(a.checkpoint),policy_sha256=before,host_sha256=host_hash,seed=a.seed,sampling_seed=9275037,hero_setup='Each block of 20 engine seeds has a separately shuffled permutation of all 20 ordered distinct-hero pairs',selection=selection.metadata,started_wall=time.time())
save_json(a.output/'manifest.json',report)
def fail(exc_type, value, traceback):
 report.update(state='failed',error=str(value),failed_wall=time.time())
 save_json(a.output/'manifest.json',report)
 sys.__excepthook__(exc_type,value,traceback)
sys.excepthook=fail
metadata=json.loads((a.catalog or source/'results/balance-card-catalog-v10.json').read_text())
last_publication=None

def publish():
 global last_publication
 files=list((a.output/'raw/forced-random').glob('session-*.json'))
 if any(f.name.endswith('.error.json') for f in files):raise RuntimeError('Statistics writer failed')
 files=[f for f in files if not f.name.endswith('.error.json')]
 if not files:return
 if len(files)!=1:raise RuntimeError('Unexpected mixed evaluation sessions')
 raw=json.loads(files[0].read_text())
 assert raw['purpose']=='final_evaluation' and raw['host_binary_sha256']==host_hash
 if raw['publication_sequence']==last_publication:return
 if raw['totals']['censored_games']:raise RuntimeError('Censored games invalidate final evaluation')
 window=delta(raw);window['run_id']='final-50000';snapshot=combine([window],metadata,run_id='final-50000',target_games=a.games)
 snapshot['scope'].update(label='Final frozen evaluation · random heroes',policy_label='Frozen AI generation '+str(selection.metadata['version']),policy_versions=[selection.metadata['version']],opponent_label='The identical frozen AI in the opposite seat',inference='Identical frozen policies; randomized balanced hero assignments; all DLC; 1v1',confidence_method='Descriptive 95% game-cluster bounds; card acquisition associations are not causal card strength',notes=['Only this evaluation is included. No training, checkpoint-selection or smoke-test games.','Randomized hero assignments are balanced over all 20 ordered distinct-hero pairs.','Acquisitions include accepted purchases and public effect events. Legal-menu exposure is not collected.','Intervals are pointwise; rankings are observational, not causal.'])
 snapshot['card_catalog']=metadata
 snapshot['scope']['balance_patch']=selection.metadata['run_identity'].get('balance_patch')
 rez_repair=selection.metadata['run_identity'].get('rez_repair')
 if rez_repair:
  snapshot['scope']['rez_repair']=rez_repair
  snapshot['scope']['notes'].append('The frozen policy received Rez-focused self-play and supervised tactical examples. This cohort measures that policy; it does not establish optimal hero balance.')
 snapshot['scope']['final_evaluation']=True;snapshot['scope']['policy_sha256']=before
 snapshot['refresh'].update(evaluation_sample_count=raw['totals']['completed_games'],snapshot_training_games=0,total_games=raw['totals']['completed_games'])
 snapshot['insights'].insert(0,'One frozen AI on both sides; every ordered hero pair receives '+str(a.games//20)+' games at completion.')
 snapshot['evaluation_progress']=dict(completed=raw['totals']['completed_games'],target=a.games,state=report['state'])
 save_json(a.output/'statistics.json',snapshot)
 if a.campaign:save_json(a.campaign/'balance-statistics-final.json',snapshot)
 last_publication=raw['publication_sequence']

if a.campaign:
 save_json(a.campaign/'final-statistics-mode.json',dict(enabled=True,evaluation_directory=str(a.output),target_games=a.games))
 # Empty final-only snapshot prevents a fallback to historic training rows.
 save_json(a.campaign/'balance-statistics-final.json',dict(schema='shards-balance-statistics-v1',state='awaiting_samples',snapshot_id='final-start',scope=dict(label='Final frozen evaluation · awaiting first 1,000 games',final_evaluation=True),totals=dict(attempted_games=0,resolved_games=0,censored_games=0),rankings={}))
counts=np.zeros(a.batch,dtype=np.int64);quota=a.games//a.batch;started=time.monotonic();last=started
host=LearningHost(a.batch,8,seed=a.seed,pinned=True,transport='shared',split_branches=8)
try:
 while np.any(counts<quota):
  actions,_=actor.act(host);active=counts<quota
  host.advance_active(actions,active)
  if np.any(host.done==2):raise RuntimeError('Engine censored a game; evaluation halted')
  counts+=(host.done==1)&active
  now=time.monotonic()
  if now-last>=10:
   report.update(completed_games=int(counts.sum()),seconds=now-started,games_per_second=float(counts.sum())/(now-started))
   save_json(a.output/'manifest.json',report);publish();print(json.dumps({k:report[k] for k in ['completed_games','seconds','games_per_second']}),flush=True);last=now
finally:
 host.close();(a.output/'host.log').write_text(host.diagnostics)
if policy_hash(policy)!=before or policy_hash(actor.policy)!=before:raise RuntimeError('Frozen weights changed')
if ledger and hashlib.sha256(ledger.read_bytes()).hexdigest()!=ledger_hash:raise RuntimeError('Training ledger changed')
report.update(state='completed',completed_games=int(counts.sum()),seconds=time.monotonic()-started,frozen_weights_unchanged=True,training_ledger_unchanged=True)
publish()
raw=json.loads(next((a.output/'raw/forced-random').glob('session-*.json')).read_text())
assert raw['final'] and raw['totals']['completed_games']==a.games and raw['totals']['unfinished_discarded_games']==0
assert len(raw['matchup_rows'])==20 and all(r['games']==a.games//20 for r in raw['matchup_rows'])
assert len(raw['hero_seat_rows'])==10 and all(r['games']==a.games//5 for r in raw['hero_seat_rows'])
assert sum(counts)==a.games
report['verified_balanced_hero_pairs']=True;save_json(a.output/'manifest.json',report)
print(json.dumps(dict(state='completed',games=int(counts.sum()),seconds=report['seconds'],policy_sha256=before)),flush=True)
