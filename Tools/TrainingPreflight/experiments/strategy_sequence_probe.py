"""Add same-turn timing and split-allocation evidence to a frozen strategy probe."""
from collections import Counter, defaultdict
import json
from pathlib import Path
import sys
from unittest.mock import patch

import numpy as np
import strategy_coverage_probe as base


def main():
    counts = Counter()
    pending = defaultdict(dict)
    examples = defaultdict(list)
    cards = None
    original = base.shadow.ObservedHost.advance_active
    split = base.heroes.context_bytes('soi.split')

    def observe(host, actions, active):
        nonlocal cards
        if cards is None:
            # The policy-independent ordered catalog is pinned by V7 setup.
            import variant_v7_runtime as runtime
            _, catalog = runtime.variant_identity(runtime.train_campaign.TrainConfig(adaptive_actors=True))
            cards = catalog['cards']
        def card_id(c):
            i = round(float(c[16]) * 192) - 1
            return cards[i] if i >= 0 else None
        for lane in np.flatnonzero(active):
            seat = int(host.seats[lane])
            key = (host.seed + int(lane), host.seat_a, seat)
            obs = host.obs[lane]
            candidate = host.candidates[lane, actions[lane]]
            kind = int(candidate[:16].argmax())
            for card, event in list(pending[key].items()):
                if base.immediate_gate(card, obs) is True:
                    code = cards.index(card)
                    # Face-up aggregate includes owned champions/destinies.
                    owned = obs[128 + 3*192 + code] > .05
                    legal_ready = any(card_id(c) == card and c[4] == 1
                                      for c, legal in zip(host.candidates[lane], host.mask[lane]) if legal)
                    if owned and not legal_ready:
                        counts['false_then_true_before_end:' + card] += 1
                        if len(examples[card]) < 8:
                            examples[card].append(dict(event, later_mastery=round(float(obs[17])*30),
                                later_faction_plays=np.rint(obs[48:55]*20).astype(int).tolist(),
                                later_ally_plays=np.rint(obs[55:62]*20).astype(int).tolist(),
                                later_champions=round(float(obs[45])*20)))
                    pending[key].pop(card)
            if kind == 4:
                card = card_id(candidate)
                if base.immediate_gate(card, obs) is False:
                    counts['false_gate_exhaust:' + card] += 1
                    pending[key][card] = {'game': key, 'round': round(float(obs[2])*100),
                        'initial_mastery': round(float(obs[17])*30),
                        'initial_champions': round(float(obs[45])*20),
                        'initial_faction_plays': np.rint(obs[48:55]*20).astype(int).tolist(),
                        'initial_ally_plays': np.rint(obs[55:62]*20).astype(int).tolist()}
            if kind == 15 and np.array_equal(obs[112:116], split):
                low, high = round(float(candidate[29])*1000), round(float(candidate[30])*1000)
                if low == high:
                    counts['explicit_split_leaf'] += 1
                    card = card_id(candidate)
                    required = round(float(candidate[26])*1000)
                    if card and 0 < low < required:
                        counts['explicit_below_announced_defense'] += 1
                        if len(examples['partial_split']) < 12:
                            examples['partial_split'].append({'game': key, 'card': card,
                                'allocation': low, 'announced_remaining_defense': required,
                                'opponent_champions': round(float(obs[93])*20)})
                    # With exactly one enemy champion, a full-allocation split
                    # commits the remaining power to that last target implicitly.
                    if card is None and round(float(obs[93])*20) == 1 and abs(float(obs[6]-obs[7])) < 1e-7:
                        entities = obs[1856:2000].reshape(24, 6)
                        enemy = [e for e in entities if e[0] > 0 and e[1] == 1]
                        # Champion records precede the opponent's destinies/play
                        # zone. Only infer a champion if its catalog type is known
                        # from a matching definition in its face-up zone; the first
                        # enemy entity is the sole champion by encoder ordering.
                        if enemy:
                            entity = enemy[0]
                            code = round(float(entity[0])*192)-1
                            required = round(float(entity[4]-entity[3])*50)
                            allocation = round(float(obs[13])*1000)-low
                            counts['sole_champion_implicit_allocations'] += 1
                            if 0 < allocation < required:
                                counts['sole_champion_positive_below_defense'] += 1
                                if len(examples['sole_champion_split']) < 12:
                                    examples['sole_champion_split'].append({'game': key, 'card': cards[code],
                                        'round': round(float(obs[2])*100), 'allocation': allocation,
                                        'announced_remaining_defense': required,
                                        'power_to_player': low, 'total_power': round(float(obs[13])*1000)})
            if kind == 10:
                pending[key].clear()
        return original(host, actions, active)

    with patch.object(base.shadow.ObservedHost, 'advance_active', observe):
        base.main()
    output = Path(sys.argv[sys.argv.index('--output') + 1])
    report = json.loads(output.read_text())
    report['sequence_checks'] = {'counts': dict(counts), 'examples': dict(examples),
        'scope': 'Observed same-acting-player sequences only, reset at its end action. Later true gate while card remains owned and unavailable is evidence of a foregone activation opportunity, not its causal win value. Split diagnostics use announced remaining defense; explicit multi-champion cases may be affected by aura destruction. Sole-champion implicit remainder assumes full mandatory allocation and prioritization order of public entity records.'}
    base.save_json(output, report)
    print(json.dumps({'sequence_counts': counts}))


if __name__ == '__main__':
    main()
