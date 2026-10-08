"""Preserved-function, distinguishability and real gradient contracts."""
import sys,unittest
from pathlib import Path
sys.path.insert(0,str(Path(__file__).resolve().parents[1]))
import torch
from choice_policy_v7 import ChoicePolicy as OldPolicy,context_bytes
from choice_policy_v8 import ChoicePolicy
class V8Tests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):torch.set_num_threads(1)
    def setUp(self):
        torch.manual_seed(9268);self.old=OldPolicy();self.new=ChoicePolicy()
        state=self.new.state_dict();state.update(self.old.state_dict());self.new.load_state_dict(state)
        self.o=torch.randn(8,2560)*.1;self.c=torch.zeros(8,64,32);self.m=torch.zeros(8,64,dtype=torch.bool)
        self.m[:,:5]=True;self.c[:,:5,0]=1;self.c[:,:5,16]=torch.arange(1,6)/192
    def run_new(self):return self.new(self.o,self.c,self.m)
    def test_preserves_v7_including_volos_and_choice_mixtures(self):
        self.o[:2,112:116]=torch.tensor(context_bytes('soi.volos'))
        self.c[:2,:,:16]=0;self.c[:2,:5,12]=1;self.c[:2,:5,25]=torch.arange(5)/128
        self.c[2:4,1:3,0]=0;self.c[2:4,1:3,7]=1
        self.c[4:6,2:5,0]=0;self.c[4:6,2:5,6]=1
        for a,b in zip(self.old(self.o[:,:2048],self.c,self.m),self.run_new()):torch.testing.assert_close(a,b,rtol=0,atol=0)
    def test_supplement_receives_gradient_and_changes_value(self):
        with torch.no_grad():self.new.core.value.weight.fill_(.1)
        logits,value=self.run_new();(value.square().sum()+logits[:,:5].square().sum()).backward()
        self.assertGreater(self.new.core.information.weight.grad.abs().sum().item(),0)
        with torch.no_grad():self.new.core.information.weight.fill_(.01)
        self.assertFalse(torch.equal(self.run_new()[1],value))
    def test_menu_contents_inform_value(self):
        with torch.no_grad():self.new.core.menu_information.weight.normal_(std=.01);self.new.core.value.weight.fill_(.1)
        a=self.run_new()[1];self.c[:,:5,16]=20/192;b=self.run_new()[1]
        self.assertFalse(torch.equal(a,b))
    def test_opposite_decisions_have_independent_logits(self):
        self.o[:,5]=3/8
        self.c[:,:,:16]=0;self.c[:,:5,12]=1;self.c[:,:5,25]=torch.arange(5)/128
        a=self.run_new()[0]
        with torch.no_grad():self.new.core.decision_head.bias[2]=4
        b=self.run_new()[0];torch.testing.assert_close(b[:,2]-a[:,2],torch.full((8,),4.))
        torch.testing.assert_close(a[:,[0,1,3,4]],b[:,[0,1,3,4]],rtol=0,atol=0)
    def test_nonmode_not_accidentally_affected(self):
        a=self.run_new()[0]
        with torch.no_grad():self.new.core.decision_head.bias.fill_(99)
        torch.testing.assert_close(a,self.run_new()[0],rtol=0,atol=0)
    def test_padding_does_not_affect_menu_value(self):
        with torch.no_grad():self.new.core.menu_information.weight.normal_(std=.01)
        a=self.run_new();self.c[:,5:,16]=99/192
        for x,y in zip(a,self.run_new()):torch.testing.assert_close(x,y,rtol=0,atol=0)
if __name__=='__main__':unittest.main()
