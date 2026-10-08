"""Import the one completed pre-watch comparison after strict checkpoint verification."""
import sys,json,fcntl,time
from pathlib import Path
sys.path.insert(0,str(Path(__file__).resolve().parents[1]))
import cpu_shadow_eval as shadow
from shadow_watch import atomic_json,process_alive
from progress_watch import classify
root=Path('/home/lva/.local/share/shards-training/2026-09-26');run=root/'main-v9';path=run/'progress-watch.json'
with (run/'progress-watch.lock').open('a') as lock:
 fcntl.flock(lock,fcntl.LOCK_EX|fcntl.LOCK_NB)
 state=json.loads(path.read_text());assert not process_alive(state['pid']) and state['active_job'] is None
 assert state['jobs_reserved']==0 and state['cycles']==0 and not state['comparisons']
 output=root/'evaluations/progress-v9-audit-vs-start-learner.json';report=json.loads(output.read_text())
 shadow.configure_cpu();shadow.configure_variant('v9')
 a,b=shadow.frozen_selections(Path(report['policy_a']['checkpoint']),'learner',Path(state['baseline']),'learner')
 for key,selection in [('policy_a',a),('policy_b',b)]:
  assert selection.metadata['checkpoint_payload_sha256']==report[key]['checkpoint_payload_sha256']
  assert selection.metadata['role']=='learner'
 assert report['games']==4096 and report['seed_start']==0x9200000000100000
 evidence=classify(report,1);assert evidence['verdict']=='improved'
 import campaign_state
 identity=json.loads(Path(a.metadata['checkpoint']).with_name('identity.json').read_text())
 loaded=campaign_state.load_checkpoint(Path(a.metadata['checkpoint']),expected_identity=identity)
 games=loaded['state']['games']
 job=dict(sequence=1,kind='previous',current=a.metadata['checkpoint'],opponent=state['baseline'],seed=report['seed_start'],output=str(output),state='completed',evidence=evidence,policy_a=a.metadata['version'],policy_b=b.metadata['version'],finished_wall=time.time(),import_scope='One pre-watch planned comparison, strict loaded payloads verified; charged to sequence1 alpha.025')
 state.update(jobs_reserved=1,comparisons=[job],cycles=1,best=a.metadata['checkpoint'],previous=a.metadata['checkpoint'],last_evaluation_games=games,last_result=job,next_evaluation_wall=time.time()+60,proof='Improvement demonstrated: generation8965 over8811; 55.04% in4096games',state='waiting',updated_wall=time.time())
 report['progress_evidence']=dict(evidence,comparison_kind='previous',sequence=1)
 atomic_json(output,report);atomic_json(path,state)
 print(json.dumps(dict(games=games,verdict=evidence['verdict'],bound=evidence['bound'],best=state['best'])))
