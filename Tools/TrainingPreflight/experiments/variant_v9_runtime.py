"""V9 public opponent collection and soft readiness continuation."""
import hashlib,json,os,subprocess
from pathlib import Path
import numpy as np
import torch
import adaptive_actor,learning_model,learning_rollout,learning_eval,pipeline_bench,train_campaign
import rollout_fastpath
import variant_v8_runtime as v8
from choice_policy_v9 import ChoicePolicy, READINESS_INDEX, READINESS_STRENGTH
from wire_v9 import LearningHost
HERE=Path(__file__).resolve().parent
BINARY=HERE/'HostV9/bin/Release/net8.0/TrainingHostV9.dll'
BASE_V8_SOURCE='90b28d0ca5608595c147b807b3a2d37768ca2fa6e282d341f03e255256a494ce'
SCHEMA='shards-real-selfplay-v9'
_installed=False

class EpisodeStore(rollout_fastpath.ContiguousEpisodeStore):
    def __init__(self,capacity=524288,device='cuda'):
        if not 64<=capacity<=1048576:raise ValueError('Invalid rollout capacity')
        self.capacity,self.device=capacity,device
        self.obs=torch.empty(capacity,2816,device=device)
        self.candidates=torch.empty(capacity,64,32,device=device)
        self.mask=torch.empty(capacity,64,dtype=torch.bool,device=device)
        self.actions=torch.empty(capacity,dtype=torch.int64,device=device)
        self.old_logp=torch.empty(capacity,device=device);self.old_values=torch.empty(capacity,device=device)
        self.episode_ids=np.empty(capacity,dtype=np.int32);self.seats=np.empty(capacity,dtype=np.int32)
        self.rows=0;self.sealed=False;self.returns=self.advantages=self.version=None
        self._owned_storage_pointers=frozenset(getattr(self,name).untyped_storage().data_ptr()
            for name in ('obs','candidates','mask','actions','old_logp','old_values'))

def variant_identity(config):
    base,old=v8.variant_identity(config)
    if base['training_source_sha256']!=BASE_V8_SOURCE:raise RuntimeError('V9 requires exact frozen V8 lineage')
    catalog=json.loads(subprocess.check_output([pipeline_bench.DOTNET,str(BINARY),'catalog'],text=True,timeout=20))
    if catalog['cards']!=old['cards'] or catalog['ObsDim']!=2816 or catalog['ActionDim']!=32 or catalog['MaxActions']!=64:
        raise RuntimeError('Unexpected V9 catalog/shape')
    sources=[HERE/name for name in ('variant_v9_runtime.py','variant_v9_entry.py','migrate_runtime_v9.py',
        'choice_policy_v9.py','choice_logits_v9.py','wire_v9.py')]
    sources+=sorted((HERE/'HostV9').glob('*.cs'))+sorted((HERE/'HostV9').glob('*.csproj'))
    return dict(base,schema=SCHEMA,observation_schema=catalog['observationSchema'],
        host_binary_sha256=hashlib.sha256(BINARY.read_bytes()).hexdigest(),
        catalog_sha256=hashlib.sha256(json.dumps(catalog,sort_keys=True).encode()).hexdigest(),
        training_source_sha256=hashlib.sha256((BASE_V8_SOURCE+train_campaign.file_hash(sources)).encode()).hexdigest(),
        base_v8_training_source_sha256=BASE_V8_SOURCE,
        public_deck_v9={'public_permanent_collection_offset':2560,'card_count':189,
            'contract':'opponent full composition public; hand/draw partition and order private',
            'public_defense':'exact only for source-audited public defense auras'},
        readiness_prior={'strength':READINESS_STRENGTH,
            'card_to_public_predicate_slot':{str(k):v for k,v in READINESS_INDEX.items()},
            'scope':'soft pre-mixture logit penalty on12exact false ownexhaustgates; alllegalactionsremainpossible',
            'old_policies_prior':0.}),catalog


class _HostFactory(v8.v7.v6._HostFactory):
    def Popen(self,args,*positional,**kwargs):
        expected=str(pipeline_bench.ROOT/'Tools/TrainingPreflight/Host/bin/Release/net8.0/TrainingHost.dll')
        if args!=[pipeline_bench.DOTNET,expected,'serve']:raise RuntimeError('Unexpected V9 host launch')
        env=dict(kwargs.get('env') or os.environ)
        for key in list(env):
            if key.startswith('SHARDS_STATS_'):del env[key]
        v5=v8.v7.v6.v5;training=v5._training_setup.get()
        env['SHARDS_HERO_FIX_SEATS']='3' if training else str(v8.v7.v6._evaluation_fix_seats.get())
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
        v5=v8.v7.v6.v5;v4=v5.v4
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
    v8.install(statistics_directory=statistics_directory)
    for module in (learning_model,adaptive_actor,rollout_fastpath):
        module.OBS_DIM=2816
        if hasattr(module,'UPLOAD_FLOATS_PER_ROW'):module.UPLOAD_FLOATS_PER_ROW=2816+64*32+64
    pipeline_bench.subprocess=_HostFactory()
    learning_rollout.LearningHost=learning_eval.LearningHost=LearningHost
    train_campaign.LearningHost=TrainingHost
    train_campaign.EpisodeStore=EpisodeStore
    learning_model.LearningPolicy=train_campaign.LearningPolicy=ChoicePolicy
    train_campaign.identity=variant_identity
    _installed=True
