"""Frozen copied model, gradients only, exclusive inactive-ledger benchmark."""
import sys,json,copy
from pathlib import Path
sys.path.insert(0,str(Path(__file__).resolve().parents[1]))
import torch
from choice_policy_v10 import ChoicePolicy as Old
from choice_policy_v11 import ChoicePolicy as New
from learning_model import PolicyConfig
from migrate_checkpoint import _inactive_ledger
from extended_budget_v10 import install
import campaign_state
root=Path('/home/lva/.local/share/shards-training/2026-09-26')
install();torch.set_num_threads(1)
with _inactive_ledger(root/'budget.json'):
 p=campaign_state.load_checkpoint(root/'migrated-v10/latest.soicp',expected_identity=json.loads((root/'migrated-v10/identity.json').read_text()))
 models=[cls(PolicyConfig(**p['state']['policy_config'])).cuda() for cls in (Old,New)]
 for m in models:m.load_state_dict(p['state']['learner']['policy'])
 torch.manual_seed(11)
 # Enable semantic gradients into every shared table coordinate, not just zero warm start.
 with torch.no_grad():models[0].core.effect_encoder[2].weight.normal_(0,.01)
 models[1].load_state_dict(models[0].state_dict())
 b=2048;o=torch.rand(b,3328,device='cuda');c=torch.zeros(b,64,32,device='cuda');mask=torch.zeros(b,64,device='cuda',dtype=torch.bool)
 mask[:,:20]=True;c[:,:20,0]=1;c[:,:20,16]=torch.randint(1,190,(b,20),device='cuda')/192
 # Real candidate padding is dominated by repeated null-card lookups.
 o[:,3005:3008]=torch.randint(0,190,(b,3),device='cuda')/192
 results=[];grads=[];outs=[]
 for m in models:
  def backward():
   m.zero_grad(set_to_none=True);l,v=m(o,c,mask);loss=-l.log_softmax(-1)[:,0].mean()+v.square().mean();loss.backward();return l,v
  stream=torch.cuda.Stream();stream.wait_stream(torch.cuda.current_stream())
  with torch.cuda.stream(stream):
   for _ in range(3):backward()
  torch.cuda.current_stream().wait_stream(stream);torch.cuda.synchronize()
  g=torch.cuda.CUDAGraph()
  with torch.cuda.graph(g):out=backward()
  a,z=torch.cuda.Event(enable_timing=True),torch.cuda.Event(enable_timing=True);a.record()
  for _ in range(30):g.replay()
  z.record();z.synchronize();results.append(a.elapsed_time(z)/30)
  grads.append({n:p.grad.clone() for n,p in m.named_parameters()});outs.append(tuple(x.clone() for x in out))
 for a,b in zip(outs[0],outs[1]):torch.testing.assert_close(a,b,atol=1e-5,rtol=1e-5)
 maxerr=max(float((grads[0][n]-grads[1][n]).abs().max()) for n in grads[0])
 for n in grads[0]:torch.testing.assert_close(grads[0][n],grads[1][n],atol=1e-4,rtol=1e-3)
 report={'passed':True,'v10_ms':results[0],'v11_ms':results[1],'speedup':results[0]/results[1],'max_abs_gradient_error':maxerr,'optimizer_updates':0,'scope':'Captured forward and backward, shared learned effect table, batch2048, real-shaped null padded menus'}
 Path('Tools/TrainingPreflight/results/v11-effect-backward-benchmark.json').write_text(json.dumps(report,indent=2));print(json.dumps(report))
