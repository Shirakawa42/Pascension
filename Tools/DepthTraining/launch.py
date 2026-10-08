"""Create a new bounded authorization, start hidden owners and hourly reviews."""
from pathlib import Path
import argparse,base64,hashlib,json,os,signal,subprocess,sys,time,uuid
HERE=Path(__file__).resolve().parent;PROJECT=HERE.parents[1];WATCH=HERE.parent/'TrainingWatchdog'
PYTHON='/home/lva/.venvs/shards-preflight/bin/python'
def sha(p):return hashlib.sha256(Path(p).read_bytes()).hexdigest()
def write(p,v):Path(p).write_text(json.dumps(v,indent=2)+'\n')
def windows(p):
    p=str(Path(p).resolve());assert p.startswith('/mnt/f/');return 'F:\\'+p[len('/mnt/f/'):].replace('/','\\')
def background(args,log):
    with Path(log).open('ab',buffering=0) as stream:return subprocess.Popen(args,stdin=subprocess.DEVNULL,stdout=stream,stderr=subprocess.STDOUT,start_new_session=True,cwd=PROJECT)
def main():
    p=argparse.ArgumentParser();p.add_argument('--seconds',type=int,default=28800);p.add_argument('--depth',type=int,default=24);p.add_argument('--batch',type=int,default=16);p.add_argument('--pilot',action='store_true');a=p.parse_args()
    if not 0<a.seconds<=28800:raise ValueError('At most eight authorized hours')
    cid=uuid.uuid4().hex;root=Path.home()/'.local/share/shards-depth'/time.strftime('%Y-%m-%d')/(('pilot-' if a.pilot else 'training-8h-')+cid[:8]);control=root/'control';run=root/'training';reviews=root/'reviews'
    for d in (control,run,reviews):d.mkdir(parents=True)
    start=time.time()
    pins={}
    for folder,pattern in ((HERE,'*.py'),(HERE/'Host','*.cs'),(HERE.parent/'ZeroDepthTraining','*.py'),(HERE.parent/'ZeroDepthTraining/Host','*.cs'),(PROJECT/'Assets/Scripts/Core','*.cs'),(PROJECT/'Assets/Scripts/Shards/Engine','*.cs'),(PROJECT/'Assets/Scripts/Shards/Content','*.cs'),(PROJECT/'Assets/Scripts/Shards/AI','*.cs')):
        for file in folder.rglob(pattern):
            if 'tests' not in file.parts and 'obj' not in file.parts:pins[str(file)]=sha(file)
    for file in (HERE/'Host/bin/Release/net8.0/DepthHost.dll',HERE.parent/'ZeroDepthTraining/Host/bin/Release/net8.0/ZeroDepthHost.dll'):
        pins[str(file)]=sha(file)
    config={'campaign_id':cid,'run':str(run),'start_wall':start,'hard_deadline_wall':start+a.seconds,'seconds':a.seconds,'start_monotonic':time.monotonic(),
        'boot_id':Path('/proc/sys/kernel/random/boot_id').read_text().strip(),'initial_policy':'/home/lva/.local/share/shards-zero-depth/2026-10-04/stall-intervention/after-policy',
        'final_eval_seconds':0 if a.pilot else 900,'incumbent_bundle':None if a.pilot else '/home/lva/.local/share/shards-zero-depth/2026-10-04/training-512-12h/incumbent',
        'batch':a.batch,'depth':a.depth,'width':4,'worlds':2,'workers':8,'epochs':2,'learning_rate':1e-5,'seed':94200000 if not a.pilot else 93600000,'pins':pins}
    write(control/'config.json',config)
    owner=background([PYTHON,str(HERE/'supervisor.py'),'--config',str(control/'config.json')],control/'supervisor.log')
    write(root/'launch.json',{'supervisor_pid':owner.pid,'config':str(control/'config.json'),'run':str(run)})
    if a.pilot:print(root,flush=True);return
    # Only replace the previous Shards monitor explicitly owned by this user.
    for proc in Path('/proc').iterdir():
        if not proc.name.isdigit():continue
        try:cmd=(proc/'cmdline').read_bytes().split(b'\0')
        except (FileNotFoundError,PermissionError,ProcessLookupError):continue
        if any(x.endswith(b'/ZeroDepthTraining/monitor.py') or x.endswith(b'/DepthTraining/monitor.py') for x in cmd) and b'8766' in cmd:
            os.kill(int(proc.name),signal.SIGTERM)
    time.sleep(.5)
    monitor=background([PYTHON,str(HERE/'monitor.py'),'--run',str(run),'--port','8766'],root/'monitor.log')
    write(root/'monitor.json',{'pid':monitor.pid,'url':'http://localhost:8766'})
    # Existing audited GUI bridge creates no foreground console or PowerShell.
    old=json.loads((HERE.parent/'TrainingPreflight/results/windows-supervisor-e2162b966ce849da8247e76dc62b63d7/manifest.json').read_text())
    folder=HERE/'results'/('windows-supervisor-'+cid);folder.mkdir(parents=True)
    (folder/'linux-config-snapshot.json').write_bytes((control/'config.json').read_bytes())
    manifest=dict(old)
    from datetime import datetime,timezone
    manifest.update(campaign_id=cid,config_linux_path=str(control/'config.json'),config_sha256=sha(control/'config.json'),config_snapshot_windows_path=windows(folder/'linux-config-snapshot.json'),
        hard_deadline_wall=config['hard_deadline_wall'],hard_deadline_utc=datetime.fromtimestamp(config['hard_deadline_wall'],timezone.utc).isoformat(),
        runner_linux_path=str(HERE/'supervisor.py'),runner_windows_path=windows(HERE/'supervisor.py'),runner_sha256=sha(HERE/'supervisor.py'),
        sentinel_linux_path=str(HERE/'supervisor.py'),sentinel_windows_path=windows(HERE/'supervisor.py'),sentinel_sha256=sha(HERE/'supervisor.py'),
        state_windows_path=windows(folder/'parent-state.json'),log_windows_path=windows(folder/'parent-events.jsonl'))
    write(folder/'manifest.json',manifest);digest=sha(folder/'manifest.json')
    write(folder/'parent-state.json',{'schema':'shards-windows-watchdog-state-v1','manifest_sha256':digest,'runner_launch_attempts':0,'transport_failures':0,'terminal_latched':False,'unsafe':False,'terminal_reason':None})
    arguments=['-Mode','Install','-ManifestPath',windows(folder/'manifest.json'),'-ManifestSha256',digest,'-ReportPath',windows(folder/'install-result.json'),'-Start']
    payload=base64.b64encode(json.dumps(arguments).encode()).decode()
    background(['/mnt/c/Windows/System32/wscript.exe','//B','//Nologo',windows(WATCH/'run_hidden.vbs'),windows(WATCH/'install_windows_startup.ps1'),payload,windows(folder/'install-bootstrap.json')],root/'native-launch.log')
    prompt=HERE/'hourly-instructions.txt'
    review_config={'directory':str(reviews),'watchdog_config':str(control/'config.json'),'start_wall':start,'deadline_wall':config['hard_deadline_wall'],
        'codex':'/mnt/c/Users/Shira/.vscode/extensions/openai.chatgpt-26.930.31730-win32-x64/bin/linux-x86_64/codex','project':str(PROJECT),'prompt':str(prompt),'prompt_sha256':sha(prompt)}
    write(reviews/'config.json',review_config)
    unit='shards-depth-review-'+cid
    service=Path.home()/'.config/systemd/user'/(unit+'.service')
    service.write_text('[Unit]\nDescription=Hourly Shards depth training reviews\n[Service]\nType=simple\nWorkingDirectory='+str(PROJECT)+'\nExecStart='+PYTHON+' '+str(WATCH/'hourly_review.py')+' --config '+str(reviews/'config.json')+'\nRestart=on-failure\nRestartSec=30\n[Install]\nWantedBy=default.target\n')
    subprocess.run(['systemctl','--user','daemon-reload'],check=True);subprocess.run(['systemctl','--user','enable','--now',unit+'.service'],check=True)
    write(root/'launch.json',{'supervisor_pid':owner.pid,'config':str(control/'config.json'),'run':str(run),'monitor_pid':monitor.pid,'windows_manifest':str(folder/'manifest.json'),'hourly_service':unit+'.service'})
    print(root,flush=True)
if __name__=='__main__':main()
