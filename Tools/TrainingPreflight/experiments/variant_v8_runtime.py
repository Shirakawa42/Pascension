"""V8 explicitly versioned information/choice and damage continuation."""
import hashlib,json,os,subprocess
from pathlib import Path
import numpy as np
import torch
import adaptive_actor,learning_model,learning_rollout,learning_eval,pipeline_bench,train_campaign
import rollout_fastpath
import variant_v7_runtime as v7
from choice_policy_v8 import ChoicePolicy
from wire_v8 import LearningHost
HERE=Path(__file__).resolve().parent
BINARY=HERE/'HostV8/bin/Release/net8.0/TrainingHostV8.dll'
BASE_V7_SOURCE='d90a6254cdba784f67d8e487b99d25f279a25e236204820b9f034a169956debb'
SCHEMA='shards-real-selfplay-v8'
_installed=False

class EpisodeStore(rollout_fastpath.ContiguousEpisodeStore):
    def __init__(self,capacity=524288,device='cuda'):
        if not 64<=capacity<=1048576:raise ValueError('Invalid rollout capacity')
        self.capacity,self.device=capacity,device
        self.obs=torch.empty(capacity,2560,device=device)
        self.candidates=torch.empty(capacity,64,32,device=device)
        self.mask=torch.empty(capacity,64,dtype=torch.bool,device=device)
        self.actions=torch.empty(capacity,dtype=torch.int64,device=device)
        self.old_logp=torch.empty(capacity,device=device);self.old_values=torch.empty(capacity,device=device)
        self.episode_ids=np.empty(capacity,dtype=np.int32);self.seats=np.empty(capacity,dtype=np.int32)
        self.rows=0;self.sealed=False;self.returns=self.advantages=self.version=None
        self._owned_storage_pointers=frozenset(getattr(self,name).untyped_storage().data_ptr()
            for name in ('obs','candidates','mask','actions','old_logp','old_values'))

def variant_identity(config):
    base,old=v7.variant_identity(config)
    if base['training_source_sha256']!=BASE_V7_SOURCE:raise RuntimeError('V8 requires exact frozen V7 lineage')
    catalog=json.loads(subprocess.check_output([pipeline_bench.DOTNET,str(BINARY),'catalog'],text=True,timeout=20))
    if catalog['cards']!=old['cards'] or catalog['ObsDim']!=2560 or catalog['ActionDim']!=32 or catalog['MaxActions']!=64:
        raise RuntimeError('Unexpected V8 catalog/shape')
    sources=[HERE/name for name in ('variant_v8_runtime.py','variant_v8_entry.py','migrate_runtime_v8.py',
        'choice_policy_v8.py','choice_logits_v8.py','wire_v8.py')]
    sources+=sorted((HERE/'HostV8').glob('*.cs'))+sorted((HERE/'HostV8').glob('*.csproj'))
    return dict(base,schema=SCHEMA,observation_schema=catalog['observationSchema'],
        host_binary_sha256=hashlib.sha256(BINARY.read_bytes()).hexdigest(),
        catalog_sha256=hashlib.sha256(json.dumps(catalog,sort_keys=True).encode()).hexdigest(),
        training_source_sha256=hashlib.sha256((BASE_V7_SOURCE+train_campaign.file_hash(sources)).encode()).hexdigest(),
        base_v7_training_source_sha256=BASE_V7_SOURCE,
        information_choice_v8={'observation_floats':2560,'legacy_prefix':2048,'supplement':512,
            'new_parameters':'zero information/menu projections and16-way pending-decision head',
            'damage':'public lethal champion allocations with Testudo overkill support',
            'conditional_exhaust':'legal; explicit public readiness; no hard disable',
            'menu_pooling':'legal candidate card embeddings inform policy AND value'}),catalog

class _HostFactory(v7.v6._HostFactory):
    def Popen(self,args,*positional,**kwargs):
        expected=str(pipeline_bench.ROOT/'Tools/TrainingPreflight/Host/bin/Release/net8.0/TrainingHost.dll')
        if args!=[pipeline_bench.DOTNET,expected,'serve']:raise RuntimeError('Unexpected V8 host launch')
        env=dict(kwargs.get('env') or os.environ)
        for key in list(env):
            if key.startswith('SHARDS_STATS_'):del env[key]
        v5=v7.v6.v5;training=v5._training_setup.get()
        env['SHARDS_HERO_FIX_SEATS']='3' if training else str(v7.v6._evaluation_fix_seats.get())
        requested=v5.v4._statistics_launch.get()
        if requested is not None:
            if not training:raise RuntimeError('Statistics outside training context')
            env.update(SHARDS_STATS_DIRECTORY=requested['directory'],SHARDS_STATS_PURPOSE='training_pool',
                SHARDS_STATS_EXPECTED_SEED=str(requested['seed']),SHARDS_STATS_EXPECTED_BATCH=str(requested['batch']))
        kwargs['env']=env
        return v5.v4.v3._original_subprocess.Popen([args[0],str(BINARY),'serve','--hero-setup',
            'curriculum-75-25' if training else 'natural'],*positional,**kwargs)

class TrainingHost(LearningHost):
    def __init__(self,*args,**kwargs):
        v5=v7.v6.v5;v4=v5.v4
        requested=None
        if v4._statistics_directory is not None:
            requested={'directory':str(v4._statistics_directory),
                'batch':kwargs.get('batch',args[0] if args else None),
                'seed':kwargs.get('seed',args[2] if len(args)>2 else 17)}
        token=v5._training_setup.set(True);stats=v4._statistics_launch.set(requested)
        try:super().__init__(*args,**kwargs)
        finally:
            v4._statistics_launch.reset(stats);v5._training_setup.reset(token)

def install(*,statistics_directory=None):
    global _installed
    if _installed:return
    variant_identity(train_campaign.TrainConfig(adaptive_actors=True))
    v7.install(statistics_directory=statistics_directory)
    for module in (learning_model,adaptive_actor,rollout_fastpath):
        module.OBS_DIM=2560
        if hasattr(module,'UPLOAD_FLOATS_PER_ROW'):module.UPLOAD_FLOATS_PER_ROW=2560+64*32+64
    pipeline_bench.subprocess=_HostFactory()
    learning_rollout.LearningHost=learning_eval.LearningHost=LearningHost
    train_campaign.LearningHost=TrainingHost
    train_campaign.EpisodeStore=EpisodeStore
    learning_model.LearningPolicy=train_campaign.LearningPolicy=ChoicePolicy
    train_campaign.identity=variant_identity
    _installed=True
