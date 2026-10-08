import sys,unittest,copy
from pathlib import Path
from unittest.mock import patch
sys.path.insert(0,str(Path(__file__).resolve().parents[1]))
import torch
from choice_policy_v9 import ChoicePolicy as Old
from choice_policy_v10 import ChoicePolicy
from migrate_runtime_v10 import expanded_state

class Contracts(unittest.TestCase):
    @classmethod
    def setUpClass(cls):torch.set_num_threads(1)
    def setUp(self):
        torch.manual_seed(10927)
        matrix=torch.zeros(193,512);matrix[1,3]=1;matrix[2,3]=4;matrix[3,4]=2
        self.old=Old()
        with patch('choice_policy_v10.effect_matrix',return_value=matrix):self.new=ChoicePolicy()
        self.new.load_state_dict(expanded_state(self.old.state_dict(),self.new.state_dict()))
        self.o=torch.zeros(4,3328);self.c=torch.zeros(4,64,32);self.m=torch.zeros(4,64,dtype=torch.bool)
        self.m[:,:3]=True;self.c[:,:3,0]=1;self.c[:,0,16]=1/192;self.c[:,1,16]=2/192;self.c[:,2,16]=3/192
        self.o[:,128]=.2;self.o[:,2560]=.3;self.o[:,3005]=3/192
    def test_zero_residual_preserves_old_policy_with_arbitrary_new_public_inputs(self):
        self.o[:,2816:]=torch.randn(4,512)
        with torch.no_grad():
            a=self.old(self.o[:,:2816].contiguous(),self.c,self.m);b=self.new(self.o,self.c,self.m)
        for x,y in zip(a,b):torch.testing.assert_close(x,y,atol=1e-6,rtol=1e-6)
    def test_semantics_receives_gradient_immediately(self):
        logit,value=self.new(self.o,self.c,self.m)
        (-logit.log_softmax(-1)[:,1].mean()+value.square().mean()).backward()
        g=self.new.core.effect_encoder[2].weight.grad
        self.assertTrue(torch.isfinite(g).all());self.assertGreater(g.abs().sum().item(),0)
    def test_effect_change_affects_outputs_after_semantic_learning(self):
        with torch.no_grad():
            self.new.core.effect_encoder[2].weight.normal_(0,.1)
            self.new.core.value.weight.normal_(0,.1)
        before=self.new(self.o,self.c,self.m)
        with torch.no_grad():self.new.card_effects[1,3]=8
        after=self.new(self.o,self.c,self.m)
        self.assertGreater((before[0]-after[0]).abs().max().item(),1e-7)
        self.assertGreater((before[1]-after[1]).abs().max().item(),1e-7)
    def test_legacy_padding_is_not_treated_as_card_ownership(self):
        with torch.no_grad():self.new.core.effect_encoder[2].weight.normal_(0,.1)
        table=self.new.semantic_table();before=self.new.semantic_context(self.o,table)
        for channel in range(9):self.o[:,128+channel*192+189:128+(channel+1)*192]=999
        torch.testing.assert_close(before,self.new.semantic_context(self.o,table),atol=0,rtol=0)
    def test_frozen_actor_cache_refreshes_without_changing_captured_addresses(self):
        frozen=copy.deepcopy(self.new).eval().requires_grad_(False)
        with torch.inference_mode():frozen(self.o,self.c,self.m)
        pointers=[frozen._effect_cache.data_ptr(),frozen._embedding_cache.data_ptr()]
        with torch.no_grad():self.new.core.effect_encoder[2].weight.normal_(0,.1)
        frozen.load_state_dict(self.new.state_dict())
        self.assertEqual(pointers,[frozen._effect_cache.data_ptr(),frozen._embedding_cache.data_ptr()])
        for a,b in zip(frozen(self.o,self.c,self.m),self.new(self.o,self.c,self.m)):
            torch.testing.assert_close(a,b,atol=1e-6,rtol=1e-6)

    def test_null_and_padded_descriptors_stay_zero(self):
        with torch.no_grad():self.new.core.effect_encoder[2].weight.normal_(0,.1)
        self.assertTrue((self.new.semantic_table()[[0,190,191,192]]==0).all())

if __name__=='__main__':unittest.main()
