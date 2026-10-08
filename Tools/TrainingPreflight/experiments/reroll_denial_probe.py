"""Frozen CPU reroll usage and public-opponent-context diagnostics.
No action overrides; faction affinities are descriptive proxies, never Q values.
"""
import argparse,json,sys,time
from collections import Counter,defaultdict
from pathlib import Path
from unittest.mock import patch
sys.path.insert(0,str(Path(__file__).resolve().parents[1]))
import cpu_shadow_eval as shadow
from hero_curriculum.balanced_evaluation import run_balanced_matrix
from wire_v8 import LearningHost
import numpy as np
import torch
from bench_common import save_json


def main():
 p=argparse.ArgumentParser(description=__doc__);p.add_argument('--checkpoint',type=Path,required=True);p.add_argument('--output',type=Path,required=True);args=p.parse_args()
 if args.output.exists():raise ValueError('Use new output')
 shadow.configure_cpu();shadow.configure_variant('v8')
 selection,_=shadow.frozen_selections(args.checkpoint,'learner',args.checkpoint,'learner')
 policy=shadow.checkpoints.materialize_policy(selection,'cpu');before=shadow.checkpoints.policy_hash(policy)
 cards=selection.catalog['cards'];metadata=json.loads((Path(__file__).resolve().parents[1]/'results/balance-card-catalog-v6.json').read_text())['cards']
 assert [r['id'] for r in metadata]==cards
 factions=['None','Homodeus','Undergrowth','Order','Wraethe','Aion','Monster']
 faction_codes=np.asarray([factions.index(r['faction']) for r in metadata])
 counts=Counter();by_card=defaultdict(Counter);bins=defaultdict(Counter);remembered={};turn_rerolls=defaultdict(list);examples=[]
 original=shadow.ObservedHost.advance_active
 def compute(actor):
  logits,values=actor.compute_logits();logp=logits.log_softmax(-1);prob=logp.exp()
  actor.diagnostics={'entropy':(-(prob*logp).sum(-1)).numpy().copy(),'max_probability':prob.max(-1).values.numpy().copy(),'legal_count':actor.mask.sum(-1).numpy().astype(np.int64),'probabilities':prob.numpy().copy()}
  return shadow.sample_actions(logits,values)
 def observe(host,actions,active):
  for lane in np.flatnonzero(active):
   seat=int(host.seats[lane]);role=int(seat!=host.seat_a);ob=host.obs[lane];cs=host.candidates[lane];legal=np.flatnonzero(host.mask[lane]==1);kind=cs[:,:16].argmax(-1);action=int(actions[lane]);chosen=int(kind[action])
   key=(host.seed+int(lane),host.seat_a,seat);enemykey=(host.seed+int(lane),host.seat_a,1-seat)
   own=np.rint(ob[320:509]*10);remembered[key]=(own.copy(),round(float(ob[2])*100))
   enemy,age=(remembered[enemykey][0],round(float(ob[2])*100)-remembered[enemykey][1]) if enemykey in remembered else (None,None)
   counts['acting_decisions']+=1
   rerolls=[s for s in legal if kind[s]==8]
   if rerolls:
    counts['menus_with_reroll']+=1
    gems=round(float(ob[18])*20);price=round(float(ob[44])*20);em=round(float(ob[65])*30)
    probabilities=host.packets[role][1]['probabilities'][lane]
    for slot in rerolls:
     code=round(float(cs[slot,16])*192)-1;card=cards[code];faction=faction_codes[code];selected=slot==action
     row=by_card[card];row['candidate_opportunities']+=1;row['probability_sum']+=float(probabilities[slot]);row['selected']+=int(selected)
     row['selected_printed_cost_sum']+=int(selected)*metadata[code]['cost']
     label='no_recent_enemy_summary'
     if enemy is not None and faction in (1,2,3,4,5):
      oa=float(own[faction_codes==faction].sum()/max(1,own[(faction_codes>0)&(faction_codes<6)].sum()))
      ea=float(enemy[faction_codes==faction].sum()/max(1,enemy[(faction_codes>0)&(faction_codes<6)].sum()))
      label='enemy_affinity_higher_20pp' if ea-oa>=.2 else 'own_affinity_higher_20pp' if oa-ea>=.2 else 'similar_affinity'
      row['enemy_minus_own_affinity_sum']+=ea-oa;row['affinity_opportunities']+=1
     elif faction not in (1,2,3,4,5):label='no_single_faction'
     bins[label]['candidate_opportunities']+=1;bins[label]['probability_sum']+=float(probabilities[slot]);bins[label]['selected']+=int(selected)
     if selected:
      counts['rerolls']+=1;counts['reroll_price_'+str(price)]+=1;counts['gem_cost_sum']+=price;counts['target_printed_cost_'+str(metadata[code]['cost'])]+=1
      counts['opponent_mastery_15plus']+=int(em>=15);counts['opponent_mastery_30']+=int(em>=30)
      counts['can_buy_same_card_now']+=int(any(kind[s]==1 and round(float(cs[s,16])*192)-1==code for s in legal))
      counts['can_fastplay_same_card_now']+=int(any(kind[s]==2 and round(float(cs[s,16])*192)-1==code for s in legal))
      other_buys=[round(float(cs[s,24])*13) for s in legal if kind[s]==1 and cs[s,25]!=cs[slot,25]]
      counts['price_removes_all_existing_other_buy_options']+=int(bool(other_buys) and min(other_buys)>gems-price)
      counts['free_rez_discount']+=int(price==0 and round(float(ob[22])*5)==5)
      event={'card':card,'gems':gems,'reroll_price':price,'printed_cost':metadata[code]['cost'],'opponent_mastery':em,'affinity_bin':label,'enemy_summary_round_age':age,'own_buys_after':0}
      turn_rerolls[key].append(event)
      if len(examples)<30:examples.append(dict(event,game=key))
   if chosen in (1,2):
    for event in turn_rerolls[key]:event['own_buys_after']+=1
   if chosen==10:
    for event in turn_rerolls.pop(key,[]):counts['reroll_without_later_own_buy_or_fastplay_before_end']+=int(event['own_buys_after']==0)
  answer=original(host,actions,active)
  for lane in np.flatnonzero(active&(host.done!=0)):
   for seat in (0,1):
    key=(host.seed+int(lane),host.seat_a,seat);remembered.pop(key,None);turn_rerolls.pop(key,None)
  return answer
 fixture=json.loads((Path(__file__).resolve().parents[1]/'results/reroll-composition-alias-v8.json').read_text())
 responses=[]
 for case in fixture['cases']:
  flat=torch.tensor(case['flat'],dtype=torch.float32);o=flat[:2560][None];c=flat[2560:4608].view(1,64,32);m=flat[4608:].bool()[None]
  with torch.inference_mode():logits,value=policy(o,c,m);prob=logits.softmax(-1)[0]
  choices=[]
  for s in torch.where(m[0]&(c[0,:,8]==1))[0].tolist():
   choices.append({'card':cards[round(float(c[0,s,16])*192)-1],'slot':s,'probability':float(prob[s])})
  responses.append({'name':case['name'],'reroll_probability':sum(x['probability'] for x in choices),'rerolls':choices,'value':float(value[0])})
 assert responses[0]['rerolls']==responses[1]['rerolls'] and responses[0]['value']==responses[1]['value']
 with patch.object(shadow.CPUActor,'compute',compute),patch.object(shadow.ObservedHost,'advance_active',observe):
  match=run_balanced_matrix(policy,policy,pairs_per_cell=4,seed=0x8600000000000000,sampling_seed=860927,batch=4,max_seconds=500,host_factory=LearningHost)
 result={'schema':'shards-reroll-denial-audit-v1','policy':selection.metadata,'scope':'Frozen selfplay,160balancedhero/seatgames. No action intervention. Selection counts do not establish denial quality or intent. Opponent affinity uses its latest observed own permanent-collection summary for auditing only, potentially stale after intervening ownership changes; never injected into the policy.',
 'match':match,'counts':dict(counts),'cards':dict(by_card),'faction_affinity_bins':dict(bins),'examples':examples,'composition_alias':{k:v for k,v in fixture.items() if k!='cases'},'fixture_responses':responses,'frozen_weights_unchanged':before==shadow.checkpoints.policy_hash(policy),'cuda_initialized':torch.cuda.is_initialized()}
 assert result['frozen_weights_unchanged'] and not torch.cuda.is_initialized()
 save_json(args.output,result);print(json.dumps({'complete':match['complete'],'games':match.get('games'),'counts':dict(counts),'responses':responses},indent=2))
if __name__=='__main__':main()
