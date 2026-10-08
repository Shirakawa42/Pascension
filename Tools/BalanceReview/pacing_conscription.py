#!/usr/bin/env python3
"""Offline follow-up: whether Conscription availability is just first-M5 imbalance."""
import argparse, collections, json
from pathlib import Path
from pacing_analysis import interval
ap=argparse.ArgumentParser();ap.add_argument('--source',required=True);ap.add_argument('--openings',required=True);ap.add_argument('--out',required=True);args=ap.parse_args()
games=[json.loads(s) for s in Path(args.source).read_text(encoding='utf-8-sig').splitlines()]
openings={x['seed']:x for x in map(json.loads,Path(args.openings).read_text().splitlines())}
card='unconditional_conscription'
groups={'available':[g for g in games if card in openings[g['seed']]['destinies']], 'absent':[g for g in games if card not in openings[g['seed']]['destinies']]}
def first(g):
    a,b=[p['mastery5_round'] for p in g['players']]
    return 'same_round' if a==b else 'seat0_earlier' if a and (not b or a<b) else 'seat1_earlier'
def rate(gs):return interval(sum(g['winner']==0 for g in gs),len(gs))
result={k:{'seat0':rate(gs),'M5_order':dict(collections.Counter(first(g) for g in gs)),'M5_strata':{category:rate([g for g in gs if first(g)==category]) for category in ['same_round','seat0_earlier','seat1_earlier']}} for k,gs in groups.items()}
result['selection']={}
for seat in (0,1):
    gs=[g for g in groups['available'] if g['players'][seat]['destiny']==card]
    diffs=collections.Counter(g['players'][seat]['destiny_round']-g['players'][1-seat]['destiny_round'] for g in gs if g['players'][1-seat]['destiny'])
    result['selection'][seat]={'n':len(gs),'chooser_win':interval(sum(g['winner']==seat for g in gs),len(gs)),'mean_selected_round':sum(g['players'][seat]['destiny_round'] for g in gs)/len(gs),'mean_activations':sum(g['players'][seat]['activated'].get(card,0) for g in gs)/len(gs),'claim_round_difference':dict(diffs),'opponent_destiny':dict(collections.Counter(g['players'][1-seat]['destiny'] or 'none' for g in gs))}
result['neither_selected_seat0']=rate([g for g in groups['available'] if all(p['destiny']!=card for p in g['players'])])
Path(args.out).write_text(json.dumps(result,indent=2)+'\n')
