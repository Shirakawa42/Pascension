"""Frozen CPU strategy audit with opportunity, timing and trajectory diagnostics.

Balanced legal hero setup is the only intervention. No learning or policy action
override. Counts deduplicate same-card candidates within a decision menu. These
are visited-state diagnostics, not counterfactual action values or causal balance.
"""
import argparse
from collections import Counter, defaultdict
import itertools
import json
from pathlib import Path
import re
import time
from unittest.mock import patch

import cpu_shadow_eval as shadow
import hero_coverage_eval as heroes
import numpy as np
import torch
from bench_common import save_json
from wire_v10 import LearningHost


GATE_SLOTS = {}
def immediate_gate(card, obs):
    slot=GATE_SLOTS.get(card)
    return None if slot is None else bool(obs[slot] > .5)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--checkpoint', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--pairs', type=int, default=32)
    parser.add_argument('--seed', type=lambda x: int(x, 0), default=0x7600000000000000)
    parser.add_argument('--sampling-seed', type=int, default=260928)
    args = parser.parse_args()
    if args.output.exists() or not 1 <= args.pairs <= 64:
        parser.error('Use a fresh output and 1..64 paired seeds per hero matchup')
    shadow.configure_cpu()
    import variant_v12_runtime as runtime
    import learning_eval,learning_model
    runtime.install()
    shadow._HOST=learning_eval.LearningHost
    shadow.checkpoints.identity=runtime.variant_identity
    shadow.checkpoints.LearningPolicy=learning_model.LearningPolicy
    selection, _ = shadow.frozen_selections(args.checkpoint, 'learner', args.checkpoint, 'learner')
    policy = shadow.checkpoints.materialize_policy(selection, 'cpu')
    cards = selection.catalog['cards']
    from choice_policy_v10 import READINESS_INDEX
    GATE_SLOTS.update({cards[code-1]:slot for code,slot in READINESS_INDEX.items()})
    text = (Path(__file__).parent / 'HostV10/Adapter.cs').read_text()
    contexts = set(re.findall(r'"(soi\.[a-z]+)"', text)) | {''}
    context_map = {heroes.context_bytes(c).tobytes(): c or 'priority' for c in contexts}
    rows = defaultdict(Counter)
    exposed, selected_games = defaultdict(set), defaultdict(set)
    decisions = Counter()
    context_stats = defaultdict(Counter)
    trajectories = {}
    examples = defaultdict(list)
    original = shadow.ObservedHost.advance_active

    def compute(actor):
        logits, values = actor.compute_logits()
        logp = logits.log_softmax(-1)
        probability = logp.exp()
        actor.diagnostics = {
            'entropy': (-(probability * logp).sum(-1)).numpy().copy(),
            'max_probability': probability.max(-1).values.numpy().copy(),
            'legal_count': actor.mask.sum(-1).numpy().astype(np.int64),
            'probabilities': probability.numpy().copy(), 'logits': logits.numpy().copy()}
        return shadow.sample_actions(logits, values)

    def card_id(candidate):
        index = int(round(float(candidate[16]) * 192)) - 1
        return cards[index] if index >= 0 else None

    def record(key, game, probability, selected, candidate_count=1):
        row = rows[key]
        row['legal_menus'] += 1
        row['candidate_slots'] += candidate_count
        row['probability_sum'] += probability
        row['selections'] += int(selected)
        exposed[key].add(game)
        if selected:
            selected_games[key].add(game)

    def observe(host, actions, active):
        pending = []
        for lane in np.flatnonzero(active):
            seat = int(host.seats[lane])
            role = int(seat != host.seat_a)
            packet, diag = host.packets[role]
            action = int(actions[lane])
            if int(packet[lane, 0]) != action:
                raise RuntimeError('Action/probability mismatch')
            obs = host.obs[lane]
            legal = np.flatnonzero(host.mask[lane] == 1)
            candidates = host.candidates[lane]
            kinds = candidates[:, :16].argmax(-1)
            context = context_map.get(obs[112:116].tobytes(), 'unknown:' + obs[112:116].tobytes().hex())
            hero = heroes.HEROES[int(round(float(obs[22]) * 5)) - 1]
            game = (host.seed + int(lane), host.seat_a, seat)
            mastery = int(round(float(obs[17]) * 30))
            health = int(round(float(obs[16]) * 50))
            turn = int(round(float(obs[2]) * 100))
            trajectory = trajectories.setdefault(game, {'hero': hero, 'actions': Counter(), 'purchases': Counter(),
                'banish_targets': Counter(), 'max_mastery': 0, 'max_champions': 0, 'max_collection': 0,
                'conditional_exhausts': Counter(), 'outcome': None})
            trajectory['max_mastery'] = max(trajectory['max_mastery'], mastery)
            trajectory['max_champions'] = max(trajectory['max_champions'], round(float(obs[45]) * 20))
            trajectory['max_collection'] = max(trajectory['max_collection'], round(float(obs[320:509].sum()) * 10))
            chosen_card = card_id(candidates[action])
            chosen_kind = int(kinds[action])
            if chosen_kind in (0, 2, 4, 9):
                trajectory['effect_source'] = chosen_card or hero
            if context == 'soi.mode':
                decisions['mode_source:' + str(trajectory.get('effect_source'))] += 1
            trajectory['actions'][shadow.KINDS[chosen_kind]] += 1
            if chosen_kind == 1:
                trajectory['purchases'][chosen_card] += 1
            if chosen_kind == 12 and context == 'soi.banish':
                trajectory['banish_targets'][chosen_card or 'unknown'] += 1
            decisions['total'] += 1
            decisions['entity_overflow'] += int(obs[63] > 0)
            decisions['staging_overflow'] += int(obs[9] * 1000 > 16.5)
            decisions['menus_over_64_total_candidates'] += int(obs[11] * 128 > 64.5)
            context_stats[context]['decisions'] += 1
            context_stats[context]['forced'] += int(len(legal) == 1)
            context_stats[context]['entropy_sum'] += float(diag['entropy'][lane])
            context_stats[context]['max_probability_sum'] += float(diag['max_probability'][lane])
            if len(legal) > 1:
                context_stats[context]['normalized_choice_entropy_sum'] += float(diag['entropy'][lane] / np.log(len(legal)))

            menu = defaultdict(lambda: [0., False, 0])
            offered_kind = set(int(kinds[i]) for i in legal)
            for index in legal:
                candidate = candidates[index]
                kind = int(kinds[index])
                card = card_id(candidate)
                probability = float(diag['probabilities'][lane, index])
                chosen = index == action
                keys = ['kind:' + shadow.KINDS[kind]]
                if card:
                    keys.append('card:' + shadow.KINDS[kind] + ':' + card)
                if kind in (12, 13, 15):
                    mode = str(int(round(float(candidate[25]) * 128)))
                    option = ((card + '@' if card else '') + mode) if kind == 12 else shadow.KINDS[kind]
                    keys.append('context:' + context + ':' + str(option))
                    if context == 'soi.mode':
                        keys.append('mode_source:' + str(trajectory.get('effect_source')) + ':' + str(option))
                if kind == 9:
                    keys.append('hero:' + hero)
                if kind == 4:
                    gate = immediate_gate(card, obs)
                    if gate is not None:
                        keys.append('gate:' + str(gate).lower() + ':' + card)
                        if chosen:
                            trajectory['conditional_exhausts'][str(gate).lower() + ':' + card] += 1
                            if not gate and len(examples[card]) < 8:
                                examples[card].append({'game': game, 'round': turn, 'hero': hero, 'mastery': mastery,
                                    'health': health, 'faction_plays': np.rint(obs[48:55] * 20).astype(int).tolist(),
                                    'ally_plays': np.rint(obs[55:62] * 20).astype(int).tolist(),
                                    'other_legal_cards': sorted(set(card_id(candidates[j]) for j in legal if card_id(candidates[j]) and j != action)),
                                    'play_available': 0 in offered_kind, 'focus_available': 3 in offered_kind})
                if context == 'soi.volos' and kind == 12:
                    mode = int(round(float(candidate[25]) * 128))
                    suffix = ':all4' if len(legal) == 4 else ':restricted'
                    keys.append('volos:' + str(mode) + suffix)
                    full_plain = health >= 50 and obs[33] == 0 and obs[35] == 0
                    keys.append('volos:' + str(mode) + (':full_health_no_conversion' if full_plain else ':injured_or_conversion') + suffix)
                    keys.append('volos:' + str(mode) + ':mastery_' + ('29' if mastery == 29 else '30' if mastery >= 30 else 'below29') + suffix)
                for key in keys:
                    bucket = menu[key]
                    bucket[0] += probability
                    bucket[1] |= bool(chosen)
                    bucket[2] += 1
            for key, (probability, chosen, slots) in menu.items():
                record(key, game, probability, chosen, slots)
            if chosen_kind == 10:
                decisions['end_turns'] += 1
                for kind in offered_kind - {10, 11}:
                    decisions['end_with_legal_' + shadow.KINDS[kind]] += 1
                if 0 in offered_kind and len(examples['end_with_play']) < 10:
                    examples['end_with_play'].append({'game': game, 'hero': hero, 'round': turn,
                        'mastery': mastery, 'health': health,
                        'play_cards': [card_id(candidates[i]) for i in legal if kinds[i] == 0]})
            pending.append((int(lane), game, (host.seed + int(lane), host.seat_a, 1-seat)))
        result = original(host, actions, active)
        for lane, game, other in pending:
            if host.done[lane]:
                for key in (game, other):
                    if key in trajectories:
                        trajectories[key]['outcome'] = float(host.rewards[lane, key[2]]) if host.done[lane] == 1 else None
        return result

    report = {'schema': 'shards-strategy-coverage-v12-audit-v1', 'policy': selection.metadata,
        'policy_sha256': shadow.checkpoints.policy_hash(policy), 'cells': [], 'seed_start': args.seed,
        'optimizer_updates': 0, 'cuda_initialized': False,
        'scope': 'Same frozen learner on both seats. Only initial heroes assigned; no action intervention. Candidate slots deduplicated by category within each visited menu; repeated menus remain repeated opportunities. Gate-false exhaust means no immediate gated effect, not necessarily foregone useful value. Pre-action state extrema can miss changes on the terminal action.'}
    started = time.monotonic()
    with patch.object(shadow.CPUActor, 'compute', compute), patch.object(shadow.ObservedHost, 'advance_active', observe):
        for number, pair in enumerate(itertools.combinations(heroes.HEROES, 2)):
            builds = 0
            def build(n, workers, seed, **kwargs):
                nonlocal builds
                assignment = pair if builds % 2 == 0 else pair[::-1]
                builds += 1
                host = LearningHost(n, workers, seed, **kwargs)
                try:
                    heroes.force_initial_draft(host, assignment)
                    return host
                except BaseException:
                    host.close()
                    raise
            result = shadow.run_cpu_match(policy, policy, games=2*args.pairs,
                seed=args.seed+number*128, batch=min(32, args.pairs),
                sampling_seed=args.sampling_seed+number, max_seconds=150,
                host_factory=build, step_delay_ms=0, telemetry=False)
            result['hero_pair'] = pair
            report['cells'].append(result)
            report['choices'] = {key: dict(row, exposed_player_games=len(exposed[key]),
                selected_player_games=len(selected_games[key]), mean_probability=row['probability_sum']/row['legal_menus']) for key, row in sorted(rows.items())}
            report['contexts'] = dict(context_stats)
            report['decisions'] = dict(decisions)
            report['examples'] = dict(examples)
            report['trajectories'] = [dict(value, game=game) for game, value in trajectories.items()]
            report['seconds'] = time.monotonic() - started
            report['complete'] = len(report['cells']) == 10 and all(c['complete'] for c in report['cells'])
            save_json(args.output, report)
            print(json.dumps({'pair': pair, 'complete': result['complete'], 'seconds': result['seconds']}), flush=True)
            if not result['complete']:
                break
    if torch.cuda.is_initialized() or shadow.checkpoints.policy_hash(policy) != report['policy_sha256']:
        raise RuntimeError('Unexpected GPU use or policy mutation')


if __name__ == '__main__':
    main()
