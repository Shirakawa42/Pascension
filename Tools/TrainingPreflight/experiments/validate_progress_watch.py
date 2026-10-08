"""Two real frozen CPU games through the checkpoint/evidence watch; no training."""
import json,os,sys,tempfile,subprocess,time
from pathlib import Path
from shadow_watch import snapshot_checkpoint,atomic_json
HERE=Path(__file__).resolve().parent
root=Path('/home/lva/.local/share/shards-training/2026-09-26')
with tempfile.TemporaryDirectory(prefix='shards-progress-contract-') as folder:
 folder=Path(folder);run=folder/'run';snapshot_checkpoint(root/'main-v9',run)
 atomic_json(run/'status.json',dict(state='running',trainer_pid=os.getpid(),games=3000000,generation=0))
 output=folder/'evaluations'
 command=[sys.executable,str(HERE/'progress_watch.py'),'--run-dir',str(run),'--baseline',str(root/'pilot-v9-public-deck/latest.soicp'),'--output-dir',str(output),'--games','2','--batch','2','--timeout','60','--once','--seed','0x9400000000000000']
 result=subprocess.run(command,stdout=subprocess.PIPE,stderr=subprocess.STDOUT,text=True,timeout=90)
 if result.returncode:raise RuntimeError(result.stdout+(run/'progress-watch.json').read_text())
 state=json.loads((run/'progress-watch.json').read_text());assert state['cycles']==1 and len(state['comparisons'])==1
 job=state['comparisons'][0];assert job['evidence']['verdict']=='inconclusive';assert state['best']==state['baseline']
 report=json.loads(Path(job['output']).read_text());assert report['games']==2 and report['policy_a']['role']==report['policy_b']['role']=='learner'
 assert len(state['snapshots'])==1 and Path(state['snapshots'][0]['path']).exists()
 saved=dict(passed=True,games=2,retention=True,learner_roles=True,no_unproven_promotion=True,progress_evidence=report['progress_evidence'],cuda_initialized=report['cuda_initialized'],budget_access='none',policy_versions=[report['policy_a']['version'],report['policy_b']['version']])
 (HERE.parent/'results/progress-watch-integration-2026-09-27.json').write_text(json.dumps(saved,indent=2));print(json.dumps(saved))
