"""Opening-state evidence from exact frozen-host setups; no game simulation."""
import collections,hashlib,json,math,os
from pathlib import Path
os.environ.setdefault('OPENBLAS_NUM_THREADS','1')
import numpy as np
BASE=Path('/home/lva/.local/share/shards-zero-depth/2026-10-08')
OUT=BASE/'balance-review'
games=[json.loads(l) for l in (BASE/'new-ai-search-statistics-10000-optimized/strategy-prefix.jsonl').read_text(encoding='utf-8-sig').splitlines()]
opening_records=list(map(json.loads,(OUT/'openings.jsonl').read_text().splitlines()))
openings={g['seed']:g for g in opening_records}
assert len(games)==len(openings)==len(opening_records)==3780 and len({g['seed'] for g in games})==len(games)
assert set(openings)=={g['seed'] for g in games}
assert all(g['winner'] in (0,1) and [p['seat'] for p in g['players']]==[0,1] for g in games)
export=json.loads((BASE/'new-ai-search-statistics-10000/game-decklists.json').read_text())
recorded={g['seed']:{k:v for k,v in g.items() if k!='number'} for g in export['games']}
assert all(recorded[g['seed']]==g for g in games)
catalog=json.loads((BASE/'new-ai-search-statistics-10000/card-catalog.json').read_text());cards={c['id']:c for c in catalog['cards']}
heroes=sorted({p['hero'] for g in games for p in g['players']});pairs=[(a,b) for a in heroes for b in heroes if a!=b]
assert all((g['players'][0]['hero'],g['players'][1]['hero']) in pairs for g in games)
y=np.array([float(g['winner']==0) for g in games]);opening=[openings[g['seed']] for g in games]

def gem(p):return p['gems']+p['hand'].count('crystal')+p['hand'].count('shard_reactor')*2
X=np.array([[int((g['players'][0]['hero'],g['players'][1]['hero'])==pair) for pair in pairs]+[gem(o['players'][0]),gem(o['players'][1])] for g,o in zip(games,opening)],dtype=float)
inv=np.linalg.pinv(X.T@X);projection=inv@X.T;ry=y-X@(projection@y)

def score(rows):
 n=len(rows);w=sum(g['winner']==0 for g in rows);p=w/n if n else None
 return dict(games=n,seat0_wins=w,seat0_win_rate=p)

def assess(z):
 z=np.asarray(z,dtype=float);rz=z-X@(projection@z);denom=rz@rz
 if denom<1e-10:return None
 b=(rz@ry)/denom;resid=ry-rz*b
 leverage=np.einsum('ij,ji->i',X,projection)+rz*rz/denom
 se=math.sqrt(np.sum((rz*resid/np.maximum(1-leverage,.01))**2))/denom
 p=math.erfc(abs(b/se)/math.sqrt(2)) if se else 0
 return dict(adjusted_difference=b,ci95=[b-1.96*se,b+1.96*se],p=p)

def bh(rows):
 valid=sorted([r for r in rows if r.get('adjustment')],key=lambda r:r['adjustment']['p']);value=1
 for i in range(len(valid)-1,-1,-1):value=min(value,valid[i]['adjustment']['p']*len(valid)/(i+1));valid[i]['adjustment']['q']=value

offers=[]
for id in sorted({c for o in opening for c in o['market'] if c}):
 mask=np.array([id in o['market'] for o in opening]);n=int(mask.sum())
 if n<30:continue
 row=dict(id=id,name=cards[id]['name'],offered=score([g for g,m in zip(games,mask) if m]),absent=score([g for g,m in zip(games,mask) if not m]),adjustment=assess(mask))
 row['round1_acquisition_when_offered']=[sum(bool(m) and g['players'][seat]['first_acquired_round'].get(id)==1 for g,m in zip(games,mask)) for seat in range(2)]
 offers.append(row)
bh(offers)
destinies=[]
for id in sorted({c for o in opening for c in o['destinies']}):
 mask=np.array([id in o['destinies'] for o in opening]);row=dict(id=id,name=cards[id]['name'],offered=score([g for g,m in zip(games,mask) if m]),absent=score([g for g,m in zip(games,mask) if not m]),adjustment=assess(mask));destinies.append(row)
bh(destinies)
# Independent opening hands: both players' initial buying resources as a game-level predictor.
coef=projection@y;resid=y-X@coef;leverage=np.einsum('ij,ji->i',X,projection);u=X*(resid/np.maximum(1-leverage,.01))[:,None];cov=inv@(u.T@u)@inv
hands=[]
for seat in range(2):
 for amount in sorted({gem(o['players'][seat]) for o in opening}):
  subset=[g for g,o in zip(games,opening) if gem(o['players'][seat])==amount];n=len(subset);wins=sum(g['winner']==seat for g in subset)
  hands.append(dict(seat=seat,gems=amount,games=n,wins=wins,win_rate=wins/n))
# Cross-check categorical seeds, round bounds, collection/choice identities and lifecycle facts.
issues=[]
for g in games:
 for seat,p in enumerate(g['players']):
  ms=[p[f'mastery{k}_round'] for k in [5,10,20,30]];seen=[x for x in ms if x]
  if seen!=sorted(seen) or any(x>g['round'] or x<0 for x in ms):issues.append([g['seed'],'threshold chronology'])
  if p['relic'] and cards[p['relic']]['character']!=p['hero']:issues.append([g['seed'],'relic identity'])
  if any(v>p['acquired'].get(k,0) for k,v in p['fast_acquired'].items()):issues.append([g['seed'],'fast count exceeds acquisition'])
  if p['hero']!=openings[g['seed']]['players'][seat]['hero']:issues.append([g['seed'],'opening mismatch'])
first=[g for g in games if int(g['seed'],16)<8003000000000000240];later=[g for g in games if g not in first]
report=dict(schema='shards-opening-balance-audit-v1',games=len(games),audit_issues=issues,
 provenance=dict(trace_sha256=hashlib.sha256((BASE/'new-ai-search-statistics-10000-optimized/strategy-prefix.jsonl').read_bytes()).hexdigest(),opening_sha256=hashlib.sha256((OUT/'openings.jsonl').read_bytes()).hexdigest(),host_sha256=hashlib.sha256((BASE/'new-ai-search-statistics-10000-optimized/runtime/DepthHost.dll').read_bytes()).hexdigest(),decklist_equality=True),
 cohorts=[dict(label='original240',**score(first),mean_rounds=sum(g['round'] for g in first)/len(first)),dict(label='optimized3540',**score(later),mean_rounds=sum(g['round'] for g in later)/len(later))],
 opening_market=offers,opening_destinies=destinies,opening_hands=hands,
 opening_gem_adjustment=[dict(seat=seat,per_gem_advantage=float(coef[-2+seat])*(1 if seat==0 else -1),ci95=[float(coef[-2+seat])*(1 if seat==0 else -1)-1.96*math.sqrt(cov[-2+seat,-2+seat]),float(coef[-2+seat])*(1 if seat==0 else -1)+1.96*math.sqrt(cov[-2+seat,-2+seat])]) for seat in range(2)],
 methods=['Initial states were reconstructed using the exact frozen host DLL and each existing seed, with no policy calls or played games. Assigned heroes matched all 7560 recorded seats.',
 'Initial market exposure tests are game-level seat0-win associations adjusted for ordered hero matchup and both hands\' base opening gem potential. HC3 robust 95% intervals; Benjamini-Hochberg within market and destiny families.',
 'The initial market is known at seat0 first action. Seat1 faces a market modified by seat0. Exposure can benefit either player; these coefficients measure opening first-seat leverage, not a card\'s overall causal strength.',
 'Base gem potential counts only starting gems, Crystals and Shard Reactor before purchases. Further draw/fast-play/bonus income is not included.',
 'Initial destiny availability changes every player\'s options. This is an offer association, not a causal effect of selecting the destiny.',
 'The 240 original and 3540 optimized games use the same frozen model/settings; their subgroup differences have substantial sampling error.'])
(OUT/'openings_analysis.json').write_text(json.dumps(report,indent=2)+'\n')
print(json.dumps(dict(issues=issues,cohorts=report['cohorts'],gem_adjustment=report['opening_gem_adjustment'],significant_market=[r for r in offers if r['adjustment']['q']<.1],significant_destiny=[r for r in destinies if r['adjustment']['q']<.1]),indent=2))
