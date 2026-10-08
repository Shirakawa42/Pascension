"""Audit a complete frozen-candidate gate against the declared strength target."""
import argparse
from collections import Counter
import json
from pathlib import Path
import sys

ROOT=Path(__file__).resolve().parents[2]
sys.path.insert(0,str(ROOT/'Tools/ZeroDepthTraining'))
from league import heroes_for_seed,sha256_file
from evaluate import GameResult,summarize_pairs

PROFILE_DEFAULTS=dict(depth=24,width=4,worlds=2,prior=.015,prior_cap=6,margin=.02,
    greedy_root=False,greedy_rollout=False,mastery_finish=False,hybrid=False,
    drop_encoder_caches=False,strategic_coverage=False,rez_coverage=False,future_scry=False,
    pooled_inference=False,shared_inference=False,horizon_turns=1,rollout_styles=4,scry_finish_bias=0.,scry_temperature=1.,
    incumbent_inference_backend='native',fixed_inference_batch=0)


def search_profile(plan):
    profile={name:plan.get(name,default) for name,default in PROFILE_DEFAULTS.items()}
    # Enabled-only extension preserves previously frozen declarations. A new
    # feature can never silently pass as an older profile with it disabled.
    if plan.get('market_plans',False):profile['market_plans']=True
    if plan.get('shared_capacity',2048)!=2048:profile['shared_capacity']=plan['shared_capacity']
    return profile


def verify_declaration(protocol,plan):
    candidate=protocol.get('candidate')
    if not candidate:raise ValueError('The final candidate was not frozen in the protocol')
    n=protocol['games'];seed=protocol['seed']
    if n%40 or plan['games']!=n or plan['seed']!=seed or plan['batch']!=protocol['batch'] or plan.get('start_pair',0):
        raise ValueError('Evaluation shape or seed range differs from the predeclared protocol')
    if plan.get('smoke') or plan.get('learning_updates') or plan.get('incumbent_inference_backend','native')!='native':
        raise ValueError('Final evaluation must use a frozen candidate and the native deployed opponent')
    for key,value in [('policy_sha256',plan['candidate']['policy_file_sha256']),
                      ('binary_sha256',plan['binary_sha256']),
                      ('incumbent_manifest_sha256',plan['incumbent_manifest_sha256'])]:
        if candidate[key]!=value:raise ValueError('Frozen candidate identity differs: '+key)
    if candidate['search_profile']!=search_profile(plan):
        raise ValueError('Search/calibration configuration differs from the frozen candidate')


def audit(protocol,plan,result):
    verify_declaration(protocol,plan)
    return audit_games(protocol,result)


def audit_games(protocol,result):
    n=protocol['games'];seed=protocol['seed'];requirements=protocol['requirements']
    games=result['completed_games']
    if result.get('state')!='complete' or result['planned']!=n or result['completed']!=n or len(games)!=n:
        raise ValueError('The entire predeclared evaluation must finish')
    expected={(seed+i,seat) for i in range(n//2) for seat in (0,1)}
    actual={(g['seed'],g['learner_seat']) for g in games}
    if len(actual)!=n or actual!=expected:raise ValueError('Missing, duplicated or foreign seed/seat pair')
    coverage=Counter();by_hero={};records=[]
    for g in games:
        if not g['completed']:raise ValueError('A censored game cannot pass the final gate')
        if type(g['winner']) is not int or g['winner'] not in (-1,0,1):
            raise ValueError('Invalid winner in final evaluation')
        if tuple(g['heroes'])!=heroes_for_seed(g['seed']):raise ValueError('Hero assignment mismatch')
        seat=g['learner_seat'];hero,opponent=g['heroes'][seat],g['heroes'][1-seat]
        coverage[hero,opponent,seat]+=1
        stats=by_hero.setdefault(hero,dict(games=0,wins=0,losses=0,draws=0))
        stats['games']+=1;stats['wins']+=g['winner']==seat
        stats['losses']+=g['winner']>=0 and g['winner']!=seat;stats['draws']+=g['winner']==-1
        records.append(GameResult(g['seed'],seat,g['winner']))
    heroes={'decima','tetra','volos','kosynwu','rez'}
    wanted={(a,b,seat) for a in heroes for b in heroes if a!=b for seat in (0,1)}
    if set(coverage)!=wanted or any(v!=n//40 for v in coverage.values()):
        raise ValueError('Every ordered hero matchup must have equal coverage in both seats')
    summary=summarize_pairs(records,planned_pairs=n//2,alpha=1-protocol['confidence'])
    passed=(summary['wins']>=requirements['minimum_actual_wins'] and
            summary['paired_hoeffding_score_interval'][0]>requirements['minimum_paired_confidence_lower_bound'])
    return dict(target_achieved=passed,actual_win_rate=summary['wins']/n,summary=summary,
                by_hero=by_hero,balanced_hero_matchups=True,rez_included=True,
                frozen_identity_verified=True,no_censors=True,actual_games=n,
                criterion='Actual wins meet the declared target; paired confidence lower bound exceeds 50%',
                deployment_performed=False)


def main():
    p=argparse.ArgumentParser(description=__doc__);p.add_argument('--protocol',type=Path,required=True)
    p.add_argument('--experiment',type=Path,required=True);p.add_argument('--output',type=Path,required=True)
    a=p.parse_args();protocol=json.loads(a.protocol.read_text());plan=json.loads((a.experiment/'plan.json').read_text())
    result=json.loads((a.experiment/'result.json').read_text())
    if plan.get('final_protocol_sha256')!=sha256_file(a.protocol):
        raise ValueError('Protocol changed after evaluation started, or was not pinned at launch')
    if sha256_file(a.experiment/'runtime/DepthHost.dll')!=plan['binary_sha256']:
        raise ValueError('Evaluated runtime artifact changed')
    report=audit(protocol,plan,result)
    report['evidence']={str(path.resolve()):sha256_file(path) for path in
        (a.protocol,a.experiment/'plan.json',a.experiment/'result.json',a.experiment/'runtime/DepthHost.dll')}
    a.output.write_text(json.dumps(report,indent=2));print(json.dumps(report,indent=2))


if __name__=='__main__':main()
