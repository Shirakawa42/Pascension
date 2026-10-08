"""CPU frozen-policy response to a constructed public-opponent-collection contrast.
No game-strength assertion; migrated zero columns intentionally preserve old logits.
"""
import argparse,json,sys
from pathlib import Path
sys.path.insert(0,str(Path(__file__).resolve().parents[1]))
import cpu_shadow_eval as shadow
import torch
from bench_common import save_json


def main():
 p=argparse.ArgumentParser(description=__doc__);p.add_argument('--initial',type=Path,required=True);p.add_argument('--trained',type=Path,required=True);p.add_argument('--output',type=Path,required=True);args=p.parse_args()
 if args.output.exists():raise ValueError('Use new output')
 status=args.trained.parent/'status.json'
 if status.exists() and json.loads(status.read_text()).get('state')=='running':raise RuntimeError('Wait for bounded pilot completion')
 shadow.configure_cpu();shadow.configure_variant('v9')
 selections=shadow.frozen_selections(args.initial,'learner',args.trained,'learner')
 fixture=json.loads((Path(__file__).resolve().parents[1]/'results/reroll-composition-alias-v9.json').read_text())
 assert fixture['legacyPrefixUnchanged'] and fixture['candidateMaskUnchanged'] and fixture['hiddenMembershipInvariant'] and fixture['hiddenDrawOrderInvariant'] and not fixture['exactCompositionAlias']
 rows=[]
 for role,selection in zip(('migrated_initial','post_pilot'),selections):
  policy=shadow.checkpoints.materialize_policy(selection,'cpu');before=shadow.checkpoints.policy_hash(policy);responses=[];all_probs=[];all_values=[]
  info=policy.core.information.weight[:,512:];decision=policy.core.decision_head.weight[:,policy.config.width+512:]
  if role=='migrated_initial':assert torch.count_nonzero(info)==0 and torch.count_nonzero(decision)==0
  for case in fixture['cases']:
   flat=torch.tensor(case['flat'],dtype=torch.float32);o=flat[:2816][None];c=flat[2816:4864].reshape(1,64,32);m=flat[4864:].bool()[None]
   with torch.inference_mode():logits,values=policy(o,c,m);probs=logits.softmax(-1)[0]
   choices=[]
   for slot in torch.where(m[0])[0].tolist():
    code=round(float(c[0,slot,16])*192)-1
    choices.append({'slot':slot,'kind':shadow.KINDS[int(c[0,slot,:16].argmax())], 'card':selection.catalog['cards'][code] if code>=0 else None,'probability':float(probs[slot])})
   rerolls=[x for x in choices if x['kind']=='reroll'];mass=sum(x['probability'] for x in rerolls)
   responses.append({'name':case['name'],'reroll_probability':mass,'rerolls':rerolls,'top5_actions':sorted(choices,key=lambda x:x['probability'],reverse=True)[:5],'value':float(values[0])})
   all_probs.append(probs);all_values.append(float(values[0]))
  full_tv=float((all_probs[0]-all_probs[1]).abs().sum()/2)
  reroll_ids=[x['slot'] for x in responses[0]['rerolls']]
  pa=all_probs[0][reroll_ids];pb=all_probs[1][reroll_ids]
  conditional_tv=float(((pa/pa.sum())-(pb/pb.sum())).abs().sum()/2) if pa.sum()>0 and pb.sum()>0 else None
  if role=='migrated_initial':assert torch.equal(all_probs[0],all_probs[1]) and all_values[0]==all_values[1]
  assert before==shadow.checkpoints.policy_hash(policy)
  rows.append({'role':role,'policy':selection.metadata,'policy_sha256':before,'public_information_weight_l2':float(info.norm()),'public_decision_weight_l2':float(decision.norm()),'responses':responses,
    'composition_response':{'full_distribution_total_variation':full_tv,'conditional_reroll_target_total_variation':conditional_tv,'reroll_probability_difference_order_minus_undergrowth':responses[1]['reroll_probability']-responses[0]['reroll_probability'],'value_difference_order_minus_undergrowth':all_values[1]-all_values[0]}})
 assert not torch.cuda.is_initialized()
 report={'schema':'shards-v9-public-collection-response-v1','fixture_contract':{k:v for k,v in fixture.items() if k!='cases'},'policies':rows,'cuda_initialized':False,'optimizer_updates':0,
   'scope':'Constructed sparse engine positions, not full seeded legal replay or a strength evaluation. Different public opponent collections with exact fixed original2560prefix and identical candidates/masks. Initial zero columns must produce equal responses; post-pilot sensitivity establishes use, not strategic correctness. Privacy invariance tested separately on hidden hand/deck membership and draw-order permutations.'}
 save_json(args.output,report);print(json.dumps([{k:r[k] for k in ('role','public_information_weight_l2','public_decision_weight_l2','composition_response')} for r in rows],indent=2))
if __name__=='__main__':main()
