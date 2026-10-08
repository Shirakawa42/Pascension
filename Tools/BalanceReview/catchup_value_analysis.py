"""Resource-by-resource checks for the proposed mastery catch-up cycle.

This calculates card outputs and fresh-stock market probabilities, not win rates
or a fitted conversion between gems, mastery, health, draws and banishing.
Run with --prior-previews to compare two proposal versions without game runs.
"""
from __future__ import annotations

import argparse
from copy import deepcopy
import hashlib
import json
from math import comb
from pathlib import Path
import re


HERE = Path(__file__).resolve().parent
DEFAULT_CATALOG = Path('/home/lva/.local/share/shards-zero-depth/2026-10-08/new-ai-search-statistics-10000/card-catalog.json')
CATCHUPS = ('horizon_seeker', 'riftbreaker', 'relief_courier', 'aegis_surveyor', 'rift_scout')
MARKET_TYPES = {'Ally', 'Mercenary', 'Champion'}
PEERS = {
    'horizon_seeker': ('cloud_oracles_sos', 'shard_seer', 'cache_warden', 'shard_abstractor'),
    'riftbreaker': ('shadow_apostle', 'nil_assassin_duel', 'leshai_knight', 'the_lost_duel', 'the_rotten_duel'),
    'relief_courier': ('kiln_drone', 'mining_drones', 'torian_commandos', 'spore_cleric_duel', 'arach_devotees'),
    'aegis_surveyor': ('kiln_drone', 'the_dispossessed', 'riposte_doctrine'),
    'rift_scout': ('brute', 'dash_duel', 'nil_assassin_duel'),
}


def market_pool(catalog, previews):
    """All-DLC Duel row cards after replacements and pending patch changes.

    Ingeminex bypass the row, and starters/relics/destinies are not row draws.
    The engine excludes original Cloud Oracles specially when SoS is enabled;
    its catalog entry is not covered by ReplacesId (ShardsEngine.cs InInitialCenterPool).
    """
    replaced = {c['replaces_id'] for c in catalog['cards'] if c.get('set') == 'duel' and c.get('replaces_id')}
    pool = {c['id']: deepcopy(c) for c in catalog['cards']
            if c['id'] not in replaced and c['id'] != 'cloud_oracles' and c['type'] in MARKET_TYPES}
    for group in previews['proposals'].values():
        for pair in group['cards']:
            card = pair.get('after')
            if card and card['type'] in MARKET_TYPES:
                pool[card['id']] = {**pool.get(card['id'], {}), **deepcopy(card)}
    return pool


def any_target_probability(total, eligible, slots=6):
    """Exact sampling without replacement, including empty/all-target edges."""
    if not 0 <= eligible <= total or not 0 <= slots <= total:
        raise ValueError('Expected 0 <= eligible <= total and 0 <= slots <= total')
    misses = comb(total - eligible, slots) if total - eligible >= slots else 0
    return 1 - misses / comb(total, slots)


def warp_targets(pool, cap):
    if cap < 0:
        raise ValueError('This calculator models capped Warp only')
    # Comet forbids fast-play irrespective of how it would be acquired.
    return [c for c in pool.values() if c['type'] in {'Ally', 'Mercenary'}
            and c['cost'] <= cap and c['id'] != 'comet' and not c.get('cannot_be_fast_played')]


def initial_shop_rules(manifest=None):
    """Read explicit setup rules; prose is never interpreted as executable data."""
    raw = (manifest or {}).get('market_rules', {}).get('initial_shop', {})
    maximum = raw.get('max_printed_cost', 5)
    allowed = raw.get('allowed_card_ids', [])
    if isinstance(maximum, bool) or not isinstance(maximum, int) or maximum < 0:
        raise ValueError('Initial-shop max_printed_cost must be a nonnegative integer')
    if not isinstance(allowed, list) or any(not isinstance(cid, str) or not cid for cid in allowed):
        raise ValueError('Initial-shop allowed_card_ids must be a list of nonempty ids')
    return {'max_printed_cost': maximum, 'allowed_card_ids': sorted(set(allowed))}


def warp_table(pool, owned_scouts=0, opening_rules=None):
    pool = deepcopy(pool)
    if owned_scouts:
        scout = pool['rift_scout']
        if not 0 <= owned_scouts <= scout['quantity']:
            raise ValueError('Owned Scouts cannot exceed physical copies')
        scout['quantity'] -= owned_scouts
    total = sum(c['quantity'] for c in pool.values())
    opening_rules = opening_rules or initial_shop_rules()
    allowed = set(opening_rules['allowed_card_ids'])
    initial = {k: c for k, c in pool.items()
               if c['cost'] <= opening_rules['max_printed_cost'] or k in allowed}
    opening_total = sum(c['quantity'] for c in initial.values())
    values = {}
    for cap in (1, 2, 3):
        targets = warp_targets(pool, cap)
        eligible = sum(c['quantity'] for c in targets)
        opening_eligible = sum(c['quantity'] for c in warp_targets(initial, cap))
        values[str(cap)] = {
            'eligible_copies': eligible,
            'eligible_definitions': sum(c['quantity'] > 0 for c in targets),
            'normal_six_probability': any_target_probability(total, eligible),
            'restricted_six_probability': any_target_probability(opening_total, opening_eligible),
            'targets': [{'id': c['id'], 'name': c['name'], 'copies': c['quantity']}
                        for c in targets if c['quantity'] > 0],
        }
    return {'row_stock': total,
            'cost_at_most_five_stock': sum(c['quantity'] for c in pool.values() if c['cost'] <= 5),
            'initial_shop_stock': opening_total, 'initial_shop_rules': deepcopy(opening_rules),
            'owned_scout_copies_excluded': owned_scouts, 'thresholds': values}


def scout_fast_play_warp_probability(pool, cap):
    """Fresh row conditioned on a Scout offer; play one and refill its slot.

    Every six-card physical row with at least one Scout is equally likely.
    This does not model strategic market depletion or choosing to buy only
    when its bonus is valuable. Other cards in the row remain unchanged.
    """
    total = sum(c['quantity'] for c in pool.values())
    scouts = pool['rift_scout']['quantity']
    if total < 6 or scouts < 1:
        raise ValueError('Scout fast-play requires six stock cards and an offered Scout')
    targets = warp_targets(pool, cap)
    eligible = sum(c['quantity'] for c in targets)
    scout_eligible = int(any(c['id'] == 'rift_scout' for c in targets))
    choose = lambda n, k: comb(n, k) if 0 <= k <= n else 0
    scout_rows = choose(total, 6) - choose(total - scouts, 6)
    if scout_eligible:
        # A second offered Scout would itself remain a legal target.
        no_target_rows = scouts * choose(total - eligible, 5)
    else:
        no_target_rows = sum(choose(scouts, count) * choose(total - scouts - eligible, 6 - count)
                             for count in range(1, min(scouts, 6) + 1))
    if no_target_rows == 0:
        return 1.0
    remaining_eligible = eligible - scout_eligible
    no_target_refill = (total - 6 - remaining_eligible) / (total - 6) if total > 6 else 1
    return 1 - no_target_rows / scout_rows * no_target_refill


def parsed_yield(card):
    """Read this deliberately small, fixed vocabulary of catch-up designs."""
    base, bonus = card['rules_text'].split('If you have less mastery than your opponent, ')
    def amount(text, unit):
        match = re.search(r'gain (\d+) ' + unit + r'\b', text, re.I)
        return int(match.group(1)) if match else 0
    warp = re.search(r'Warp (\d+)', bonus)
    return {
        'base_power': amount(base, 'power'), 'base_gems': amount(base, 'gems?'),
        'base_draw': int('draw a card' in base.lower()),
        'bonus_mastery': amount(bonus, 'mastery'), 'bonus_gems': amount(bonus, 'gems?'),
        'bonus_health': amount(bonus, 'health'),
        'bonus_banish': int('banish a card from your hand or discard pile' in bonus),
        'bonus_warp_cap': int(warp.group(1)) if warp else 0,
    }


def output_scenarios(card, pool):
    amounts = parsed_yield(card)
    cap = amounts['bonus_warp_cap']
    # Ordinary purchase goes to discard; model a later random row once one
    # Scout has left the stock. Do not let the casting Scout target itself.
    availability = warp_table(pool, owned_scouts=1)['thresholds'][str(cap)]['normal_six_probability'] if cap else 0
    fast_play_availability = scout_fast_play_warp_probability(pool, cap) if cap and card['type'] == 'Mercenary' else 0
    result = []
    for chance in (0, .25, .5, .75, 1):
        row = {
            'assumed_lower_mastery_probability': chance,
            'power': amounts['base_power'],
            'gems': amounts['base_gems'] + chance * amounts['bonus_gems'],
            'mastery': chance * amounts['bonus_mastery'],
            'draws': amounts['base_draw'],
            'net_hand_cards_before_banish': amounts['base_draw'] - 1,
            'requested_health': chance * amounts['bonus_health'],
            'actual_healing_examples': {str(health): chance * min(amounts['bonus_health'], 50 - health)
                                        for health in (40, 45, 48, 50)},
            'optional_banish_opportunities': chance * amounts['bonus_banish'],
            'warp_with_legal_target_model': chance * availability,
        }
        if card['type'] == 'Mercenary':
            row['paid_fast_play_net_gems'] = row['gems'] - card['cost']
            row['paid_fast_play_draws_without_spending_hand_card'] = amounts['base_draw']
            row['paid_fast_play_shield'] = 0
            if cap:
                row['paid_fast_play_warp_with_legal_target_model'] = chance * fast_play_availability
        result.append(row)
    return result


def analyze(catalog, current, prior, manifest=None, prior_manifest=None):
    for previews, supplied_manifest in ((current, manifest), (prior, prior_manifest)):
        if supplied_manifest and supplied_manifest.get('version') not in (None, previews['proposal_version']):
            raise ValueError('Manifest and preview proposal versions must match')
    pools = {'previous': market_pool(catalog, prior), 'revised': market_pool(catalog, current)}
    rules = {'previous': initial_shop_rules(prior_manifest), 'revised': initial_shop_rules(manifest)}
    cards = {}
    removed = {}
    for cid in CATCHUPS:
        versions = {}
        for label, pool in pools.items():
            if cid not in pool:
                continue
            card = pool[cid]
            versions[label] = {'card': card, 'yield': parsed_yield(card),
                               'scenarios': output_scenarios(card, pool)}
        if cid not in pools['revised']:
            if versions:
                removed[cid] = {**versions, 'status': 'removed'}
            continue
        cards[cid] = {**versions, 'current_patch_peers': [pools['revised'][p] for p in PEERS[cid]]}
    return {
        'schema': 'shards-catchup-value-analysis-v1',
        'proposal_version': current['proposal_version'],
        'compared_proposal_version': prior['proposal_version'],
        'method': [
            'Compare printed costs and separate resource outputs; no universal gem/power/mastery exchange rate is assumed.',
            'Trigger probabilities 0%, 25%, 50%, 75%, 100% are sensitivity inputs, not measured gameplay frequencies.',
            'Warp availability = 1 - choose(N-K, 6) / choose(N, 6), using physical copies without replacement.',
            'The market model uses fresh stock, with no strategic depletion or mastery/row correlation. Real later shops differ.',
            'Per-play Scout scenarios remove one owned Scout, then sample a normal row. Initial restricted rows are a separate setup illustration.',
            'Initial-shop exceptions come only from explicit manifest market_rules.initial_shop fields; absent those fields, printed cost 5 or less is used.',
            'Mercenary Scout paid-fast-play odds condition a fresh six-card row on offering at least one Scout, remove one and refill before checking Warp targets.',
            'Healing examples use min(printed heal, 50-current health), without special relic modifiers. Banishing values are opportunities, not guaranteed removals.',
            'Printed hand shields are not immediate healing and do not work from a fast-played card in the play zone.',
            'Only mercenaries have ordinary paid fast-play. Refund examples exclude discounts, doubled effects and free-play effects.',
            'No games were simulated; this is an auditable design calculation, not proof of post-patch win rates.',
        ],
        'stock_models': {label: {'fresh': warp_table(pool, opening_rules=rules[label]),
                                 **({'one_scout_owned': warp_table(pool, 1, rules[label])}
                                    if pool.get('rift_scout', {}).get('quantity', 0) > 0 else {})}
                         for label, pool in pools.items()},
        'cards': cards,
        'removed_designs': removed,
        'banish_example': {
            'assumption': '20-card deck, 8 useful cards, draw 5 without replacement; banish one other card before drawing.',
            'expected_useful_before': 5 * 8 / 20,
            'expected_useful_after': 5 * 8 / 19,
            'increase_per_hand': 5 * 8 / 19 - 5 * 8 / 20,
            'limitation': 'The value depends on which card was removed and future draws; one banish is not assigned a fixed number of gems.',
        },
    }


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--catalog', type=Path, default=DEFAULT_CATALOG)
    parser.add_argument('--previews', type=Path, default=HERE / 'balance_card_previews.json')
    parser.add_argument('--prior-previews', type=Path, required=True)
    parser.add_argument('--manifest', type=Path, help='Explicit current market setup rules; version must match previews')
    parser.add_argument('--prior-manifest', type=Path, help='Explicit previous market setup rules; defaults to cost <= 5')
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    paths = {'catalog': args.catalog, 'previews': args.previews, 'prior_previews': args.prior_previews}
    if args.manifest:
        paths['manifest'] = args.manifest
    if args.prior_manifest:
        paths['prior_manifest'] = args.prior_manifest
    data = {key: json.loads(path.read_text()) for key, path in paths.items()}
    result = analyze(data['catalog'], data['previews'], data['prior_previews'],
                     data.get('manifest'), data.get('prior_manifest'))
    result['inputs'] = {key: {'path': str(path), 'sha256': hashlib.sha256(path.read_bytes()).hexdigest()}
                        for key, path in paths.items()}
    args.output.write_text(json.dumps(result, ensure_ascii=False, indent=2) + '\n')
    print(f'Calculated {len(result["cards"])} designs; wrote {args.output}')


if __name__ == '__main__':
    main()
