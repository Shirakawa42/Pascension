"""Observed strategy combinations and action patterns from complete frozen games."""
from collections import defaultdict
import hashlib
import json
from pathlib import Path

PATTERNS = {
    'mastery30': ('Mastery 30 reached', 'Reached 30 mastery during the game.'),
    'mastery_ramp': ('Early mastery growth', 'Reached mastery 20 by round 8.'),
    'starter_thinning': ('Starter thinning', 'Banished at least 5 distinct starter cards.'),
    'champion_engine': ('Champion engine', 'Activated champions at least 8 times.'),
    'fastplay_pressure': ('Fast-play pressure', 'Made at least 5 fast-play acquisitions, including effect grants.'),
    'monster_rewards': ('Monster reward engine', 'Defeated at least 3 Ingeminex.'),
    'healing': ('Sustained healing', 'Recovered at least 20 health through positive health-change events.'),
    'market_control': ('Repeated market rerolls', 'Rerolled the market at least 5 times.'),
    'power_burst': ('High-power turns', 'Held at least 30 power at one point in a turn.'),
}

def patterns(player, catalog):
    predicates = {
        'mastery30': player['mastery30_round'] > 0,
        'mastery_ramp': 0 < player['mastery20_round'] <= 8,
        'starter_thinning': player['starter_banishes'] >= 5,
        'champion_engine': player['champion_activations'] >= 8,
        'fastplay_pressure': player['fastplays'] >= 5,
        'monster_rewards': player['monsters'] >= 3,
        'healing': player['healed'] >= 20,
        'market_control': player['rerolls'] >= 5,
        'power_burst': player['peak_power'] >= 30,
    }
    result=[(key, *PATTERNS[key]) for key,match in predicates.items() if match]
    factions=defaultdict(int)
    for key,count in player['collection'].items():
        definition=catalog[key]
        if definition['type'] in ('Ally','Champion') and definition['faction']!='None':
            factions[definition['faction']]+=count
    total=sum(factions.values())
    for faction,count in factions.items():
        if count>=5 and count*2>=total:
            result.append(('faction_'+faction,faction+' collection',
                           'Ended with at least 5 '+faction+' allies/champions, comprising at least half of the printed-faction allies/champions owned.'))
    return result

def build(trace_path, snapshot, metadata):
    catalog={x['id']:x for x in metadata['cards']+metadata['heroes']}
    groups={}; seeds=set(); outcomes=[0,0,0]; round_sum=0
    def add(key,category,name,description,hero,images,game,seat,player):
        if key not in groups:
            groups[key]=dict(id=key,category=category,name=name,description=description,hero_id=hero,
                             image_ids=images,games=0,wins=0,draws=0,losses=0,round_sum=0,winning_round_sum=0,
                             mastery_sum=0,winning_mastery_sum=0,seat0_games=0,seat0_wins=0,seat1_games=0,seat1_wins=0,
                             components=defaultdict(lambda:[0,0]))
        r=groups[key];won=game['winner']==seat;draw=game['winner']==-1
        r['games']+=1;r['wins']+=won;r['draws']+=draw;r['losses']+=not(won or draw)
        r['round_sum']+=game['round'];r['mastery_sum']+=player['mastery']
        r['winning_round_sum']+=game['round']*won;r['winning_mastery_sum']+=player['mastery']*won
        r[f'seat{seat}_games']+=1;r[f'seat{seat}_wins']+=won
        for card in player['collection']:
            if catalog[card]['type']=='Starter':continue
            r['components'][card][0]+=1;r['components'][card][1]+=won
    with Path(trace_path).open(encoding='utf-8-sig') as f:
        for line in f:
            game=json.loads(line)
            if game['seed'] in seeds:raise ValueError('Duplicate strategy game')
            seeds.add(game['seed']);winner=game['winner']
            if winner not in (-1,0,1) or len(game['players'])!=2:raise ValueError('Invalid terminal result')
            outcomes[winner if winner>=0 else 2]+=1;round_sum+=game['round']
            for seat,p in enumerate(game['players']):
                if p['seat']!=seat:raise ValueError('Strategy seat mismatch')
                hero=p['hero'];relic=p['relic'];destiny=p['destiny']
                images=[x for x in (hero,relic,destiny) if x]
                name=' + '.join(catalog[x]['name'] if x else label for x,label in
                                [(hero,''),(relic,'No normal relic pick'),(destiny,'No normal destiny pick')])
                add('setup:'+hero+':'+str(relic)+':'+str(destiny),'builds',name,
                    'Hero plus the relic and destiny selected through the normal mastery-unlocked actions. Extra reward acquisitions are excluded from this label.',hero,images,game,seat,p)
                for key,name,description in patterns(p,catalog):
                    add('pattern:'+key,'patterns',name,description,None,[],game,seat,p)
                    add('hero_pattern:'+hero+':'+key,'hero_patterns',catalog[hero]['name']+' · '+name,description,hero,[hero],game,seat,p)
    totals=snapshot['totals'];expected=totals['resolved_games']
    if len(seeds)!=expected or outcomes!=[totals['seat0_wins'],totals['seat1_wins'],totals['draws']]:
        raise ValueError('Strategy records do not match this evaluation snapshot')
    if abs(round_sum/expected-totals['mean_rounds'])>1e-6:raise ValueError('Strategy rounds differ from evaluation')
    rows=[]
    for r in groups.values():
        r['score']=(r['wins']+r['draws']/2)/r['games']
        r['win_share']=r['wins']/max(1,expected-totals['draws'])
        r['mean_rounds']=r.pop('round_sum')/r['games']
        r['mean_winning_rounds']=r.pop('winning_round_sum')/r['wins'] if r['wins'] else None
        r['mean_mastery']=r.pop('mastery_sum')/r['games']
        r['mean_winning_mastery']=r.pop('winning_mastery_sum')/r['wins'] if r['wins'] else None
        r['supporting_cards']=[dict(id=card,games=v[0],wins=v[1],winning_presence=v[1]/r['wins'] if r['wins'] else None)
                               for card,v in sorted(r.pop('components').items(),key=lambda x:(-x[1][1],x[0]))[:8]]
        rows.append(r)
    rows.sort(key=lambda r:(-r['wins'],-r['games'],r['id']))
    return dict(schema='shards-observed-strategies-v1',state='ready',games=expected,
                trace_sha256=hashlib.sha256(Path(trace_path).read_bytes()).hexdigest(),
                label='Winning strategies observed in the frozen evaluation',
                notes=['Ranked by number of wins, not only win rate. Every row also includes losing and drawn games.',
                       'Builds are normal hero/relic/destiny selections; bonus destiny rewards are not mistaken for normal picks.',
                       'Patterns use explicit thresholds over observed actions and can overlap; their win shares do not add to 100%.',
                       'These describe achieved play, not causal effects or intended plans. Longer games offer more opportunities to meet thresholds.',
                       'Supporting cards are the most frequent permanent non-starter cards in winning final collections, not proven causes of victory.'],
                rows=rows)
