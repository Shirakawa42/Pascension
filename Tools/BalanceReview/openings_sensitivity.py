"""Joint-market and mediator sensitivity checks for opening evidence."""
from openings_analysis import *
ids=[r['id'] for r in offers];features=np.array([[float(id in o['market']) for id in ids] for o in opening]);joint=np.concatenate([X,features],axis=1)
invj=np.linalg.pinv(joint.T@joint);coeff=invj@joint.T@y;res=y-joint@coeff;lev=np.einsum('ij,jk,ik->i',joint,invj,joint);u=joint*(res/np.maximum(1-lev,.01))[:,None];covj=invj@(u.T@u)@invj
rows=[]
for j,id in enumerate(ids):
 k=X.shape[1]+j;b=float(coeff[k]);se=math.sqrt(covj[k,k]);rows.append(dict(id=id,name=cards[id]['name'],adjustment=dict(adjusted_difference=b,ci95=[b-1.96*se,b+1.96*se],p=math.erfc(abs(b/se)/math.sqrt(2)))))
bh(rows)
# Six market slots form a composition. Drop one count column to identify the
# model, then report invariant contrasts against the observed slot-weighted mean
# card rather than coefficients against an arbitrary reference definition.
counts=np.array([[o['market'].count(id) for id in ids] for o in opening],dtype=float)
assert np.all(counts.sum(axis=1)==6)
composition=np.concatenate([X,counts[:,:-1]],axis=1)
invc=np.linalg.pinv(composition.T@composition);bc=invc@composition.T@y
rc=y-composition@bc;hc=np.einsum('ij,jk,ik->i',composition,invc,composition)
uc=composition*(rc/np.maximum(1-hc,.01))[:,None];vc=invc@(uc.T@uc)@invc
weights=counts.sum(axis=0)/counts.sum()
contrasts=np.zeros((len(ids),composition.shape[1]))
contrasts[:,X.shape[1]:]=np.eye(len(ids))[:,:-1]-weights[:-1][None,:]
effects=contrasts@bc;variances=np.einsum('ij,jk,ik->i',contrasts,vc,contrasts)
composition_rows=[]
for j,id in enumerate(ids):
 b=float(effects[j]);se=math.sqrt(max(0,float(variances[j])))
 composition_rows.append(dict(id=id,name=cards[id]['name'],observed_slot_share=float(weights[j]),
  adjustment=dict(adjusted_difference=b,ci95=[b-1.96*se,b+1.96*se],p=math.erfc(abs(b/se)/math.sqrt(2)) if se else None)))
bh(composition_rows)
diagnostics=dict(binary_design_columns=joint.shape[1],binary_design_rank=int(np.linalg.matrix_rank(joint)),
 binary_design_condition=float(np.linalg.cond(joint)),composition_design_columns=composition.shape[1],
 composition_design_rank=int(np.linalg.matrix_rank(composition)),
 distinct_market_cards=dict(collections.Counter(int(v) for v in features.sum(axis=1))),
 composition_weighted_contrast_mean=float(weights@effects))
# Fixed round milestone outcomes, no adjustment for downstream selected relic or game duration.
details={}
for id in ['giga_source_adept','j_chord_duel','shard_abstractor']:
 groups=[]
 for offered in [True,False]:
  selected=[g for g,o in zip(games,opening) if (id in o['market'])==offered];n=len(selected)
  groups.append(dict(initially_offered=offered,games=n,seat0_wins=sum(g['winner']==0 for g in selected),mean_rounds=sum(g['round'] for g in selected)/n,
   seat0_m10_by6=sum(0<g['players'][0]['mastery10_round']<=6 for g in selected)/n,
   seat0_m20_by8=sum(0<g['players'][0]['mastery20_round']<=8 for g in selected)/n,
   seat0_mastery_wins=sum(g['winner']==0 and g['victory']=='mastery' for g in selected)/n,
   seat0_damage_wins=sum(g['winner']==0 and g['victory']=='normal_damage' for g in selected)/n,
   seat0_recorded_card_plays_mean=sum(g['players'][0]['played'].get(id,0) for g in selected)/n,
   examples=[dict(seed=g['seed'],number=int(g['seed'],16)-8003000000000000000+1,winner=g['winner'],round=g['round']) for g in selected[:5]]))
 details[id]=groups
# Post-discovery equal seed-half stability check; not a held-out confirmation.
median=sorted(g['seed'] for g in games)[len(games)//2]
halves=[]
for second in [False,True]:
 rows0=[g for g in games if (g['seed']>=median)==second]
 halves.append(dict(second_half=second,offered=score([g for g in rows0 if 'giga_source_adept' in openings[g['seed']]['market']]),absent=score([g for g in rows0 if 'giga_source_adept' not in openings[g['seed']]['market']])))
report=dict(schema='shards-opening-sensitivity-v2',joint_market_rows=rows,joint_market_count_contrasts=composition_rows,
 design_diagnostics=diagnostics,details=details,giga_seed_halves=halves,
 note='Joint linear models control all 90 opening market definitions, ordered hero matchups and both opening hands\' base gems; HC3 intervals. Binary-presence coefficients condition on other presences; because slots are fixed, these can represent replacing duplicates, not freely adding a card. The count-composition model instead reports the fitted difference for replacing one card drawn from the observed slot distribution with the named card, holding the other five slots fixed, under an additive model. This is an adjusted association, not a causal guarantee. These sensitivity analyses and the seed-half check follow signal discovery and are not independent validation. Fixed-round milestones are descriptive potential mechanisms, not formal causal mediation. Recorded card plays exclude immediate fast-play effects unless separately played from hand.')
(OUT/'openings_sensitivity.json').write_text(json.dumps(report,indent=2)+'\n')
print(json.dumps(dict(significant_joint=[r for r in rows if r['adjustment']['q']<.1],details=details,giga_halves=halves),indent=2))
