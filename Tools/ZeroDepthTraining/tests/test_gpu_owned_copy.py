"""Owned-prefix fast-path guards and original indexed/optional fallbacks."""
from types import SimpleNamespace
import unittest
from unittest.mock import patch
import numpy as np
import torch
import gpu_owned_copy as module
from train import Rollout


class FakeTensor:
    def __init__(self,shape,dtype=torch.float32,device='cuda',contiguous=True):
        self.shape=shape;self.ndim=len(shape);self.dtype=dtype
        self.device=torch.device(device);self.contiguous=contiguous
    def is_contiguous(self):return self.contiguous


def fake_fixture():
    store=SimpleNamespace(rows=3,capacity=20,
        obs=FakeTensor((20,9)),candidates=FakeTensor((20,4,7)),
        mask=FakeTensor((20,4),torch.bool),packet=FakeTensor((20,3)))
    actor=SimpleNamespace(obs=FakeTensor((8,9)),candidates=FakeTensor((8,4,7)),
        mask=FakeTensor((8,4)),packet=FakeTensor((8,3)))
    return store,actor


class GuardTests(unittest.TestCase):
    def test_matching_scalar_sources_and_destinations_return_fallback_before_indexing(self):
        for name in ('obs','candidates','mask','packet'):
            store,actor=fake_fixture()
            setattr(actor,name,FakeTensor(()))
            setattr(store,name,FakeTensor((),torch.bool if name=='mask' else torch.float32))
            with self.subTest(name=name),patch.object(module,'_copy_prefix') as kernel:
                self.assertFalse(module.try_copy_prefix(store,actor,1))
                kernel.__getitem__.assert_not_called()

    def test_full_capacity_int32_offset_boundary_falls_back_without_launch(self):
        for first,accepted in ((87380,True),(87400,False),(131070,False)):
            store=SimpleNamespace(rows=first,capacity=131072,obs=FakeTensor((131072,24576)),
                candidates=FakeTensor((131072,64,48)),mask=FakeTensor((131072,64),torch.bool),
                packet=FakeTensor((131072,3)))
            actor=SimpleNamespace(obs=FakeTensor((128,24576)),candidates=FakeTensor((128,64,48)),
                mask=FakeTensor((128,64)),packet=FakeTensor((128,3)))
            with self.subTest(first=first),patch.object(module,'_copy_prefix') as kernel:
                self.assertEqual(module.try_copy_prefix(store,actor,1),accepted)
                if not accepted:kernel.__getitem__.assert_not_called()

    def test_large_candidate_stride_requires_the_same_safe_offset_fallback(self):
        store,actor=fake_fixture()
        store.capacity=131072;store.rows=87400
        store.obs=FakeTensor((131072,1));actor.obs=FakeTensor((8,1))
        store.candidates=FakeTensor((131072,64,400));actor.candidates=FakeTensor((8,64,400))
        store.mask=FakeTensor((131072,64),torch.bool);actor.mask=FakeTensor((8,64))
        store.packet=FakeTensor((131072,3))
        with patch.object(module,'_copy_prefix') as kernel:
            self.assertFalse(module.try_copy_prefix(store,actor,1));kernel.__getitem__.assert_not_called()

    def test_cpu_device_never_dispatches_into_cuda_behavior_even_for_forced_graph_flag(self):
        from test_gpu_behavior import fixture
        import gpu_behavior
        learner,store=fixture()
        learner.graph_learning=True
        with patch.object(gpu_behavior,'verify_behavior',side_effect=AssertionError('Unexpected CUDA verification')):
            result=learner.verify_behavior(store,np.arange(5))
        self.assertTrue(result['behavior_verification_complete'])

    def test_foreign_store_uses_eager_verification_without_releasing_cached_owners(self):
        from test_gpu_behavior import CpuCapturedGraph,fixture
        import gpu_behavior
        learner,original=fixture()
        with patch.object(gpu_behavior,'BehaviorGraph',CpuCapturedGraph):
            gpu_behavior.verify_behavior(learner,original,np.arange(5))
        graph=learner.behavior_graph
        sources=tuple(graph.source_tensors)
        replay_count=graph.calls
        foreign=SimpleNamespace(gpu=True,**{name:getattr(original,name).clone()
            for name in ('obs','candidates','mask','packet')})
        foreign.obs.add_(.6)
        with torch.no_grad():
            logits,values=learner.policy(foreign.obs,foreign.candidates,foreign.mask)
            foreign.packet[:,1]=logits.log_softmax(-1)[:,0]
            foreign.packet[:,2]=values
        # Exercise the real CUDA dispatch condition with CPU allocations only.
        learner.graph_learning=True;learner.device=torch.device('cuda')
        original_zeros=torch.zeros;original_as_tensor=torch.as_tensor
        def on_cpu(factory):
            def create(*args,**kwargs):
                kwargs['device']='cpu'
                return factory(*args,**kwargs)
            return create
        beats=[]
        with patch.object(torch,'zeros',on_cpu(original_zeros)), \
             patch.object(torch,'as_tensor',on_cpu(original_as_tensor)), \
             patch.object(gpu_behavior,'verify_behavior',side_effect=AssertionError('Foreign store dispatched cached graph')):
            result=learner.verify_behavior(foreign,np.arange(5),heartbeat=lambda:beats.append(True))
            self.assertEqual(result,{'behavior_logp_error':0.,'behavior_value_error':0.,
                                     'behavior_verification_complete':True})
            self.assertEqual(len(beats),3)
            foreign.packet[-1,1]+=.03
            with self.assertRaisesRegex(RuntimeError,'behavior mismatch'):
                learner.verify_behavior(foreign,np.arange(5))
        self.assertIs(learner.behavior_graph,graph)
        self.assertIs(graph.store,original)
        self.assertEqual(graph.calls,replay_count)
        for retained,source in zip(graph.source_tensors,sources):
            self.assertIs(retained,source)
        original.obs=original.obs.clone()
        with patch.object(torch,'as_tensor',on_cpu(original_as_tensor)), \
             self.assertRaisesRegex(ValueError,'input tensor'):
            learner.verify_behavior(original,np.arange(5))
        self.assertIs(learner.behavior_graph,graph)
        self.assertEqual(graph.calls,replay_count)

    def test_optional_backend_and_cpu_storage_keep_original_fallback(self):
        store,actor=fake_fixture()
        with patch.object(module,'triton',None):
            self.assertFalse(module.try_copy_prefix(store,actor,5))
        store.obs.device=torch.device('cpu')
        self.assertFalse(module.try_copy_prefix(store,actor,5))

    def test_invalid_count_capacity_dtype_device_shape_and_stride_never_launch(self):
        changes=(lambda s,a:setattr(s,'rows',-1),lambda s,a:setattr(s,'rows',18),
            lambda s,a:setattr(a.obs,'dtype',torch.float16),
            lambda s,a:setattr(s.mask,'dtype',torch.float32),
            lambda s,a:setattr(a.packet,'device',torch.device('cuda:1')),
            lambda s,a:setattr(a.obs,'contiguous',False),
            lambda s,a:setattr(s.packet,'contiguous',False),
            lambda s,a:setattr(a,'candidates',FakeTensor((8,4,6))),
            lambda s,a:setattr(a,'obs',FakeTensor((4,9))))
        for change in changes:
            store,actor=fake_fixture();change(store,actor)
            with patch.object(module,'_copy_prefix') as kernel:
                self.assertFalse(module.try_copy_prefix(store,actor,5));kernel.__getitem__.assert_not_called()
        for count in (-1,1.5,True):
            store,actor=fake_fixture()
            with patch.object(module,'_copy_prefix') as kernel:
                self.assertFalse(module.try_copy_prefix(store,actor,count));kernel.__getitem__.assert_not_called()

    def test_supported_prefix_launches_all_four_complete_fields_with_owned_offset(self):
        store,actor=fake_fixture()
        with patch.object(module,'_copy_prefix') as kernel:
            self.assertTrue(module.try_copy_prefix(store,actor,5))
        kernel.__getitem__.assert_called_once_with((5,1))
        arguments=kernel.__getitem__.return_value.call_args.args
        self.assertEqual(arguments[:8],tuple(getattr(actor,name) for name in ('obs','candidates','mask','packet'))+
            tuple(getattr(store,name) for name in ('obs','candidates','mask','packet')))
        self.assertEqual(arguments[8:],(3,9,28,4,2048))

    def test_indexed_and_prefix_torch_fallbacks_keep_owned_bits_after_source_overwrite(self):
        for mapping in ((0,1),(2,0)):
            with self.subTest(mapping=mapping):
                store=Rollout.__new__(Rollout)
                store.rows=1;store.capacity=8;store.gpu=True;store.device=torch.device('cpu')
                for name,shape,dtype in (('obs',(8,3),torch.float32),('candidates',(8,2,2),torch.float32),
                                        ('mask',(8,2),torch.bool),('packet',(8,3),torch.float32)):
                    setattr(store,name,torch.zeros(shape,dtype=dtype))
                store.lanes=np.full(8,-1,np.int32);store.seats=np.full(8,-1,np.int32)
                store.rounds=np.full(8,-1,np.int32)
                actor=SimpleNamespace(obs=torch.tensor([[-0.,1.,2.],[3.,4.,5.],[6.,7.,8.]]),
                    candidates=torch.arange(12,dtype=torch.float32).reshape(3,2,2),
                    mask=torch.tensor([[1.,0.],[1.,1.],[1.,0.]]),
                    packet=torch.tensor([[0.,-.3,.2],[0.,-.4,.1],[0.,-.2,-.1]]),
                    source_rows=lambda lanes:np.asarray(mapping,np.int64))
                host=SimpleNamespace(actors=np.array([1,0,1]),mask=np.ones((3,2),np.float32),
                                     obs=np.array([[0,0,.01],[0,0,.02],[0,0,.03]],np.float32))
                expected={name:getattr(actor,name)[list(mapping)].clone() for name in ('obs','candidates','mask','packet')}
                with patch.object(module,'triton',None):store.append(host,np.zeros((3,3),np.float32),[0,2],actor=actor)
                for name in expected:
                    getattr(actor,name).fill_(99)
                    wanted=expected[name].bool() if name=='mask' else expected[name]
                    self.assertTrue(torch.equal(getattr(store,name)[1:3].contiguous().view(torch.uint8),wanted.contiguous().view(torch.uint8)))
                self.assertEqual(store.rows,3)
                np.testing.assert_array_equal(store.lanes[1:3],[0,2]);np.testing.assert_array_equal(store.seats[1:3],[1,1])
                np.testing.assert_array_equal(store.rounds[1:3],[1,3])


if __name__=='__main__':unittest.main(verbosity=2)
