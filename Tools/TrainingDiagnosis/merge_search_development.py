"""Merge disjoint completed development cohorts of exactly one frozen profile."""
import argparse
from collections import Counter
import json
from pathlib import Path
from audit_goal_evaluation import search_profile,heroes_for_seed,sha256_file,GameResult,summarize_pairs


def merge(directories):
    games=[];sources=[];identity=None;seconds=0
    for directory in directories:
        plan=json.loads((directory/'plan.json').read_text());result=json.loads((directory/'result.json').read_text())
        current=dict(policy_sha256=plan['candidate']['policy_file_sha256'],binary_sha256=plan['binary_sha256'],
                     incumbent_manifest_sha256=plan['incumbent_manifest_sha256'],profile=search_profile(plan))
        if identity is not None and identity!=current:raise ValueError('Development cohorts use different frozen models/runtimes/settings')
        identity=current
        if result['state']!='complete' or result['completed']!=plan['games'] or any(not g['completed'] for g in result['completed_games']):
            raise ValueError('Only complete uncensored development cohorts can be merged')
        games.extend(result['completed_games']);seconds+=result['elapsed_seconds']
        sources.append(dict(directory=str(directory.resolve()),plan_sha256=sha256_file(directory/'plan.json'),
                            result_sha256=sha256_file(directory/'result.json')))
    if len(games)%40:raise ValueError('Development aggregate must contain complete hero cycles')
    games.sort(key=lambda g:(g['seed'],g['learner_seat']))
    for g in games:
        if tuple(g['heroes'])!=heroes_for_seed(g['seed']):raise ValueError('Hero assignment mismatch')
    summary=summarize_pairs([GameResult(g['seed'],g['learner_seat'],g['winner']) for g in games],planned_pairs=len(games)//2)
    summary.pop('stronger_than_current',None);summary['verdict']='development_only'
    summary['gate']='This selected development result cannot prove the final strength target'
    coverage=Counter((g['heroes'][g['learner_seat']],g['heroes'][1-g['learner_seat']],g['learner_seat']) for g in games)
    if len(coverage)!=40 or len(set(coverage.values()))!=1:raise ValueError('Hero/seat coverage is not balanced')
    return dict(state='complete',completed=len(games),planned=len(games),summary=summary,development_only=True,
                strength_proven=False,identity=identity,sources=sources,elapsed_seconds=seconds,completed_games=games,
                wins_by_hero={hero:dict(games=sum(g['heroes'][g['learner_seat']]==hero for g in games),
                    wins=sum(g['heroes'][g['learner_seat']]==hero and g['winner']==g['learner_seat'] for g in games))
                    for hero in ('decima','tetra','volos','kosynwu','rez')})


if __name__=='__main__':
    p=argparse.ArgumentParser(description=__doc__);p.add_argument('--output',type=Path,required=True)
    p.add_argument('directories',type=Path,nargs='+');a=p.parse_args();result=merge(a.directories)
    a.output.mkdir(exist_ok=False);(a.output/'result.json').write_text(json.dumps(result,indent=2))
    (a.output/'progress.json').write_text(json.dumps(result,indent=2))
    print(json.dumps({k:result[k] for k in ('completed','summary','wins_by_hero')},indent=2))
