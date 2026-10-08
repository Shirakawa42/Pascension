"""Exclusive frozen V9-representation versus migratedV10 parity and4096games.

Both policies play the same V10 rules. The old policy receives only its2816
inputs and retains the V9 forward/kernel. No optimizer or budget session opens.
"""
import sys,json,time
from pathlib import Path
sys.path.insert(0,str(Path(__file__).resolve().parents[1]))
import torch
from torch import nn
from bench_common import save_json
from migrate_checkpoint import _inactive_ledger,_tree_digest
from migrate_runtime_v10 import prepare,PADDED
from extended_budget_v10 import install as install_budget
from choice_policy_v9 import ChoicePolicy as OldPolicy
from choice_policy_v10 import ChoicePolicy
from learning_model import PolicyConfig
import variant_v10_runtime as runtime

class OldView(nn.Module):
    def __init__(self,policy):super().__init__();self.policy=policy;self.config=policy.config
    def forward(self,o,c,m):return self.policy(o[:,:2816].contiguous(),c,m)

def main():
    root=Path('/home/lva/.local/share/shards-training/2026-09-26')
    output=Path('Tools/TrainingPreflight/results/v10-migration-parity-match.json')
    if output.exists():raise FileExistsError(output)
    install_budget();torch.set_num_threads(1)
    with _inactive_ledger(root/'budget.json'):
        payload,migration=prepare(root/'main-v9/latest.soicp')
        newer=ChoicePolicy(PolicyConfig(**payload['state']['policy_config']))
        newer.load_state_dict(payload['state']['learner']['policy'])
        older=OldPolicy(newer.config)
        old={k:v.clone() for k,v in newer.state_dict().items() if k in older.state_dict()}
        for k,n in PADDED.items():old[k]=old[k][:,:n].contiguous()
        older.load_state_dict(old);older=OldView(older)
        runtime.install()
        from wire_v10 import LearningHost
        import learning_eval
        rows=[]
        host=LearningHost(32,1,0xA000000000000000,pinned=False,transport='shared',split_branches=8)
        try:
            # Real decisions with multiple contexts; deterministic sampling on CPU.
            torch.manual_seed(100927)
            for step in range(128):
                o=torch.from_numpy(host.obs.copy());c=torch.from_numpy(host.candidates.copy());m=torch.from_numpy(host.mask.copy()).bool()
                with torch.no_grad():
                    a=older(o,c,m);b=newer(o,c,m)
                    log_error=float((a[0].log_softmax(-1)-b[0].log_softmax(-1)).abs().max());value_error=float((a[1]-b[1]).abs().max())
                    torch.testing.assert_close(a[0],b[0],atol=1e-5,rtol=1e-6);torch.testing.assert_close(a[1],b[1],atol=1e-5,rtol=1e-6)
                    if step%8==0:rows.append((o,c,m))
                    actions=torch.distributions.Categorical(logits=b[0]).sample().numpy()
                host.advance(actions)
        finally:host.close()
        torch.backends.fp32_precision='ieee';torch.backends.cuda.matmul.fp32_precision='ieee'
        newer.cuda().eval().requires_grad_(False);older.cuda().eval().requires_grad_(False)
        max_logp=max_value=0.
        with torch.inference_mode():
            for o,c,m in rows:
                o,c,m=o.cuda(),c.cuda(),m.cuda();a=older(o,c,m);b=newer(o,c,m)
                max_logp=max(max_logp,float((a[0].log_softmax(-1)-b[0].log_softmax(-1)).abs().max()))
                max_value=max(max_value,float((a[1]-b[1]).abs().max()))
        assert max_logp<=1e-4 and max_value<=1e-5
        before=[_tree_digest(p.state_dict()) for p in (newer,older)]
        torch.manual_seed(101927)
        result=learning_eval.evaluate_match(newer,older,games=4096,batch=128,workers=8,
                seed=0xA100000000000000,censor_truncated=True)
        result.update(schema='shards-v10-migration-preservation-v1',source_generation=migration['generation'],
            rules_scope='Both use correctedV10engine; B executes originalV9forward with2816prefix',
            cpu_real_rows=4096,gpu_real_rows=len(rows)*32,gpu_max_logp_error=max_logp,gpu_max_value_error=max_value,
            optimizer_updates=0,training_budget_seconds_charged=0,
            frozen_weights_unchanged=before==[_tree_digest(p.state_dict()) for p in (newer,older)])
        result['passed']=bool(result['complete'] and result['censored_games']==0 and .47<=result['score_a']<=.53
            and result['score_bound_95'][0]<=.5<=result['score_bound_95'][1] and result['frozen_weights_unchanged'])
        save_json(output,result);print(json.dumps({k:result[k] for k in ['passed','score_a','score_bound_95','censored_games','gpu_max_logp_error','seconds']}))
        if not result['passed']:raise RuntimeError('Migration parity gate failed')

if __name__=='__main__':main()
