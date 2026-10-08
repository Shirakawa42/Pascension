"""Read-only verification of the exact resources shipped in a Unity player archive.

No editor, player or simulations are launched. Tested with UnityPy 1.25.3.
API reference: https://github.com/K0lb3/UnityPy#textasset
"""
import argparse
import hashlib
import json
from pathlib import Path
import UnityPy


RESOURCES = {
    'ai/shards-policy': 'shards-policy.bytes',
    'ai/shards-search-settings': 'shards-search-settings.json',
    'ai/shards-policy-metadata': 'shards-policy-metadata.json',
    'ai/shards-inference': 'shards-inference.json',
}


def verify(archive, expected_directory):
    environment = UnityPy.load(str(archive))
    entries = {key: [] for key in RESOURCES}
    for obj in environment.objects:
        if obj.type.name != 'ResourceManager':
            continue
        manager = obj.parse_as_object()
        for key, pointer in manager.m_Container:
            key = key.lower()
            if key not in entries:
                continue
            asset = pointer.deref()
            if asset.type.name != 'TextAsset':
                entries[key].append({'type': asset.type.name})
                continue
            data = asset.parse_as_object()
            # TextAsset can contain binary weights; surrogateescape preserves
            # their bytes rather than replacing invalid UTF-8 sequences.
            payload = data.m_Script.encode('utf-8', 'surrogateescape')
            entries[key].append({'type': 'TextAsset', 'name': data.m_Name,
                                 'bytes': len(payload), 'sha256': hashlib.sha256(payload).hexdigest()})
    checks = []
    for key, filename in RESOURCES.items():
        expected = expected_directory / filename
        digest = hashlib.sha256(expected.read_bytes()).hexdigest()
        actual = entries[key]
        checks.append({'resource': key, 'expected_file': filename, 'expected_sha256': digest,
                       'actual': actual,
                       'passed': len(actual) == 1 and actual[0].get('sha256') == digest})
    return {'passed': all(c['passed'] for c in checks), 'archive': str(archive),
            'unitypy': UnityPy.__version__, 'unity_executed': False, 'read_only': True,
            'checks': checks}


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--archive', type=Path, required=True)
    parser.add_argument('--expected-directory', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    result = verify(args.archive, args.expected_directory)
    args.output.write_text(json.dumps(result, indent=2) + '\n')
    print(json.dumps({'passed': result['passed'], 'resources': len(result['checks'])}))
    raise SystemExit(0 if result['passed'] else 1)
