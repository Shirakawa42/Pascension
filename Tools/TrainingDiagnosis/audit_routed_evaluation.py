"""Audit one frozen hero-routed controller on a single complete held-out game set."""
from collections import Counter
import json
from pathlib import Path

from audit_goal_evaluation import audit_games, heroes_for_seed, search_profile, sha256_file
from evaluate import GameResult, summarize_pairs

HEROES={'decima','tetra','volos','kosynwu','rez'}


def verify_component(protocol,name,plan):
    component=protocol['candidate']['components'][name]
    if (plan['games']!=protocol['games'] or plan['seed']!=protocol['seed'] or plan['batch']!=protocol['batch']
            or plan.get('start_pair',0) or plan.get('smoke') or plan.get('learning_updates')):
        raise ValueError('Component evaluation shape differs from the frozen protocol')
    if plan.get('hero_filter')!=sorted(component['heroes']):
        raise ValueError('Component hero route differs from the frozen protocol')
    if plan.get('incumbent_inference_backend','native')!='native':
        raise ValueError('Routed final evaluation requires the native deployed opponent')
    identity=dict(policy_sha256=plan['candidate']['policy_file_sha256'],binary_sha256=plan['binary_sha256'],
                  incumbent_manifest_sha256=plan['incumbent_manifest_sha256'],search_profile=search_profile(plan))
    for key,value in identity.items():
        if component[key]!=value:raise ValueError('Component identity changed: '+key)


def expected_keys(protocol,heroes):
    seed=protocol['seed']
    return {(seed+i,seat) for i in range(protocol['games']//2) for seat in (0,1)
            if heroes_for_seed(seed+i)[seat] in heroes}


def merged_progress(protocol,results,elapsed,state='running'):
    rows=[game for result in results.values() for game in result.get('completed_games',[])]
    rows.sort(key=lambda g:(g['seed'],g['learner_seat']))
    records=[GameResult(g['seed'],g['learner_seat'],g['winner'] if g['completed'] else None,not g['completed']) for g in rows]
    summary=summarize_pairs(records,planned_pairs=protocol['games']//2)
    summary['stronger_than_current']=False  # Only the completed strict audit can certify the target.
    return dict(state=state,completed=len(rows),planned=protocol['games'],completed_games=rows,summary=summary,
        elapsed_seconds=elapsed,hero_routed=True,single_policy_parallel=bool(protocol.get('single_policy_parallel',False)),
        wins_by_hero={hero:dict(games=sum(g['heroes'][g['learner_seat']]==hero for g in rows),
            wins=sum(g['heroes'][g['learner_seat']]==hero and g['winner']==g['learner_seat'] for g in rows)) for hero in sorted(HEROES)},
        victory_causes={role:{cause:sum(g.get('victoryCause')==cause and g['winner']>=0 and
            (g['winner']==g['learner_seat'])==(role=='learner') for g in rows)
            for cause in ('mastery','normal_damage','comet','other_health_loss','concession','unknown')}
            for role in ('learner','incumbent')})


def audit_routed(protocol,parts):
    components=protocol['candidate']['components']
    if protocol.get('single_policy_parallel'):
        identities=[{key:c[key] for key in ('policy_sha256','binary_sha256','incumbent_manifest_sha256','search_profile')}
                    for c in components.values()]
        if not identities or any(identity!=identities[0] for identity in identities[1:]):
            raise ValueError('Parallel partitions must use one identical frozen controller')
    assigned=Counter(hero for component in components.values() for hero in component['heroes'])
    if assigned!=Counter(HEROES) or set(parts)!=set(components):
        raise ValueError('Every hero must have exactly one frozen component')
    results={}
    for name,(plan,result) in parts.items():
        verify_component(protocol,name,plan)
        expected=expected_keys(protocol,components[name]['heroes'])
        rows=result['completed_games']
        if (result['state']!='complete' or result['planned']!=len(expected) or result['completed']!=len(expected)
                or len(rows)!=len(expected)):
            raise ValueError('An entire routed component must finish')
        if Counter((g['seed'],g['learner_seat']) for g in rows)!=Counter(expected):
            raise ValueError('Missing, duplicated or incorrectly routed game')
        results[name]=result
    combined=merged_progress(protocol,results,0,state='complete')
    report=audit_games(protocol,combined)
    report['controller']=('One frozen policy evaluated in disjoint public-hero work partitions' if protocol.get('single_policy_parallel')
                          else 'Frozen whole-policy routing selected solely by the public learner hero')
    report['routes']={name:component['heroes'] for name,component in components.items()}
    return report


def audit_artifacts(protocol_path):
    protocol_path=Path(protocol_path);protocol=json.loads(protocol_path.read_text());parts={};evidence={str(protocol_path):sha256_file(protocol_path)}
    for name,component in protocol['candidate']['components'].items():
        directory=Path(component['output']);plan_path=directory/'plan.json';result_path=directory/'result.json'
        plan=json.loads(plan_path.read_text());result=json.loads(result_path.read_text());binary=directory/'runtime/DepthHost.dll'
        if plan.get('routed_protocol_sha256')!=sha256_file(protocol_path) or plan.get('component_name')!=name:
            raise ValueError('Component did not pin the unchanged protocol at launch')
        if sha256_file(binary)!=plan['binary_sha256']:raise ValueError('Component runtime artifact changed')
        policy=Path(component['policy'])/'policy.pt'
        if sha256_file(policy)!=component['policy_sha256']:raise ValueError('Frozen component weights changed')
        parts[name]=plan,result
        evidence.update({str(path):sha256_file(path) for path in (plan_path,result_path,binary,policy)})
    report=audit_routed(protocol,parts);report['evidence']=evidence
    return report
