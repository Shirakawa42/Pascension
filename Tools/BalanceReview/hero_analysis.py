#!/usr/bin/env python3
"""Retrospective hero/relic analysis. Reads existing traces only; never simulates.

One hero occurrence per game: distinct-hero schedule is asserted. Confidence
intervals for individual hero/relic groups therefore use independent games.
The hero regression has exactly one row per game, not two player outcomes.
Within-hero relic models adjust measured pre-choice timing only. They cannot
remove deck-state, available destiny, or strategy selection confounding.
"""
import argparse, collections, hashlib, json, math
from pathlib import Path
import numpy as np

HEROES = ['decima', 'tetra', 'volos', 'kosynwu', 'rez']


def wilson(w, n):
    if not n: return None
    z=1.959963984540054; p=w/n; d=1+z*z/n
    c=(p+z*z/(2*n))/d; h=z*math.sqrt(p*(1-p)/n+z*z/(4*n*n))/d
    return [c-h,c+h]


def summary(rows):
    n=len(rows); w=sum(r['win'] for r in rows)
    return {'n':n,'wins':w,'win_rate':w/n if n else None,'ci95':wilson(w,n)}


def mean(rows,key,nonzero=False):
    a=[r['p'][key] for r in rows if not nonzero or r['p'][key]>0]
    return float(np.mean(a)) if a else None


def logit(x,y,ridge=1e-6):
    x=np.asarray(x,dtype=float); y=np.asarray(y,dtype=float); b=np.zeros(x.shape[1]); reg=np.eye(x.shape[1])*ridge; reg[0,0]=0
    for _ in range(100):
        p=1/(1+np.exp(-np.clip(x@b,-35,35))); w=p*(1-p)
        h=x.T@(w[:,None]*x)+reg; step=np.linalg.solve(h,x.T@(y-p)-reg@b); b+=step
        if np.max(abs(step))<1e-9: break
    p=1/(1+np.exp(-np.clip(x@b,-35,35))); w=p*(1-p)
    bread=np.linalg.inv(x.T@(w[:,None]*x)+reg)
    score=x*(y-p)[:,None]; cov=bread@(score.T@score)@bread
    return b,cov


def model_terms(names,b,cov):
    out={}
    for i,name in enumerate(names):
        se=math.sqrt(max(0,cov[i,i])); out[name]={'log_odds':float(b[i]),'se':se,'odds_ratio':math.exp(float(b[i])), 'odds_ratio_ci95':[math.exp(float(b[i])-1.96*se),math.exp(float(b[i])+1.96*se)]}
    return out


def analyse(source,catalog,openings=None):
    games=[json.loads(l) for l in source.read_text(encoding='utf-8-sig').splitlines() if l.strip()]
    assert len(set(g['seed'] for g in games))==len(games)
    assert all(g['winner'] in (0,1) and len(g['players'])==2 and g['players'][0]['hero']!=g['players'][1]['hero'] for g in games)
    cards={c['id']:c for c in catalog['cards']}
    rows=[]
    for i,g in enumerate(games):
        for p in g['players']:
            rows.append({'game':i,'g':g,'p':p,'o':g['players'][1-p['seat']],'win':int(p['seat']==g['winner'])})
    def filt(**kw): return [r for r in rows if all(r['p'].get(k)==v for k,v in kw.items())]
    out={'source':str(source),'sha256':hashlib.sha256(source.read_bytes()).hexdigest(),'games':len(games),'ordered_matchup_counts':dict(collections.Counter(' / '.join(p['hero'] for p in g['players']) for g in games)), 'seat0':summary([r for r in rows if r['p']['seat']==0]), 'heroes':{},'relics':{},'destinies':{},'hero_destinies':{},'synergies':{}}
    # Additive paired hero model (one observation per game).
    names=['seat0_advantage']+HEROES[:-1]
    x=[[1]+[int(g['players'][0]['hero']==h)-int(g['players'][1]['hero']==h) for h in HEROES[:-1]] for g in games]
    b,cov=logit(x,[int(g['winner']==0) for g in games]); out['paired_hero_model']={'reference_hero':'rez','terms':model_terms(names,b,cov)}
    for hero in HEROES:
        rr=filt(hero=hero)
        h=summary(rr); h['seats']={str(s):summary([r for r in rr if r['p']['seat']==s]) for s in (0,1)}
        h['opponents']={o:summary([r for r in rr if r['o']['hero']==o]) for o in HEROES if o!=hero}
        h['all_relics_never_played_either_side']=summary([r for r in rr if not any(cards[k]['type']=='Relic' for player in (r['p'],r['o']) for k,n in player['played'].items() if n)])
        h['no_relics_acquired_either_side']=summary([r for r in rr if not any(cards[k]['type']=='Relic' for player in (r['p'],r['o']) for k in player['acquired'])])
        h['means']={k:mean(rr,k) for k in ['turns','hero_abilities','starter_banishes','cards_drawn','fastplays','rerolls','healed','gems_gained','gems_paid_for_cards','champions','champion_activations','focuses','mastery']}
        h['mastery_round_means_reachers_only']={str(t):mean(rr,f'mastery{t}_round',True) for t in [5,10,20,30]}
        h['mastery_reached']={str(t):sum(r['p'][f'mastery{t}_round']>0 for r in rr) for t in [5,10,20,30]}
        h['victory_rates']={v:sum(r['win'] and r['g']['victory']==v for r in rr)/len(rr) for v in sorted({g['victory'] for g in games})}
        h['wins_by_victory']=dict(collections.Counter(r['g']['victory'] for r in rr if r['win']))
        h['no_relic_selected']=summary([r for r in rr if not r['p']['relic']])
        h['no_relic_acquired']=summary([r for r in rr if not any(cards[k]['type']=='Relic' for k in r['p']['acquired'])])
        h['any_relic_acquired']=summary([r for r in rr if any(cards[k]['type']=='Relic' for k in r['p']['acquired'])])
        h['normal_relic_selected']=summary([r for r in rr if r['p']['relic']])
        h['both_no_relic_selected']=summary([r for r in rr if not r['p']['relic'] and not r['o']['relic']])
        h['both_reached_m10']=summary([r for r in rr if r['p']['mastery10_round'] and r['o']['mastery10_round']])
        h['no_selected_relic_ever_played_either_side']=summary([r for r in rr if not r['p']['played'].get(r['p']['relic'],0) and not r['o']['played'].get(r['o']['relic'],0)])
        h['abilities_eligible_round_upper_bound']=sum(max(0,r['p']['turns']-r['p']['mastery5_round']+1) for r in rr if r['p']['mastery5_round'])
        h['modes']=dict(sum((collections.Counter(r['p']['modes']) for r in rr),collections.Counter()))
        out['heroes'][hero]=h
        relics=sorted({r['p']['relic'] for r in rr if r['p']['relic']})
        for relic in relics:
            rs=[r for r in rr if r['p']['relic']==relic]; z=summary(rs)
            z.update({'hero':hero,'game_round_mean':float(np.mean([r['g']['round'] for r in rs])),'m20_reachers':sum(r['p']['mastery20_round']>0 for r in rs),'wins_by_victory':dict(collections.Counter(r['g']['victory'] for r in rs if r['win'])),'only_this_relic_acquired':summary([r for r in rs if sum(cards[k]['type']=='Relic' for k in r['p']['acquired'])==1]),'another_relic_acquired_strictly_before_normal_choice':sum(any(cards[k]['type']=='Relic' and k!=relic and r['p']['first_acquired_round'][k]<r['p']['relic_round'] for k in r['p']['acquired']) for r in rs),'any_additional_relic_acquired':sum(any(cards[k]['type']=='Relic' and k!=relic for k in r['p']['acquired']) for r in rs),'hero_modes':dict(sum((collections.Counter({k:v for k,v in r['p']['modes'].items() if k.startswith('volos|')}) for r in rs),collections.Counter())),'name':cards[relic]['name'],'rules_text':cards[relic]['rules_text'],'share_of_eligible_choosers':len(rs)/sum(bool(r['p']['relic']) for r in rr),'selected_round_mean':mean(rs,'relic_round'),'m10_round_mean':mean(rs,'mastery10_round'),'m5_round_mean':mean(rs,'mastery5_round'),'first_played_round_mean':float(np.mean([r['p']['first_played_round'][relic] for r in rs if relic in r['p']['first_played_round']])) if any(relic in r['p']['first_played_round'] for r in rs) else None,'played_at_least_once':summary([r for r in rs if r['p']['played'].get(relic,0)]),'selected_never_played':summary([r for r in rs if not r['p']['played'].get(relic,0)]),'times_played_mean':float(np.mean([r['p']['played'].get(relic,0) for r in rs])),'times_activated_mean':float(np.mean([r['p']['activated'].get(relic,0) for r in rs])),'both_m10':summary([r for r in rs if r['o']['mastery10_round']]),'m10_timing':{label:summary([r for r in rs if lo<=r['p']['mastery10_round']<=hi]) for label,lo,hi in [('by6',1,6),('7to8',7,8),('9plus',9,99)]},'by_opponent':{o:summary([r for r in rs if r['o']['hero']==o]) for o in HEROES if o!=hero}})
            out['relics'][relic]=z
        # Comparisons within same hero, no outcomes/final-state covariates.
        rs=[r for r in rr if r['p']['relic']]
        ref=max(relics,key=lambda q:sum(r['p']['relic']==q for r in rs)); others=[q for q in relics if q!=ref]; opps=[q for q in HEROES if q!=hero]
        def encode(r,relic=None):
            p=r['p']; relic=relic or p['relic']
            return [1,p['seat']]+[int(r['o']['hero']==o) for o in opps[:-1]]+[p['mastery5_round']-4,p['mastery10_round']-7,p['relic_round']-p['mastery10_round']]+[int(relic==q) for q in others]
        names=['intercept','seat1']+['opponent:'+o for o in opps[:-1]]+['own_m5_round','own_m10_round','recruit_delay']+others
        x=np.asarray([encode(r) for r in rs]); b,cov=logit(x,[r['win'] for r in rs],ridge=0.01)
        adjusted={}
        for q in relics:
            xq=np.asarray([encode(r,q) for r in rs]); pq=1/(1+np.exp(-np.clip(xq@b,-35,35))); grad=np.mean((pq*(1-pq))[:,None]*xq,axis=0); se=math.sqrt(float(grad@cov@grad))
            adjusted[q]={'win_rate':float(np.mean(pq)), 'ci95':[max(0,float(np.mean(pq))-1.96*se),min(1,float(np.mean(pq))+1.96*se)]}
        h['relic_timing_model']={'reference_relic':ref,'sample_games':len(rs),'terms':model_terms(names,b,cov),'standardized_association':adjusted,'warning':'Association, not causal. Adjusts seat, opposing hero, own mastery-5/10 rounds and relic recruitment delay. No snapshot of deck, health, resources or available destinies at choice. Do not interpret changing relic as obtaining predicted win rate.'}
    # Destiny groups are observed choices, not randomized offers. Each hero occurs once/game.
    destinies=sorted({r['p']['destiny'] for r in rows if r['p']['destiny']})
    for d in destinies:
        rs=[r for r in rows if r['p']['destiny']==d]; z=summary(rs)
        # The shared, non-refilling normal destiny row means at most one normal take per game.
        assert len({r['game'] for r in rs})==len(rs)
        z.update({'name':cards[d]['name'],'rules_text':cards[d]['rules_text'],'selected_round_mean':mean(rs,'destiny_round'),'heroes':{h:summary([r for r in rs if r['p']['hero']==h]) for h in HEROES}})
        out['destinies'][d]=z
    for hero in HEROES:
        out['hero_destinies'][hero]={d:out['destinies'][d]['heroes'][hero] for d in destinies if out['destinies'][d]['heroes'][hero]['n']>=20}
    for relic in out['relics']:
        rr=[r for r in rows if r['p']['relic']==relic]
        for d in destinies:
            rs=[r for r in rr if r['p']['destiny']==d]
            if len(rs)>=20: out['synergies'][relic+' + '+d]=summary(rs)
    out['volos_cross_relic']={field:summary([r for r in rows if r['p']['hero']=='volos' and all(r['p'][field].get(k,0)>0 for k in ('panconscious_crown_duel','entropic_talons'))]) for field in ['acquired','played']}
    out['doom_gate']={}
    for label,condition in [('played',lambda r:r['p']['played'].get('doom_gate',0)>0),('acquired',lambda r:'doom_gate' in r['p']['acquired']),('never_acquired',lambda r:'doom_gate' not in r['p']['acquired'])]:
        rr=[r for r in rows if r['p']['hero']=='kosynwu' and condition(r)]
        z=summary(rr); z['own_monsters_defeated_mean']=mean(rr,'monsters'); z['opponent_monsters_defeated_mean']=float(np.mean([r['o']['monsters'] for r in rr]))
        for side in ['p','o']:
            prefix='own' if side=='p' else 'opponent'
            z[prefix+'_multiple_relic_acquired']=sum(sum(cards[k]['type']=='Relic' for k in r[side]['acquired'])>1 for r in rr)
            if label=='played':
                z[prefix+'_reward_relic_acquired_strictly_after_first_doom_play']=sum(any(cards[k]['type']=='Relic' and k!=r[side]['relic'] and r[side]['first_acquired_round'][k]>r['p']['first_played_round']['doom_gate'] for k in r[side]['acquired']) for r in rr)
        out['doom_gate'][label]=z
    if openings is not None:
        opening_rows=[json.loads(l) for l in openings.read_text().splitlines() if l.strip()]; initial={q['seed']:q for q in opening_rows}
        assert len(initial)==len(games) and set(initial)=={g['seed'] for g in games}
        all_destinies=sorted({d for q in initial.values() for d in q['destinies']}); out['initial_destiny_availability']={}; tests=[]
        for hero in HEROES:
            rr=[r for r in rows if r['p']['hero']==hero]; opps=[o for o in HEROES if o!=hero]; out['initial_destiny_availability'][hero]={}
            for d in all_destinies:
                offered=[d in initial[r['g']['seed']]['destinies'] for r in rr]
                rs=[r for r,f in zip(rr,offered) if f]; ns=[r for r,f in zip(rr,offered) if not f]
                x=[[1,r['p']['seat']]+[int(r['o']['hero']==o) for o in opps[:-1]]+[int(f)] for r,f in zip(rr,offered)]
                b,cov=logit(x,[r['win'] for r in rr]); se=math.sqrt(cov[-1,-1]); p=math.erfc(abs(float(b[-1]))/se/math.sqrt(2))
                z={'offered':summary(rs),'not_offered':summary(ns),'raw_difference':summary(rs)['win_rate']-summary(ns)['win_rate'],'own_normal_takes_when_offered':sum(r['p']['destiny']==d for r in rs),'opponent_normal_takes_when_offered':sum(r['o']['destiny']==d for r in rs),'adjusted_offer_odds_ratio':model_terms(['offer'],b[-1:],cov[-1:,-1:])['offer'],'nominal_p':p}
                out['initial_destiny_availability'][hero][d]=z; tests.append(z)
        order=sorted(tests,key=lambda z:z['nominal_p']); q=1.0
        for i in range(len(order)-1,-1,-1):
            q=min(q,order[i]['nominal_p']*len(order)/(i+1));order[i]['bh_q_all_hero_destiny_tests']=q
        out['initial_destiny_caveat']='Initial offers are shared by both players. Their association estimates relative matchup advantage of presence under this policy, not causal benefit to the eventual taker. 150 hero/card comparisons are exploratory; Benjamini-Hochberg q values supplied.'
    out['relic_contamination']={'players_acquired_multiple_distinct_relics':sum(sum(cards[k]['type']=='Relic' for k in r['p']['acquired'])>1 for r in rows),'no_normal_choice_but_relic_acquired':sum(not r['p']['relic'] and any(cards[k]['type']=='Relic' for k in r['p']['acquired']) for r in rows)}
    out['caveats']=['Exactly balanced distinct-hero matchups, no mirrors. Within each hero n=1512 independent games; all-player n=7560 is not 7560 independent games.', 'Normal relic and destiny fields capture explicit RecruitRelic/TakeDestiny choices only. Corruption reward relics and extra destinies remain in acquired/extra_destinies; no-selection does not strictly mean no relic effect.', 'Mastery and first acquisition/play timestamps have round granularity. Earliest ordinal actions inside equal rounds are unrecorded.', 'Relic recruitment and use are post-start choices conditioned on survival, hero, deck and opponent. Missing relic is not a randomized no-relic control.', 'There are extra turns, so turns-minus-M5-round is only approximate. Healing records effective HP gained, not at-cap healing that can still produce Talons power. Shield and damage totals are not clean health-save/dealt metrics.', 'Final counters are exposure- and survival-confounded. Hero ability use divided by rounds after M5 is an upper-bound opportunity proxy, not exact legality or good-decision rate.', 'No causal effect identified for hero-versus-relic decomposition, destiny choice, or combo from this fixed-policy sample. Choice/availability and human-policy differences remain.', 'Current mechanics checked in current C# plus frozen catalog. Only suggested hypotheses; no balance files modified.']
    return out


def main():
    ap=argparse.ArgumentParser(); ap.add_argument('--source',type=Path,required=True); ap.add_argument('--catalog',type=Path,required=True); ap.add_argument('--output',type=Path,required=True); ap.add_argument('--openings',type=Path); a=ap.parse_args()
    result=analyse(a.source,json.loads(a.catalog.read_text()),a.openings); a.output.parent.mkdir(parents=True,exist_ok=True); a.output.write_text(json.dumps(result,indent=2)+'\n'); print(json.dumps({h:{k:v for k,v in s.items() if k in ['n','wins','win_rate','ci95']} for h,s in result['heroes'].items()},indent=2))

if __name__=='__main__': main()
