"""Rez adaptation in separately accounted, user-authorized ten-minute blocks."""
import copy
import hashlib
import json
import os
from pathlib import Path
import subprocess
import sys
from dataclasses import asdict

PROJECT = Path(__file__).resolve().parents[2]
ROOT = Path('/home/lva/.local/share/shards-training/2026-09-26')
FROZEN = ROOT/'frozen-evaluation'
CAMPAIGN = ROOT/'rez-repair-20260927'
SOURCE = ROOT/'balance-patch-20260927/train/latest.soicp'
HOST = PROJECT/'Tools/RezTrainingHost/bin/Release/net8.0/RezTrainingHost.dll'
TACTICAL = CAMPAIGN/'tactical-training-v2.json'
sys.path[:0] = [str(FROZEN/'Tools/TrainingPreflight/experiments'), str(FROZEN/'Tools/TrainingPreflight')]
import campaign_state as persistence
Budget = persistence.CampaignBudget
import train_campaign
import variant_v12_runtime as original
import variant_v10_runtime as v10
import effect_catalog_v10 as effects
import learning_model
from rez_policy import ChoicePolicy

_installed = False
_catalog = None

class RezBudget(Budget):
    def __init__(self, path, limit_seconds=600, **kwargs):
        if Path(path).resolve().parent.parent != CAMPAIGN or limit_seconds != 600:
            raise RuntimeError('Rez training requires a separate 600-second block ledger')
        super().__init__(path, limit_seconds=600, **kwargs)

def variant_identity(config):
    if not _installed: raise RuntimeError('Install Rez runtime first')
    sources = [Path(__file__), PROJECT/'Tools/TrainingPreflight/rez_policy.py', PROJECT/'Tools/TrainingPreflight/rez_entry.py', PROJECT/'Tools/TrainingPreflight/rez_tactical_learning.py']
    for folder in ('Tools/RezTrainingHost','Assets/Scripts/Core','Assets/Scripts/Shards/Engine','Assets/Scripts/Shards/Content','Tools/TrainingPreflight/experiments/HostV10'):
        sources += [p for p in (PROJECT/folder).rglob('*.cs') if not {'bin','obj'} & set(p.parts)]
    digest = hashlib.sha256()
    for p in sorted(sources): digest.update(str(p.relative_to(PROJECT)).encode()); digest.update(p.read_bytes())
    base = json.loads((SOURCE.parent/'identity.json').read_text())
    base.update(schema='shards-rez-ordered-memory-v3', configuration=asdict(config),
        training_source_sha256=digest.hexdigest(), rules_sha256=digest.hexdigest(),
        host_binary_sha256=hashlib.sha256(HOST.read_bytes()).hexdigest(),
        catalog_sha256=hashlib.sha256(json.dumps(_catalog,sort_keys=True).encode()).hexdigest(),
        rez_repair=dict(source_sha256=hashlib.sha256(SOURCE.read_bytes()).hexdigest(),
            memory='typed public center takes; unrelated reveals preserve known prefix',
            representation='four ordered semantic card embeddings; zero-initialized added projection',
            training='Rez in one seat every game; balanced eight ordered matchups; separate supervised tactical loss',
            tactical_dataset_sha256=hashlib.sha256(TACTICAL.read_bytes()).hexdigest(),
            authorization='repeat ten-minute blocks with tactical validation between blocks'))
    return base, copy.deepcopy(_catalog)

def install(*, statistics_directory=None):
    global _installed, _catalog
    if _installed: return
    original.install(statistics_directory=statistics_directory)
    persistence.CampaignBudget = train_campaign.CampaignBudget = RezBudget
    v10.BINARY = effects.BINARY = HOST
    effects.effect_catalog.cache_clear()
    _catalog = json.loads(subprocess.check_output([v10.pipeline_bench.DOTNET,str(HOST),'catalog'],text=True))
    learning_model.LearningPolicy = train_campaign.LearningPolicy = ChoicePolicy
    import rez_tactical_learning
    rez_tactical_learning.DATA=TACTICAL
    train_campaign.PPOLearner=rez_tactical_learning.TacticalLearner
    train_campaign.identity = variant_identity
    _installed = True

def prepare(source, block):
    import torch
    from bench_common import save_json
    install()
    source=Path(source); destination=CAMPAIGN/f'block-{block:03d}'
    destination.mkdir(parents=True,exist_ok=False)
    old_identity=json.loads((source.parent/'identity.json').read_text())
    payload=persistence.load_checkpoint(source,expected_identity=old_identity)
    state=copy.deepcopy(payload['state'])
    config=train_campaign.TrainConfig(**state['configuration'])
    if block==1:
        config.engine_seed=0x7c00000000000000
        state['next_engine_seed']=config.engine_seed
    state['configuration']=asdict(config)
    policy_states=[state['learner']['policy'],state['initial_policy'],state['champion']['policy'],*[a['policy'] for a in state['archive']]]
    key='core.effect_state.weight'
    with torch.random.fork_rng(devices=[]): model=ChoicePolicy(learning_model.PolicyConfig(**state['policy_config']))
    names=list(dict(model.named_parameters()))
    widened=False
    for p in policy_states:
        old=p[key]
        if old.shape[1]==704:
            new=old.new_zeros(old.shape[0],960);new[:,:704]=old;p[key]=new;widened=True
        model.load_state_dict(p,strict=True)
    if widened:
        optimizer=state['learner']['optimizer']
        param=optimizer['param_groups'][0]['params'][names.index(key)]
        for moment in ('exp_avg','exp_avg_sq'):
            old=optimizer['state'][param][moment]
            new=old.new_zeros(old.shape[0],960);new[:,:704]=old;optimizer['state'][param][moment]=new
    identity,_=variant_identity(config)
    save_json(destination/'config.json',asdict(config))
    initial=destination/'initial';initial.mkdir()
    with RezBudget(destination/'budget.json') as budget:
        persistence.restore_rng(payload,include_cuda=False)
        persistence.save_checkpoint_atomic(initial/'latest.soicp',state,identity=identity,budget=budget,include_cuda_rng=False)
    save_json(initial/'identity.json',identity)
    save_json(destination/'migration.json',dict(source=str(source),source_sha256=hashlib.sha256(source.read_bytes()).hexdigest(),
        generation=state['generations'],games=state['games'],widened=widened,authorized_seconds=600))
    return destination
