#!/usr/bin/env python3
"""Offline, deterministic pacing audit of completed StrategyTrace schema-2 games.
No inference, simulations, gameplay, or training. Requires only numpy + stdlib.
"""
import argparse, collections, hashlib, json, math
from pathlib import Path
import numpy as np


def interval(w,n):
    if not n:return None
    p=w/n;z=1.959963984540054;d=1+z*z/n
    m=(p+z*z/(2*n))/d;s=z*math.sqrt(p*(1-p)/n+z*z/(4*n*n))/d
    return dict(n=int(n),wins=int(w),rate=p,ci95=[m-s,m+s])


def summarize(a):
    if not a:return None
    a=np.asarray(a,dtype=float)
    return dict(n=len(a),mean=float(a.mean()),median=float(np.median(a)),p10=float(np.quantile(a,.1)),p90=float(np.quantile(a,.9)),min=float(a.min()),max=float(a.max()))


def regression(games, feature, label):
    """Seat-0 game outcome with ordered matchup fixed effects, no duplicate players.
    Sandwich asymptotic CIs; no repeated-game dependence. 0.000001 ridge only for
    numerical stabilization. No outcome-derived final-state covariates.
    """
    if not games:return None
    pairs=sorted({tuple(p['hero'] for p in g['players']) for g in games})
    v=np.array([feature(g) for g in games],float)
    if v.ndim==1:v=v[:,None]
    x=np.column_stack([np.ones(len(games)),*[np.array([tuple(p['hero'] for p in g['players'])==pair for g in games],float) for pair in pairs[1:]],v])
    y=np.array([g['winner']==0 for g in games],float)
    if len(games)<max(200,15*x.shape[1]) or min(y.sum(),len(y)-y.sum())<30:
        return dict(n=len(games),status='suppressed: insufficient events per parameter for stable adjusted estimate')
    b=np.zeros(x.shape[1]);pen=np.eye(len(b))*1e-6
    for i in range(60):
        p=1/(1+np.exp(-np.clip(x@b,-35,35)));w=np.maximum(p*(1-p),1e-9)
        h=(x.T*w)@x+pen;step=np.linalg.solve(h,x.T@(y-p)-pen@b);b+=step
        if np.max(abs(step))<1e-9:break
    p=1/(1+np.exp(-np.clip(x@b,-35,35)));w=np.maximum(p*(1-p),1e-9)
    bread=np.linalg.pinv((x.T*w)@x+pen)
    meat=(x.T*((y-p)**2))@x
    cov=bread@meat@bread
    if np.max(abs(b))>10:
        return dict(n=len(games),status='suppressed: separation or extreme fitted coefficients')
    out=[]
    labels=label if isinstance(label,list) else [label]
    for j,name in enumerate(labels):
        k=len(b)-v.shape[1]+j;se=math.sqrt(max(0,cov[k,k]));coef=float(b[k]);lo=coef-1.96*se;hi=coef+1.96*se
        out.append(dict(feature=name,log_odds=coef,se=se,odds_ratio=math.exp(coef),ci95=[math.exp(max(-700,lo)),math.exp(min(700,hi))]))
    return dict(n=len(games),controls='ordered hero matchup (20 possible), seat-0 outcome; one record/game',converged=i<59,estimates=out)


def leader_standardized(games, leader):
    """Standardize earlier-player win rate to equal hero matchup and starting seat.
    No causal interpretation. Sandwich uncertainty by independent game.
    """
    pairs=sorted({(g['players'][leader(g)]['hero'],g['players'][1-leader(g)]['hero']) for g in games})
    def row(pair, seat):return [1,*[int(pair==p) for p in pairs[1:]],seat]
    x=np.array([row((g['players'][leader(g)]['hero'],g['players'][1-leader(g)]['hero']),leader(g)) for g in games],float)
    y=np.array([g['winner']==leader(g) for g in games],float)
    if len(y)<max(200,15*x.shape[1]) or min(y.sum(),len(y)-y.sum())<30:return dict(status='suppressed: sparse sample',n=len(y))
    b=np.zeros(x.shape[1]);pen=np.eye(len(b))*1e-6
    for i in range(60):
        p=1/(1+np.exp(-np.clip(x@b,-35,35)));w=np.maximum(p*(1-p),1e-9)
        step=np.linalg.solve((x.T*w)@x+pen,x.T@(y-p)-pen@b);b+=step
        if np.max(abs(step))<1e-9:break
    if np.max(abs(b))>10:return dict(status='suppressed: separation',n=len(y))
    p=1/(1+np.exp(-np.clip(x@b,-35,35)));w=np.maximum(p*(1-p),1e-9)
    bread=np.linalg.pinv((x.T*w)@x+pen);cov=bread@((x.T*((y-p)**2))@x)@bread
    xx=np.array([row(pair,seat) for pair in pairs for seat in (0,1)],float)
    pp=1/(1+np.exp(-np.clip(xx@b,-35,35)));grad=(xx*(pp*(1-pp))[:,None]).mean(axis=0)
    mean=float(pp.mean());se=math.sqrt(float(grad@cov@grad))
    return dict(n=len(y),rate=mean,ci95=[max(0,mean-1.96*se),min(1,mean+1.96*se)],standardization='equal weight to each ordered earlier-hero/opponent matchup and each earlier-player starting seat',converged=i<59)


def main():
    ap=argparse.ArgumentParser();ap.add_argument('--source',required=True);ap.add_argument('--out',required=True);a=ap.parse_args()
    src=Path(a.source);raw=src.read_bytes();games=[json.loads(s) for s in raw.decode('utf-8-sig').splitlines() if s.strip()]
    assert len({g['seed'] for g in games})==len(games)
    assert all(g['schema']==2 and g['winner'] in (0,1) and len(g['players'])==2 for g in games)
    rows=[dict(p,win=int(g['winner']==p['seat']),round=g['round'],victory=g['victory'],opponent=g['players'][1-p['seat']]['hero']) for g in games for p in g['players']]
    for r in rows:r['deck_size']=sum(r['collection'].values())
    heroes=sorted({p['hero'] for p in rows})
    out=dict(source=str(src),sha256=hashlib.sha256(raw).hexdigest(),games=len(games),players=len(rows),ordered_matchups=dict(collections.Counter('|'.join(p['hero'] for p in g['players']) for g in games)),rounds=summarize([g['round'] for g in games]),round_histogram=dict(sorted(collections.Counter(g['round'] for g in games).items())),seat0=interval(sum(g['winner']==0 for g in games),len(games)))
    out['victories']={v:dict(games=len(gs),fraction=len(gs)/len(games),rounds=summarize([g['round'] for g in gs]),seat0=interval(sum(g['winner']==0 for g in gs),len(gs))) for v in sorted({g['victory'] for g in games}) if (gs:=[g for g in games if g['victory']==v])}
    out['heroes']={}
    for hero in heroes:
        hr=[r for r in rows if r['hero']==hero]
        h=dict(win=interval(sum(r['win'] for r in hr),len(hr)),rounds=summarize([r['round'] for r in hr]),seat={s:interval(sum(r['win'] for r in hr if r['seat']==s),sum(r['seat']==s for r in hr)) for s in (0,1)},wins_by_cause=dict(collections.Counter(r['victory'] for r in hr if r['win'])),victory_rounds={v:summarize([r['round'] for r in hr if r['win'] and r['victory']==v]) for v in out['victories']})
        h['measures']={f:dict(all=summarize([r[f] for r in hr]),won=summarize([r[f] for r in hr if r['win']]),lost=summarize([r[f] for r in hr if not r['win']])) for f in ['mastery','turns','starter_banishes','fastplays','champions','champion_activations','monsters','rerolls','healed','gems_gained','gems_paid_for_cards','gems_left_at_cleanup','damage_dealt','health_lost','shields_prevented','hero_abilities','focuses','cards_drawn','champions_destroyed','deck_size']}
        h['mastery']={t:dict(reached=sum(r[f'mastery{t}_round']>0 for r in hr),reached_timing=summarize([r[f'mastery{t}_round'] for r in hr if r[f'mastery{t}_round']])) for t in (5,10,20,30)}
        out['heroes'][hero]=h
    out['thresholds']={}
    for t in (5,10,20,30):
        key=f'mastery{t}_round';cat=collections.defaultdict(list)
        for g in games:
            aa,bb=[p[key] for p in g['players']]
            if aa and bb:
                if aa==bb:cat['both_same_round'].append((g,None))
                else:cat['both_different_round'].append((g,0 if aa<bb else 1))
            elif aa or bb:cat['only_one_reached'].append((g,0 if aa else 1))
            else:cat['neither_reached'].append((g,None))
        result={}
        for c,xs in cat.items():
            result[c]=dict(games=len(xs),rounds=summarize([g['round'] for g,_ in xs]))
            if xs[0][1] is not None:result[c]['leader_win']=interval(sum(g['winner']==leader for g,leader in xs),len(xs))
        distinct=[g for g,_ in cat['both_different_round']]
        if distinct:
            result['both_earlier_standardized']=leader_standardized(distinct,lambda g:0 if g['players'][0][key]<g['players'][1][key] else 1)
            result['both_earlier_adjusted']=regression(distinct,lambda g:[int(g['players'][0][key]<g['players'][1][key])],'seat0 reached in earlier round (vs seat1)')
            result['earlier_by_hero']={hero:interval(sum(g['winner']==s for g,s in cat['both_different_round'] if g['players'][s]['hero']==hero),sum(g['players'][s]['hero']==hero for g,s in cat['both_different_round'])) for hero in heroes}
            result['both_gap']={gap:interval(sum(g['winner']==s for g,s in cat['both_different_round'] if min(abs(g['players'][0][key]-g['players'][1][key]),3)==gap),sum(min(abs(g['players'][0][key]-g['players'][1][key]),3)==gap for g,s in cat['both_different_round'])) for gap in (1,2,3)}
            result['both_by_victory']={v:interval(sum(g['winner']==s for g,s in cat['both_different_round'] if g['victory']==v),sum(g['victory']==v for g,s in cat['both_different_round'])) for v in out['victories']}
        result['earlier_starting_seat']={seat:interval(sum(g['winner']==leader for g,leader in cat['both_different_round'] if leader==seat),sum(leader==seat for g,leader in cat['both_different_round'])) for seat in (0,1)}
        assert sum(len(xs) for xs in cat.values())==len(games)
        out['thresholds'][t]=result
    # Landmark estimates: only games still alive after the named round; determine
    # threshold attainment by that round, never from future rounds. These still
    # measure associations, not causal intervention effects.
    out['landmarks']={}
    for r in (3,4,5,6,7,8):
        live=[g for g in games if g['round']>r]
        result={}
        for t in (5,10,20):
            key=f'mastery{t}_round'
            reached=lambda p:bool(p[key] and p[key]<=r)
            diff=[g for g in live if reached(g['players'][0])!=reached(g['players'][1])]
            counts=collections.Counter(sum(reached(p) for p in g['players']) for g in live)
            item=dict(alive_after_round=len(live),neither=counts[0],one=counts[1],both=counts[2],one_reached_win=interval(sum(g['winner']==int(not reached(g['players'][0])) for g in diff),len(diff)))
            if len(diff)>=30:
                # Fit only discordant players: coefficient OR = reached versus not.
                item['standardized']=leader_standardized(diff,lambda g:0 if reached(g['players'][0]) else 1)
                item['adjusted']=regression(diff,lambda g:[int(reached(g['players'][0]))],f'seat0 has reached M{t} by round {r} (vs seat1)')
            result[t]=item
        out['landmarks'][r]=result
    # Same-round groups are deliberately never assigned a chronological leader.
    out['relic_delays']={}
    for hero in heroes+['all']:
        hr=rows if hero=='all' else [r for r in rows if r['hero']==hero]
        got=[r for r in hr if r['relic']];reach=[r for r in hr if r['mastery10_round']]
        played=[r for r in got if r['relic'] in r['first_played_round']]
        out['relic_delays'][hero]=dict(players=len(hr),m10=len(reach),selected=len(got),selected_rate_given_m10=len(got)/len(reach),selection_delay=dict(sorted(collections.Counter(r['relic_round']-r['mastery10_round'] for r in got).items())),selected_never_played=len(got)-len(played),firstplay_delay=dict(sorted(collections.Counter(r['first_played_round'][r['relic']]-r['relic_round'] for r in played).items())),selected_never_played_win=interval(sum(r['win'] for r in got if r not in played),len(got)-len(played)))
    out['destiny_delays']={hero:dict(players=len(hr),m5=len(reach),selected=len(got),delay=dict(sorted(collections.Counter(r['destiny_round']-r['mastery5_round'] for r in got).items())),never_selected=len(reach)-len(got)) for hero in heroes+['all'] if (hr:=rows if hero=='all' else [r for r in rows if r['hero']==hero]) and (reach:=[r for r in hr if r['mastery5_round']]) and (got:=[r for r in hr if r['destiny']])}
    out['m30']={hero:dict(reached=len(hr),win=interval(sum(r['win'] for r in hr),len(hr)),infinity_activations=sum(r['infinity_activations'] for r in hr),losses=len([r for r in hr if not r['win']]),loss_causes=dict(collections.Counter(r['victory'] for r in hr if not r['win']))) for hero in heroes+['all'] if (hr:=[r for r in rows if r['mastery30_round'] and (hero=='all' or r['hero']==hero)])}
    out['mastery_transitions']={hero:{'M10_to_M30':dict(sorted(collections.Counter(r['mastery30_round']-r['mastery10_round'] for r in rr).items())),'M20_to_M30':dict(sorted(collections.Counter(r['mastery30_round']-r['mastery20_round'] for r in rr).items()))} for hero in heroes if (rr:=[r for r in rows if r['hero']==hero and r['mastery30_round']])}
    out['tempo_bands']={}
    for name,pred in [('rounds5_8',lambda g:g['round']<=8),('rounds9_11',lambda g:9<=g['round']<=11),('rounds12plus',lambda g:g['round']>=12)]:
        gg=[g for g in games if pred(g)];rr=[r for r in rows if pred(r)]
        out['tempo_bands'][name]=dict(games=len(gg),seat0=interval(sum(g['winner']==0 for g in gg),len(gg)),victories=dict(collections.Counter(g['victory'] for g in gg)),hero_wins={h:interval(sum(r['win'] for r in rr if r['hero']==h),sum(r['hero']==h for r in rr)) for h in heroes})
    out['extra_turns']=dict(games_with_turn_count_exceeding_round=sum(any(p['turns']>g['round'] for p in g['players']) for g in games),note='This is a sufficient but not necessary flag for an extra turn: seat1 may have an extra turn yet still equal round if game ends on seat0 turn.')
    # Matchup-adjusted final style outcomes are not causal. Use equal-per-round
    # metrics to avoid merely rewarding more turns; outcome still causes exposure.
    out['final_style_associations']={}
    for f in ['starter_banishes','fastplays','champion_activations','healed','rerolls','cards_drawn','focuses','deck_size']:
        feature=lambda g: [(g['players'][0][f]/max(1,g['players'][0]['turns']) if f!='deck_size' else sum(g['players'][0]['collection'].values()))-(g['players'][1][f]/max(1,g['players'][1]['turns']) if f!='deck_size' else sum(g['players'][1]['collection'].values()))]
        out['final_style_associations'][f]=regression(games,feature,f'seat0 minus seat1 {f}'+(' per turn' if f!='deck_size' else ''))
    path=Path(a.out);path.mkdir(parents=True,exist_ok=True)
    (path/'pacing_results.json').write_text(json.dumps(out,indent=2)+'\n')
    print(json.dumps(dict(games=out['games'],rounds=out['rounds'],seat0=out['seat0'],thresholds=out['thresholds'],relic_delays=out['relic_delays']),indent=2))

if __name__=='__main__':main()
