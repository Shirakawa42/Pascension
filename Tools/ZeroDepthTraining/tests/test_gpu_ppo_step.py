"""Independent prototype guard tests; never touch the active campaign."""
import copy, importlib.util, sys, unittest
from pathlib import Path
from types import SimpleNamespace
from unittest.mock import patch
import numpy as np
import torch
sys.path.insert(0,str(Path(__file__).resolve().parents[1]))
import train as candidate
import gpu_ppo_step as stepmod
torch.set_num_threads(1)

class ToyPolicy(torch.nn.Module):
    def __init__(self):
        super().__init__();self.w=torch.nn.Parameter(torch.tensor([.2,.1,-.1]));self.v=torch.nn.Parameter(torch.tensor(.15))
    def forward(self,obs,candidates,mask):
        return obs[:, :1]*self.w,obs[:,0]*self.v

def config():return SimpleNamespace(learning_rate=.001,fused_optimizer=False,epochs=1,minibatch=4,target_kl=.03,entropy=.01,graph=True)
def fixture():
    p=ToyPolicy();obs=torch.tensor([[1.],[2.],[.5],[1.5]]);c=torch.zeros(4,3,1);mask=torch.ones(4,3,dtype=torch.bool)
    with torch.no_grad():
        logits,v=p(obs,c,mask);packet=torch.stack((torch.zeros(4),logits.log_softmax(-1)[:,0],v),-1)
    store=SimpleNamespace(gpu=True,obs=obs,candidates=c,mask=mask,packet=packet)
    return p,store,np.arange(4),np.ones(4,np.float32),np.array([1.,-.5,.2,-.7],np.float32)
def equal_tree(test,a,b):
    if torch.is_tensor(a):test.assertTrue(torch.equal(a,b))
    elif isinstance(a,dict):
        test.assertEqual(a.keys(),b.keys())
        for k in a:equal_tree(test,a[k],b[k])
    elif isinstance(a,(list,tuple)):
        test.assertEqual(len(a),len(b))
        for x,y in zip(a,b):equal_tree(test,x,y)
    else:test.assertEqual(a,b)

class GuardTests(unittest.TestCase):
    def runner(self):
        p,store,rows,ret,adv=fixture();learner=candidate.Learner(p,config());learner.graph_learning=True
        # Seed actual Adam moments so guard equality tests cover existing state.
        for parameter in p.parameters():parameter.grad=torch.ones_like(parameter)
        learner.optimizer.step();p.load_state_dict(ToyPolicy().state_dict())
        return learner,(store,rows,ret,adv)
    def test_deadline_during_graph_setup_prevents_first_adam_step(self):
        learner,args=self.runner();stopped=[False]
        class Setup:
            def __init__(self,policy,config,inputs,optimizer,**kwargs):stopped[0]=True;self.policy=policy
            def run(self,inputs):
                for parameter in self.policy.parameters():parameter.grad=torch.ones_like(parameter)
                return [0.,.1,.5,1.]
        before=copy.deepcopy((learner.policy.state_dict(),learner.optimizer.state_dict()))
        with patch.object(stepmod,'GpuPpoStep',Setup):result=learner.update(*args,stop_check=lambda:stopped[0])
        self.assertEqual(result['optimizer_steps'],0)
        self.assertTrue(result.get('deadline_stop'))
        equal_tree(self,before,(learner.policy.state_dict(),learner.optimizer.state_dict()))
    def test_nonfinite_and_kl_guards_preserve_policy_and_existing_adam(self):
        for output,error in [([.2,.1,.5,1.],None),([float('nan'),.1,.5,1.],'likelihood'),([0.,float('inf'),.5,1.],'loss'),([0.,.1,.5,float('inf')],'gradient norm')]:
            with self.subTest(output=output):
                learner,args=self.runner()
                class Setup:
                    def __init__(self,policy,config,inputs,optimizer,**kwargs):self.policy=policy
                    def run(self,inputs):
                        for parameter in self.policy.parameters():parameter.grad=torch.full_like(parameter,float('nan'))
                        return output
                before=copy.deepcopy((learner.policy.state_dict(),learner.optimizer.state_dict()))
                with patch.object(stepmod,'GpuPpoStep',Setup):
                    if error:
                        with self.assertRaisesRegex(RuntimeError,error):learner.update(*args)
                    else:
                        result=learner.update(*args);self.assertEqual(result['optimizer_steps'],0);self.assertTrue(result['kl_early_stop'])
                equal_tree(self,before,(learner.policy.state_dict(),learner.optimizer.state_dict()))
    def test_preexisting_stop_never_constructs_graph(self):
        learner,args=self.runner()
        with patch.object(stepmod,'GpuPpoStep',side_effect=AssertionError('constructed after stop')):
            result=learner.update(*args,stop_check=lambda:True)
        self.assertEqual(result['optimizer_steps'],0);self.assertFalse(result['behavior_verification_complete'])
    def test_behavior_mismatch_rejected_before_graph_or_adam(self):
        learner,args=self.runner();args[0].packet[:,1]+=.02
        before=copy.deepcopy((learner.policy.state_dict(),learner.optimizer.state_dict()))
        with patch.object(stepmod,'GpuPpoStep',side_effect=AssertionError('constructed after mismatch')):
            with self.assertRaisesRegex(RuntimeError,'behavior mismatch'):learner.update(*args)
        equal_tree(self,before,(learner.policy.state_dict(),learner.optimizer.state_dict()))
@unittest.skipUnless(torch.cuda.is_available(), "CUDA required")
class CudaTests(unittest.TestCase):
    def inputs(self,p,size=4):
        x=torch.arange(1,size+1,device='cuda',dtype=torch.float32).reshape(-1,1)/2;c=torch.zeros(size,3,1,device='cuda');mask=torch.ones(size,3,dtype=torch.bool,device='cuda')
        with torch.no_grad():logits,v=p(x,c,mask);packet=torch.stack((torch.zeros(size,device='cuda'),logits.log_softmax(-1)[:,0],v),-1)
        return [x,c,mask,packet,torch.ones(size,device='cuda'),torch.linspace(-1,1,size,device='cuda')]
    def test_partial_full_partial_full_and_resumed_adam_bind_current_gradients(self):
        cfg=config();cfg.fused_optimizer=True;base=candidate.Learner(ToyPolicy().cuda(),cfg);graph=candidate.Learner(ToyPolicy().cuda(),cfg);base.graph_learning=False;initial=None
        for stage,size in enumerate((2,4,3,4)):
            if stage==3:
                for learner in (base,graph):learner.policy.load_state_dict(initial[0]);learner.optimizer.load_state_dict(copy.deepcopy(initial[1]))
            inputs=self.inputs(base.policy,size);store=SimpleNamespace(gpu=True,obs=inputs[0],candidates=inputs[1],mask=inputs[2],packet=inputs[3]);args=(store,np.arange(size),np.ones(size,np.float32),np.linspace(-1,1,size,dtype=np.float32));seed=np.random.RandomState(243+stage).get_state();np.random.set_state(seed);a=base.update(*args);np.random.set_state(seed);rng=torch.cuda.get_rng_state().clone();b=graph.update(*args)
            self.assertTrue(torch.equal(rng,torch.cuda.get_rng_state()));self.assertEqual((a['optimizer_steps'],a['example_passes']),(b['optimizer_steps'],b['example_passes']))
            for key in (*stepmod.DIAGNOSTIC_FIELDS[:8], 'value_explained_variance', 'effective_epochs', 'update_coverage'):
                if a[key] is None:self.assertIsNone(b[key])
                else:self.assertAlmostEqual(a[key],b[key],delta=2e-6,msg=key)
            for p,q in zip(base.policy.parameters(),graph.policy.parameters()):torch.testing.assert_close(p,q,atol=2e-6,rtol=0);torch.testing.assert_close(p.grad,q.grad,atol=2e-6,rtol=0)
            for k,state in base.optimizer.state_dict()['state'].items():
                for name,value in state.items():torch.testing.assert_close(value,graph.optimizer.state_dict()['state'][k][name],atol=2e-6,rtol=0)
            if stage==0:initial=copy.deepcopy((base.policy.state_dict(),base.optimizer.state_dict()));self.assertIsNone(graph.graph_step)
            else:self.assertIsNotNone(graph.graph_step)
    def test_graph_readback_all_eleven_values_matches_original_eager_math(self):
        p=ToyPolicy().cuda();inputs=self.inputs(p);inputs[2][0,2]=False;inputs[2][1,1:]=False
        # The policy is intentionally simple. Diagnostics still must use EACH
        # real mask row instead of a batch-wide legal-count approximation.
        targets=torch.tensor([1.,-.5,.2,.8],device='cuda');inputs[4]=targets
        inputs[3][:,1]-=torch.tensor([.3,-.3,0.,.1],device='cuda')
        original=ToyPolicy().cuda();original.load_state_dict(p.state_dict())
        logits,values=original(*inputs[:3]);all_logp=logits.log_softmax(-1)
        delta=all_logp[:,0]-inputs[3][:,1];ratio=delta.exp();kl=((ratio-1)-delta).mean()
        actor=-torch.minimum(ratio*inputs[5],ratio.clamp(.8,1.2)*inputs[5]).mean()
        mse=(values-targets).square().mean();row_entropy=-(all_logp.exp()*all_logp).sum(-1)
        loss=actor+.25*mse-config().entropy*row_entropy.mean();loss.backward()
        norm=torch.nn.utils.clip_grad_norm_(original.parameters(),.5)
        legal=inputs[2].sum(-1).float();normalized=torch.where(legal>1,row_entropy/legal.clamp_min(2).log(),0.)
        expected=torch.stack((kl.detach(),loss.detach(),row_entropy.mean().detach(),norm.detach(),
            ((ratio<.8)|(ratio>1.2)).float().mean(),normalized.mean().detach(),legal.mean(),mse.detach(),
            targets.mean(),targets.square().mean(),(values-targets).mean().detach())).cpu()
        optimizer=torch.optim.Adam(p.parameters(),lr=.001)
        step=stepmod.GpuPpoStep(p,config(),inputs,optimizer)
        actual=torch.tensor(step.run(inputs))
        self.assertEqual(len(actual),11)
        torch.testing.assert_close(actual,expected,atol=2e-6,rtol=0)
        for a,b in zip(p.parameters(),original.parameters()):torch.testing.assert_close(a.grad,b.grad,atol=2e-6,rtol=0)
    def test_stopped_warmup_joins_side_stream_and_keeps_weights_and_adam(self):
        p=ToyPolicy().cuda();o=torch.optim.Adam(p.parameters(),lr=.001);before=copy.deepcopy((p.state_dict(),o.state_dict()));stopped=[False];events=[];original=stepmod.GpuPpoStep.compute
        def delayed(step):
            torch.cuda._sleep(1000000);result=original(step);event=torch.cuda.Event();event.record();events.append(event);return result
        def beat():stopped[0]=True
        with patch.object(stepmod.GpuPpoStep,'compute',delayed):
            with self.assertRaises(stepmod.GraphSetupStopped):stepmod.GpuPpoStep(p,config(),self.inputs(p),o,stop_check=lambda:stopped[0],heartbeat=beat)
        torch.cuda.current_stream().synchronize();self.assertEqual(len(events),1);self.assertTrue(events[0].query());equal_tree(self,before,(p.state_dict(),o.state_dict()))
    def test_capture_cancellation_stops_before_adam_and_keeps_rng(self):
        cfg=config();cfg.fused_optimizer=True;learner=candidate.Learner(ToyPolicy().cuda(),cfg);inputs=self.inputs(learner.policy);store=SimpleNamespace(gpu=True,obs=inputs[0],candidates=inputs[1],mask=inputs[2],packet=inputs[3]);before=copy.deepcopy((learner.policy.state_dict(),learner.optimizer.state_dict()));beats=[0];rng=torch.cuda.get_rng_state().clone()
        def beat():beats[0]+=1
        result=learner.update(store,np.arange(4),np.ones(4,np.float32),np.linspace(-1,1,4,dtype=np.float32),stop_check=lambda:beats[0]>=5,heartbeat=beat)
        self.assertTrue(result['deadline_stop']);self.assertEqual(result['optimizer_steps'],0);self.assertEqual(beats[0],5);self.assertIsNone(learner.graph_step);equal_tree(self,before,(learner.policy.state_dict(),learner.optimizer.state_dict()));self.assertTrue(torch.equal(rng,torch.cuda.get_rng_state()))

if __name__=='__main__':unittest.main(verbosity=2)
