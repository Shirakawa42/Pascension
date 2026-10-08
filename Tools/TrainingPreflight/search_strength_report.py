"""Validate paired search-vs-identical-network matches and report paired uncertainty."""
import argparse,json,math
from collections import Counter
from pathlib import Path
import numpy as np

def analyze(data):
    n=data['games']; rows=sorted(data['rows'],key=lambda r:r['index'])
    if n%40 or len(rows)!=n or [r['index'] for r in rows]!=list(range(n)):
        raise ValueError('Incomplete or duplicate balanced evaluation')
    score=lambda r: .5 if r['winner']<0 else float(r['winner']==r['treatment'])
    heroes={'decima','tetra','volos','kosynwu','rez'}
    if any(r['hero0'] not in heroes or r['hero1'] not in heroes or r['hero0']==r['hero1'] or r['winner'] not in (-1,0,1) for r in rows):
        raise ValueError('Invalid hero or game outcome')
    observed={'wins':sum(r['winner']==r['treatment'] for r in rows), 'draws':sum(r['winner']==-1 for r in rows)}
    observed['losses']=n-observed['wins']-observed['draws']
    if any(data[k]!=v for k,v in observed.items()):raise ValueError('Outcome totals disagree with individual games')
    pairs=[];seeds=set()
    for a,b in zip(rows[::2],rows[1::2]):
        if (a['seed'],a['hero0'],a['hero1'])!=(b['seed'],b['hero0'],b['hero1']) or (a['treatment'],b['treatment'])!=(0,1):
            raise ValueError('Unmatched seed, heroes, or treatment seats')
        if a['seed'] in seeds:raise ValueError('Repeated engine seed across pairs')
        seeds.add(a['seed'])
        pairs.append((score(a)+score(b))/2)
    coverage=Counter((r['hero0'],r['hero1'],r['treatment']) for r in rows)
    if len(coverage)!=40 or set(coverage.values())!={n//40}:
        raise ValueError('Hero/seat coverage not balanced')
    values=np.asarray(pairs); rng=np.random.default_rng(271927)
    bootstrap=values[rng.integers(len(values),size=(20000,len(values)))].mean(1)
    up=int((values>.5).sum()); down=int((values<.5).sum()); informative=up+down
    p=min(1.,2*sum(math.comb(informative,k) for k in range(min(up,down)+1))/2**informative) if informative else 1.
    return dict(games=n,paired_seeds=len(values),wins=data['wins'],losses=data['losses'],draws=data['draws'],
        score=float(values.mean()),paired_bootstrap_95=np.quantile(bootstrap,[.025,.975]).tolist(),
        improved_pairs=up,worse_pairs=down,neutral_pairs=len(values)-informative,paired_sign_p_two_sided=p,
        by_seat={str(s):dict(games=n//2,score=sum(score(r) for r in rows if r['treatment']==s)/(n//2)) for s in [0,1]},
        by_hero={h:dict(games=n//5,score=np.mean([score(r) for r in rows if r['hero0' if r['treatment']==0 else 'hero1']==h])) for h in sorted({r['hero0'] for r in rows})},
        sampling='Same engine seed and hero seats per pair; search treatment alternates seats; fixed separate action RNG streams per seat',
        uncertainty='Resample complete seed pairs, not individual dependent games; this estimates this benchmark distribution')

if __name__=='__main__':
    p=argparse.ArgumentParser(description=__doc__);p.add_argument('input',type=Path);p.add_argument('--output',type=Path);a=p.parse_args()
    result=analyze(json.loads(a.input.read_text()))
    if a.output:a.output.write_text(json.dumps(result,indent=2)+'\n')
    print(json.dumps(result,indent=2))
