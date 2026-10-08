import sys,unittest
from pathlib import Path
sys.path.insert(0,str(Path(__file__).resolve().parents[1]))
import torch
from choice_policy_v8 import ChoicePolicy as Old
from migrate_runtime_v9 import expanded_state
from choice_policy_v9 import ChoicePolicy,READINESS_INDEX
class Tests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):torch.set_num_threads(1)
    def setUp(self):
        torch.manual_seed(99);self.old=Old();self.new=ChoicePolicy();s=expanded_state(self.old.state_dict(),self.new.state_dict());self.new.load_state_dict(s)
        self.o=torch.zeros(2,2816);self.c=torch.zeros(2,64,32);self.m=torch.zeros(2,64,dtype=torch.bool);self.m[:,:4]=True;self.c[:,:4,0]=1
        self.card,self.slot=next(iter(READINESS_INDEX.items()));self.c[:,1,0]=0;self.c[:,1,4]=1;self.c[:,1,16]=self.card/192
    def outputs(self,p):return p(self.o[:,:2560] if isinstance(p,Old) and not isinstance(p,ChoicePolicy) else self.o,self.c,self.m)
    def test_zero_prior_exact_preservation(self):
        self.new.readiness_strength.zero_()
        for a,b in zip(self.outputs(self.old),self.outputs(self.new)):torch.testing.assert_close(a,b,atol=0,rtol=0)
    def test_public_collection_reaches_policy_and_value_gradients(self):
        self.o[:,2560]=.3;self.o[:,2561]=.2
        logits,value=self.outputs(self.new)
        (-logits.log_softmax(-1)[:,1].mean()+value.square().mean()).backward()
        gradient=self.new.core.information.weight.grad[:,512:]
        self.assertTrue(torch.isfinite(gradient).all())
        self.assertGreater(float(gradient.abs().sum()),0.)
    def test_false_gate_soft_penalty_full_support(self):
        old,v=self.outputs(self.old);new,w=self.outputs(self.new)
        torch.testing.assert_close(new[:,1],old[:,1]-1.5)
        torch.testing.assert_close(new[:,[0,2,3]],old[:,[0,2,3]],atol=0,rtol=0)
        self.assertTrue((new.softmax(-1)[:,1]>0).all());torch.testing.assert_close(v,w,atol=0,rtol=0)
    def test_satisfied_gate_unchanged(self):
        self.o[:,self.slot]=1
        for a,b in zip(self.outputs(self.old),self.outputs(self.new)):torch.testing.assert_close(a,b,atol=0,rtol=0)
    def test_not_applied_to_other_actions_or_unmapped_cards(self):
        self.c[:,1,4]=0;self.c[:,1,0]=1
        torch.testing.assert_close(self.outputs(self.old)[0],self.outputs(self.new)[0],atol=0,rtol=0)
        self.c[:,1,4]=1;self.c[:,1,0]=0;self.c[:,1,16]=0
        torch.testing.assert_close(self.outputs(self.old)[0],self.outputs(self.new)[0],atol=0,rtol=0)
    def test_penalty_precedes_actual_mixture(self):
        self.c[:,2,0]=0;self.c[:,2,7]=1;self.c[:,3,0]=0;self.c[:,3,6]=1
        self.old.choice_exploration.zero_();base=self.outputs(self.old)[0];base[:,1]-=1.5
        expected=.92*base.softmax(-1);expected[:,2]+=.06;expected[:,3]+=.02
        actual=self.outputs(self.new)[0].softmax(-1)
        torch.testing.assert_close(actual,expected,atol=1e-7,rtol=1e-5)
        (-self.outputs(self.new)[0].log_softmax(-1)[:,1].mean()).backward()
        self.assertTrue(all(p.grad is None or torch.isfinite(p.grad).all() for p in self.new.parameters()))
if __name__=='__main__':unittest.main()
