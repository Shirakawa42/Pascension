"""Complete an interrupted screen using disjoint cohorts from the same frozen AI."""
import argparse
import json
from pathlib import Path
import sys

ROOT=Path(__file__).resolve().parents[2]
sys.path[:0]=[str(ROOT/'Tools/MatchupBenchmark'),str(ROOT/'Tools/ZeroDepthTraining')]
from evaluate import GameResult,summarize_pairs
from league import sha256_file,heroes_for_seed


def merge(base,continuation,output):
    plans=[json.loads((p/'plan.json').read_text()) for p in (base,continuation)]
    records=[json.loads((p/'progress.json').read_text()) for p in (base,continuation)]
    for key in ('candidate','incumbent_manifest_sha256','binary_sha256','depth','width','worlds','batch','workers','seed','prior','prior_cap','margin'):
        if plans[0][key]!=plans[1][key]:raise ValueError('Cohorts differ: '+key)
    for key in ('greedy_root','greedy_rollout','mastery_finish','hybrid'):
        if plans[0].get(key,False)!=plans[1].get(key,False):raise ValueError('Cohorts differ: '+key)
    if records[1]['state']!='complete':raise ValueError('Continuation has not completed')
    games=records[0]['completed_games']+records[1]['completed_games']
    expected={(plans[0]['seed']+i//2,i%2) for i in range(plans[0]['games'])}
    identities=[(g['seed'],g['learner_seat']) for g in games]
    if len(set(identities))!=len(identities) or set(identities)!=expected:
        raise ValueError('Missing, overlapping, or extraneous evaluation seeds/seats')
    if not all(g['completed'] for g in games):raise ValueError('Censored games require separate analysis')
    if not all(tuple(g['heroes'])==heroes_for_seed(g['seed']) for g in games):raise ValueError('Unbalanced heroes')
    results=[GameResult(g['seed'],g['learner_seat'],g['winner'],False) for g in games]
    data=dict(state='complete',completed=len(games),planned=plans[0]['games'],
        summary=summarize_pairs(results,planned_pairs=plans[0]['games']//2),completed_games=games,
        elapsed_seconds=sum(d['elapsed_seconds'] for d in records),
        provenance=[dict(directory=str(p.resolve()),plan_sha256=sha256_file(p/'plan.json'),progress_sha256=sha256_file(p/'progress.json')) for p in (base,continuation)],
        note='Development screen. Original run stopped on a reproduced numerical-check false alarm; remaining disjoint cohort used the identical frozen host/model/settings and a validated inference check. No duplicate games counted.',
        wins_by_hero={hero:dict(games=sum(g['heroes'][g['learner_seat']]==hero for g in games),
            wins=sum(g['heroes'][g['learner_seat']]==hero and g['winner']==g['learner_seat'] for g in games))
            for hero in ('decima','tetra','volos','kosynwu','rez')})
    output.mkdir(parents=True,exist_ok=False)
    (output/'plan.json').write_text(json.dumps(plans[0],indent=2))
    for name in ('progress.json','result.json'):(output/name).write_text(json.dumps(data,indent=2))
    print(json.dumps({k:v for k,v in data.items() if k!='completed_games'},indent=2))


if __name__=='__main__':
    p=argparse.ArgumentParser(description=__doc__)
    p.add_argument('--base',type=Path,required=True);p.add_argument('--continuation',type=Path,required=True)
    p.add_argument('--output',type=Path,required=True);a=p.parse_args();merge(a.base,a.continuation,a.output)
