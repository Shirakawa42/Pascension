"""Stage reviewed AI resources outside Assets; never installs or publishes them."""
import argparse
import hashlib
import json
from pathlib import Path
import shutil
import uuid


def sha(path):
    return hashlib.sha256(Path(path).read_bytes()).hexdigest()


def prepare(campaign, output, runtime_tactics=None, equivalence_path=None):
    selection = json.loads((campaign / 'final-candidate-selection.json').read_text())
    policy = Path(selection['policy'])
    if sha(policy) != selection['sha256']:
        raise ValueError('Selected weights changed')
    tactics = runtime_tactics or campaign / 'final-candidate-tactics'
    checks = json.loads((tactics / 'games.json').read_text())
    manifest = json.loads((tactics / 'manifest.json').read_text())
    if (not manifest.get('completed') or manifest['policy_sha256'] != sha(policy)
            or len(checks['rows']) != 512 or not all(r['passed'] for r in checks['rows'])):
        raise ValueError('Selected model has not passed the complete tactical gate')
    # This source archive was captured with the local build, unlike a later
    # custom-host run whose working-tree archive can be merely contemporaneous.
    source_manifest = (tactics if runtime_tactics else campaign / 'sequence-repair-original-tactics') / 'compiled-source-manifest.json'
    compiled = json.loads(source_manifest.read_text())
    if not compiled['local_host']:
        raise ValueError('Missing local-build source provenance')
    strength_manifest = json.loads((campaign / 'final-candidate-independent-800/manifest.json').read_text())
    equivalence = None
    if manifest['host_sha256'] != strength_manifest['host_sha256']:
        if equivalence_path is None:
            raise ValueError('A changed runtime requires reviewed equivalence evidence')
        from verify_replay_equivalence import compare
        recorded = json.loads(equivalence_path.read_text())
        equivalence = compare(Path(recorded['reference']), Path(recorded['candidate']))
        if (equivalence != recorded or equivalence['reference_host_sha256'] != strength_manifest['host_sha256']
                or equivalence['candidate_host_sha256'] != manifest['host_sha256']
                or equivalence['policy_sha256'] != sha(policy)):
            raise ValueError('Runtime equivalence does not connect the validated and current artifacts')
    changed = [name for name, digest in compiled['sha256'].items()
               if name.startswith(('Assets/Scripts/Core/', 'Assets/Scripts/Shards/'))
               and sha(name) != digest]
    if changed:
        raise ValueError('Gameplay sources changed after validation: ' + ', '.join(changed))
    original_path = Path('Assets/Resources/AI/shards-policy-metadata.json')
    if not original_path.exists():
        original_path = Path('Assets/Resources/AI/shards-policy.json')
    original = json.loads(original_path.read_text())
    report_path = policy.parent / 'report.json'
    report = json.loads(report_path.read_text())
    evidence_path = campaign / 'final-candidate-independent-800/assessment.json'
    evidence = json.loads(evidence_path.read_text()) if evidence_path.exists() else None
    status = json.loads((campaign / 'final-candidate-validation-status.json').read_text())
    ready = status['state'] == 'completed_awaiting_evidence_review'
    output.mkdir(parents=True, exist_ok=True)
    shutil.copyfile(policy, output / 'shards-policy.bytes')
    metadata = {
        'schema': 'shards-adapted-policy-provenance-v1',
        'format': 'native-generic-effects-v1',
        'role': 'frozen_inference', 'training': False,
        'export_sha256': sha(policy),
        'width': original['width'],
        'policy_configuration': original['policy_configuration'],
        'base_policy_sha256': report['source_policy_sha256'],
        'base_checkpoint_generation': original.get('base_checkpoint_generation', original.get('checkpoint_generation')),
        'adaptation': {
            'report': str(report_path), 'report_sha256': sha(report_path),
            'scope': report['value_scope'], 'objective': report['objective'],
            'games': report['games'], 'train_games': report['train_games'],
            'held_games': report['held_games'], 'data_sha256': report['data_sha256'],
        },
        'selection': str(campaign / 'final-candidate-selection.json'),
    }
    profile = {
        'schema': 'shards-inference-profile-v2',
        'profile': 'public-hybrid-sequence-repairs-2026-09-28',
        'network_sha256': sha(policy),
        'validated_host_sha256': manifest['host_sha256'],
        'runtime_settings_resource': 'AI/shards-search-settings',
        'search_settings': checks['config'],
        'search_contract': {
            'from_opening_turn': True,
            'information': 'Public collection, public boards, own hand and remembered reveals; hidden partitions and orders sampled.',
            'depth_unit': 'Decision steps, not turns; end-turn resolution has a separate extension.',
            'candidates': 'Policy shortlist plus guarded actions and resource/menu/setup proposals.',
            'certainty': 'Bounded sampled search; neither exhaustive nor guaranteed optimal.',
        },
        'execution': {'background_worker': True, 'minimum_action_delay_seconds': 1,
                      'stale_results_discarded': True},
        'validation': {'state': 'awaiting_final_review' if ready else 'running',
                       'tactical_cases': 512, 'independent_strength': evidence,
                       'runtime_equivalence': equivalence,
                       'reference': 'Original weights and hybrid profile on the same corrected engine; not an installed-binary comparison.'},
        'balance_statistics': {'state': 'pending', 'target_games': 2500,
                               'policy': 'Identical selected AI on both seats; balanced random distinct heroes.'},
    }
    for name, data in [('shards-policy-metadata.json', metadata), ('shards-inference.json', profile),
                       ('shards-search-settings.json', checks['config'])]:
        (output / name).write_text(json.dumps(data, indent=2) + '\n')
    # The model and its metadata must not share one Resources.Load path. The
    # existing package contains two entries for ai/shards-policy (.bytes/.json).
    for asset in ['shards-policy.bytes', 'shards-policy-metadata.json', 'shards-inference.json', 'shards-search-settings.json']:
        existing = Path('Assets/Resources/AI') / (asset + '.meta')
        if asset == 'shards-policy-metadata.json' and not existing.exists():
            existing = Path('Assets/Resources/AI/shards-policy.json.meta')
        target = output / (asset + '.meta')
        if existing.exists():
            shutil.copyfile(existing, target)
        elif not target.exists():
            target.write_text('fileFormatVersion: 2\nguid: ' + uuid.uuid4().hex
                              + '\nTextScriptImporter:\n  externalObjects: {}\n  userData: \n  assetBundleName: \n  assetBundleVariant: \n')
    release = {'state': 'prepared_not_installed', 'final_validation_complete': ready,
               'rename_on_promotion': {'from': 'Assets/Resources/AI/shards-policy.json',
                                       'to': 'Assets/Resources/AI/shards-policy-metadata.json',
                                       'preserve_meta_guid': True},
               'independent_strength': evidence, 'runtime_equivalence': equivalence, 'source_manifest': str(source_manifest), 'resources': {p.name: sha(p) for p in output.iterdir() if p.is_file()},
               'required_before_promotion': ['Review final independent strength and natural games',
                                            'Generate the new isolated 2500-game cohort',
                                            'Refresh hero drafting from the selected AI cohort',
                                            'Build and verify matching game, engine, content and AI assemblies']}
    (output.parent / 'release-manifest.json').write_text(json.dumps(release, indent=2) + '\n')
    return release


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--campaign', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--runtime-tactics', type=Path)
    parser.add_argument('--equivalence', type=Path)
    args = parser.parse_args()
    result = prepare(args.campaign, args.output, args.runtime_tactics, args.equivalence)
    print(json.dumps({'state': result['state'], 'final_validation_complete': result['final_validation_complete'],
                      'resources': len(result['resources'])}))
