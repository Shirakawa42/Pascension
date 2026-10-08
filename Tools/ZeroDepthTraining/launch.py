"""Explicitly launch prepared training, then evaluate against the frozen current AI."""
from __future__ import annotations

import argparse
import hashlib
import json
import math
import os
from pathlib import Path
import signal
import subprocess
import sys
import time

from evaluate import run_evaluation, verify_bundle
from host import HERE, catalog
from train import TrainConfig, identity

sys.path.append(str(HERE.parent / "TrainingPreflight"))
from supervise_training import supervise
from bench_common import save_json


def _service_identity(pid):
    try:
        folder = Path('/proc') / str(pid)
        before = (folder / 'stat').read_text().rsplit(')', 1)[1].split()
        if before[0] in ('Z', 'X'):
            return None
        command = (folder / 'cmdline').read_bytes()
        after = (folder / 'stat').read_text().rsplit(')', 1)[1].split()
        if not command or after[0] in ('Z', 'X') or any(before[i] != after[i] for i in (2, 3, 19)):
            return None
        return (pid, int(after[19]), int(after[2]), int(after[3]), command,
                Path('/proc/sys/kernel/random/boot_id').read_text().strip())
    except (OSError, ValueError, IndexError):
        return None


def _unreaped_service_owner(owner):
    try:
        row = (Path('/proc') / str(owner[0]) / 'stat').read_text().rsplit(')', 1)[1].split()
        return (row[0] == 'Z' and int(row[19]) == owner[1]
                and (int(row[2]), int(row[3])) == owner[2:4])
    except (OSError, ValueError, IndexError):
        return False


def _service_members(owner, known=()):
    """Numeric session IDs alone cannot admit a new process after PID reuse."""
    if owner is None:
        return []
    if owner[0] != owner[2] or owner[0] != owner[3] or owner[0] == os.getpgrp():
        raise RuntimeError('League process did not own a private session')
    if owner[5] != Path('/proc/sys/kernel/random/boot_id').read_text().strip():
        raise RuntimeError('League process boot ownership changed')
    continuity = (_service_identity(owner[0]) == owner or _unreaped_service_owner(owner)
                  or any(_service_identity(member[0]) == member for member in known))
    if not continuity:
        return []
    members = []
    for folder in Path('/proc').iterdir():
        if not folder.name.isdecimal():
            continue
        member = _service_identity(int(folder.name))
        if (member is not None and member[2:4] == owner[2:4]
                and member[1] >= owner[1] and member[5] == owner[5]):
            members.append(member)
    return members


def _join_service_descendants(owner, known=()):
    """Bind and join private-session members while exact continuity is proved."""
    bound = []
    try:
        for member in _service_members(owner, known):
            try:
                descriptor = os.pidfd_open(member[0])
            except ProcessLookupError:
                continue
            if _service_identity(member[0]) != member:
                os.close(descriptor)
                raise RuntimeError('League descendant identity changed before cleanup')
            bound.append((descriptor, member))
        for descriptor, _ in bound:
            try:
                signal.pidfd_send_signal(descriptor, signal.SIGKILL)
            except ProcessLookupError:
                pass
        end = time.monotonic() + 3
        while any(_service_identity(member[0]) == member for _, member in bound):
            if time.monotonic() >= end:
                raise RuntimeError('Owned league descendants failed to exit')
            time.sleep(.01)
    finally:
        for descriptor, _ in bound:
            os.close(descriptor)


class LeagueService:
    """Optional bounded diagnostics owned by this launcher, outside PPO data."""
    def __init__(self, campaign):
        self.campaign = Path(campaign)
        self.process = None
        self.output = None
        self.owner = None
        self.descendants = []

    def start(self):
        path = self.campaign / 'league-plan.json'
        if not path.exists():
            return
        if path.is_symlink() or path.stat().st_size > 32768:
            raise ValueError('Invalid league plan file')
        plan = json.loads(path.read_text())
        fields = {'schema', 'baseline', 'baseline_manifest_sha256', 'every_games',
                  'games', 'workers', 'max_seconds', 'seed_base'}
        if set(plan) != fields or plan['schema'] != 'shards-zero-depth-league-plan-v1':
            raise ValueError('Unexpected league plan')
        for name, low, high in (('every_games', 10000, 10000000), ('games', 40, 2000),
                                ('workers', 1, 4), ('seed_base', 0xA000000000000000, 0xB000000000000000 - 2000)):
            if type(plan[name]) is not int or not low <= plan[name] <= high:
                raise ValueError('Invalid league plan field: ' + name)
        if plan['games'] % 40 or plan['seed_base'] % 20:
            raise ValueError('League must balance complete hero and seat cycles')
        if (type(plan['max_seconds']) not in (float, int) or not math.isfinite(plan['max_seconds'])
                or not 10 <= plan['max_seconds'] <= 600):
            raise ValueError('League diagnostic budget must be bounded at 10..600 seconds')
        baseline = Path(plan['baseline'])
        if not baseline.is_absolute() or baseline.is_symlink():
            raise ValueError('League baseline must be an immutable absolute bundle')
        manifest = baseline / 'manifest.json'
        if manifest.is_symlink() or hashlib.sha256(manifest.read_bytes()).hexdigest() != plan['baseline_manifest_sha256']:
            raise ValueError('League baseline manifest changed')
        command = [sys.executable, str(HERE / 'league.py'), 'watch', '--campaign', str(self.campaign),
                   '--baseline', str(baseline), '--every-games', str(plan['every_games']),
                   '--games', str(plan['games']), '--workers', str(plan['workers']),
                   '--max-seconds', str(plan['max_seconds']), '--seed-base', str(plan['seed_base']),
                   '--parent-pid', str(os.getpid()), '--device', 'cuda']
        self.output = (self.campaign / 'league-service.log').open('a')
        try:
            self.process = subprocess.Popen(command, stdout=self.output, stderr=subprocess.STDOUT,
                                            start_new_session=True)
            self.owner = _service_identity(self.process.pid)
        except BaseException:
            self.output.close()
            self.output = None
            raise
        save_json(self.campaign / 'league-service.json', {'state': 'starting', 'pid': self.process.pid,
                  'parent_pid': os.getpid(), 'command': command})

    def close(self):
        if self.process is not None:
            # Capture before poll/wait reaps the owner. An exact unreaped zombie
            # also proves the old session birth; later numeric reuse does not.
            self.descendants = _service_members(self.owner, self.descendants)
            if self.process.poll() is None:
                self.process.terminate()
                try:
                    self.process.wait(timeout=5)
                except subprocess.TimeoutExpired:
                    _join_service_descendants(self.owner, self.descendants)
                    if self.process.poll() is None:
                        self.process.kill()
                    self.process.wait(timeout=5)
            # A crashed/exited watcher can leave its Host orphaned. The private
            # session birth/boot identity still binds those actual descendants.
            _join_service_descendants(self.owner, self.descendants)
            save_json(self.campaign / 'league-service.json', {'state': 'stopped', 'pid': self.process.pid,
                      'parent_pid': os.getpid(), 'returncode': self.process.returncode})
        if self.output is not None:
            self.output.close()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--campaign", type=Path, required=True)
    parser.add_argument("--seconds", type=float, required=True)
    parser.add_argument("--games", type=int)
    parser.add_argument("--resume", action="store_true")
    args = parser.parse_args()
    if not 1 <= args.seconds <= 43200 or (args.games is not None and args.games < 1):
        parser.error("Require explicit duration 1..43200 seconds and optional positive game limit")
    directory = args.campaign.resolve()
    config = TrainConfig(**json.loads((directory / "config.json").read_text()))
    pinned = json.loads((directory / "identity.json").read_text())
    if identity(config, catalog()) != pinned:
        raise RuntimeError("Prepared source, binary, observation catalog or configuration changed; prepare a new campaign")
    verify_bundle(directory / "incumbent", repo_root=HERE.parents[1])
    command = [sys.executable, str(HERE / "train.py"), "--run-dir", str(directory),
               "--seconds", str(args.seconds), "--config", str(directory / "config.json")]
    if args.games is not None:
        command += ["--games", str(args.games)]
    if args.resume:
        command += ["--resume", str(directory / "latest.soicp")]
    # Supervision installs graceful training-stop handlers. They must not remain
    # active after that child exits, or signals during evaluation are swallowed.
    previous_handlers = {number: signal.getsignal(number) for number in (signal.SIGTERM, signal.SIGINT)}
    league = LeagueService(directory)
    try:
        league.start()
        code = supervise(command, directory, directory / "budget.json")
    finally:
        try:
            league.close()
        finally:
            for number, handler in previous_handlers.items():
                signal.signal(number, handler)
    if code:
        raise SystemExit(code)
    supervision = json.loads((directory / "supervisor.json").read_text())
    if supervision.get("stop_reason") == "requested_stop":
        print("Training stopped at the user's request; automatic evaluation was skipped.")
        return
    if supervision.get("stop_reason") not in (None, "hard_deadline"):
        raise RuntimeError("Supervisor stopped unhealthy training; automatic evaluation was skipped")
    status = json.loads((directory / "status.json").read_text())
    if status.get("optimizer_steps", 0) < 1:
        raise RuntimeError("No learning update completed; trained strength evaluation was not started")
    plan = json.loads((directory / "prepared.json").read_text())
    checkpoint = directory / "latest.soicp"
    digest = hashlib.sha256(checkpoint.read_bytes()).hexdigest()
    report = run_evaluation(checkpoint, bundle_directory=directory / "incumbent",
                            output=directory / f"post-training-evaluation-{digest[:16]}.json",
                            pairs=plan["post_training_pairs"], workers=config.workers)
    print(json.dumps(report["summary"], indent=2))


if __name__ == "__main__":
    main()
