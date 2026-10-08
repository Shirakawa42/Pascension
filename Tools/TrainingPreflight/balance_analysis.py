"""Balance-patch evidence from complete games, with exclusive finishing causes.

All associations are descriptive. No policy inputs or game rules are changed.
"""
from collections import Counter
import json
from pathlib import Path

CAUSES = {'mastery': 'Mastery / Infinity Shard', 'comet': 'Comet',
          'normal_damage': 'Normal damage', 'other_health_loss': 'Other health loss',
          'concession': 'Concession', 'unknown': 'Unattributed', 'draw': 'Draw'}
METRICS = {
    'turns': 'Turns started', 'gems_gained': 'Gems gained (including refunds)',
    'gems_paid_for_cards': 'Gems paid for acquisitions', 'gems_left_at_cleanup': 'Gems left at cleanup',
    'cleanups': 'Completed turn cleanups', 'damage_dealt': 'Normal post-shield damage dealt',
    'health_lost': 'Health lost (overkill excluded)', 'shields_prevented': 'Damage stopped by revealed shields',
    'healed': 'Health gained', 'cards_drawn': 'Cards drawn (including opening hands)',
    'hero_abilities': 'Hero ability uses', 'focuses': 'Focus uses',
    'starter_banishes': 'Distinct starters banished', 'fastplays': 'Fast-play acquisitions',
    'champions': 'Champion deployments', 'champion_activations': 'Champion activations',
    'champions_destroyed': 'Champions destroyed', 'monsters': 'Monsters defeated',
    'rerolls': 'Market rerolls', 'infinity_activations': 'Infinity mastery-30 effects',
}
MILESTONES = {**{f'mastery{n}_round': f'Mastery {n}' for n in (5, 10, 20, 30)},
              'relic_round': 'Normal relic pick', 'destiny_round': 'Normal destiny pick'}


def ratio(a, b):
    return a / b if b else None


def distribution(hist):
    count = sum(hist.values())
    def quantile(q):
        acc = 0
        for value, n in sorted(hist.items()):
            acc += n
            if acc >= q * count:
                return value
        return None
    return dict(count=count, mean=ratio(sum(k*v for k, v in hist.items()), count),
                p10=quantile(.1), median=quantile(.5), p90=quantile(.9),
                histogram=[dict(value=k, count=v) for k, v in sorted(hist.items())])


def group(hero, seat):
    return dict(hero=hero, seat=seat, games=0, wins=0, draws=0, victory=Counter(),
                rounds=Counter(), winning_health=Counter(), winning_mastery=Counter(),
                sums=Counter(), winning_sums=Counter(), milestones={}, cards={}, modes={}, destiny_sources={})


def outcome_row():
    return dict(games=0, wins=0, draws=0, count=0, round_sum=0)


def add_outcome(row, won, drawn, count=1, round_=0):
    row['games'] += 1; row['wins'] += won; row['draws'] += drawn
    row['count'] += count; row['round_sum'] += round_


def finish_outcome(row):
    row['score'] = ratio(row['wins'] + row['draws'] / 2, row['games'])
    row['mean_round'] = ratio(row.pop('round_sum'), row['games'])
    return row


def add_player(g, game, p):
    won = game['winner'] == p['seat']; drawn = game['winner'] == -1
    g['games'] += 1; g['wins'] += won; g['draws'] += drawn
    g['rounds'][game['round']] += 1
    if won:
        g['victory'][game['victory']] += 1
        g['winning_health'][p['health']] += 1
        g['winning_mastery'][p['mastery']] += 1
    for metric in METRICS:
        value = p[metric]
        if value < 0:
            raise ValueError('Negative balance counter: ' + metric)
        g['sums'][metric] += value
        if won: g['winning_sums'][metric] += value
    for key in MILESTONES:
        r = g['milestones'].setdefault(key, outcome_row())
        if p[key] > 0: add_outcome(r, won, drawn, round_=p[key])
    used = set(p['played']) | set(p['fast_acquired']) | set(p['activated']) | set(p['deployed'])
    ids = set(p['acquired']) | used | set(p['banished']) | set(p['rerolled'])
    for id_ in ids:
        r = g['cards'].setdefault(id_, dict(id=id_, acquired_games=0, acquired_wins=0,
            acquired_draws=0, acquired_count=0, acquired_used_games=0, used_games=0,
            used_wins=0, played_count=0, activated_count=0, deployed_count=0,
            fast_count=0, banished_count=0, rerolled_count=0, first_acquisition_round_sum=0))
        if id_ in p['acquired']:
            r['acquired_games'] += 1; r['acquired_wins'] += won; r['acquired_draws'] += drawn
            r['acquired_count'] += p['acquired'][id_]
            r['acquired_used_games'] += id_ in used
            r['first_acquisition_round_sum'] += p['first_acquired_round'][id_]
        r['used_games'] += id_ in used; r['used_wins'] += won and id_ in used
        for field, source in [('played_count','played'), ('activated_count','activated'),
                              ('deployed_count','deployed'), ('fast_count','fast_acquired'),
                              ('banished_count','banished'), ('rerolled_count','rerolled')]:
            r[field] += p[source].get(id_, 0)
    for id_, n in p['modes'].items():
        r = g['modes'].setdefault(id_, outcome_row()); add_outcome(r, won, drawn, n)
    for id_, source in [(p['destiny'], 'normal')] + [(x, 'reward') for x in p['extra_destinies']]:
        if id_:
            r = g['destiny_sources'].setdefault(id_+'|'+source, outcome_row())
            add_outcome(r, won, drawn, round_=p['destiny_round'] if source=='normal' else p['first_acquired_round'][id_])


def build(trace_path, snapshot):
    groups = {}; causes = Counter(); rounds = Counter(); outcomes = Counter(); seeds = set()
    comet = Counter(); latency = Counter()
    with Path(trace_path).open(encoding='utf-8-sig') as f:
        for line in f:
            game = json.loads(line)
            if game.get('schema') != 2: raise ValueError('Balance telemetry schema 2 is required')
            if game['seed'] in seeds: raise ValueError('Duplicate balance game')
            seeds.add(game['seed']); cause = game['victory']; winner = game['winner']
            if winner not in (-1, 0, 1) or len(game['players']) != 2: raise ValueError('Invalid outcome')
            if cause not in CAUSES or (cause=='draw') != (winner==-1): raise ValueError('Invalid victory cause')
            if cause=='unknown': raise ValueError('Unattributed victory: ' + game['seed'])
            causes[cause] += 1; rounds[game['round']] += 1; outcomes[winner] += 1
            comet['market_games'] += game['comet_market_seen']
            comet['finishing_wins'] += cause=='comet'
            for seat, p in enumerate(game['players']):
                if p['seat'] != seat: raise ValueError('Player seat mismatch')
                if p['infinity_activations'] and not p['mastery30_round']:
                    raise ValueError('Infinity activation without mastery-30 evidence')
                for hero, role in [(None,None), (p['hero'],None), (None,seat), (p['hero'],seat)]:
                    key = f'{hero or "all"}:{"all" if role is None else role}'
                    add_player(groups.setdefault(key, group(hero, role)), game, p)
                acquired = p['acquired'].get('comet', 0)>0; played = p['played'].get('comet', 0)>0
                comet['acquiring_players'] += acquired; comet['playing_players'] += played
                comet['acquirer_wins'] += acquired and winner==seat
                comet['acquired_unplayed_players'] += acquired and not played
                comet['finishing_without_winner_play'] += cause=='comet' and winner==seat and not played
                if acquired and played:
                    delay = p['first_played_round']['comet'] - p['first_acquired_round']['comet']
                    if delay >= 0: latency[delay] += 1
    totals = snapshot['totals']; n = len(seeds)
    if n != totals['resolved_games'] or [outcomes[x] for x in (0,1,-1)] != [totals[x] for x in ('seat0_wins','seat1_wins','draws')]:
        raise ValueError('Balance evidence does not match evaluation outcomes')
    if abs(distribution(rounds)['mean'] - totals['mean_rounds']) > 1e-6:
        raise ValueError('Balance rounds differ from evaluation')
    for g in groups.values():
        g['score'] = ratio(g['wins'] + g['draws']/2, g['games'])
        for key in ('rounds','winning_health','winning_mastery'): g[key] = distribution(g[key])
        g['metrics'] = [dict(id=k, name=label, total=g['sums'][k],
            per_player_game=ratio(g['sums'][k],g['games']),
            per_turn=ratio(g['sums'][k],g['sums']['turns']),
            in_wins=ratio(g['winning_sums'][k],g['wins'])) for k,label in METRICS.items()]
        del g['sums']; del g['winning_sums']
        g['milestones'] = [dict(id=k, name=MILESTONES[k], reach_rate=ratio(r['games'],g['games']), **finish_outcome(r)) for k,r in g['milestones'].items()]
        for r in g['cards'].values():
            r['acquired_score'] = ratio(r['acquired_wins']+r['acquired_draws']/2,r['acquired_games'])
            r['use_rate'] = ratio(r['acquired_used_games'],r['acquired_games'])
            r['mean_first_acquisition_round'] = ratio(r.pop('first_acquisition_round_sum'),r['acquired_games'])
        g['cards'] = sorted(g['cards'].values(), key=lambda r:(-r['acquired_games'],r['id']))
        g['modes'] = [dict(id=k, **finish_outcome(v)) for k,v in sorted(g['modes'].items())]
        g['destiny_sources'] = [dict(id=k.split('|')[0],source=k.split('|')[1],**finish_outcome(v)) for k,v in sorted(g['destiny_sources'].items())]
    return dict(schema='shards-balance-analysis-v1', state='ready', games=n,
        victory=[dict(id=k,name=v,count=causes[k],share=ratio(causes[k],n),win_share=ratio(causes[k],n-outcomes[-1]) if k!='draw' else None) for k,v in CAUSES.items()],
        rounds=distribution(rounds), groups=groups, comet=dict(**comet,acquisition_to_play_rounds=distribution(latency)),
        definitions=[
            'Victory causes partition complete games. Mastery means lethal damage after Infinity Shard’s +9994 mastery-30 power effect, not merely reaching 30 mastery. Comet means the resolving DestroyOpponent effect (-1,000,000 health), including a copied effect. Signatures match this frozen content version.',
            'Other health loss includes direct health-loss effects and monster damage; it is not assigned normal combat damage. Concessions are reported separately. Draws are never counted as wins.',
            'Hero and seat filters count player outcomes: 100,000 outcomes from 50,000 games for All heroes / Both seats. Victory totals count each winner once. Score gives a draw half a win.',
            'Acquisition includes effect grants and temporary fast-plays. Observed use means a play from hand, fast-play, deployment or activation of that definition in the same player-game. Copies are not tracked individually. Passive destiny effects do not emit use events; no observed activation does not mean a passive destiny was unused.',
            'Card, milestone and destiny scores are conditional associations, not causal strength. Timing, hero, opponent, game length and the policy influence selection. Missing or small samples do not establish weakness.',
            'Normal damage totals exclude turns with Infinity’s special power. Health loss excludes overkill. Shield totals cover revealed shield prevention only, not passive reduction. Cleanup gem totals exclude unfinished final turns. Per-turn values divide by turns started, including incomplete final turns.',
            'Mode counts describe resolved card mode choices and Volos choices. They are not opportunity or pick rates. Reward destiny rows use first acquisition round; normal picks use their actual normal-pick round.',
            'The Comet funnel counts games for market appearance and player-games for acquisition/play. Acquisition-to-play latency is in rounds for players who both acquired and played Comet, not an unconditional time-to-use estimate.'
        ])
