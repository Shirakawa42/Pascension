"""One immutable eight-hour authorization; checkpoint recovery, no fresh budgets."""
import argparse,fcntl,hashlib,json,os,signal,subprocess,sys,time
from pathlib import Path
HERE=Path(__file__).resolve().parent

def atomic(path,value):
    path=Path(path);tmp=path.with_suffix('.tmp')
    with tmp.open('w') as f:json.dump(value,f,indent=2);f.flush();os.fsync(f.fileno())
    os.replace(tmp,path)

def sha(p):return hashlib.sha256(Path(p).read_bytes()).hexdigest()
def read(p,default=None):
    try:return json.loads(Path(p).read_text())
    except FileNotFoundError:return default

def check(config):
    if config['seconds']<=0 or config['seconds']>8*3600:raise ValueError('Invalid eight-hour allocation')
    if abs(config['hard_deadline_wall']-config['start_wall']-config['seconds'])>.01:raise ValueError('Deadline changed')
    for p,digest in config['pins'].items():
        if sha(p)!=digest:raise RuntimeError('Pinned depth code changed: '+p)

def remaining(config):
    left=min(config['seconds'],config['hard_deadline_wall']-time.time())
    boot=Path('/proc/sys/kernel/random/boot_id').read_text().strip()
    if config.get('boot_id')==boot:
        left=min(left,config['start_monotonic']+config['seconds']-time.monotonic())
    return max(0,left)

def lock_file(path):
    f=Path(path).open('a+')
    try:fcntl.flock(f,fcntl.LOCK_EX|fcntl.LOCK_NB)
    except BlockingIOError:f.close();return None
    return f

def finish_child(p):
    if p.poll() is None:
        os.killpg(p.pid,signal.SIGTERM)
        try:p.wait(timeout=20)
        except subprocess.TimeoutExpired:os.killpg(p.pid,signal.SIGKILL);p.wait()

def main():
    p=argparse.ArgumentParser();p.add_argument('--config',type=Path,required=True);p.add_argument('--expected-sha');p.add_argument('--hard-deadline-wall',type=float);p.add_argument('--expected-runner-sha');a=p.parse_args()
    config=read(a.config);control=a.config.parent;digest=sha(a.config)
    check(config)
    if a.expected_sha:
        if digest!=a.expected_sha or config['hard_deadline_wall']!=a.hard_deadline_wall or sha(__file__)!=a.expected_runner_sha:raise RuntimeError('Native supervisor pin mismatch')
        while True:
            state=read(control/'state.json',{})
            terminal=(control/'STOP').exists() or state.get('status') in ('complete','stopped','failed') or time.time()>=config['hard_deadline_wall']
            probe=lock_file(control/'supervisor.lock')
            if probe:probe.close()
            if terminal or probe:
                print(json.dumps({'disposition':'terminal' if terminal else 'retry','reason':'authorization ended' if terminal else 'no Linux owner',
                    'config_sha256':digest,'hard_deadline_wall':config['hard_deadline_wall']}),flush=True)
                return 0 if terminal else 10
            time.sleep(5)
    lock=lock_file(control/'supervisor.lock')
    if lock is None:return 0
    state=read(control/'state.json',{'attempts':0})
    if state.get('status') in ('complete','stopped','failed') or (control/'STOP').exists():return 0
    deadline=time.monotonic()+remaining(config)
    stop=False
    def on_signal(*_):
        nonlocal stop
        stop=True
    signal.signal(signal.SIGTERM,on_signal);signal.signal(signal.SIGINT,on_signal)
    state.update(status='running',current_campaign=config['run'],supervisor_pid=os.getpid(),hard_deadline_wall=config['hard_deadline_wall'])
    atomic(control/'state.json',state)
    while state['attempts']<4 and not stop and not (control/'STOP').exists() and time.monotonic()<deadline and time.time()<config['hard_deadline_wall']:
        check(config)
        if sha(a.config)!=digest:raise RuntimeError('Immutable allocation config changed')
        state['attempts']+=1;atomic(control/'state.json',state)
        with (control/'trainer.log').open('ab',buffering=0) as log:
            child=subprocess.Popen([sys.executable,str(HERE/'depth_train.py'),'--config',str(a.config)],stdin=subprocess.DEVNULL,stdout=log,stderr=subprocess.STDOUT,start_new_session=True,cwd=HERE)
            state.update(trainer_pid=child.pid,started_wall=time.time());atomic(control/'state.json',state)
            last_progress=time.monotonic();stamp=None;reason='trainer_exit'
            try:
                while child.poll() is None:
                    status_path=Path(config['run'])/'status.json'
                    current=status_path.stat().st_mtime_ns if status_path.exists() else None
                    if current!=stamp:stamp=current;last_progress=time.monotonic()
                    if stop or (control/'STOP').exists():reason='stopped';break
                    if time.monotonic()>=deadline or time.time()>=config['hard_deadline_wall']:reason='complete';break
                    # An evaluation is bounded independently at 300 seconds.
                    phase=read(status_path,{}).get('state');limit=360 if phase in ('evaluating','evaluating_incumbent') else 180
                    if time.monotonic()-last_progress>limit:reason='stale_heartbeat';break
                    time.sleep(2)
            finally:finish_child(child)
            state.update(last_exit=child.returncode,last_reason=reason);atomic(control/'state.json',state)
            if reason in ('stopped','complete'):break
            if child.returncode==0:break
            time.sleep(5)
    clean=child.returncode==0 and read(Path(config['run'])/'status.json',{}).get('state')=='complete' if 'child' in locals() else False
    terminal='stopped' if stop or (control/'STOP').exists() else 'complete' if clean or time.monotonic()>=deadline or time.time()>=config['hard_deadline_wall'] else 'failed'
    state.update(status=terminal,finished_wall=time.time());atomic(control/'state.json',state)
    return 0
if __name__=='__main__':sys.exit(main())
