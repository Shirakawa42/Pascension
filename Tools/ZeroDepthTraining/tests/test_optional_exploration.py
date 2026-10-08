import sys,unittest
from pathlib import Path
sys.path.insert(0,str(Path(__file__).resolve().parents[1]))
import torch
from model import optional_menu_logits,Policy,PolicyConfig
from test_learning import small_catalog

class Tests(unittest.TestCase):
    def fixture(self):
        logits=torch.tensor([[25.,-25.,-1e9],[10.,0.,-1e9]],requires_grad=True)
        candidates=torch.zeros(2,3,48);candidates[0,0,12]=1;candidates[0,1,13]=1
        candidates[1,0,0]=1;candidates[1,1,11]=1 # Play vs Concede: never explore here.
        return logits,candidates,torch.tensor([[True,True,False]]*2)
    def test_floor_only_applies_to_optional_menus_and_illegal_probability_is_zero(self):
        z,c,m=self.fixture();out=optional_menu_logits(z,c,m,torch.tensor(.02))
        p=out.softmax(-1)
        self.assertAlmostEqual(float(p[0,1]),.01,places=6)
        self.assertEqual(float(p[:,2].sum()),0)
        self.assertTrue(torch.equal(out[1],z[1]))
        (-out.log_softmax(-1)[0,1]).backward();self.assertTrue(torch.isfinite(z.grad).all())
    def test_disabled_path_preserves_exact_logits_and_gradients(self):
        z,c,m=self.fixture();out=optional_menu_logits(z,c,m,torch.tensor(0.))
        self.assertTrue(torch.equal(out,z));out.sum().backward()
        self.assertTrue(torch.equal(z.grad,torch.ones_like(z)))
    def test_legacy_archive_disables_exploration_on_reuse(self):
        p=Policy(small_catalog(),PolicyConfig(width=64));legacy={k:v.clone() for k,v in p.state_dict().items() if k!='optional_exploration'}
        p.optional_exploration.fill_(.02);p.load_state_dict(legacy)
        self.assertEqual(float(p.optional_exploration),0.)
    def test_invalid_buffer_is_rejected(self):
        p=Policy(small_catalog(),PolicyConfig(width=64));s=p.state_dict();s['optional_exploration']=torch.tensor(.8)
        with self.assertRaises(ValueError):p.load_state_dict(s)

if __name__=='__main__':unittest.main()
