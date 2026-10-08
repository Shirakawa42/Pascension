"""Explicit September 27 patch continuation of the frozen, verified V12 trainer.

Old runtime/checkpoints/ledger are read-only. A new user-authorized 1200-second
ledger bounds this adaptation. Network architecture and Adam state are retained;
public effect buffers in every learner/opponent are refreshed for the new rules.
"""
import copy
import hashlib
import json
import os
from pathlib import Path
import subprocess
import sys
from dataclasses import asdict

PROJECT = Path(__file__).resolve().parents[2]
CAMPAIGN = Path('/home/lva/.local/share/shards-training/2026-09-26')
FROZEN = CAMPAIGN/'frozen-evaluation'
PATCH = CAMPAIGN/'balance-patch-20260927'
HOST = PROJECT/'Tools/BalancePatchHost/bin/Release/net8.0/BalancePatchHost.dll'
SOURCE = CAMPAIGN/'overnight-v12/retained/segment-0029/latest.soicp'
sys.path[:0] = [str(FROZEN/'Tools/TrainingPreflight/experiments'),str(FROZEN/'Tools/TrainingPreflight')]
import campaign_state as persistence
ORIGINAL_BUDGET = persistence.CampaignBudget
import train_campaign
from train_campaign import TrainConfig
import variant_v12_runtime as original
import variant_v10_runtime as v10
import effect_catalog_v10 as effects

_installed = False
_base = None
_catalog = None
_effects = None

class PatchBudget(ORIGINAL_BUDGET):
    def __init__(self,path,limit_seconds=1200,**kwargs):
        if Path(path).resolve() != PATCH/'budget.json' or limit_seconds!=1200:
            raise RuntimeError('This continuation is authorized only for its new 20-minute ledger')
        super().__init__(path,limit_seconds=1200,**kwargs)


def sha(path):
    return hashlib.sha256(Path(path).read_bytes()).hexdigest()


def variant_identity(config):
    if not _installed: raise RuntimeError('Install the explicit patch runtime first')
    sources = [Path(__file__),PROJECT/'Tools/TrainingPreflight/balance_patch_entry.py']
    for folder in ('Tools/BalancePatchHost','Assets/Scripts/Core','Assets/Scripts/Shards/Engine','Assets/Scripts/Shards/Content'):
        sources += [p for p in (PROJECT/folder).rglob('*.cs') if not {'bin','obj'} & set(p.parts)]
    sources += list((PROJECT/'Tools/BalancePatchHost').glob('*.csproj'))
    # Link dependencies are immutable reviewed V10 code; the host binary pins them too.
    digest=hashlib.sha256()
    for p in sorted(sources):digest.update(str(p.relative_to(PROJECT)).encode());digest.update(p.read_bytes())
    identity=copy.deepcopy(_base)
    identity.pop('overnight_authorization',None)
    identity.update(schema='shards-balance-patch-20260927-v1',configuration=asdict(config),
        rules_sha256=digest.hexdigest(),host_binary_sha256=sha(HOST),
        catalog_sha256=hashlib.sha256(json.dumps(_catalog,sort_keys=True).encode()).hexdigest(),
        training_source_sha256=hashlib.sha256((_base['training_source_sha256']+digest.hexdigest()).encode()).hexdigest(),
        generic_effects_v10=dict(width=512,rows=193,sha256=hashlib.sha256(json.dumps(_effects,sort_keys=True).encode()).hexdigest(),
            representation='Refreshed patched public effects; unchanged learned architecture'),
        balance_patch=dict(id='2026-09-27',source_checkpoint=str(SOURCE),source_checkpoint_sha256=sha(SOURCE),
            source_rules_sha256=_base['rules_sha256'],authorized_training_seconds=1200,
            heroes='100% randomized, balanced ordered distinct-hero pairs',
            opening_hand='1v1 seat 1: 6 cards, retaining 1 mastery; later hands 5',
            changes=['Ko Syn Wu costs 1 health','Tetra costs 3 gems','Rez Scry 3 plus passive -1 on every reroll at M5',
                     'Warpquartz copies each banished effect twice','Doom Gate defense 7',
                     'Praetorian-02 shields 4/8','Duel Cinder Scars quantity 4']))
    return identity,copy.deepcopy(_catalog)


def install(*,statistics_directory=None):
    global _installed,_base,_catalog,_effects
    if _installed:return
    saved=json.loads((SOURCE.parent/'identity.json').read_text())
    _base,_=original.variant_identity(TrainConfig(**saved['configuration']))
    if _base!=saved:raise RuntimeError('Frozen source runtime identity changed')
    original.install(statistics_directory=statistics_directory)
    # Replace the historical overnight deadline with the new, explicitly bounded grant.
    persistence.CampaignBudget=train_campaign.CampaignBudget=PatchBudget
    v10.BINARY=effects.BINARY=HOST
    effects.effect_catalog.cache_clear()
    _effects=effects.effect_catalog()
    _catalog=json.loads(subprocess.check_output([v10.pipeline_bench.DOTNET,str(HOST),'catalog'],text=True))
    if _catalog['ObsDim']!=3328 or _catalog['ActionDim']!=32 or _catalog['MaxActions']!=64:
        raise RuntimeError('Patch must preserve the learned input shapes')
    _installed=True
    train_campaign.identity=variant_identity


def migrate():
    import torch
    from bench_common import save_json
    install()
    payload=persistence.load_checkpoint(SOURCE,expected_identity=_base)
    state=copy.deepcopy(payload['state'])
    config=TrainConfig(**state['configuration'])
    config.engine_seed=0x7700000000000000
    state['configuration']=asdict(config);state['next_engine_seed']=config.engine_seed
    matrix=torch.tensor(_effects['matrix'],dtype=torch.float32)
    policies=[state['learner']['policy'],state['initial_policy'],state['champion']['policy']]
    policies += [x['policy'] for x in state['archive']]
    changes=[]
    for i,policy in enumerate(policies):
        old=policy['card_effects'];rows=(old!=matrix).any(dim=1).nonzero().flatten().tolist()
        changes.append(rows);policy['card_effects']=matrix.clone()
    expected={_catalog['cards'].index(id)+1 for id in ('doom_gate','praetorian_02_duel','warpquartz_duel')}
    if any(set(rows)!=expected for rows in changes):raise RuntimeError('Unexpected effect-matrix migration '+str(changes))
    # Preserve every learned tensor and Adam parameter; no optimizer step here.
    for key,value in payload['state']['learner']['policy'].items():
        if key!='card_effects' and not torch.equal(value,state['learner']['policy'][key]):raise RuntimeError('Learned weights changed')
    identity,_=variant_identity(config)
    destination=PATCH/'initial'
    destination.mkdir(parents=True,exist_ok=False)
    with PatchBudget(PATCH/'budget.json') as budget:
        persistence.save_checkpoint_atomic(destination/'latest.soicp',state,identity=identity,budget=budget,include_cuda_rng=False)
    save_json(destination/'identity.json',identity);save_json(PATCH/'config.json',asdict(config))
    save_json(PATCH/'migration.json',dict(source=str(SOURCE),destination=str(destination/'latest.soicp'),
        inherited_generation=state['generations'],inherited_games=state['games'],old_budget=payload['budget'],
        learned_weights_unchanged=True,optimizer_preserved=True,refreshed_policy_states=len(policies),
        changed_effect_cards=['doom_gate','praetorian_02_duel','warpquartz_duel'],
        separate_authorized_seconds=1200,new_engine_seed=config.engine_seed,identity=identity))
    reloaded=persistence.load_checkpoint(destination/'latest.soicp',expected_identity=identity)
    if not torch.equal(reloaded['state']['learner']['policy']['card_effects'],matrix):raise RuntimeError('Migration reload failed')
    return dict(checkpoint=str(destination/'latest.soicp'),generation=state['generations'],policy_states=len(policies))
