"""Run and monitor a frozen hero-routed final benchmark; never train or deploy."""
import argparse
import json
import os
from pathlib import Path
import signal
import subprocess
import sys
import time
from types import SimpleNamespace
from collections import Counter

from audit_routed_evaluation import audit_artifacts,merged_progress,HEROES
from final_handoff import owner_for,ROOT
from league import save_json,sha256_file


def run(protocol_path):
    protocol=json.loads(protocol_path.read_text());folder=protocol_path.parent
    if Counter(hero for component in protocol['candidate']['components'].values() for hero in component['heroes'])!=Counter(HEROES):
        raise ValueError('Every public hero must have exactly one frozen route')
    if time.time()>=protocol['deadline_wall']:raise ValueError('Final benchmark deadline has passed')
    output=Path(protocol['output']);output.mkdir(parents=True,exist_ok=False)
    save_json(output/'plan.json',dict(final_protocol_sha256=sha256_file(protocol_path),hero_routed=True,
        protocol=str(protocol_path),candidate=protocol['candidate'],games=protocol['games'],seed=protocol['seed']))
    owner=owner_for(SimpleNamespace(pid=os.getpid()),[part.decode() for part in Path('/proc/self/cmdline').read_bytes().rstrip(b'\0').split(b'\0')])
    save_json(output/'owner.json',owner)
    stopped=False;children={};started=time.monotonic()
    def stop(*_):
        nonlocal stopped
        stopped=True
    signal.signal(signal.SIGTERM,stop);signal.signal(signal.SIGINT,stop)
    def progress(state='running'):
        results={}
        for name,component in protocol['candidate']['components'].items():
            path=Path(component['output'])/'progress.json'
            if path.exists():results[name]=json.loads(path.read_text())
        data=merged_progress(protocol,results,time.monotonic()-started,state)
        save_json(output/'progress.json',data)
        return data
    try:
        for name,component in protocol['candidate']['components'].items():
            command=component['command']
            with (folder/f'final-{name}-launcher.log').open('ab') as log:
                process=subprocess.Popen(command,cwd=ROOT,stdin=subprocess.DEVNULL,stdout=log,
                    stderr=subprocess.STDOUT,start_new_session=True)
            children[name]=(process,owner_for(process,command))
            save_json(folder/f'final-{name}-owner.json',children[name][1])
        goal_path=Path(protocol.get('goal_state_path',str(folder/'goal-state.json')));goal=json.loads(goal_path.read_text())
        description=('One frozen policy, parallel600-game final benchmark' if protocol.get('single_policy_parallel') else 'Frozen hero-routed final benchmark')
        goal.update(phase=description+':600 fresh games, all five heroes including Rez',
            active_job=str(output),updated_wall=time.time())
        goal['tracked_jobs']=protocol.get('tracked_jobs',goal.get('tracked_jobs',[]))+[str(output)]
        goal['final_supervisor']=dict(owner=str(output/'owner.json'),stop_file=str(folder/'STOP-ROUTED-FINAL'))
        goal['next_work']=['Monitor the frozen600-game final benchmark; no learning, candidate changes or outcome-based routing.','Audit all600 games, every hero matchup, both seats and frozen component identities. Require360 actual wins and paired confidence lower bound above50%.']
        save_json(goal_path,goal)
        while True:
            for name,(process,child_owner) in children.items():
                directory=Path(protocol['candidate']['components'][name]['output'])
                if directory.exists() and not (directory/'owner.json').exists():save_json(directory/'owner.json',child_owner)
                if process.poll() not in (None,0):raise RuntimeError(f'Final component {name} exited with status{process.returncode}')
            progress()
            if all(process.poll()==0 for process,_ in children.values()):break
            if stopped or (folder/'STOP-ROUTED-FINAL').exists() or time.time()>=protocol['deadline_wall']:
                progress('stopped at requested stop or deadline');return
            time.sleep(3)
        report=audit_artifacts(protocol_path)
        result=progress('complete');result['target_achieved']=report['target_achieved']
        save_json(output/'result.json',result);save_json(output/'progress.json',result)
        save_json(folder/'final-audit.json',report)
        goal=json.loads(goal_path.read_text());goal['phase']=('Final benchmark passed the declared target' if report['target_achieved'] else 'Final benchmark complete;60% target not achieved')
        goal['updated_wall']=time.time();save_json(goal_path,goal)
    except BaseException as error:
        progress('failed: '+str(error));raise
    finally:
        for process,_ in children.values():
            if process.poll() is None:process.terminate()
        for process,_ in children.values():
            if process.poll() is None:
                try:process.wait(timeout=20)
                except subprocess.TimeoutExpired:process.kill();process.wait()


if __name__=='__main__':
    parser=argparse.ArgumentParser(description=__doc__);parser.add_argument('--protocol',type=Path,required=True)
    run(parser.parse_args().protocol.resolve())
