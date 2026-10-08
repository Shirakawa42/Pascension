"""Freeze the preselected Decima/Rez specialization before any final games."""
import argparse
import hashlib
import json
from pathlib import Path
import shutil
import time

from audit_routed_evaluation import merged_progress
from final_handoff import ROOT,validated_development
from audit_goal_evaluation import search_profile,sha256_file
from league import save_json


def prepare(folder):
    route_plan=json.loads((folder/'hero-routing-plan.json').read_text())
    if route_plan['proposed_trial_heroes']!=['decima','rez']:raise ValueError('Declared hero route changed')
    for name in ('hero-routing-parity.json','hero-routing-scry-parity.json'):
        if not json.loads((folder/name).read_text())['passed']:raise ValueError('Routing parity did not pass')
    protocol_path=folder/'final-evaluation-plan.json';protocol=json.loads(protocol_path.read_text())
    if protocol['candidate'] is not None or (folder/'final-strength600').exists():
        raise ValueError('Final benchmark was already frozen or launched')
    if protocol['games']!=600 or protocol['requirements']['minimum_actual_wins']!=360:
        raise ValueError('Do not weaken the final strength criterion')
    config=json.loads((folder/'final-handoff-config.json').read_text())
    runtime=ROOT/'Tools/DepthTraining/Host/bin/Release/net8.0';binary_hash=sha256_file(runtime/'DepthHost.dll')
    for name in ('routing-parity-all4','routing-scry-all4'):
        if json.loads((folder/name/'plan.json').read_text())['binary_sha256']!=binary_hash:
            raise ValueError('Runtime changed after routing parity')
    components={};development={};counts={}
    for name,index,heroes in [('average',0,['kosynwu','tetra','volos']),('scry',1,['decima','rez'])]:
        entry=config['candidates'][index];experiment=Path(entry['experiment'])
        plan,wins=validated_development(experiment,80,config['development_seed'])
        if sha256_file(Path(entry['policy'])/'policy.pt')!=plan['candidate']['policy_file_sha256']:
            raise ValueError('Model weights changed after development')
        result=json.loads((experiment/'result.json').read_text());counts[name]=wins
        final_profile=search_profile(plan);final_profile['fixed_inference_batch']=512
        chosen=[g for g in result['completed_games'] if g['heroes'][g['learner_seat']] in heroes]
        development[name]=dict(completed_games=chosen)
        package=folder/'routed-runtime-packages'/name;package.mkdir(parents=True,exist_ok=False)
        target=package/'runtime';target.mkdir()
        for source in runtime.iterdir():
            if source.suffix in ('.dll','.json'):shutil.copy2(source,target/source.name)
        save_json(package/'plan.json',dict(**final_profile,binary_sha256=binary_hash,
            schema='verified-runtime-replay-package-v1',source_development_plan_sha256=sha256_file(experiment/'plan.json'),
            purpose='Identical controller settings with audited public-hero lane selection'))
        output=folder/'final-components'/name
        command=json.loads((experiment/'owner.json').read_text())['command'][:]
        if '--runtime' in command or '--final-protocol' in command:raise ValueError('Unexpected development launcher')
        for flag,value in {'--output':str(output),'--games':'600','--batch':str(protocol['batch']),
            '--seed':str(protocol['seed']),'--seconds':'10800'}.items():command[command.index(flag)+1]=value
        command+=['--runtime',str(target),'--hero-filter',*heroes,'--routed-protocol',str(protocol_path),'--component-name',name,'--fixed-inference-batch','512']
        components[name]=dict(heroes=heroes,policy=entry['policy'],policy_sha256=plan['candidate']['policy_file_sha256'],
            binary_sha256=binary_hash,incumbent_manifest_sha256=plan['incumbent_manifest_sha256'],search_profile=final_profile,
            output=str(output),command=command,source_experiment=str(experiment),source_result_sha256=sha256_file(experiment/'result.json'))
    composed=merged_progress(dict(games=80,seed=config['development_seed']),development,0,state='complete')
    if composed['completed']!=80 or composed['summary']['wins']<max(counts.values()):
        raise ValueError('Declared routing did not retain the completed development advantage')
    composed.update(development_only=True,strength_proven=False,post_hoc_composition=True,
        note='Reuses the completed component tests; routing fixed before final seeds. Not an independent strength claim.')
    dev=folder/'hero-routed-development80';dev.mkdir(exist_ok=False)
    save_json(dev/'result.json',composed);save_json(dev/'progress.json',composed)
    save_json(folder/'final-single-policy-plan-before-routing.json',protocol)
    identity={name:{key:component[key] for key in ('heroes','policy_sha256','binary_sha256','incumbent_manifest_sha256','search_profile')}
              for name,component in components.items()}
    protocol.update(schema='shards-goal-hero-routed-final-v1',output=str(folder/'final-strength600'),
        candidate=dict(kind='whole-policy-per-public-hero',components=components,
            controller_sha256=hashlib.sha256(json.dumps(identity,sort_keys=True).encode()).hexdigest(),
            parameters_per_component=13746737,distinct_component_parameters=27493474),
        frozen_wall=time.time(),status='Routed controller frozen before final games',
        selection='Use the Scry-trained profile for Decima and Rez; averaged baseline for Tetra,Volos and Ko. Route declared after48 development games and retained after complete80-game component tests. Final seeds are untouched.',
        development_evidence=dict(component_wins=counts,routed_wins=composed['summary']['wins'],games=80,strength_proven=False))
    save_json(protocol_path,protocol)
    print(json.dumps(dict(protocol=str(protocol_path),routes={k:v['heroes'] for k,v in components.items()},
        development_wins=composed['summary']['wins'],final_games=600,minimum_actual_wins=360,controller_sha256=protocol['candidate']['controller_sha256']),indent=2))


if __name__=='__main__':
    parser=argparse.ArgumentParser(description=__doc__);parser.add_argument('--directory',type=Path,required=True)
    prepare(parser.parse_args().directory.resolve())
