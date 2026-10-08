"""Copy the exact frozen routed controller and evaluation provenance into one bundle."""
import argparse
from collections import Counter
import json
from pathlib import Path
import shutil
import time

from audit_routed_evaluation import HEROES,verify_component
from league import save_json,sha256_file
from final_handoff import ROOT


def package(protocol_path,output):
    protocol=json.loads(protocol_path.read_text());components=protocol['candidate']['components']
    if Counter(h for c in components.values() for h in c['heroes'])!=Counter(HEROES):
        raise ValueError('Hero routes are incomplete or overlap')
    if output.exists():raise ValueError('Refusing to overwrite a controller bundle')
    output.mkdir(parents=True);files={};models={};sources={}
    def copy(source,target,expected=None):
        source=Path(source);target=output/target
        digest=sha256_file(source)
        if expected is not None and digest!=expected:raise ValueError('Source artifact changed: '+str(source))
        target.parent.mkdir(parents=True,exist_ok=True);shutil.copy2(source,target)
        if sha256_file(target)!=digest:raise ValueError('Copied artifact differs: '+str(target))
        files[str(target.relative_to(output))]=dict(sha256=digest,bytes=target.stat().st_size)
    runtime_inventory=None
    for name,c in components.items():
        experiment=Path(c['output']);plan=json.loads((experiment/'plan.json').read_text())
        verify_component(protocol,name,plan)
        if plan['routed_protocol_sha256']!=sha256_file(protocol_path):raise ValueError('Unpinned final protocol')
        policy=Path(c['policy']);meta=json.loads((policy/'manifest.json').read_text())
        if meta['policy_file_sha256']!=c['policy_sha256']:raise ValueError('Model manifest changed')
        copy(policy/'policy.pt',Path('models')/name/'policy.pt',c['policy_sha256'])
        copy(policy/'manifest.json',Path('models')/name/'manifest.json')
        runtime=experiment/'runtime'
        inventory={p.name:sha256_file(p) for p in runtime.iterdir() if p.is_file()}
        if inventory.get('DepthHost.dll')!=c['binary_sha256']:raise ValueError('Runtime changed')
        if runtime_inventory is None:
            runtime_inventory=inventory
            for file,digest in inventory.items():copy(runtime/file,Path('runtime')/file,digest)
        elif inventory!=runtime_inventory:raise ValueError('Components do not share the same runtime dependencies')
        models[name]=dict(heroes=c['heroes'],directory='models/'+name,policy_sha256=c['policy_sha256'],
            parameters=protocol['candidate']['parameters_per_component'],search_profile=c['search_profile'],
            required_inference_calibration=meta.get('required_inference_calibration',{}))
        for relative,digest in plan['source_files'].items():
            if relative in sources and sources[relative]!=digest:raise ValueError('Component sources differ')
            sources[relative]=digest
    for relative,digest in sources.items():copy(ROOT/relative,Path('sources')/relative,digest)
    for name in ('audit_routed_evaluation.py','run_routed_final.py','prepare_routed_final.py'):
        copy(Path(__file__).with_name(name),Path('sources/Tools/TrainingDiagnosis')/name)
    copy(protocol_path,'evaluation-protocol.json')
    for name in ('hero-routing-parity.json','hero-routing-scry-parity.json','mastery-value-audit.json','scry80-behavior-change.json'):
        copy(protocol_path.parent/name,Path('evidence')/name)
    controller=dict(schema='shards-routed-controller-v1',controller_sha256=protocol['candidate']['controller_sha256'],
        routing_input='Public learner hero identity, fixed before the game',models=models,runtime='runtime',
        observation_schema='Full current-information Shards observation v2',
        parameters_per_model=protocol['candidate']['parameters_per_component'],
        distinct_model_parameters=protocol['candidate']['distinct_component_parameters'],
        inference='Use one entire component, including its calibration and search profile, throughout each game and all its hypothetical branches',
        supported_runtime='Headless Python/PyTorch CUDA and .NET evaluation runtime; repository Python tooling is required',
        unity_export_available=False,deployment_performed=False,strength_status='Pending complete independent600-game evaluation',
        final_protocol_sha256=sha256_file(protocol_path),created_wall=time.time())
    save_json(output/'controller.json',controller)
    files['controller.json']=dict(sha256=sha256_file(output/'controller.json'),bytes=(output/'controller.json').stat().st_size)
    save_json(output/'manifest.json',dict(schema='shards-routed-controller-artifacts-v1',files=files))
    for relative,entry in files.items():
        if sha256_file(output/relative)!=entry['sha256']:raise ValueError('Bundle verification failed')
    print(json.dumps(dict(output=str(output),files_verified=len(files),controller_sha256=controller['controller_sha256'],
        parameters_per_model=controller['parameters_per_model'],distinct_model_parameters=controller['distinct_model_parameters'],
        strength_status=controller['strength_status'],unity_export_available=False),indent=2))


if __name__=='__main__':
    parser=argparse.ArgumentParser(description=__doc__);parser.add_argument('--protocol',type=Path,required=True)
    parser.add_argument('--output',type=Path,required=True)
    args=parser.parse_args();package(args.protocol.resolve(),args.output.resolve())
