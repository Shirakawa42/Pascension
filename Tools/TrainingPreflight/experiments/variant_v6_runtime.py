"""Versioned hero knowledge and pre-payment Sacrifice, on the frozen V5 learner."""
import hashlib
import json
import os
from pathlib import Path
import subprocess
import contextvars
from contextlib import contextmanager

import pipeline_bench
import train_campaign
import variant_v5_runtime as v5

HERE = Path(__file__).resolve().parent
BINARY = HERE / 'HostV6/bin/Release/net8.0/TrainingHostV6.dll'
BASE_V5_SOURCE = '93358e40bfb57b975037803e7d2caca870c6c9541220aa40c2d0477e7c3794ec'
SCHEMA = 'shards-real-selfplay-v6'
SLOTS = (702, 703, 893, 894, 895, 1085, 1086, 1087, 1277, 1278,
         1279, 1469, 1470, 1471, 1661, 1662, 1663, 1853, 1854, 1855)
PINNED_HOST = '028cfd00a1f7737f2d70847a501f69e3e094ce9cfa56a99bc94861b1f1714cfd'
_installed = False
_evaluation_fix_seats = contextvars.ContextVar('shards_v6_evaluation_fix_seats', default=3)


@contextmanager
def evaluation_seats(mask):
    if type(mask) is not int or not 0 <= mask <= 3:
        raise ValueError('Expected explicit hero-fix seat mask 0..3')
    token = _evaluation_fix_seats.set(mask)
    try:
        yield
    finally:
        _evaluation_fix_seats.reset(token)


def variant_identity(config):
    base, old_catalog = v5.variant_identity(config)
    if base['training_source_sha256'] != BASE_V5_SOURCE:
        raise RuntimeError('V6 requires the exact frozen V5 lineage')
    binary_hash = hashlib.sha256(BINARY.read_bytes()).hexdigest()
    if binary_hash != PINNED_HOST:
        raise RuntimeError('V6 binary differs from tested hero-fix host')
    catalog = json.loads(subprocess.check_output([pipeline_bench.DOTNET, str(BINARY), 'catalog'], text=True, timeout=20))
    expected = dict(old_catalog, observationSchema='shards-observation-v4')
    descriptor = catalog.get('heroFeatures')
    if not isinstance(descriptor, dict) or descriptor.get('slots') != list(SLOTS):
        raise RuntimeError('Unexpected V6 feature slots')
    expected['heroFeatures'] = descriptor
    if catalog != expected:
        raise RuntimeError('V6 changed an undeclared catalog field')
    sources = [HERE/name for name in ('variant_v6_runtime.py', 'variant_v6_entry.py', 'migrate_runtime_v6.py')]
    sources += sorted((HERE/'HostV6').glob('*.cs')) + sorted((HERE/'HostV6').glob('*.csproj'))
    identity = dict(base, schema=SCHEMA, observation_schema='shards-observation-v4',
        host_binary_sha256=binary_hash,
        catalog_sha256=hashlib.sha256(json.dumps(catalog, sort_keys=True).encode()).hexdigest(),
        training_source_sha256=hashlib.sha256((BASE_V5_SOURCE+train_campaign.file_hash(sources)).encode()).hexdigest(),
        base_v5_training_source_sha256=BASE_V5_SOURCE, hero_feature_slots=list(SLOTS), hero_features=descriptor)
    return identity, catalog


class _HostFactory:
    def __getattr__(self, name):
        return getattr(v5.v4.v3._original_subprocess, name)

    def Popen(self, args, *positional, **kwargs):
        expected = str(pipeline_bench.ROOT/'Tools/TrainingPreflight/Host/bin/Release/net8.0/TrainingHost.dll')
        if not isinstance(args, list) or args != [pipeline_bench.DOTNET, expected, 'serve']:
            raise RuntimeError('Unexpected V6 host launch')
        env = dict(kwargs.get('env') or os.environ)
        for key in list(env):
            if key.startswith('SHARDS_STATS_'):
                del env[key]
        training = v5._training_setup.get()
        env['SHARDS_HERO_FIX_SEATS'] = '3' if training else str(_evaluation_fix_seats.get())
        requested = v5.v4._statistics_launch.get()
        if requested is not None:
            if not training:
                raise RuntimeError('Statistics outside explicit training context')
            env.update(SHARDS_STATS_DIRECTORY=requested['directory'], SHARDS_STATS_PURPOSE='training_pool',
                SHARDS_STATS_EXPECTED_SEED=str(requested['seed']), SHARDS_STATS_EXPECTED_BATCH=str(requested['batch']))
        kwargs['env'] = env
        return v5.v4.v3._original_subprocess.Popen(
            [args[0], str(BINARY), 'serve', '--hero-setup', 'curriculum-75-25' if training else 'natural'],
            *positional, **kwargs)


def install(*, statistics_directory=None):
    global _installed
    if _installed:
        return
    variant_identity(train_campaign.TrainConfig(adaptive_actors=True))
    v5.install(statistics_directory=statistics_directory)
    pipeline_bench.subprocess = _HostFactory()
    train_campaign.identity = variant_identity
    _installed = True
