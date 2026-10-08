"""Training hero configuration, replay provenance and isolated orchestration."""
import contextlib
from dataclasses import asdict
import io
import json
import os
from pathlib import Path
import signal
import sys
import tempfile
import unittest
from unittest import mock
sys.path.insert(0,str(Path(__file__).resolve().parents[1]))
import numpy as np
import torch
import host
import prepare
import replay
import train
from campaign_state import load_checkpoint

CATALOG={'obs_dim':32,'max_actions':64,'action_dim':48,'card_ids':['crystal'],'histograms':[]}
class FinishedHost:
    instances=[]
    def __init__(self,batch,workers,seed,*,automation,hero_mode):
        self.batch=batch;self.hero_mode=hero_mode;self.seeds=[];self.instances.append(self);self.obs=np.zeros((batch,32),np.float32);self.candidates=np.zeros((batch,64,48),np.float32);self.mask=np.zeros((batch,64),np.float32);self.actors=np.arange(batch,dtype=np.int32)%2;self.done=np.zeros(batch,np.int32);self.rewards=np.zeros((batch,2),np.float32);self.reset(seed)
    def reset(self,seed):self.seeds.append(seed);self.done[:]=0;self.rewards[:]=0;self.mask[:]=0;self.mask[:,0]=1
    def advance(self,actions):self.done[:]=1;self.rewards[:]=[1,-1];self.mask[:]=0
    def __enter__(self):return self
    def __exit__(self,*args):pass

class HeroModeTests(unittest.TestCase):
    def test_legacy_config_defaults_policy_and_invalid_modes_fail(self):
        old=asdict(train.TrainConfig());old.pop('hero_mode');legacy=train.TrainConfig(**old);legacy.validate();self.assertEqual(legacy.hero_mode,'policy')
        new=train.TrainConfig(**old,hero_mode='balanced_random');new.validate();self.assertEqual({k:v for k,v in asdict(new).items() if k!='hero_mode'},old)
        for value in (None,True,'fixed','random','balanced'):
            with self.subTest(value=value),self.assertRaisesRegex(ValueError,'Hero mode'):train.TrainConfig(hero_mode=value).validate()
    def test_host_explicit_mode_overrides_stale_environment(self):
        for requested,inherited in (('policy','balanced_random'),('balanced_random','policy')):
            seen=[]
            def fail(*args,**kwargs):seen.append(kwargs['env']);raise OSError('spawn sentinel')
            with mock.patch.dict(os.environ,{'SHARDS_ZERO_HERO_MODE':inherited}),mock.patch.object(host,'catalog',return_value=CATALOG),mock.patch.object(host.subprocess,'Popen',side_effect=fail):
                with self.assertRaisesRegex(OSError,'spawn sentinel'):host.Host(batch=2,workers=1,hero_mode=requested)
            self.assertEqual(seen[0]['SHARDS_ZERO_HERO_MODE'],requested)
    def test_balanced_mode_never_changes_evaluation_or_paired_draft(self):
        with mock.patch.object(host,'catalog') as catalog,mock.patch.object(host.subprocess,'Popen') as popen:
            for options in ({'paired':True},{'opponent_bundle':'unused-bundle'}):
                with self.assertRaisesRegex(ValueError,'training-only'):host.Host(batch=2,hero_mode='balanced_random',**options)
            for mode in ('bad',None,True):
                with self.assertRaisesRegex(ValueError,'Hero mode'):host.Host(hero_mode=mode)
            catalog.assert_not_called();popen.assert_not_called()
    def test_trace_replay_records_requested_mode_and_defaults_old_traces_to_policy(self):
        for mode in ('balanced_random',None):
            with tempfile.TemporaryDirectory() as temporary:
                directory=Path(temporary);binary=directory/'host.dll';binary.write_bytes(b'fixture-binary');config=train.TrainConfig(batch=2,workers=1,hero_mode='balanced_random')
                with mock.patch.object(train,'BINARY',binary),mock.patch.object(train,'source_fingerprint',return_value='fixture-source'):
                    train.save_trace(directory,config=config,host_catalog=CATALOG,seed=321,generation=9,actions=[np.zeros(2,np.int32)],learner_seats=np.array([0,1]),archived=np.array([False,True]),reason='fixture')
                trace=next((directory/'diagnostics').glob('*.npz'));metadata=json.loads(trace.with_suffix('.json').read_text());self.assertEqual(metadata['hero_mode'],'balanced_random')
                if mode is None:metadata.pop('hero_mode');trace.with_suffix('.json').write_text(json.dumps(metadata))
                seen=[]
                class ReplayHost:
                    def __init__(self,*args,**kwargs):seen.append(kwargs['hero_mode']);self.catalog=CATALOG;self.done=np.array([1,1])
                    def advance(self,actions):np.testing.assert_array_equal(actions,[0,0])
                    def __enter__(self):return self
                    def __exit__(self,*args):pass
                with mock.patch.object(replay,'Host',ReplayHost):result=replay.replay(trace,binary=binary)
                self.assertEqual(seen,[mode or 'policy']);self.assertEqual(result['hero_mode'],mode or 'policy');self.assertEqual(result['learning_updates'],0)
    def test_train_passes_mode_through_cohorts_resume_and_metrics(self):
        FinishedHost.instances=[];config=train.TrainConfig(batch=2,workers=1,width=64,capacity=64,minibatch=16,epochs=1,archive_fraction=0,graph=False,adaptive_actors=False,packed_inputs=False,fused_optimizer=False,hero_mode='balanced_random');pinned={'schema':'isolated-hero-mode-fixture','configuration':asdict(config)};previous={sig:signal.getsignal(sig) for sig in (signal.SIGTERM,signal.SIGINT)}
        try:
            with tempfile.TemporaryDirectory() as temporary:
                directory=Path(temporary)
                raw_ticks=iter(range(0,10000,10))
                with mock.patch.object(train.time,'CLOCK_MONOTONIC_RAW',4,create=True),mock.patch.object(train.time,'clock_gettime',side_effect=lambda clock:next(raw_ticks),create=True),mock.patch.object(train,'Host',FinishedHost),mock.patch.object(train,'catalog',return_value=CATALOG),mock.patch.object(train,'identity',return_value=pinned),mock.patch.object(train.Learner,'update',return_value={'optimizer_steps':0,'example_passes':0}),contextlib.redirect_stdout(io.StringIO()):
                    checkpoint=train.train(config,directory,120,max_games=4,device='cpu');first=load_checkpoint(checkpoint,expected_identity=pinned);train.train(config,directory,120,max_games=6,resume=checkpoint,device='cpu')
                final=load_checkpoint(checkpoint,expected_identity=pinned);self.assertEqual(first['state']['configuration']['hero_mode'],'balanced_random');self.assertEqual(final['state']['configuration']['hero_mode'],'balanced_random');self.assertEqual(final['state']['games'],6);self.assertEqual(final['state']['optimizer_steps'],0)
                self.assertEqual([(h.hero_mode,h.seeds) for h in FinishedHost.instances],[('balanced_random',[config.engine_seed,config.engine_seed+2]),('balanced_random',[config.engine_seed+4])])
                events=[json.loads(line) for line in (directory/'metrics.jsonl').read_text().splitlines()];self.assertTrue(all(isinstance(e['monotonic'],(float,int)) and isinstance(e['boot_id'],str) and e['boot_id'] for e in events));self.assertTrue(all(events[i]['monotonic']<=events[i+1]['monotonic'] for i in range(len(events)-1)))
                self.assertTrue(all(events[i]['monotonic_raw']<events[i+1]['monotonic_raw'] for i in range(len(events)-1)))
                for event in events:
                    if event['event']=='generation':
                        self.assertEqual(event['seconds_clock'],'monotonic_raw');self.assertEqual(event['seconds'],10);self.assertEqual(event['games_per_second'],event['completed']/10)
        finally:
            for sig,handler in previous.items():signal.signal(sig,handler)
    def test_fresh_prepare_uses_balanced_random_and_explicit_config_is_respected(self):
        for explicit,expected in ((None,'balanced_random'),(train.TrainConfig(),'policy')):
            with tempfile.TemporaryDirectory() as temporary,mock.patch.object(prepare.subprocess,'run'),mock.patch.object(prepare,'freeze_incumbent',return_value={'files':{'shards-policy.bytes':{'sha256':'fixture'}}}),mock.patch.object(prepare,'catalog',return_value=CATALOG),mock.patch.object(prepare,'identity',side_effect=lambda config,cat:{'configuration':asdict(config)}),mock.patch.object(torch.cuda,'is_available',return_value=False):
                directory=Path(temporary)/'fresh';prepare.prepare(directory,explicit,build=False);self.assertEqual(json.loads((directory/'config.json').read_text())['hero_mode'],expected);self.assertFalse(json.loads((directory/'prepared.json').read_text())['training_started'])
    def test_prepare_cli_without_config_prepares_balanced_random(self):
        with tempfile.TemporaryDirectory() as temporary,mock.patch.object(prepare.subprocess,'run'),mock.patch.object(prepare,'freeze_incumbent',return_value={'files':{'shards-policy.bytes':{'sha256':'fixture'}}}),mock.patch.object(prepare,'catalog',return_value=CATALOG),mock.patch.object(prepare,'identity',side_effect=lambda config,cat:{'configuration':asdict(config)}),mock.patch.object(torch.cuda,'is_available',return_value=False),contextlib.redirect_stdout(io.StringIO()):
            directory=Path(temporary)/'fresh'
            with mock.patch.object(sys,'argv',['prepare.py','--output',str(directory),'--no-build']):prepare.main()
            self.assertEqual(json.loads((directory/'config.json').read_text())['hero_mode'],'balanced_random')
    def test_unavailable_raw_clock_does_not_fabricate_telemetry(self):
        config=train.TrainConfig(batch=2,workers=1,width=64,capacity=64,minibatch=16,epochs=1,archive_fraction=0,graph=False,adaptive_actors=False,packed_inputs=False,fused_optimizer=False,hero_mode='balanced_random');pinned={'schema':'isolated-clock-fallback','configuration':asdict(config)};previous={sig:signal.getsignal(sig) for sig in (signal.SIGTERM,signal.SIGINT)}
        try:
            with tempfile.TemporaryDirectory() as temporary,mock.patch.object(train.time,'CLOCK_MONOTONIC_RAW',None,create=True),mock.patch.object(train,'Host',FinishedHost),mock.patch.object(train,'catalog',return_value=CATALOG),mock.patch.object(train,'identity',return_value=pinned),mock.patch.object(train.Learner,'update',return_value={'optimizer_steps':0,'example_passes':0}),contextlib.redirect_stdout(io.StringIO()):
                directory=Path(temporary);train.train(config,directory,120,max_games=2,device='cpu')
                events=[json.loads(line) for line in (directory/'metrics.jsonl').read_text().splitlines()];self.assertTrue(all('monotonic_raw' not in event for event in events))
                generation=next(event for event in events if event['event']=='generation');self.assertEqual(generation['seconds_clock'],'monotonic');self.assertGreater(generation['seconds'],0)
        finally:
            for sig,handler in previous.items():signal.signal(sig,handler)
if __name__=='__main__':unittest.main(verbosity=2)
