"""Preserve one frozen headless controller without claiming evaluation success."""
import argparse
import json
from pathlib import Path
import shutil
import time

from audit_goal_evaluation import verify_declaration, sha256_file
from final_handoff import ROOT
from league import load_frozen_policy, save_json


def package(protocol_path, experiment, policy, output, source_archives):
    protocol=json.loads(protocol_path.read_text())
    plan=json.loads((experiment/'plan.json').read_text())
    verify_declaration(protocol,plan)
    if plan['final_protocol_sha256']!=sha256_file(protocol_path):
        raise ValueError('Protocol changed after benchmark launch')
    candidate=protocol['candidate']
    if sha256_file(policy/'policy.pt')!=candidate['policy_sha256']:
        raise ValueError('Frozen policy changed')
    if sha256_file(experiment/'runtime/DepthHost.dll')!=candidate['binary_sha256']:
        raise ValueError('Frozen runtime changed')
    model,meta=load_frozen_policy(policy,device='cpu',allow_external_calibration=True)
    parameters=sum(p.numel() for p in model.parameters());del model
    if meta['policy_file_sha256']!=candidate['policy_sha256']:
        raise ValueError('Policy manifest and final declaration disagree')
    if output.exists():raise ValueError('Refusing to overwrite an existing bundle')
    output.mkdir(parents=True);files={}

    def copy(source,relative,expected=None):
        digest=sha256_file(source)
        if expected is not None and digest!=expected:raise ValueError('Source changed: '+str(source))
        target=output/relative;target.parent.mkdir(parents=True,exist_ok=True)
        shutil.copy2(source,target)
        if sha256_file(target)!=digest:raise ValueError('Copied artifact changed: '+str(target))
        files[str(relative)]=dict(sha256=digest,bytes=target.stat().st_size)

    def source(relative,digest):
        for base in [ROOT,*source_archives]:
            path=base/relative
            if path.is_file() and sha256_file(path)==digest:return path
        raise ValueError('Cannot recover the exact declared source: '+relative)

    copy(policy/'policy.pt',Path('model/policy.pt'),candidate['policy_sha256'])
    copy(policy/'manifest.json',Path('model/manifest.json'))
    for file in (experiment/'runtime').iterdir():
        if file.is_file():copy(file,Path('runtime')/file.name)
    copy(protocol_path,Path('evaluation-protocol.json'))
    copy(experiment/'plan.json',Path('evaluation-launch-plan.json'))
    # A replay uses the saved DLL. Its build sources can differ from the
    # Python evaluator's launch-time inventory; preserve both explicitly.
    origin=plan.get('runtime_origin_plan') or plan
    for relative,digest in origin['source_files'].items():
        if relative.endswith('.cs'):
            copy(source(relative,digest),Path('runtime-sources')/relative,digest)
    for relative,digest in plan['source_files'].items():
        copy(source(relative,digest),Path('evaluation-launch-sources')/relative,digest)
    incumbent=Path(plan['incumbent'])
    copy(incumbent/'manifest.json',Path('incumbent/manifest.json'),candidate['incumbent_manifest_sha256'])
    incumbent_meta=json.loads((incumbent/'manifest.json').read_text())
    for name,entry in incumbent_meta['files'].items():
        copy(incumbent/name,Path('incumbent')/name,entry['sha256'])
    for name in ['mastery-investigation-conclusion.json','final-v2-launch-verification.json']:
        copy(protocol_path.parent/name,Path('evidence')/name)
    controller=dict(schema='shards-single-controller-v1',model='model',runtime='runtime',
        parameters=parameters,policy_sha256=candidate['policy_sha256'],
        binary_sha256=candidate['binary_sha256'],search_profile=candidate['search_profile'],
        required_inference_calibration=meta.get('required_inference_calibration',{}),
        supported_runtime='Headless Python/PyTorch CUDA and .NET; repository Python tooling is required',
        unity_export_available=False,deployment_performed=False,
        strength_status='Pending complete independent600-game evaluation',
        evidence_is_not_a_success_claim=True,final_protocol_sha256=sha256_file(protocol_path),
        runtime_source_note='runtime-sources matches the frozen DLL origin; evaluation-launch-sources records the evaluator at launch',
        created_wall=time.time())
    save_json(output/'controller.json',controller)
    files['controller.json']=dict(sha256=sha256_file(output/'controller.json'),bytes=(output/'controller.json').stat().st_size)
    save_json(output/'manifest.json',dict(schema='shards-single-controller-artifacts-v1',files=files))
    for relative,entry in files.items():
        if sha256_file(output/relative)!=entry['sha256']:raise ValueError('Bundle verification failed: '+relative)
    return dict(output=str(output),files_verified=len(files),parameters=parameters,
                strength_status=controller['strength_status'],unity_export_available=False)


if __name__=='__main__':
    parser=argparse.ArgumentParser(description=__doc__)
    for name in ('protocol','experiment','policy','output'):parser.add_argument('--'+name,type=Path,required=True)
    parser.add_argument('--source-archive',type=Path,action='append',default=[])
    a=parser.parse_args()
    print(json.dumps(package(a.protocol,a.experiment,a.policy,a.output,a.source_archive),indent=2))
