"""Independent queued actor protocol tests. CPU fakes only, no GPU touched."""
import importlib.util,sys,unittest
from pathlib import Path
from types import SimpleNamespace
from unittest.mock import patch
import numpy as np
import torch
sys.path.insert(0,str(Path(__file__).resolve().parents[1]))
import pipeline_actor as m
from adaptive import AdaptiveActor
from train import Rollout
class Stream:
 def __init__(self):self.joins=0
 def synchronize(self):self.joins+=1
class Transport:
 def stage(self,host,lanes):self.lanes=lanes.copy()
 def upload_into(self,*args):pass
class Graph:
 def replay(self):pass

def actor(stream):
 a=m.PipelineActor.__new__(m.PipelineActor);a.batch=4;a.device=torch.device('cuda:0');a.cuda=True;a.buckets=(4,);a._pending=None;a._source=np.full(4,-1,np.int64);a.active_actor=None;a.last_active_count=0
 child=SimpleNamespace(packed_transport=Transport(),graph=Graph(),obs=torch.zeros(4,1),candidates=torch.zeros(4,3,1),mask=torch.ones(4,3),packet=torch.tensor([[0.,-.4,.3]]*4),output_host=torch.empty(4,3))
 a.children={4:child};return a

def host():return SimpleNamespace(done=np.zeros(4,np.int32),obs=np.ones((4,1),np.float32),candidates=np.zeros((4,3,1),np.float32),mask=np.ones((4,3),np.float32))

class Guards(unittest.TestCase):
 def setUp(self):self.stream=Stream();self.a=actor(self.stream);self.b=actor(self.stream);self.host=host();self.cuda=patch.object(m.torch.cuda,'current_stream',return_value=self.stream);self.cuda.start()
 def tearDown(self):self.cuda.stop()
 def invalid(self,a):
  self.assertIsNone(a._pending);self.assertIsNone(a.active_actor);self.assertEqual(a.last_active_count,0)
  with self.assertRaises(ValueError):a.source_rows([0])
 def test_no_legal_action_invalidates_old_source_mapping(self):
  self.a.act(self.host);self.host.mask[0]=0
  with self.assertRaisesRegex(RuntimeError,'no legal action'):self.a.enqueue(self.host)
  self.invalid(self.a)
 def test_second_stage_validation_failure_joins_first_and_invalidates_both(self):
  self.b.act(self.host);self.host.mask[2]=0;active=np.ones(4,bool);other=np.array([False,False,True,False]);joins=self.stream.joins
  with self.assertRaisesRegex(RuntimeError,'no legal action'):m.act_pair(self.a,self.b,self.host,active,other)
  self.assertGreater(self.stream.joins,joins);self.invalid(self.a);self.invalid(self.b)
 def test_failure_after_first_finish_invalidates_both_histories(self):
  with patch.object(self.b,'finish',side_effect=RuntimeError('second finish failed')):
   with self.assertRaisesRegex(RuntimeError,'second finish failed'):m.act_pair(self.a,self.b,self.host,np.ones(4,bool),np.array([False,True,False,True]))
  self.invalid(self.a);self.invalid(self.b)
 def test_queued_reuse_refresh_rejected_and_abandon_joins(self):
  self.a.enqueue(self.host)
  with self.assertRaisesRegex(RuntimeError,'Finish queued'):self.a.enqueue(self.host)
  with self.assertRaisesRegex(RuntimeError,'Finish queued'):self.a.refresh(None)
  joins=self.stream.joins;self.a.abandon();self.assertEqual(self.stream.joins,joins+1);self.invalid(self.a)
  with self.assertRaisesRegex(RuntimeError,'No queued'):self.a.finish()
 def test_pair_success_keeps_current_sources_and_returns_owned_packets(self):
  left,right=m.act_pair(self.a,self.b,self.host,np.ones(4,bool),np.array([False,True,False,True]));np.testing.assert_array_equal(self.a.source_rows([0,2]),[0,1]);np.testing.assert_array_equal(self.b.source_rows([1,3]),[0,1]);saved=left[1].copy();self.a.children[4].output_host.fill_(999);np.testing.assert_array_equal(left[1],saved)
 def test_terminal_empty_and_bad_active_masks(self):
  self.host.done[:]=1;actions,packet=self.a.act(self.host);np.testing.assert_array_equal(actions,np.full(4,-1));np.testing.assert_array_equal(packet,np.zeros((4,3)));self.assertIsNone(self.a.active_actor)
  with self.assertRaisesRegex(ValueError,'Terminal'):self.a.enqueue(self.host,np.ones(4,bool))
  with self.assertRaisesRegex(ValueError,'boolean'):self.a.enqueue(self.host,np.ones(4,np.int32))
 def test_pair_rejects_invalid_masks_and_device_before_queue(self):
  with self.assertRaises(ValueError):m.act_pair(self.a,self.b,self.host,np.zeros(4,bool),np.ones(4,bool))
  self.b.device=torch.device('cuda:1')
  with self.assertRaisesRegex(ValueError,'same CUDA'):m.act_pair(self.a,self.b,self.host,np.ones(4,bool),np.zeros(4,bool))
  self.assertIsNone(self.a._pending)
class ToyPolicy(torch.nn.Module):
 def __init__(self):
  super().__init__();self.catalog={'obs_dim':1,'max_actions':3,'action_dim':1,'tables':{}};self.w=torch.nn.Parameter(torch.tensor([.2,.1,-.1]));self.v=torch.nn.Parameter(torch.tensor(.15));self.cached_table=None
 def cache_frozen_table(self):pass
 def forward(self,obs,candidates,mask):return (obs[:,:1]*self.w).masked_fill(~mask.bool(),-1e9),obs[:,0]*self.v

@unittest.skipUnless(torch.cuda.is_available(), "CUDA required")
class CudaTests(unittest.TestCase):
 def actors(self):
  policy=ToyPolicy().cuda();other=ToyPolicy().cuda()
  with torch.no_grad():other.w.mul_(-1);other.v.add_(.1)
  return policy,AdaptiveActor(policy,4,graph=True,packed=True,min_bucket=4),AdaptiveActor(other,4,graph=True,packed=True,min_bucket=4),m.PipelineActor(policy,4,graph=True,packed=True,min_bucket=4),m.PipelineActor(other,4,graph=True,packed=True,min_bucket=4)
 def test_serial_pair_actions_packets_and_cuda_rng_match(self):
  policy,serial,serial_other,a,b=self.actors()
  for count,opponent in ((4,2),(3,1),(1,1),(0,0),(4,0),(4,4)):
   h=host();h.obs[:,0]=np.arange(1,5)/2;active=np.arange(4)<count;other=np.arange(4)<opponent;h.done[~active]=1;rng=torch.cuda.get_rng_state();left=serial.act(h,active&~other);right=serial_other.act(h,other);expected_rng=torch.cuda.get_rng_state();torch.cuda.set_rng_state(rng);aa,bb=m.act_pair(a,b,h,active,other)
   for x,y in zip(left+right,aa+bb):np.testing.assert_array_equal(x.view(np.uint32),y.view(np.uint32))
   self.assertTrue(torch.equal(expected_rng,torch.cuda.get_rng_state()))
 def test_owned_staging_history_and_pending_refresh_cleanup(self):
  policy,serial,serial_other,a,b=self.actors();store=Rollout(16,policy.catalog,device='cuda');expected=[]
  for turn in range(2):
   h=host();h.obs[:,0]=np.arange(1,5)+turn;h.obs[0,0]=-0.;original=SimpleNamespace(**{name:getattr(h,name).copy() for name in ('obs','candidates','mask','done','actors')}) if hasattr(h,'actors') else None
   h.actors=np.arange(4,dtype=np.int32)%2;original=SimpleNamespace(**{name:getattr(h,name).copy() for name in ('obs','candidates','mask','done','actors')});a.enqueue(h,np.ones(4,bool))
   with self.assertRaisesRegex(RuntimeError,'Finish queued'):a.enqueue(h)
   with self.assertRaisesRegex(RuntimeError,'Finish queued'):a.refresh(policy)
   h.obs.fill(777);h.candidates.fill(777);h.mask.fill(0);actions,packet=a.finish();saved=packet.copy();start=store.rows;store.append(original,packet,np.arange(4),actor=a);expected.append({name:getattr(store,name)[start:store.rows].cpu().clone() for name in ('obs','candidates','mask','packet')});np.testing.assert_array_equal(a.obs[:4].cpu().numpy().view(np.uint32),original.obs.view(np.uint32))
   with torch.inference_mode():a.obs.fill_(888);a.candidates.fill_(888);a.packet.fill_(888);a.children[4].output_host.fill_(888)
   np.testing.assert_array_equal(packet.view(np.uint32),saved.view(np.uint32))
  for name in ('obs','candidates','mask','packet'):self.assertTrue(torch.equal(getattr(store,name)[:8].cpu(),torch.cat([x[name] for x in expected])))
  h=host();other=np.array([False,True,False,True]);h.mask[1]=0
  with self.assertRaisesRegex(RuntimeError,'no legal'):m.act_pair(a,b,h,np.ones(4,bool),other)
  for actor in (a,b):
   self.assertIsNone(actor._pending);self.assertIsNone(actor.active_actor)
   with self.assertRaises(ValueError):actor.source_rows([0])
  self.assertTrue(torch.cuda.current_stream().query())

if __name__=='__main__':unittest.main(verbosity=2)
