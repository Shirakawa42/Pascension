"""Read-only, bounded CPU diagnostic of frozen V5 hero decisions.

Uses actual legal draft interventions and unchanged sampled policies. Records
public/own pre-action data; never opens a ledger or initializes CUDA. Run with
an outer process-group timeout. Results diagnose behavior, not optimal balance.
"""
from collections import Counter, defaultdict
import argparse
import itertools
import json
from pathlib import Path
import time
from unittest.mock import patch

import cpu_shadow_eval as shadow
import hero_coverage_eval as heroes
import numpy as np
import torch
from bench_common import save_json
from learning_rollout import LearningHost


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--checkpoint', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--pairs', type=int, default=16)
    args = parser.parse_args()
    if args.output.exists() or not 1 <= args.pairs <= 64:
        parser.error('Require a new output and 1..64 pairs per matchup')
    shadow.configure_cpu()
    shadow.configure_variant('v5')
    selection, _ = shadow.frozen_selections(args.checkpoint, 'learner', args.checkpoint, 'learner')
    policy = shadow.checkpoints.materialize_policy(selection, 'cpu')
    cards = selection.catalog['cards']
    counts = defaultdict(Counter)
    opportunities = defaultdict(set)
    ko_records = []
    original = shadow.ObservedHost.advance_active
    context = {name: heroes.context_bytes('soi.'+name) for name in ('banish', 'scry', 'reorder')}

    def card_id(candidate):
        index = int(round(float(candidate[16])*192))-1
        return cards[index] if index >= 0 else None

    def observe(host, actions, active):
        if not hasattr(host, 'hero_probe_pending'):
            host.hero_probe_pending = {}
        for lane in np.flatnonzero(active):
            o, candidates = host.obs[lane], host.candidates[lane]
            hero = heroes.HEROES[int(round(float(o[22])*5))-1]
            seat = int(host.seats[lane])
            c = counts[hero]
            legal = candidates[host.mask[lane] == 1]
            kinds = legal[:, :16].argmax(-1)
            selected = candidates[actions[lane]]
            kind = int(selected[:16].argmax())
            turn = (host.seed+int(lane), host.seat_a, seat, int(round(float(o[2])*100)))
            c['decisions'] += 1
            c['action_'+shadow.KINDS[kind]] += 1
            if 9 in kinds:
                opportunities[hero].add(turn)
                c['ability_legal_menus'] += 1
            if lane in host.hero_probe_pending:
                record = host.hero_probe_pending.pop(lane)
                if np.array_equal(o[112:116], context['banish']):
                    if kind not in (12, 13) or seat != record['seat']:
                        raise RuntimeError('Unexpected staged Sacrifice resolution; do not infer decline')
                    record['offered'] = [card_id(x) for x in legal if x[:16].argmax() == 12]
                    record['selected'] = card_id(selected) if kind == 12 else None
                    record['resolution'] = 'banished' if kind == 12 else 'declined'
                else:
                    if record['hand_count'] or record['discard_count']:
                        raise RuntimeError('Nonempty Sacrifice had no immediately observed banish request')
                    record['resolution'] = 'no_banish_request'
                ko_records.append(record)
            if kind == 9:
                c['ability_activations'] += 1
                c['ability_activation_health_sum'] += int(round(float(o[16])*50))
                if hero == 'kosynwu':
                    host.hero_probe_pending[lane] = {
                        'seed': host.seed+int(lane), 'policy_a_seat': host.seat_a,
                        'seat': seat, 'round': turn[-1], 'health': int(round(float(o[16])*50)),
                        'hand_count': int(round(float(o[20])*20)),
                        'discard_count': int(round(float(o[512:701].sum())*10)),
                        'resolution': 'unobserved'}
            for name, value in context.items():
                if np.array_equal(o[112:116], value):
                    c[name+'_decisions'] += 1
                    if kind == 12:
                        c[name+'_selected_'+str(card_id(selected))] += 1
                    if kind == 13:
                        c[name+'_finish'] += 1
            if kind == 8:
                c['reroll_cost_sum'] += int(round(float(o[44])*20))
                if hero == 'rez' and o[25] == 1:
                    c['rerolls_after_ability'] += 1
            if kind == 10:
                c['end_turns'] += 1
                c['end_with_play_available'] += int(0 in kinds)
                c['end_with_focus_available'] += int(3 in kinds)
                c['end_with_ability_available'] += int(9 in kinds)
                c['unspent_gems_sum_at_end'] += int(round(float(o[18])*20))
        return original(host, actions, active)

    report = {'schema': 'shards-hero-behavior-probe-v1', 'policy': selection.metadata,
        'policy_sha256': shadow.checkpoints.policy_hash(policy), 'cells': [],
        'scope': 'One frozen policy on both seats; all ten distinct hero matchups, seat-swapped fresh seeds. Descriptive behavior, not optimal balance. Turn opportunities keyed by game/seat/round; extra turns in one round may merge.',
        'seed_start': 0x6A00000000000000, 'pairs_per_cell': args.pairs,
        'optimizer_updates': 0, 'cuda_initialized': False}
    save_json(args.output, report)
    started = time.monotonic()
    with patch.object(shadow.ObservedHost, 'advance_active', observe):
        for index, pair in enumerate(itertools.combinations(heroes.HEROES, 2)):
            build_count = 0
            def build(n, workers, seed, **kwargs):
                nonlocal build_count
                assigned = pair if build_count % 2 == 0 else pair[::-1]
                build_count += 1
                host = LearningHost(n, workers, seed, **kwargs)
                try:
                    heroes.force_initial_draft(host, assigned)
                    return host
                except BaseException:
                    host.close()
                    raise
            result = shadow.run_cpu_match(policy, policy, games=2*args.pairs,
                seed=report['seed_start']+index*128, batch=min(16, args.pairs),
                sampling_seed=260926+index, max_seconds=90, host_factory=build,
                step_delay_ms=2, telemetry=False)
            result['hero_pair'] = pair
            report['cells'].append(result)
            report['hero_behavior'] = {h: dict(v, ability_opportunity_turns=len(opportunities[h])) for h,v in counts.items()}
            report['ko_activations'] = ko_records
            report['seconds'] = time.monotonic()-started
            report['complete'] = len(report['cells']) == 10 and all(x['complete'] for x in report['cells'])
            save_json(args.output, report)
            print(json.dumps({'pair': pair, 'complete': result['complete'], 'seconds': result['seconds']}), flush=True)
            if not result['complete']:
                break
    if torch.cuda.is_initialized():
        raise RuntimeError('Unexpected CUDA initialization')


if __name__ == '__main__':
    main()
