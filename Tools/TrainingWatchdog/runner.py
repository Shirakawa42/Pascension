"""Restart one verified training campaign within one durable wall-time cap.

This stdlib controller never creates CUDA state. Its separate CPU helper checks
checkpoint finite state and authoritative budget; reboot continuation uses the
reviewed recover.py to create a NEW allocation without refunding the old one.
"""
from __future__ import annotations
import argparse
from contextlib import nullcontext
from dataclasses import asdict, dataclass
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
import uuid

ROOT = Path(__file__).resolve().parents[2]
SCHEMA = 'shards-training-watchdog-config-v1'
STATE_SCHEMA = 'shards-training-watchdog-state-v1'
TERMINAL = {'completed', 'stopped', 'deadline', 'blocked', 'retry_exhausted'}


def read(path):
    path = Path(path)
    if path.is_symlink():
        raise ValueError('Metadata symlinks are not allowed')
    value = json.loads(path.read_text())
    if not isinstance(value, dict):
        raise ValueError('Expected JSON object')
    return value


def optional(path):
    try:
        return read(path)
    except FileNotFoundError:
        return {}


def atomic(path, value):
    path = Path(path)
    temporary = path.with_name(path.name + '.tmp-' + uuid.uuid4().hex)
    try:
        with temporary.open('x') as stream:
            json.dump(value, stream, indent=2, allow_nan=False)
            stream.write('\n');stream.flush();os.fsync(stream.fileno())
        os.replace(temporary, path)
        descriptor = os.open(path.parent, os.O_DIRECTORY)
        try:
            os.fsync(descriptor)
        finally:
            os.close(descriptor)
    finally:
        temporary.unlink(missing_ok=True)


def boot():
    return Path('/proc/sys/kernel/random/boot_id').read_text().strip()


@dataclass(frozen=True)
class Process:
    pid: int
    start_ticks: int
    pgid: int
    session: int
    command: tuple[str, ...]
    boot_id: str


def inspect(pid):
    if type(pid) is not int or pid < 1:
        return None
    try:
        folder = Path('/proc') / str(pid)
        fields = (folder / 'stat').read_text().rsplit(')', 1)[1].split()
        if fields[0] in ('Z','X'):
            return None
        command = tuple(os.fsdecode(part) for part in (folder / 'cmdline').read_bytes().split(b'\0') if part)
        # The task can exit between reading stat and cmdline. Linux then returns
        # empty argv before the final zombie stat becomes visible. That is a
        # vanished process, not a new owner with a different command.
        if not command:return None
        after=(folder/'stat').read_text().rsplit(')',1)[1].split()
        if after[0] in ('Z','X') or any(fields[index]!=after[index] for index in (2,3,19)):
            return None
        return Process(pid, int(after[19]), int(after[2]), int(after[3]), command, boot())
    except (FileNotFoundError, ProcessLookupError):
        return None


def decode(value):
    if not value:
        return None
    return Process(**(value | {'command': tuple(value['command'])}))


def signal_owned(owner, number, *, group=False):
    """Revalidate boot/start/argv/session before every signal, including kill."""
    current = inspect(owner.pid)
    if current is None:
        return False
    if current != owner or owner.pid == os.getpid() or owner.pgid == os.getpgrp():
        raise RuntimeError('Process ownership changed; refusing signal')
    if group and (owner.pgid != owner.pid or owner.session != owner.pid):
        raise RuntimeError('Refusing a non-owned process group')
    targets = [owner]
    if group:
        targets += [member for member in session_members(owner) if member != owner]
    descriptors = []
    try:
        for target in targets:
            try:
                descriptor = os.pidfd_open(target.pid)
            except ProcessLookupError:
                continue
            if inspect(target.pid) != target:
                os.close(descriptor)
                raise RuntimeError('Process changed while binding pidfd; refusing signal')
            descriptors.append(descriptor)
        for descriptor in descriptors:
            try:signal.pidfd_send_signal(descriptor, number)
            except ProcessLookupError:pass
        return bool(descriptors)
    finally:
        for descriptor in descriptors:os.close(descriptor)


def session_members(owner):
    """A fresh private session contains only this trainer's descendants."""
    result = []
    for folder in Path('/proc').iterdir():
        if not folder.name.isdecimal():continue
        member = inspect(int(folder.name))
        if (member and member.boot_id == owner.boot_id and member.session == owner.session
                and member.pgid == owner.pgid and member.start_ticks >= owner.start_ticks):
            result.append(member)
    return result


def bind_spawn(process, command, timeout=2.):
    """Popen may return before exec; wait briefly for the exact argv to bind."""
    end=time.monotonic()+timeout
    while time.monotonic()<end:
        current=inspect(process.pid)
        if current and current.command==tuple(command) and current.pgid==process.pid and current.session==process.pid:
            return current
        if process.poll() is not None:break
        time.sleep(.005)
    raise RuntimeError('Spawned process failed to bind exact executable argv/session')


class Lock:
    def __init__(self, path):
        self.path = Path(path);self.fd = None
    def __enter__(self):
        self.path.parent.mkdir(parents=True, exist_ok=True)
        self.fd = os.open(self.path, os.O_CREAT | os.O_RDWR, 0o600)
        os.set_inheritable(self.fd, False)
        try:
            fcntl.flock(self.fd, fcntl.LOCK_EX | fcntl.LOCK_NB)
        except BaseException:
            os.close(self.fd);self.fd = None
            raise RuntimeError('Another watchdog owns this authorization')
        return self
    def __exit__(self, *_):
        os.close(self.fd);self.fd = None


def validate_config(config):
    expected = {'schema', 'initial_campaign', 'python', 'seconds', 'hard_deadline_wall', 'port', 'bind',
                'max_restarts', 'poll_seconds', 'grace_seconds', 'recovery_root', 'project'}
    if set(config) != expected or config['schema'] != SCHEMA:
        raise ValueError('Incomplete/unexpected watchdog configuration')
    for key in ('seconds', 'hard_deadline_wall', 'poll_seconds', 'grace_seconds'):
        value = config[key]
        if isinstance(value, bool) or not isinstance(value, (int, float)) or not math.isfinite(value) or value <= 0:
            raise ValueError('Invalid numeric watchdog field: ' + key)
    if not 1 <= config['seconds'] <= 43200 or not .01 <= config['poll_seconds'] <= 60 or not .01 <= config['grace_seconds'] <= 120:
        raise ValueError('Invalid duration/poll/grace bound')
    if type(config['max_restarts']) is not int or not 0 <= config['max_restarts'] <= 8:
        raise ValueError('Retries must be bounded at0..8')
    if type(config['port']) is not int or not 0 <= config['port'] <= 65535 or config['bind'] not in ('127.0.0.1', '0.0.0.0'):
        raise ValueError('Invalid monitor address')
    for key in ('initial_campaign', 'python', 'recovery_root', 'project'):
        if not isinstance(config[key], str) or not Path(config[key]).is_absolute():
            raise ValueError('Watchdog paths must be absolute')
    return config


def checkpoint_check(campaign, project, *, read_only=False):
    """CPU subprocess entry: strict finite checkpoint and ledger, no CUDA."""
    campaign, project = Path(campaign), Path(project)
    sys.path.insert(0, str(project / 'Tools/ZeroDepthTraining'))
    sys.path.insert(0, str(project / 'Tools/TrainingPreflight'))
    from campaign_state import CampaignBudget, load_checkpoint
    from performance_upgrade import source_inventory
    from evaluate import verify_bundle
    import torch
    torch.set_num_threads(1)
    pinned, config, ledger = [read(campaign / name) for name in ('identity.json', 'config.json', 'budget.json')]
    if pinned.get('configuration') != config:
        raise ValueError('Campaign configuration differs from identity')
    if source_inventory(project)['source_fingerprint'] != pinned['source_fingerprint']:
        raise ValueError('Pinned training source changed')
    binary = project / 'Tools/ZeroDepthTraining/Host/bin/Release/net8.0/ZeroDepthHost.dll'
    if hashlib.sha256(binary.read_bytes()).hexdigest() != pinned['host_sha256']:
        raise ValueError('Pinned Host binary changed')
    verify_bundle(campaign/'incumbent',repo_root=project)
    active = ledger.get('active')
    if active and active['boot_id'] != boot():
        raise ValueError('boot_recovery_required')
    if active and inspect(active['pid']) is not None and not read_only:
        raise ValueError('Campaign still has a live trainer; refusing recovery')
    context=CampaignBudget(campaign / 'budget.json', limit_seconds=ledger['limit_seconds'])
    if read_only:
        # Validate a copied authoritative ledger without acquiring/refunding its
        # active trainer's lock. Snapshot metadata remains conservative.
        context._data=ledger;context._validate()
        class LedgerView:
            campaign_id=ledger['campaign_id'];limit_seconds=ledger['limit_seconds']
            charged_seconds=ledger['charged_seconds']
            remaining_seconds=max(0.,limit_seconds-charged_seconds)
            def snapshot(self):return dict(campaign_id=self.campaign_id,limit_seconds=self.limit_seconds,charged_seconds=self.charged_seconds)
            def collection_summary(self):return {'recent_censored':sum(item['censored_games'] for item in ledger['collection_accounting']['recent_batches'])}
        context=nullcontext(LedgerView())
    with context as budget:
        payload = load_checkpoint(campaign / 'latest.soicp', expected_identity=pinned, budget=budget)
        state = payload['state']
        if state['configuration'] != config or state['catalog'] != read(campaign / 'catalog.json'):
            raise ValueError('Checkpoint configuration or public catalog changed')
        if budget.collection_summary()['recent_censored'] >= 4:
            raise ValueError('Unsafe recent censor threshold')
        summary = dict(campaign_id=budget.campaign_id, limit_seconds=budget.limit_seconds,
                       remaining_seconds=budget.remaining_seconds, checkpoint_charge=payload['budget']['charged_seconds'],
                       authoritative_charge=budget.charged_seconds, games=state['games'],
                       next_engine_seed=state['next_engine_seed'], finite=True, cpu_only=True)
    if torch.cuda.is_initialized():
        raise RuntimeError('Checkpoint helper unexpectedly initialized CUDA')
    return summary


def unsafe_recent_censors(ledger):
    """Use the trainer's validated trailing window without opening its budget.

    A lifetime censor outside this window was already excluded from learning.
    Missing/corrupt nonzero history cannot prove health. Minimal zero-history
    fixtures remain compatible; real ledgers use CampaignBudget's full checks.
    """
    accounting=ledger.get('collection_accounting')
    if not isinstance(accounting,dict):return True
    total=accounting.get('censored_games')
    if type(total) is not int or total<0:return True
    recent=accounting.get('recent_batches')
    if total==0 and set(accounting)<={'censored_games','recent_batches'}:
        return 'recent_batches' in accounting and recent!=[]
    if not isinstance(recent,list) or total and not recent:return True
    try:
        sys.path.insert(0,str(ROOT/'Tools/TrainingPreflight'))
        from campaign_state import CampaignBudget
        if any(not isinstance(row,dict) or type(row.get('collection')) is not int for row in recent):return True
        view=CampaignBudget(Path('/dev/null'),limit_seconds=ledger['limit_seconds'])
        view._data=ledger
        view._validate()  # Read-only: no __enter__, flock, recovery or publication.
        return sum(row['censored_games'] for row in recent)>=4
    except Exception:
        return True


def failure_reason(code, supervision, log, *, censored_increased=False):
    """Unknown Python/model errors are terminal; only process loss is retried."""
    if supervision.get('stop_reason') in ('hard_deadline', 'deadline_kill'):
        return 'deadline'
    if supervision.get('stop_reason') == 'requested_stop':
        return 'stopped'
    if code == 0 or code is None and supervision.get('returncode') == 0 and supervision.get('state') == 'complete':
        return 'completed'
    lowered = log.lower()
    unsafe = ('traceback (most recent call last)', 'nonfinite', 'nan', 'censored games',
              'behavior mismatch', 'checksum', 'identity', 'illegal memory access', 'assertionerror',
              'out of memory', 'cuderror', 'cuda error', 'runtimeerror', 'persistence', 'exception')
    if censored_increased or any(word in lowered for word in unsafe):
        return 'unsafe_error'
    # A launcher converts a killed trainer's negative status into a positive
    # SystemExit code. The current owned supervisor supplies the actual status.
    child_code = supervision.get('returncode')
    if code in (-signal.SIGKILL, -signal.SIGTERM) or child_code in (-signal.SIGKILL, -signal.SIGTERM):
        return 'recoverable_process_loss'
    if code is None and not supervision:
        return 'recoverable_process_loss'
    return 'unsafe_error'


class Runner:
    def __init__(self, config, state_dir, *, checker=None, recovery=None):
        self.config = validate_config(config);self.directory = Path(state_dir)
        self.directory.mkdir(parents=True, exist_ok=True)
        self.path = self.directory / 'state.json'
        self.state = optional(self.path) or dict(schema=STATE_SCHEMA, current_campaign=config['initial_campaign'],
            campaign_id=None, status='starting', boot_id=boot(), restarts=0, monitor_restarts=0,
            launcher=None, trainer=None, monitor=None, attempt_has_active=False, owned_descendants=[])
        if self.state.get('schema') != STATE_SCHEMA:
            raise ValueError('Invalid durable watchdog state')
        self.checker = checker or self.cpu_check;self.recovery = recovery or self.cpu_recovery
        self.launcher_process = self.monitor_process = None
        self.stop_requested = False;self.deadline_stop_sent = False
        self.attempt_log = None

    def cancelled(self):
        return (self.directory/'STOP').exists()

    def remaining_cap(self):
        now,mono,current_boot=time.time(),time.monotonic(),boot()
        previous=self.state.get('observed_wall',self.config['hard_deadline_wall']-self.config['seconds'])
        if now < previous-.01:raise RuntimeError('Wall clock moved backwards; fixed authorization continuity is uncertain')
        left=min(self.config['seconds'],self.config['hard_deadline_wall']-now,
            self.state.get('authorization_remaining_seconds',self.config['seconds'])-max(0.,now-previous))
        if self.state.get('observed_boot_id')==current_boot:
            old_mono=self.state['observed_monotonic']
            if mono < old_mono:raise RuntimeError('Monotonic clock moved backwards')
            left=min(left,self.state['authorization_remaining_seconds']-max(0.,mono-old_mono),
                self.state.get('authorization_deadline_monotonic',mono+left)-mono)
        return max(0.,left)

    @property
    def campaign(self):
        return Path(self.state['current_campaign'])

    def publish(self, status=None, **fields):
        remaining=self.remaining_cap()
        if status is not None:
            self.state['status'] = status
        self.state.update(fields);self.state['updated_wall'] = time.time()
        self.state['watchdog_pid'] = os.getpid();self.state['watchdog_boot_id'] = boot()
        if self.state.get('observed_boot_id')!=boot():self.state['authorization_deadline_monotonic']=time.monotonic()+remaining
        self.state.update(authorization_remaining_seconds=remaining,observed_wall=time.time(),
            observed_monotonic=time.monotonic(),observed_boot_id=boot())
        atomic(self.path, self.state)

    def cpu_check(self, campaign, *, read_only=False):
        env = os.environ.copy();env['CUDA_VISIBLE_DEVICES'] = '';env['PYTHONDONTWRITEBYTECODE'] = '1'
        command = [self.config['python'], str(Path(__file__)), '--checkpoint-check', str(campaign), '--project', self.config['project']]
        if read_only:command.append('--read-only')
        result = subprocess.run(command, env=env, capture_output=True, text=True, timeout=120)
        if result.returncode:
            raise RuntimeError('Unsafe checkpoint/budget preflight: ' + result.stderr[-8000:])
        return json.loads(result.stdout.splitlines()[-1])

    def cpu_recovery(self, campaign, destination, seconds):
        env = os.environ.copy();env['CUDA_VISIBLE_DEVICES'] = '';env['PYTHONDONTWRITEBYTECODE'] = '1'
        command = [self.config['python'], str(Path(__file__).with_name('recover.py')), '--parent', str(campaign),
            '--destination', str(destination), '--seconds', str(seconds), '--reason', 'watchdog-bounded-boot-recovery']
        result = subprocess.run(command, env=env, capture_output=True, text=True, timeout=180)
        if result.returncode:
            raise RuntimeError('Boot continuation rejected: ' + result.stderr[-8000:])
        proof = json.loads(result.stdout.splitlines()[-1])
        if (proof.get('campaign') != str(destination) or not proof.get('complete_state_byte_exact')
                or not proof.get('all_rng_byte_exact') or proof.get('new_allocation_seconds') != seconds):
            raise RuntimeError('Incomplete boot continuation proof')
        return proof

    def valid_role(self, owner, role):
        if owner is None or owner.boot_id != boot() or owner.pgid != owner.pid or owner.session != owner.pid:
            return False
        script = Path(self.config['project']) / 'Tools/ZeroDepthTraining' / (role + '.py')
        command = owner.command
        if len(command) < 3:
            return False
        try:
            if not Path(command[1]).samefile(script):return False
            flag = '--run-dir' if role == 'train' else '--campaign'
            if not Path(command[command.index(flag)+1]).samefile(self.campaign):return False
            if role in ('train', 'launch') and '--resume' not in command:return False
        except (ValueError, OSError, IndexError):
            return False
        return True

    def trainer(self):
        supervision = optional(self.campaign / 'supervisor.json')
        launcher = decode(self.state.get('launcher'))
        if launcher and supervision.get('supervisor_pid') == launcher.pid:
            current = inspect(supervision.get('trainer_pid'))
            if self.valid_role(current, 'train'):
                previous = decode(self.state.get('trainer'))
                if previous and current != previous:raise RuntimeError('Owned trainer identity changed')
                self.state['trainer'] = asdict(current)
        saved = decode(self.state.get('trainer'))
        if saved:
            # Strong individual identities survive a dead session leader. Only
            # actual members of the original private trainer session qualify.
            descendants={entry['pid']:entry for entry in self.state.get('owned_descendants',[])}
            # Numeric session IDs may eventually be reused. An exact leader or
            # already-bound surviving child must prove this original session
            # still exists before any NEW identity can be admitted.
            continuity=inspect(saved.pid)==saved or any(inspect(pid)==decode(entry) for pid,entry in descendants.items())
            if continuity:
                for child in session_members(saved):
                    if child!=saved:descendants[child.pid]=asdict(child)
            self.state['owned_descendants']=list(descendants.values())
        return saved if saved and inspect(saved.pid) == saved else None

    def spawn_monitor(self):
        if self.monitor_process is not None and self.monitor_process.poll() is not None:self.monitor_process.wait()
        saved = decode(self.state.get('monitor'))
        if saved and inspect(saved.pid) == saved:
            return
        if saved and inspect(saved.pid) is not None and saved.boot_id == boot():
            raise RuntimeError('Monitor PID identity changed; refusing replacement')
        # Honor a separately managed existing monitor instead of fighting its
        # campaign lock or claiming permission to terminate it.
        existing = optional(self.campaign / 'monitor.json')
        pid = existing.get('pid');current = inspect(pid)
        if self.valid_role(current, 'monitor'):
            self.state['monitor'] = asdict(current);return
        if self.state['monitor_restarts'] >= 20:
            self.state['monitor_error'] = 'Bounded monitor retry limit exhausted';return
        command = [self.config['python'], str(Path(self.config['project']) / 'Tools/ZeroDepthTraining/monitor.py'),
            '--campaign', str(self.campaign), '--port', str(self.config['port']), '--bind', self.config['bind']]
        with (self.directory / 'monitor.log').open('a') as output:
            self.monitor_process = subprocess.Popen(command, stdout=output, stderr=subprocess.STDOUT, start_new_session=True)
        owner = bind_spawn(self.monitor_process,command)
        self.state['monitor'] = asdict(owner);self.state['monitor_restarts'] += 1

    def launch(self, *, adopt=None):
        if self.cancelled() or self.stop_requested:self.publish('stopped' if self.cancelled() else 'interrupted');return
        if adopt and not self.valid_role(inspect(adopt),'launch'):raise RuntimeError('Adopted launcher is not this owned campaign')
        checked = self.cpu_check(self.campaign,read_only=True) if adopt and self.checker==self.cpu_check else self.checker(self.campaign)
        if self.cancelled() or self.stop_requested:self.publish('stopped' if self.cancelled() else 'interrupted');return
        if checked['remaining_seconds'] <= 1 or self.remaining_cap()<=1:
            self.publish('deadline');return
        previous_id = self.state.get('campaign_id')
        if previous_id and previous_id != checked['campaign_id']:raise RuntimeError('Campaign allocation identity changed')
        self.state['campaign_id'] = checked['campaign_id']
        ledger = read(self.campaign / 'budget.json')
        self.state['attempt_censored_baseline'] = ledger.get('collection_accounting', {}).get('censored_games', 0)
        self.state['attempt_has_active'] = False;self.state['trainer'] = None
        self.state['owned_descendants']=[]
        self.state['attempt'] = self.state['restarts'] + 1
        self.attempt_log = self.directory / ('launcher-attempt-%02d.log' % self.state['attempt'])
        if adopt:
            owner = inspect(adopt)
            if not self.valid_role(owner, 'launch'):raise RuntimeError('Adopted launcher is not this owned resume campaign')
        else:
            command = [self.config['python'], str(Path(self.config['project']) / 'Tools/ZeroDepthTraining/launch.py'),
                '--campaign', str(self.campaign), '--seconds', str(checked['limit_seconds']), '--resume']
            with self.attempt_log.open('a') as output:
                self.launcher_process = subprocess.Popen(command, stdout=output, stderr=subprocess.STDOUT, start_new_session=True)
            owner = bind_spawn(self.launcher_process,command)
        if self.cancelled() or self.stop_requested:
            signal_owned(owner,signal.SIGTERM)
        self.publish('starting', launcher=asdict(owner), boot_id=boot(), attempt_log=str(self.attempt_log))
        self.state['attempt_started_monotonic']=time.monotonic()
        self.state['progress_monotonic']=time.monotonic();self.state['last_progress']=None
        self.state.pop('stall_reason',None)
        self.state.pop('stop_reason',None)

    def stop_owned(self, *, deadline=False):
        trainer = self.trainer();launcher = decode(self.state.get('launcher'))
        # A fixed training cap stops only the trainer, allowing the launcher to
        # perform its existing incumbent evaluation. Explicit user stop goes to
        # the launcher so its requested-stop flow skips evaluation.
        launcher_alive=launcher and inspect(launcher.pid)==launcher
        target = trainer if trainer and (deadline or not launcher_alive) else launcher
        if target:
            signal_owned(target, signal.SIGTERM)
        end = min(time.time() + self.config['grace_seconds'], self.config['hard_deadline_wall']) if deadline else time.time() + self.config['grace_seconds']
        while target and inspect(target.pid) == target and time.time() < end:
            time.sleep(min(self.config['poll_seconds'], max(.01, end-time.time())))
        if trainer and inspect(trainer.pid) == trainer:
            signal_owned(trainer, signal.SIGKILL, group=True)
        if not deadline and launcher and inspect(launcher.pid) == launcher:
            signal_owned(launcher, signal.SIGKILL, group=True)
        for entry in self.state.get('owned_descendants',[]):
            child=decode(entry)
            if inspect(child.pid)==child:signal_owned(child,signal.SIGKILL)
        joined=[owner for owner in (trainer,launcher if not deadline else None) if owner]
        joined.extend(decode(entry) for entry in self.state.get('owned_descendants',[]))
        join_until=time.monotonic()+2.
        while any(inspect(owner.pid)==owner for owner in joined):
            if time.monotonic()>=join_until:
                raise RuntimeError('Owned trainer/Host failed to exit; refusing overlap or budget recovery')
            time.sleep(.01)
        if self.launcher_process is not None and self.launcher_process.poll() is not None:
            self.launcher_process.wait()

    def recover_boot(self):
        if self.cancelled() or self.stop_requested:self.publish('stopped' if self.cancelled() else 'interrupted');return False
        if self.state['restarts'] >= self.config['max_restarts']:
            self.publish('retry_exhausted');return False
        remaining = min(43200.,self.remaining_cap())
        if remaining < 1:
            self.publish('deadline');return False
        destination = Path(self.config['recovery_root']) / ('boot-' + uuid.uuid4().hex)
        destination.parent.mkdir(parents=True, exist_ok=True)
        # Debit BEFORE invoking a helper: repeated crashes cannot evade retries.
        self.publish('recovering', recovery_destination=str(destination), recovery_seconds=remaining,
            restarts=self.state['restarts']+1)
        self.recovery(self.campaign, destination, remaining)
        # Recovery/finite verification consumes the SAME fixed authorization.
        # Never grant more elapsed time simply because copying was slow.
        if self.remaining_cap()<=0:
            self.publish('deadline');return False
        self.publish('starting', current_campaign=str(destination), campaign_id=None,
            launcher=None, trainer=None, monitor=None, boot_id=boot(), restarts=self.state['restarts'])
        return True

    def run(self, *, adopt=None):
        with Lock(self.directory / 'watchdog.lock'):
            saved=optional(self.path)
            if saved:self.state=saved  # Reload AFTER acquiring exclusive ownership.
            signature=hashlib.sha256(json.dumps(self.config,sort_keys=True).encode()).hexdigest()
            if self.state.get('config_sha256',signature)!=signature:raise RuntimeError('Immutable watchdog authorization changed')
            self.state['config_sha256']=signature
            if self.cancelled():
                # An earlier controller may have died with its separately
                # supervised trainer still alive. Cancel those exact owners
                # before publishing a terminal stop; never start/check a model.
                self.stop_owned();self.publish('stopped',stop_reason='durable_STOP');return self.state
            if self.state['status'] in TERMINAL:return self.state
            try:
                previous_boot = self.state['boot_id']
                ledger = read(self.campaign / 'budget.json')
                active = ledger.get('active')
                old_supervision=optional(self.campaign/'supervisor.json')
                old_launcher=decode(self.state.get('launcher'))
                completed_training=old_launcher and old_supervision.get('supervisor_pid')==old_launcher.pid and \
                    old_supervision.get('state')=='complete' and old_supervision.get('returncode')==0 and old_supervision.get('stop_reason') in (None,'hard_deadline')
                if previous_boot!=boot() and (self.state['status']=='evaluating' or completed_training):
                    if completed_evaluation(self.campaign):self.publish('completed')
                    else:self.publish('blocked',error='Boot interrupted evaluation; training must not restart')
                    return self.state
                changed_boot = previous_boot != boot() or active and active['boot_id'] != boot()
                if changed_boot and not self.recover_boot():return self.state
                saved = decode(self.state.get('launcher'))
                if saved and inspect(saved.pid) == saved:
                    self.attempt_log = Path(self.state['attempt_log'])
                elif saved:
                    # A same-boot watchdog restart follows normal process-loss
                    # guards, rather than silently starting a second launcher.
                    if not self.after_exit(None):return self.state
                else:
                    self.launch(adopt=adopt)
                while self.state['status'] not in TERMINAL:
                    self.spawn_monitor()
                    launcher = decode(self.state.get('launcher'));current = inspect(launcher.pid) if launcher else None
                    if current is not None and current != launcher:raise RuntimeError('Launcher ownership changed')
                    if self.stop_requested or self.cancelled():
                        self.stop_owned();self.publish('stopped' if self.cancelled() else 'interrupted',
                            stop_reason='durable_STOP' if self.cancelled() else 'service_signal');break
                    trainer = self.trainer();ledger = read(self.campaign / 'budget.json');active = ledger.get('active')
                    if active and trainer and active.get('pid') == trainer.pid:
                        self.state['attempt_has_active'] = True
                        progress=tuple(active.get('progress',{}).get(key) for key in ('games','generations'))
                        if self.state.get('last_progress')!=list(progress):
                            self.state['last_progress']=list(progress);self.state['progress_monotonic']=time.monotonic()
                    if current is None:
                        code = self.launcher_process.poll() if self.launcher_process else None
                        if not self.after_exit(code):break
                        continue
                    evaluating = self.state['attempt_has_active'] and trainer is None and active is None
                    if evaluating:
                        self.publish('evaluating')
                    elif self.remaining_cap()<=self.config['grace_seconds'] and not self.deadline_stop_sent:
                        self.deadline_stop_sent = True;self.publish('deadline_stopping')
                        self.stop_owned(deadline=True)
                    elif self.deadline_stop_sent:
                        if trainer and self.remaining_cap()<=0:
                            signal_owned(trainer, signal.SIGKILL, group=True)
                    else:
                        elapsed=time.monotonic()-self.state.get('attempt_started_monotonic',time.monotonic())
                        stale=None
                        if not active and not self.state['attempt_has_active'] and elapsed>180:stale='startup_timeout'
                        elif active and time.monotonic()-active['last_heartbeat_monotonic']>120:stale='heartbeat_timeout'
                        elif active and time.monotonic()-self.state.get('progress_monotonic',time.monotonic())>300:stale='progress_timeout'
                        if stale:
                            self.publish('retry_wait',stall_reason=stale)
                            self.stop_owned()
                            if not self.after_exit(self.launcher_process.poll() if self.launcher_process else None):break
                            continue
                        self.publish('training' if active else 'starting')
                    time.sleep(self.config['poll_seconds'])
            except Exception as error:
                # Failed finite/source/ownership checks never cause a retry.
                # A live original owned trainer should finish/checkpoint first.
                try:self.stop_owned()
                except Exception as cleanup:self.state['cleanup_error'] = str(cleanup)
                try:self.publish('blocked', error=str(error))
                except Exception:
                    # Backward-clock detection must still durably refuse work,
                    # without overwriting the last trusted time observation.
                    self.state.update(status='blocked',error=str(error));atomic(self.path,self.state)
            return self.state

    def after_exit(self, code):
        launcher = decode(self.state.get('launcher'))
        supervision = optional(self.campaign / 'supervisor.json')
        if launcher and supervision.get('supervisor_pid') != launcher.pid:supervision = {}
        log_path = Path(self.state.get('attempt_log', str(self.directory/'missing.log')))
        log = log_path.read_bytes()[-65536:].decode(errors='replace') if log_path.exists() else ''
        ledger = read(self.campaign / 'budget.json')
        unsafe_censors=unsafe_recent_censors(ledger)
        reason = failure_reason(code, supervision, log,
            censored_increased=unsafe_censors)
        training_ended=supervision.get('state')=='complete' and supervision.get('returncode')==0 and supervision.get('stop_reason') in (None,'hard_deadline')
        if self.state.get('status')=='evaluating' or training_ended:
            if completed_evaluation(self.campaign):reason='completed'
            else:
                self.stop_owned();self.publish('blocked',error='Evaluation did not finish; training must not restart')
                return False
        elif (self.state.get('stall_reason') or self.state.get('status')=='interrupted' and
              self.state.get('stop_reason')=='service_signal') and not self.cancelled():
            reason=failure_reason(-signal.SIGKILL,{},log,
                censored_increased=unsafe_censors)
        if reason=='completed' and not completed_evaluation(self.campaign):
            self.stop_owned();self.publish('blocked',error='No complete checkpoint-bound incumbent evaluation')
            return False
        if reason!='completed' and (self.deadline_stop_sent or self.remaining_cap()<=0):reason='deadline'
        if reason != 'recoverable_process_loss':
            if reason in ('unsafe_error',):
                self.stop_owned();self.publish('blocked', error='Unsafe process/evaluation failure', returncode=code)
            else:
                if reason in ('deadline','stopped'):self.stop_owned(deadline=reason=='deadline')
                self.publish(reason, returncode=code)
            return False
        self.stop_owned()  # Joined owned orphan trainer before ledger recovery.
        if self.state['restarts'] >= self.config['max_restarts']:
            self.publish('retry_exhausted', returncode=code);return False
        self.publish('retry_wait', restarts=self.state['restarts']+1, returncode=code,
                     launcher=None, trainer=None)
        self.launch()
        return self.state['status'] not in TERMINAL


def completed_evaluation(campaign):
    """A progress report alone never proves the planned incumbent test finished."""
    campaign=Path(campaign)
    prepared=read(campaign/'prepared.json')
    checkpoint=campaign/'latest.soicp';manifest=campaign/'incumbent/manifest.json'
    candidates=[]
    for path in campaign.glob('post-training-evaluation-*.json'):
        if path.name.endswith('.plan.json'):continue
        value=read(path);summary=value.get('summary',{})
        if (value.get('purpose')=='final_strength_evaluation' and not value.get('error') and
                summary.get('evaluation_finished') is True and value.get('pairs')==prepared['post_training_pairs'] and
                summary.get('planned_pairs')==prepared['post_training_pairs'] and
                summary.get('complete_pairs')==prepared['post_training_pairs'] and
                summary.get('recorded_games')==2*prepared['post_training_pairs']):
            candidates.append(value)
    if not candidates:return False
    with checkpoint.open('rb') as stream:checkpoint_sha=hashlib.file_digest(stream,'sha256').hexdigest()
    with manifest.open('rb') as stream:manifest_sha=hashlib.file_digest(stream,'sha256').hexdigest()
    return any(value.get('checkpoint_sha256')==checkpoint_sha and value.get('incumbent_manifest_sha256')==manifest_sha for value in candidates)


def main():
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--config',type=Path)
    parser.add_argument('--campaign',type=Path);parser.add_argument('--seconds',type=float)
    parser.add_argument('--state-dir',type=Path);parser.add_argument('--python',type=Path,default=Path(sys.executable))
    parser.add_argument('--port',type=int,default=8766);parser.add_argument('--max-restarts',type=int,default=3)
    parser.add_argument('--poll-seconds',type=float,default=5);parser.add_argument('--adopt-launcher',type=int)
    parser.add_argument('--checkpoint-check',type=Path);parser.add_argument('--project',type=Path,default=ROOT)
    parser.add_argument('--read-only',action='store_true')
    args=parser.parse_args()
    if args.checkpoint_check:
        print(json.dumps(checkpoint_check(args.checkpoint_check,args.project,read_only=args.read_only)),flush=True);return
    if args.config:
        config=validate_config(read(args.config));directory=args.config.parent
    else:
        if not args.campaign or args.seconds is None or not args.state_dir:
            parser.error('Provide --config, or --campaign/--seconds/--state-dir to initialize authorization')
        directory=args.state_dir.resolve();directory.mkdir(parents=True,exist_ok=True)
        path=directory/'config.json'
        config=dict(schema=SCHEMA,initial_campaign=str(args.campaign.resolve()),python=str(args.python.absolute()),
            seconds=args.seconds,hard_deadline_wall=time.time()+args.seconds,port=args.port,bind='127.0.0.1',
            max_restarts=args.max_restarts,poll_seconds=args.poll_seconds,grace_seconds=25.,
            recovery_root=str(directory/'recoveries'),project=str(args.project.resolve()))
        validate_config(config)
        with Lock(directory/'watchdog.lock'):
            if path.exists():
                saved=validate_config(read(path))
                for key in set(config)-{'hard_deadline_wall'}:
                    if saved[key]!=config[key]:raise ValueError('Existing fixed watchdog authorization differs')
                config=saved
            else:atomic(path,config)
    runner=Runner(config,directory)
    signal.signal(signal.SIGTERM,lambda *_:setattr(runner,'stop_requested',True))
    signal.signal(signal.SIGINT,lambda *_:setattr(runner,'stop_requested',True))
    result=runner.run(adopt=args.adopt_launcher)
    print(json.dumps(result),flush=True)
    if result['status'] in ('blocked','retry_exhausted'):raise SystemExit(2)


if __name__=='__main__':main()
