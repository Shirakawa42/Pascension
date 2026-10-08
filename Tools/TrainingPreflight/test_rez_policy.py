import json
from pathlib import Path
import unittest
import torch
import rez_runtime as runtime

class RezPolicyTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        runtime.install();torch.set_num_threads(1)
        from learning_model import PolicyConfig
        from choice_policy_v11 import ChoicePolicy as Previous
        from rez_policy import ChoicePolicy
        saved=json.loads((runtime.SOURCE.parent/'identity.json').read_text())
        source=runtime.persistence.load_checkpoint(runtime.SOURCE,expected_identity=saved)['state']
        cls.old=Previous(PolicyConfig(**source['policy_config']))
        cls.old.load_state_dict(source['learner']['policy'])
        cls.new=ChoicePolicy(cls.old.config)
        weights={k:v.clone() for k,v in cls.old.state_dict().items()}
        key='core.effect_state.weight';weights[key]=torch.cat((weights[key],torch.zeros(weights[key].shape[0],256)),1)
        cls.new.load_state_dict(weights)
        cls.o=torch.zeros(4,3328);cls.c=torch.zeros(4,64,32);cls.m=torch.zeros(4,64,dtype=torch.bool)
        cls.o[:,702]=1/192;cls.o[:,894]=2/192;cls.o[:,2]=.5
        cls.m[:,:3]=True;cls.c[:,:3,0]=1;cls.c[:,:3,16]=torch.arange(1,4)/192

    def test_migration_preserves_logits_and_values(self):
        with torch.no_grad():
            for a,b in zip(self.old(self.o,self.c,self.m),self.new(self.o,self.c,self.m)):
                torch.testing.assert_close(a,b,atol=1e-5,rtol=1e-5)

    def test_ordered_memory_uses_semantics_and_unknown_is_zero(self):
        table=self.new.semantic_table();o=self.o.clone()
        for slot in (702,894,1086,1278):o[:,slot]=0
        self.assertTrue((self.new.semantic_context(o,table)[:,704:]==0).all())
        o[:,702]=1/192;o[:,894]=2/192
        c=self.new.semantic_context(o,table)
        torch.testing.assert_close(c[:,704:768],table[1].expand(len(o),-1))
        torch.testing.assert_close(c[:,768:832],table[2].expand(len(o),-1))
        self.assertTrue((c[:,832:]==0).all())

    def test_new_projection_can_learn_immediately(self):
        self.new.zero_grad();logits,value=self.new(self.o,self.c,self.m)
        (-logits.log_softmax(-1)[:,0].mean()+value.square().mean()).backward()
        g=self.new.core.effect_state.weight.grad[:,704:]
        self.assertTrue(torch.isfinite(g).all());self.assertGreater(g.abs().sum().item(),0)

if __name__=='__main__':unittest.main()
