"""Verify the actual saved controller artifacts before issuing a strength verdict."""
import argparse
import json
from pathlib import Path
import time

from audit_goal_evaluation import ROOT, audit, sha256_file, verify_declaration
from evaluate import verify_bundle


def checked_hash(path, expected):
    actual = sha256_file(path)
    if actual != expected:
        raise ValueError('Frozen artifact changed: ' + str(path))
    return actual


def verify(protocol_path, experiment, policy, bundle):
    protocol = json.loads(protocol_path.read_text())
    plan = json.loads((experiment/'plan.json').read_text())
    verify_declaration(protocol, plan)
    protocol_hash = checked_hash(protocol_path, plan['final_protocol_sha256'])
    candidate = protocol['candidate']
    checked_hash(policy/'policy.pt', candidate['policy_sha256'])
    policy_meta = json.loads((policy/'manifest.json').read_text())
    if policy_meta != plan['candidate']:
        raise ValueError('Policy manifest changed after launch')
    checked_hash(experiment/'runtime/DepthHost.dll', candidate['binary_sha256'])

    manifest = json.loads((bundle/'manifest.json').read_text())
    for relative, entry in manifest['files'].items():
        path = bundle/relative
        checked_hash(path, entry['sha256'])
        if path.stat().st_size != entry['bytes']:
            raise ValueError('Bundle artifact size changed: ' + relative)
    controller = json.loads((bundle/'controller.json').read_text())
    for key in ('policy_sha256', 'binary_sha256', 'search_profile'):
        if controller[key] != candidate[key]:
            raise ValueError('Packaged controller differs from evaluated candidate: ' + key)
    if controller['final_protocol_sha256'] != protocol_hash:
        raise ValueError('Packaged controller cites a different final protocol')
    checked_hash(bundle/'evaluation-protocol.json', protocol_hash)
    checked_hash(bundle/'evaluation-launch-plan.json', sha256_file(experiment/'plan.json'))
    checked_hash(bundle/'model/policy.pt', candidate['policy_sha256'])
    checked_hash(bundle/'model/manifest.json', sha256_file(policy/'manifest.json'))
    for file in (experiment/'runtime').iterdir():
        if file.is_file():
            checked_hash(file, sha256_file(bundle/'runtime'/file.name))
    for relative, digest in plan['source_files'].items():
        checked_hash(bundle/'evaluation-launch-sources'/relative, digest)
    origin = plan.get('runtime_origin_plan') or plan
    for relative, digest in origin['source_files'].items():
        if relative.endswith('.cs'):
            checked_hash(bundle/'runtime-sources'/relative, digest)

    incumbent = Path(plan['incumbent'])
    checked_hash(incumbent/'manifest.json', candidate['incumbent_manifest_sha256'])
    checked_hash(bundle/'incumbent/manifest.json', candidate['incumbent_manifest_sha256'])
    incumbent_meta = verify_bundle(incumbent, repo_root=ROOT)
    for name, entry in incumbent_meta['files'].items():
        checked_hash(bundle/'incumbent'/name, entry['sha256'])
        checked_hash(ROOT/'Assets/Resources/AI'/name, entry['sha256'])

    strength = None
    if (experiment/'result.json').exists():
        strength = audit(protocol, plan, json.loads((experiment/'result.json').read_text()))
    return dict(artifact_checks_passed=True, observed_wall=time.time(),
        bundle_files_verified=len(manifest['files']),
        frozen_policy_sha256=candidate['policy_sha256'],
        frozen_runtime_sha256=candidate['binary_sha256'], final_protocol_sha256=protocol_hash,
        launch_sources_verified=True, compiled_runtime_origin_sources_verified=True,
        production_source_inventory_unchanged=True, deployed_opponent_files_unchanged=True,
        controller_bundle_matches_evaluation=True,
        full_evaluation_audited=strength is not None,
        target_achieved=bool(strength and strength['target_achieved']), strength=strength,
        supported_runtime=controller['supported_runtime'],
        unity_export_available=controller['unity_export_available'], deployment_performed=False,
        limitation='Artifact identity checks do not establish playing strength; only the complete frozen gate can do that.')


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    for name in ('protocol', 'experiment', 'policy', 'bundle', 'output'):
        parser.add_argument('--'+name, type=Path, required=True)
    args = parser.parse_args()
    report = verify(args.protocol, args.experiment, args.policy, args.bundle)
    args.output.write_text(json.dumps(report, indent=2)+'\n')
    print(json.dumps(report, indent=2))
