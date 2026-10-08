"""Run bounded, nonoverlapping Codex reviews for one training authorization.

Only this scheduler's child is supervised here; the independent training
watchdog owns training and its immutable deadline. No CUDA context is created.
"""
from __future__ import annotations

import argparse
import hashlib
import json
import math
import os
from pathlib import Path
import signal
import subprocess
import time

from runner import Lock, atomic, read, optional


def sha(path):
    return hashlib.sha256(Path(path).read_bytes()).hexdigest()


def stopped(config):
    control = Path(config['watchdog_config']).parent
    return (control / 'STOP').exists() or (Path(config['directory']) / 'STOP').exists()


def next_slot(start, now, last, interval=3600, final_slot=12):
    """Coalesce missed hours after reboot; never run a burst of stale reviews."""
    due = min(final_slot, max(0, int((now - start) // interval)))
    return due if due > last and now >= start else None


def intervention_required(campaign, game_threshold=500000, min_trials=4, trial_anchor=0):
    """A healthy process does not excuse an unresolved long learning plateau."""
    campaign = Path(campaign)
    status = optional(campaign / 'status.json')
    if status.get('mode') == 'depth expert iteration':
        champion = optional(campaign / 'champion.json')
        return bool(status.get('games', 0) - champion.get('games', 0) >= 256 and
                    champion.get('trials', 0) - champion.get('trial_at_promotion', 0) >= 2)
    champion = optional(campaign / 'champion/state.json')
    recent_change = optional(campaign / 'learning-intervention.json')
    anchor = max(champion.get('training', {}).get('games', 0), recent_change.get('at_games', 0))
    return bool(anchor and champion.get('trials', 0) - max(trial_anchor, recent_change.get('at_champion_trial', 0)) >= min_trials and
                status.get('games', 0) - anchor >= game_threshold)


def learning_pilot_required(directory, slot, limit):
    """Repeated strength measurements must eventually test a learning change."""
    if not limit:
        return False
    consecutive = 0
    for previous in range(slot - 1, 0, -1):
        evidence = optional(Path(directory) / f'action-evidence-{previous:02d}.json')
        if evidence.get('action') in ('learning_change', 'repair') or evidence.get('experiment_kind') == 'learning_pilot':
            break
        receipt = optional(Path(directory) / f'review-{previous:02d}.receipt.json')
        if receipt.get('intervention_required'):
            consecutive += 1
        if consecutive >= limit:
            return True
    return False


def verify_intervention(path, slot, require_learning=False):
    evidence = optional(path)
    if evidence.get('slot') != slot or evidence.get('action') not in ('experiment', 'repair', 'learning_change'):
        return False
    if require_learning and evidence.get('action') == 'experiment' and evidence.get('experiment_kind') != 'learning_pilot':
        return False
    artifacts = evidence.get('artifacts')
    if not isinstance(artifacts, list) or not artifacts or not evidence.get('conclusion'):
        return False
    return all(isinstance(p, str) and Path(p).is_absolute() and Path(p).is_file()
               and Path(p).stat().st_size > 0 for p in artifacts)


def command(config, report, read_only=False):
    return [config['codex'], 'exec', '-C', config['project'],
            '-s', 'read-only' if read_only else 'danger-full-access',
            '-c', 'approval_policy="never"', '--ephemeral', '--json',
            '-o', str(report), '-']


def run_review(config, slot, *, now=time.time):
    directory = Path(config['directory'])
    control = Path(config['watchdog_config']).parent
    owner = optional(control / 'state.json')
    readonly = slot in (0, config.get('final_slot', 12)) or now() >= config['deadline_wall']
    campaign = owner.get('current_campaign')
    needs_action = bool(not readonly and campaign and intervention_required(campaign, **config.get('stall_policy', {})))
    needs_learning = bool(needs_action and learning_pilot_required(directory, slot,
        config.get('require_learning_pilot_after_evaluation_reviews', 0)))
    evidence_path = directory / f'action-evidence-{slot:02d}.json'
    # First check is a short startup audit; later checks may investigate/repair.
    timeout = 600 if readonly else min(config.get('max_review_seconds',2400), config['deadline_wall'] - now() - 60)
    if timeout <= 0 or stopped(config):
        return {'slot': slot, 'state': 'cancelled', 'finished_wall': now()}
    report = directory / f'review-{slot:02d}.txt'
    log = directory / f'review-{slot:02d}.jsonl'
    prompt = Path(config['prompt']).read_text()
    prompt += '\nInvocation context (authoritative scheduler configuration):\n' + json.dumps({
        'slot': slot, 'mode': 'read-only audit' if readonly else 'review and improve if justified',
        'watchdog_config': config['watchdog_config'], 'watchdog_state': owner,
        'review_directory': str(directory), 'hard_deadline_wall': config['deadline_wall'],
        'review_time_limit_seconds': timeout,
        'intervention_required': needs_action,
        'learning_pilot_required': needs_learning,
        'action_evidence_path': str(evidence_path),
        'recent_learning_intervention': optional(Path(campaign) / 'learning-intervention.json') if campaign else {},
    }, indent=2)
    if needs_action:
        prompt += ('\nA long unresolved plateau has triggered an experiment requirement. '
            'Finite losses alone do not justify another unchanged review. Complete a bounded '
            'diagnostic experiment (for example a larger fresh-seed strength comparison or a '
            'controlled learning pilot), or a tested repair/change. Write action_evidence_path '
            'as JSON with slot, action (experiment/repair/learning_change), conclusion and artifacts '
            '(absolute paths of actual nonempty experiment/validation outputs from this review). '
            'If safety or the deadline prevents completion, explain the concrete blocker; do not '
            'manufacture evidence. The scheduler will mark the intervention incomplete.\n')
    if needs_learning:
        prompt += ('\nRepeated evaluation-only reviews have reached their limit. This review must run '
            'a controlled LEARNING pilot from a shared checkpoint (current settings versus one justified '
            'alternative), with equal game budgets, finite/coverage checks and a paired comparison, '
            'or complete a tested repair/learning change. Set experiment_kind="learning_pilot" and '
            'cite actual training/update and comparison artifacts. Another strength-only evaluation '
            'does not satisfy this requirement. Apply a promising tested intervention with explicit '
            'rollback criteria; uncertainty is a reason for a controlled trial, not indefinite unchanged training.\n')
    started = now()
    receipt = {'slot': slot, 'state': 'running', 'started_wall': started,
               'read_only': readonly, 'report': str(report), 'log': str(log),
               'intervention_required': needs_action}
    receipt['learning_pilot_required'] = needs_learning
    with log.open('ab') as stream:
        process = subprocess.Popen(command(config, report, readonly), stdin=subprocess.PIPE,
            stdout=stream, stderr=subprocess.STDOUT, start_new_session=True,
            cwd=config['project'])
        receipt['pid'] = process.pid
        atomic(directory / 'active.json', receipt)
        process.stdin.write(prompt.encode()); process.stdin.close()
        try:
            while process.poll() is None:
                if stopped(config) or now() - started >= timeout:
                    # This process is still our unreaped child: its PID cannot be reused.
                    os.killpg(process.pid, signal.SIGTERM)
                    try: process.wait(timeout=15)
                    except subprocess.TimeoutExpired:
                        os.killpg(process.pid, signal.SIGKILL); process.wait()
                    receipt['state'] = 'cancelled' if stopped(config) else 'timed_out'
                    break
                time.sleep(2)
            else:
                receipt['state'] = ('complete' if process.returncode == 0 and
                    report.exists() and report.stat().st_size else 'failed')
        finally:
            if process.poll() is None:
                os.killpg(process.pid, signal.SIGTERM)
                try: process.wait(timeout=15)
                except subprocess.TimeoutExpired:
                    os.killpg(process.pid, signal.SIGKILL); process.wait()
    receipt.update(returncode=process.returncode, finished_wall=now())
    if receipt['state'] == 'complete' and needs_action and not verify_intervention(evidence_path, slot, needs_learning):
        receipt['state'] = 'intervention_incomplete'
    atomic(directory / f'review-{slot:02d}.receipt.json', receipt)
    atomic(directory / 'active.json', receipt)
    return receipt


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--config', type=Path, required=True)
    args = parser.parse_args()
    config = read(args.config)
    interval=config.get('interval_seconds',3600)
    if interval not in (1800,3600):raise ValueError('Review interval must be 30 or 60 minutes')
    final_slot=config.get('final_slot',math.ceil((config['deadline_wall']-config['start_wall'])/interval))
    if final_slot!=math.ceil((config['deadline_wall']-config['start_wall'])/interval):raise ValueError('Review slots differ from authorization')
    directory = Path(config['directory'])
    config_hash = sha(args.config)
    with Lock(directory / 'review.lock'):
        watchdog = read(config['watchdog_config'])
        if watchdog['hard_deadline_wall'] != config['deadline_wall']:
            raise ValueError('Review deadline differs from training authorization')
        state = optional(directory / 'state.json') or {
            'last_slot': -1, 'config_sha256': config_hash, 'status': 'waiting'}
        if state['config_sha256'] != config_hash:
            raise ValueError('Review authorization changed')
        while not stopped(config):
            if sha(args.config) != config_hash or sha(config['prompt']) != config['prompt_sha256']:
                raise ValueError('Review configuration or instructions changed')
            now = time.time()
            if now > config['deadline_wall'] + 900:
                break
            slot = next_slot(config['start_wall'], now, state['last_slot'], interval, final_slot)
            if slot is not None:
                # Charge before launch: an interrupted agent cannot be duplicated on recovery.
                state.update(last_slot=slot, status='reviewing', updated_wall=now)
                atomic(directory / 'state.json', state)
                try: result = run_review(config, slot)
                except Exception as exc:
                    result = {'slot': slot, 'state': 'failed', 'error': str(exc), 'finished_wall': time.time()}
                    atomic(directory / f'review-{slot:02d}.receipt.json', result)
                state.update(status='waiting', last_result=result,
                    next_review_wall=config['start_wall'] + (slot + 1) * interval)
                atomic(directory / 'state.json', state)
                if slot == final_slot: break
            time.sleep(5)
        state.update(status='stopped' if stopped(config) else 'complete', updated_wall=time.time())
        atomic(directory / 'state.json', state)


if __name__ == '__main__':
    main()
