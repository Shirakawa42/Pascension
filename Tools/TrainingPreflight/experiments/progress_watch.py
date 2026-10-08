"""Retain checkpoints and measure progress against previous and retained-best learners.

CPU-only independent evaluations; no optimizer, budget writes, or trainer control.
Promotion updates only this evaluator's best reference, never training state.
"""
import argparse
import fcntl
import hashlib
import json
import math
import os
from pathlib import Path
import signal
import subprocess
import sys
import time

from shadow_watch import atomic_json, read_json, snapshot_checkpoint, terminate_group, process_alive

HERE = Path(__file__).resolve().parent
SCHEMA = 'shards-progress-watch-v1'


def test_alpha(sequence):
    if type(sequence) is not int or sequence < 1:
        raise ValueError('Comparison sequence starts at one')
    return .05 / (sequence * (sequence + 1))


def classify(report, sequence):
    """Simultaneous error control over all adaptively selected future comparisons.

    Each comparison uses fresh reserved seed pairs and a fixed sample count.
    Paired outcomes lie in [0,1]. Hoeffding plus summable alpha spending avoids
    calling a lucky result progress after repeated looks at many checkpoints.
    """
    score, games, pairs = (report.get(k) for k in ('score_a', 'games', 'paired_seed_count'))
    valid = (report.get('complete') is True and report.get('all_terminal') is True
             and report.get('censored_games') == 0 and report.get('seat_swapped') is True
             and report.get('frozen_weights_unchanged') is True
             and report.get('cuda_initialized') is False
             and type(games) is int and games > 0 and games % 2 == 0
             and type(pairs) is int and pairs == games // 2
             and isinstance(score, (int, float)) and math.isfinite(score) and 0 <= score <= 1)
    if not valid:
        return {'verdict': 'invalid', 'reason': 'Incomplete, censored, unfrozen, or malformed comparison'}
    alpha = test_alpha(sequence)
    margin = math.sqrt(math.log(2 / alpha) / (2 * pairs))
    low, high = max(0., score-margin), min(1., score+margin)
    return dict(verdict='improved' if low > .5 else 'regressed' if high < .5 else 'inconclusive',
                score=score, bound=[low, high], margin=margin, alpha=alpha,
                scope='Summable 5% error budget over fresh paired comparisons in this watch; not global game strength')


def command(args, current, opponent, output, seed, sampling_seed):
    return [sys.executable, str(HERE/'cpu_shadow_eval.py'), '--variant', args.variant,
            '--a', str(current), '--a-role', 'learner', '--b', str(opponent), '--b-role', 'learner',
            '--games', str(args.games), '--batch', str(args.batch), '--seed', str(seed),
            '--sampling-seed', str(sampling_seed), '--max-seconds', str(args.timeout-15),
            '--no-card-telemetry', '--output', str(output)]


def file_hash(path):
    h=hashlib.sha256()
    with Path(path).open('rb') as f:
        for block in iter(lambda:f.read(1<<20), b''): h.update(block)
    return h.hexdigest()


def run(args):
    os.nice(10)
    run_dir=args.run_dir.resolve(strict=True)
    output_dir=args.output_dir.resolve();output_dir.mkdir(parents=True, exist_ok=True)
    state_path=run_dir/'progress-watch.json'
    configuration={k:str(v) if isinstance(v,Path) else v for k,v in vars(args).items()}
    stopped=False
    def stop(signum, frame):
        nonlocal stopped
        stopped=True
    signal.signal(signal.SIGTERM,stop);signal.signal(signal.SIGINT,stop)
    with (run_dir/'progress-watch.lock').open('a') as lock:
        fcntl.flock(lock,fcntl.LOCK_EX|fcntl.LOCK_NB)
        state=read_json(state_path)
        if state and state['configuration'] != configuration:
            raise ValueError('Existing watch configuration differs; do not reset evidence or seed history')
        if state and state.get('active_job'):
            raise RuntimeError('Interrupted evaluation retained; inspect before restarting')
        if not state:
            baseline=snapshot_checkpoint(args.baseline.parent,run_dir/'progress-checkpoints/baseline',source_path=args.baseline)
            state=dict(schema=SCHEMA,configuration=configuration,state='waiting',created_wall=time.time(),
                       baseline=str(baseline),best=str(baseline),previous=str(baseline),
                       snapshots=[],comparisons=[],jobs_reserved=0,next_seed=args.seed,
                       cycles=0,next_evaluation_wall=time.time()+args.initial_delay,
                       last_evaluation_games=None,last_snapshot_wall=0,active_job=None,
                       proof='No improvement demonstrated yet',training_control=False)
        state.update(pid=os.getpid(),state='waiting')
        def save():
            state['updated_wall']=time.time();atomic_json(state_path,state)
        save()

        def retain():
            now=time.time()
            if now-state['last_snapshot_wall'] < args.checkpoint_seconds:
                return
            status=read_json(run_dir/'status.json') or {}
            if not (run_dir/'latest.soicp').exists():return
            destination=run_dir/'progress-checkpoints'/('retained-%05d'%len(state['snapshots']))
            path=snapshot_checkpoint(run_dir,destination)
            state['snapshots'].append(dict(path=str(path),sha256=file_hash(path),wall=now,
                observed_generation=status.get('generation'),observed_games=status.get('games'),
                metadata_scope='Status observed near copy; exact policy version comes from validated evaluation'))
            state['last_snapshot_wall']=now;save()

        def compare(current, opponent, kind):
            sequence=state['jobs_reserved']+1
            seed=state['next_seed'];state['next_seed']+=args.games//2
            if state['next_seed']>=2**64:raise ValueError('Seed namespace exhausted')
            output=output_dir/f'progress-{run_dir.name}-{sequence:05d}-{kind}.json'
            job=dict(sequence=sequence,kind=kind,current=str(current),opponent=str(opponent),
                     seed=seed,output=str(output),state='running',started_wall=time.time())
            state.update(jobs_reserved=sequence,active_job=job,state='evaluating');save()
            env=dict(os.environ,CUDA_VISIBLE_DEVICES='',OMP_NUM_THREADS='1',MKL_NUM_THREADS='1',OPENBLAS_NUM_THREADS='1')
            child=None
            try:
                with output.with_suffix('.log').open('wb') as log:
                    child=subprocess.Popen(command(args,current,opponent,output,seed,args.sampling_seed+sequence),
                                           env=env,stdout=log,stderr=subprocess.STDOUT,start_new_session=True)
                    while child.poll() is None:
                        retain()
                        if stopped or time.time()-job['started_wall']>args.timeout:
                            terminate_group(child)
                            raise RuntimeError('Progress evaluation interrupted or timed out')
                        time.sleep(2)
                if child.returncode:raise RuntimeError('Evaluator exited '+str(child.returncode))
            finally:
                if child is not None:terminate_group(child)
            report=read_json(output) or {}
            if (report.get('games') != args.games or report.get('seed_start') != seed
                or report.get('policy_a',{}).get('role') != 'learner'
                or report.get('policy_b',{}).get('role') != 'learner'):
                raise RuntimeError('Comparison provenance differs from reserved learner-versus-learner job')
            evidence=classify(report,sequence)
            job.update(state='completed',evidence=evidence,finished_wall=time.time(),
                       policy_a=report.get('policy_a',{}).get('version'),
                       policy_b=report.get('policy_b',{}).get('version'))
            report['progress_evidence']=dict(evidence,comparison_kind=kind,sequence=sequence)
            atomic_json(output,report)
            state['comparisons'].append(job);state['active_job']=None;save()
            if evidence['verdict']=='invalid':raise RuntimeError('Invalid progress result')
            return job

        try:
            while not stopped:
                retain()
                status=read_json(run_dir/'status.json') or {}
                terminal=status.get('state') in ('failed','session_complete','completed','stopped')
                if status.get('state')=='running' and not process_alive(status.get('trainer_pid')):
                    state.update(state='failed',error='Trainer is absent; retained checkpoints remain available');save();return 1
                games=status.get('games')
                due=(isinstance(games,int) and time.time()>=state['next_evaluation_wall'] and
                     (state['last_evaluation_games'] is None or games-state['last_evaluation_games']>=args.every_games))
                if terminal and not due:
                    state.update(state='stopped',reason='trainer_'+status['state']);save();return 0
                if not due:
                    time.sleep(2);continue
                current=Path(state['snapshots'][-1]['path'])
                # Make the evaluated checkpoint current even if retention cadence is not due.
                current=snapshot_checkpoint(run_dir,run_dir/'progress-checkpoints'/('evaluated-%05d'%state['cycles']))
                previous=Path(state['previous']);best=Path(state['best'])
                first=compare(current,previous,'previous')
                best_result=first if previous==best else compare(current,best,'best')
                if best_result['evidence']['verdict']=='improved':
                    state['best']=str(current)
                    state['proof']='Improvement demonstrated against retained best at generation '+str(best_result['policy_b'])
                elif best_result['evidence']['verdict']=='regressed':
                    state['proof']='Regression demonstrated against retained best; older best preserved'
                else:
                    state['proof']='No demonstrated improvement in latest comparison; result inconclusive'
                state.update(previous=str(current),last_evaluation_games=games,cycles=state['cycles']+1,
                             next_evaluation_wall=time.time()+60,state='waiting',last_result=best_result)
                save()
                if args.once:return 0
            state.update(state='stopped',reason='signal');save();return 0
        except BaseException as error:
            state.update(state='stopped' if stopped else 'failed',error=str(error),automatic_retry=False);save()
            return 1


def main():
    p=argparse.ArgumentParser(description=__doc__)
    p.add_argument('--run-dir',type=Path,required=True);p.add_argument('--baseline',type=Path,required=True)
    p.add_argument('--output-dir',type=Path,required=True);p.add_argument('--variant',choices=['v9'],default='v9')
    p.add_argument('--games',type=int,default=4096);p.add_argument('--batch',type=int,default=64)
    p.add_argument('--timeout',type=float,default=900);p.add_argument('--checkpoint-seconds',type=float,default=300)
    p.add_argument('--every-games',type=int,default=100000);p.add_argument('--initial-delay',type=float,default=0)
    p.add_argument('--seed',type=lambda v:int(v,0),default=0x9300000000000000)
    p.add_argument('--sampling-seed',type=int,default=930927);p.add_argument('--once',action='store_true')
    a=p.parse_args()
    if not(2<=a.games<=16384 and a.games%2==0 and 1<=a.batch<=128 and 30<=a.timeout<=3600
           and 60<=a.checkpoint_seconds<=3600 and 1000<=a.every_games<=1000000 and 0<=a.initial_delay<=3600
           and 0<=a.seed<2**64-10000000 and 0<=a.sampling_seed<2**63-100000):p.error('Invalid bounded configuration')
    return run(a)


if __name__=='__main__':raise SystemExit(main())
