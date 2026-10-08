"""Small exhaustive checks for the balance calculator; no games or model runs."""
from copy import deepcopy
from itertools import combinations
import unittest

from catchup_value_analysis import (
    PEERS,
    analyze,
    any_target_probability,
    initial_shop_rules,
    market_pool,
    output_scenarios,
    parsed_yield,
    scout_fast_play_warp_probability,
    warp_table,
    warp_targets,
)


def card(cid, *, cost=2, quantity=1, kind='Ally', **fields):
    return {
        'id': cid, 'name': cid.replace('_', ' ').title(), 'set': 'duel',
        'type': kind, 'cost': cost, 'quantity': quantity,
        'faction': 'Aion', 'rules_text': '', **fields,
    }


def enumerated_probability(physical_cards, eligible_ids, slots=6):
    """Enumerate physical indices so identical copies remain distinct draws."""
    rows = list(combinations(range(len(physical_cards)), slots))
    return sum(any(physical_cards[i] in eligible_ids for i in row) for row in rows) / len(rows)


def miniature_market():
    return {c['id']: c for c in (
        card('rift_scout', quantity=2),
        card('cheap_mercenary', cost=1, kind='Mercenary', quantity=2),
        card('expensive_ally', cost=7),
        card('champion', cost=2, kind='Champion', quantity=8),
    )}


def enumerated_scout_fast_play_probability(pool, cap):
    physical = [cid for cid, c in pool.items() for _ in range(c['quantity'])]
    eligible = {cid for cid, c in pool.items()
                if c['cost'] <= cap and c['type'] in ('Ally', 'Mercenary') and cid != 'comet'}
    successes = outcomes = 0
    for indices in combinations(range(len(physical)), 6):
        row = [physical[index] for index in indices]
        if 'rift_scout' not in row:
            continue
        row.remove('rift_scout')
        refills = [physical[i] for i in range(len(physical)) if i not in indices] or [None]
        for refill in refills:
            successes += any(cid in eligible for cid in row + [refill])
            outcomes += 1
    return successes / outcomes


def analysis_fixture():
    definitions = {cid: card(cid, cost=5) for peers in PEERS.values() for cid in peers}
    definitions['comet'] = card('comet', cost=13)
    rules = {
        'horizon_seeker': 'Draw a card.\nIf you have less mastery than your opponent, gain 2 mastery.',
        'riftbreaker': 'Gain 5 power.\nIf you have less mastery than your opponent, you may banish a card from your hand or discard pile.',
        'relief_courier': 'Gain 2 gems.\nIf you have less mastery than your opponent, gain 5 health.',
        'aegis_surveyor': 'Gain 2 power.\nIf you have less mastery than your opponent, gain 1 gem.',
        'rift_scout': 'Gain 3 power.\nIf you have less mastery than your opponent, Warp 2.',
    }
    previous = {'proposal_version': 'v4', 'proposals': {
        cid: {'cards': [{'before': None, 'after': card(cid, quantity=2, rules_text=text)}]}
        for cid, text in rules.items()
    }}
    current = deepcopy(previous)
    current['proposal_version'] = 'v5'
    for cid in ('relief_courier', 'aegis_surveyor'):
        del current['proposals'][cid]
    current['proposals']['rift_scout']['cards'][0]['after']['type'] = 'Mercenary'
    current['proposals']['horizon_seeker']['cards'][0]['after']['rules_text'] = rules['horizon_seeker'].replace('gain 2 mastery', 'gain 1 mastery')
    return {'cards': list(definitions.values())}, current, previous


class TargetProbabilityTests(unittest.TestCase):
    def test_hypergeometric_matches_exhaustive_physical_draws(self):
        # Includes drawing zero cards, every card, zero targets and all targets.
        for total in range(9):
            for eligible in range(total + 1):
                for slots in range(total + 1):
                    with self.subTest(total=total, eligible=eligible, slots=slots):
                        expected = enumerated_probability(
                            list(range(total)), set(range(eligible)), slots)
                        self.assertAlmostEqual(
                            any_target_probability(total, eligible, slots), expected)

    def test_impossible_sampling_inputs_are_rejected(self):
        for args in ((5, -1, 1), (5, 6, 1), (5, 1, -1), (5, 1, 6), (-1, 0, 0)):
            with self.subTest(args=args), self.assertRaises(ValueError):
                any_target_probability(*args)


class MarketPoolTests(unittest.TestCase):
    def test_comet_opening_exception_never_grants_warp_permission(self):
        pool = miniature_market()
        pool['comet'] = card('comet', cost=13)
        default = warp_table(pool)
        rules = initial_shop_rules({'market_rules': {'initial_shop': {
            'max_printed_cost': 5, 'allowed_card_ids': ['comet'],
        }}})
        changed = warp_table(pool, opening_rules=rules)
        self.assertEqual(changed['initial_shop_stock'], default['initial_shop_stock'] + 1)
        self.assertEqual(changed['cost_at_most_five_stock'], default['cost_at_most_five_stock'])
        opening = [cid for cid, c in pool.items() for _ in range(c['quantity'])
                   if c['cost'] <= 5 or cid == 'comet']
        for cap in (1, 2, 3):
            targets = {cid for cid, c in pool.items()
                       if c['cost'] <= cap and c['type'] in ('Ally', 'Mercenary') and cid != 'comet'}
            self.assertAlmostEqual(changed['thresholds'][str(cap)]['restricted_six_probability'],
                                   enumerated_probability(opening, targets))
            self.assertEqual(changed['thresholds'][str(cap)]['normal_six_probability'],
                             default['thresholds'][str(cap)]['normal_six_probability'])
        self.assertNotIn('comet', {c['id'] for c in warp_targets(pool, 99)})

    def test_opening_rules_ignore_prose_and_validate_structured_fields(self):
        self.assertEqual(initial_shop_rules({'summary': 'Comet can start in the shop'}),
                         {'max_printed_cost': 5, 'allowed_card_ids': []})
        for bad in ({'max_printed_cost': True}, {'max_printed_cost': -1},
                    {'allowed_card_ids': 'comet'}, {'allowed_card_ids': [None]}):
            with self.subTest(bad=bad), self.assertRaises(ValueError):
                initial_shop_rules({'market_rules': {'initial_shop': bad}})

    def test_duel_replacements_cloud_exception_and_patch_overlay(self):
        catalog = {'cards': [
            card('original', set='base', quantity=3),
            card('original_duel', replaces_id='original', quantity=2),
            card('cloud_oracles', set='relics_of_the_future', quantity=3),
            card('cloud_oracles_sos', set='shadow_of_salvation', quantity=3),
            card('starter', kind='Starter'), card('relic', kind='Relic'),
            card('destiny', kind='Destiny'), card('ingeminex', kind='Monster'),
        ]}
        previews = {'proposals': {'edit': {'cards': [
            {'before': {}, 'after': {'id': 'original_duel', 'type': 'Ally', 'cost': 4}},
            {'before': None, 'after': card('new_card', quantity=2)},
            {'before': {}, 'after': card('hero_rule', kind='Hero')},
        ]}}}
        original_catalog, original_previews = deepcopy(catalog), deepcopy(previews)
        result = market_pool(catalog, previews)
        self.assertEqual(set(result), {'original_duel', 'cloud_oracles_sos', 'new_card'})
        self.assertEqual(result['original_duel']['cost'], 4)
        self.assertEqual(result['original_duel']['quantity'], 2)
        self.assertEqual(result['original_duel']['replaces_id'], 'original')
        result['original_duel']['quantity'] = 99
        self.assertEqual(catalog, original_catalog)
        self.assertEqual(previews, original_previews)

    def test_warp_accepts_mercenaries_but_rejects_champions_and_forbidden_cards(self):
        cards = [
            card('ally', cost=2), card('mercenary', cost=2, kind='Mercenary'),
            card('champion', cost=1, kind='Champion'),
            card('comet', cost=1),  # Name exclusion still works without exported flag.
            card('future_forbidden', cost=1, cannot_be_fast_played=True),
            card('too_expensive', cost=3), card('relic', cost=0, kind='Relic'),
        ]
        result = warp_targets({c['id']: c for c in cards}, 2)
        self.assertEqual({c['id'] for c in result}, {'ally', 'mercenary'})

    def test_owned_scout_is_removed_from_denominator_and_eligible_copies(self):
        pool = miniature_market()
        original = deepcopy(pool)
        fresh = warp_table(pool)
        owned = warp_table(pool, owned_scouts=1)
        physical = [cid for cid, c in pool.items() for _ in range(c['quantity'])]
        physical.remove('rift_scout')
        for cap in (1, 2, 3):
            with self.subTest(cap=cap):
                eligible = {cid for cid, c in pool.items()
                            if c['cost'] <= cap and c['type'] != 'Champion'}
                self.assertAlmostEqual(
                    owned['thresholds'][str(cap)]['normal_six_probability'],
                    enumerated_probability(physical, eligible))
                opening = [cid for cid in physical if pool[cid]['cost'] <= 5]
                self.assertAlmostEqual(
                    owned['thresholds'][str(cap)]['restricted_six_probability'],
                    enumerated_probability(opening, eligible))
                delta = fresh['thresholds'][str(cap)]['eligible_copies'] - owned['thresholds'][str(cap)]['eligible_copies']
                self.assertEqual(delta, int(pool['rift_scout']['cost'] <= cap))
        self.assertEqual(owned['row_stock'], len(physical))
        self.assertEqual(pool, original)

    def test_all_scouts_owned_leave_no_phantom_target_definition(self):
        result = warp_table(miniature_market(), owned_scouts=2)
        warp_two = result['thresholds']['2']
        self.assertEqual(warp_two['eligible_definitions'], 1)
        self.assertEqual([c['id'] for c in warp_two['targets']], ['cheap_mercenary'])

    def test_invalid_owned_scout_counts_are_rejected(self):
        for count in (-1, 3):
            with self.subTest(count=count), self.assertRaises(ValueError):
                warp_table(miniature_market(), owned_scouts=count)


class OutputScenarioTests(unittest.TestCase):
    def test_scout_fast_play_matches_physical_row_and_refill_enumeration(self):
        for scout_cost in (1, 2, 4):
            for scout_quantity in (1, 2):
                pool = miniature_market()
                pool['rift_scout'].update(cost=scout_cost, quantity=scout_quantity, type='Mercenary')
                for cap in (1, 2, 3):
                    with self.subTest(cost=scout_cost, quantity=scout_quantity, cap=cap):
                        self.assertAlmostEqual(scout_fast_play_warp_probability(pool, cap),
                                               enumerated_scout_fast_play_probability(pool, cap))

    def test_scout_fast_play_with_exactly_six_stock_cards_has_no_refill(self):
        pool = miniature_market()
        pool['champion']['quantity'] = 1
        for cap in (1, 2, 3):
            self.assertAlmostEqual(scout_fast_play_warp_probability(pool, cap),
                                   enumerated_scout_fast_play_probability(pool, cap))

    def test_mercenary_scout_has_paid_fast_play_cost_and_separate_warp_sensitivity(self):
        pool = miniature_market()
        scout = {**pool['rift_scout'], 'type': 'Mercenary', 'rules_text': (
            'Gain 3 power.\nIf you have less mastery than your opponent, Warp 2.')}
        availability = enumerated_scout_fast_play_probability(pool, 2)
        for row in output_scenarios(scout, pool):
            chance = row['assumed_lower_mastery_probability']
            self.assertEqual(row['paid_fast_play_net_gems'], -2)
            self.assertEqual(row['paid_fast_play_draws_without_spending_hand_card'], 0)
            self.assertEqual(row['paid_fast_play_shield'], 0)
            self.assertAlmostEqual(row['paid_fast_play_warp_with_legal_target_model'], chance * availability)

    def test_healing_caps_requested_and_actual_output_separately(self):
        courier = card('courier', cost=2, rules_text=(
            'Gain 2 gems.\nIf you have less mastery than your opponent, gain 5 health.'))
        scenarios = output_scenarios(courier, miniature_market())
        for row in scenarios:
            probability = row['assumed_lower_mastery_probability']
            self.assertEqual(row['gems'], 2)
            self.assertEqual(row['requested_health'], 5 * probability)
            self.assertEqual(row['actual_healing_examples'], {
                '40': 5 * probability, '45': 5 * probability,
                '48': 2 * probability, '50': 0,
            })
            self.assertNotIn('paid_fast_play_net_gems', row)

    def test_mercenary_fast_play_refunds_only_generated_gems_and_gives_no_hand_shield(self):
        aegis = card('aegis', cost=1, kind='Mercenary', shield=2, rules_text=(
            'Shield 2.\nGain 2 power.\nIf you have less mastery than your opponent, gain 1 gem.'))
        for row in output_scenarios(aegis, miniature_market()):
            probability = row['assumed_lower_mastery_probability']
            self.assertEqual(row['power'], 2)
            self.assertEqual(row['gems'], probability)
            self.assertEqual(row['paid_fast_play_net_gems'], probability - 1)
            self.assertEqual(row['paid_fast_play_shield'], 0)
            self.assertEqual(row['paid_fast_play_draws_without_spending_hand_card'], 0)

    def test_draw_mastery_and_banish_remain_distinct_resources(self):
        seeker = card('seeker', rules_text=(
            'Draw a card.\nIf you have less mastery than your opponent, gain 2 mastery.'))
        riftbreaker = card('riftbreaker', cost=3, kind='Mercenary', rules_text=(
            'Gain 5 power.\nIf you have less mastery than your opponent, '
            'you may banish a card from your hand or discard pile.'))
        for seeker_row, breaker_row in zip(
                output_scenarios(seeker, miniature_market()),
                output_scenarios(riftbreaker, miniature_market())):
            probability = seeker_row['assumed_lower_mastery_probability']
            self.assertEqual(seeker_row['draws'], 1)
            self.assertEqual(seeker_row['net_hand_cards_before_banish'], 0)
            self.assertEqual(seeker_row['mastery'], probability * 2)
            self.assertEqual(breaker_row['power'], 5)
            self.assertEqual(breaker_row['optional_banish_opportunities'], probability)
            self.assertEqual(breaker_row['net_hand_cards_before_banish'], -1)
            self.assertEqual(breaker_row['paid_fast_play_net_gems'], -3)

    def test_scout_sensitivity_multiplies_assumed_trigger_by_owned_card_row_model(self):
        pool = miniature_market()
        scout = {**pool['rift_scout'], 'rules_text': (
            'Gain 3 power.\nIf you have less mastery than your opponent, Warp 2.')}
        physical = [cid for cid, c in pool.items() for _ in range(c['quantity'])]
        physical.remove('rift_scout')
        availability = enumerated_probability(physical, {'rift_scout', 'cheap_mercenary'})
        scenarios = output_scenarios(scout, pool)
        self.assertEqual([r['assumed_lower_mastery_probability'] for r in scenarios], [0, .25, .5, .75, 1])
        for row in scenarios:
            self.assertAlmostEqual(row['warp_with_legal_target_model'],
                                   row['assumed_lower_mastery_probability'] * availability)
            self.assertEqual(row['power'], 3)
            self.assertEqual(row['gems'], 0)

    def test_singular_gem_is_parsed_and_absent_effects_do_not_become_resources(self):
        parsed = parsed_yield(card('fixture', rules_text=(
            'Gain 1 gem.\nIf you have less mastery than your opponent, gain 2 gems.')))
        self.assertEqual(parsed['base_gems'], 1)
        self.assertEqual(parsed['bonus_gems'], 2)
        self.assertEqual(parsed['base_draw'], 0)
        self.assertEqual(parsed['bonus_banish'], 0)
        self.assertEqual(parsed['bonus_warp_cap'], 0)


class AnalysisRevisionTests(unittest.TestCase):
    def test_reduced_cycle_records_removed_designs_without_active_analyses(self):
        catalog, current, previous = analysis_fixture()
        result = analyze(catalog, current, previous)
        self.assertEqual(set(result['cards']), {'horizon_seeker', 'riftbreaker', 'rift_scout'})
        self.assertEqual(set(result['removed_designs']), {'relief_courier', 'aegis_surveyor'})
        for removed in result['removed_designs'].values():
            self.assertEqual(removed['status'], 'removed')
            self.assertIn('previous', removed)
            self.assertNotIn('revised', removed)
        stocks = result['stock_models']
        self.assertEqual(stocks['previous']['fresh']['row_stock'] - stocks['revised']['fresh']['row_stock'], 4)
        seeker = result['cards']['horizon_seeker']
        self.assertEqual(seeker['previous']['yield']['bonus_mastery'], 2)
        self.assertEqual(seeker['revised']['yield']['bonus_mastery'], 1)
        scout = result['cards']['rift_scout']
        self.assertNotIn('paid_fast_play_net_gems', scout['previous']['scenarios'][0])
        self.assertIn('paid_fast_play_net_gems', scout['revised']['scenarios'][0])

    def test_current_manifest_exception_does_not_rewrite_previous_rules(self):
        catalog, current, previous = analysis_fixture()
        manifest = {'version': 'v5', 'market_rules': {'initial_shop': {
            'max_printed_cost': 5, 'allowed_card_ids': ['comet'],
        }}}
        result = analyze(catalog, current, previous, manifest=manifest)
        old = result['stock_models']['previous']['fresh']
        new = result['stock_models']['revised']['fresh']
        self.assertEqual(old['initial_shop_stock'], old['cost_at_most_five_stock'])
        self.assertEqual(new['initial_shop_stock'], new['cost_at_most_five_stock'] + 1)
        self.assertEqual(new['initial_shop_rules']['allowed_card_ids'], ['comet'])
        with self.assertRaisesRegex(ValueError, 'versions must match'):
            analyze(catalog, current, previous, manifest={**manifest, 'version': 'v4'})

    def test_no_manifest_reproduces_previous_five_design_analysis(self):
        catalog, _, previous = analysis_fixture()
        result = analyze(catalog, previous, previous)
        self.assertEqual(len(result['cards']), 5)
        self.assertEqual(result['removed_designs'], {})
        fresh = result['stock_models']['revised']['fresh']
        self.assertEqual(fresh['initial_shop_stock'], fresh['cost_at_most_five_stock'])


if __name__ == '__main__':
    unittest.main()
