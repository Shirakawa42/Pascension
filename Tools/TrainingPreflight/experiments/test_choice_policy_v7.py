"""CPU numerical contracts for on-policy exploration and mode separation."""
import sys
from pathlib import Path
sys.path.insert(0,str(Path(__file__).resolve().parents[1]))
import unittest
import torch
from learning_model import LearningPolicy
from choice_policy_v7 import ChoicePolicy,context_bytes


class ChoicePolicyTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):torch.set_num_threads(1)
    def setUp(self):
        torch.manual_seed(93026)
        self.old=LearningPolicy()
        self.new=ChoicePolicy()
        state=self.new.state_dict();state.update(self.old.state_dict());self.new.load_state_dict(state)
        self.obs=torch.randn(6,2048)*.1
        self.c=torch.zeros(6,64,32);self.mask=torch.zeros(6,64,dtype=torch.bool)
        self.mask[:,:6]=True;self.c[:,:,28]=-1
        self.c[:,:6,0]=1
    def forward(self,p=None):return (p or self.new)(self.obs,self.c,self.mask)
    def test_zero_migration_preserves_every_logit_and_value(self):
        self.new.choice_exploration.zero_()
        old=self.forward(self.old);new=self.forward()
        for a,b in zip(old,new):torch.testing.assert_close(a,b,rtol=0,atol=0)
    def test_zero_head_preserves_volos_logits(self):
        self.new.choice_exploration.zero_();self.obs[:,112:116]=torch.tensor(context_bytes('soi.volos'))
        self.c[:,:,:16]=0;self.c[:,:4,12]=1;self.c[:,:4,25]=torch.arange(4)/128
        self.mask[:,4:]=False
        for a,b in zip(self.forward(self.old),self.forward()):torch.testing.assert_close(a,b,rtol=0,atol=0)
    def test_interior_mode_has_independent_score_and_context_guard(self):
        self.obs[:,112:116]=torch.tensor(context_bytes('soi.volos'))
        self.c[:,:,:16]=0;self.c[:,:4,12]=1;self.c[:,:4,25]=torch.arange(4)/128;self.mask[:,4:]=False
        baseline=self.forward()[0]
        with torch.no_grad():self.new.core.volos_head.bias[2]=6
        after=self.forward()[0]
        torch.testing.assert_close(after[:,2]-baseline[:,2],torch.full((6,),6.))
        torch.testing.assert_close(after[:,[0,1,3]],baseline[:,[0,1,3]],rtol=0,atol=0)
        self.obs[:,112:116]=torch.tensor(context_bytes('soi.banish'))
        torch.testing.assert_close(self.forward()[0],self.forward(self.old)[0],rtol=0,atol=0)
    def choices(self):
        self.c[:,1:4,0]=0;self.c[:,1:4,7]=1
        self.c[:,4:6,0]=0;self.c[:,4:6,6]=1
    def test_exact_mixture_and_legal_support(self):
        self.choices();old=self.forward(self.old)[0].softmax(-1);actual=self.forward()[0].softmax(-1)
        expected=.92*old;expected[:,1:4]+=.06/3;expected[:,4:6]+=.02/2
        torch.testing.assert_close(actual,expected,rtol=1.e-5,atol=1.e-7)
        self.assertTrue(torch.all(actual[:,1:4]>=.02));self.assertTrue(torch.all(actual[:,4:6]>=.01))
        self.assertEqual(int(torch.count_nonzero(actual[:,6:])),0)
    def test_missing_category_does_not_take_probability_mass(self):
        self.c[:,1:4,0]=0;self.c[:,1:4,7]=1
        old=self.forward(self.old)[0].softmax(-1);actual=self.forward()[0].softmax(-1)
        expected=.94*old;expected[:,1:4]+=.02
        torch.testing.assert_close(actual,expected,rtol=1.e-5,atol=1.e-7)
    def test_no_legal_choices_exact_original_output_and_finite_backward(self):
        # Disabled relic must not activate the mixture.
        self.c[:,9,7]=1
        a,v=self.forward();b,w=self.forward(self.old)
        torch.testing.assert_close(a,b,rtol=0,atol=0)
        loss=-a.log_softmax(-1)[:,0].mean()+v.square().mean();loss.backward()
        self.assertTrue(all(p.grad is None or torch.isfinite(p.grad).all() for p in self.new.parameters()))
    def test_mixture_gradients_finite_including_zero_exploration(self):
        self.choices()
        for epsilon in ([.06,.02],[0.,0.],[.06,0.]):
            self.new.choice_exploration.copy_(torch.tensor(epsilon));self.new.zero_grad()
            logits,values=self.forward();loss=-logits.log_softmax(-1)[:,2].mean()+values.square().mean();loss.backward()
            self.assertTrue(all(p.grad is None or torch.isfinite(p.grad).all() for p in self.new.parameters()))
    def test_nonchoice_action_value_unchanged(self):
        self.choices()
        torch.testing.assert_close(self.forward()[1],self.forward(self.old)[1],rtol=0,atol=0)

if __name__=='__main__':unittest.main()
