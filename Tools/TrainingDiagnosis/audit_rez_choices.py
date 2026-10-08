"""Recount actual Rez decisions from lossless development traces; no new games."""
import argparse
from collections import Counter
import json
from pathlib import Path
import sys

ROOT=Path(__file__).resolve().parents[2]
sys.path.insert(0,str(ROOT/'Tools/ZeroDepthTraining'))
import numpy as np
import torch
from league import sha256_file, save_json
from search_experience import dense_rows


def audit(experiment,policy):
    result=json.loads((experiment/'result.json').read_text())
    if result['state']!='complete':raise ValueError('Require completed development games')
    payload=torch.load(policy/'policy.pt',map_location='cpu',weights_only=True)
    catalog=payload['catalog'];del payload
    contexts=catalog['contexts'];scry_index=contexts.index('soi.scry')
    card_ids=catalog.get('card_ids',catalog.get('cards'));longshot=card_ids.index('longshot')+1
    games={(g['seed'],g['learner_seat']):g for g in result['completed_games']}
    counts=Counter();by_game={};sources=[]
    for item in result['experience_files']:
        file=experiment/item['file']
        if sha256_file(file)!=item['sha256']:raise ValueError('Experience artifact changed')
        sources.append(dict(file=str(file),sha256=item['sha256']))
        with np.load(file) as z:data={k:z[k] for k in z.files}
        for start in range(0,len(data['actions']),256):
            indices=np.arange(start,min(start+256,len(data['actions'])))
            packet=dense_rows(data,indices)
            for row,index in zip(packet,indices):
                seat=int(data['meta'][index,1]);key=int(data['seeds'][index]),seat
                game=games[key]
                if game['heroes'][seat]!='rez':continue
                stats=by_game.setdefault(f'{key[0]}:{seat}',Counter())
                def add(name,n=1):counts[name]+=n;stats[name]+=n
                obs=row[:24576];candidates=row[24576:-64].reshape(64,48);mask=row[-64:]>0
                action=int(data['actions'][index])
                if not mask[action]:raise ValueError('Recorded action is illegal')
                chosen=candidates[action];kind=int(chosen[:16].argmax())
                ability_legal=bool(((candidates[:,9]>.5)&mask).any())
                add('actual_decisions');add('hero_available_decisions',int(ability_legal))
                add('hero_activations',int(kind==9));add('end_turn_decisions',int(kind==10))
                add('ended_turn_with_unused_legal_hero_power',int(kind==10 and ability_legal))
                is_scry=obs[144]>.5 and round(float(obs[157])*len(contexts))==scry_index
                if is_scry:
                    selected=round(float(obs[150])*1000);size=round(float(obs[149])*384)
                    add('scry_decisions');add('scry_menus',int(selected==0))
                    add('scry_card_selections',int(kind==12));add('scry_finish_actions',int(kind==13))
                    if kind==13:
                        add('scry_keep_all',int(selected==0));add('scry_completed_menus');add('scry_cards_bottomed',selected)
                    elif kind==12 and selected+1==size:
                        add('scry_completed_menus');add('scry_bottom_all');add('scry_cards_bottomed',size)
                facts=obs[17152:20224].reshape(384,8)
                known=(facts[:,1]==np.float32(1/3))&(facts[:,2]==0)&(facts[:,3]==0)&(facts[:,4]>.5)&(facts[:,5]<.5)&(facts[:,6]>.5)
                known_top=int(known.sum())==1
                if kind==8:
                    add('rerolls');add('free_rerolls',int(obs[46]==0));add('rerolls_with_exactly_known_top',int(known_top))
                if kind==0 and round(float(chosen[16])*192)==longshot:
                    add('longshots_played');add('longshot_with_unused_legal_hero_power',int(ability_legal))
    if counts['scry_menus']!=counts['scry_completed_menus']:raise ValueError('Scry menu accounting is incomplete')
    return dict(experiment=str(experiment),games=len(by_game),counts=dict(counts),by_game=by_game,
        sources=sources,result_sha256=sha256_file(experiment/'result.json'),
        interpretation='Actual decision counts, not hypothetical search branches. Unused power and Longshot timing identify review candidates, not proven mistakes. Longshot reveals and optionally fast-plays center cards; it has no hero-used damage bonus.',
        limitations='Small selected development sample; no causal or strength claim. A known monster may be the top card and is not directly acquirable.')


if __name__=='__main__':
    parser=argparse.ArgumentParser(description=__doc__)
    for name in ('experiment','policy','output'):parser.add_argument('--'+name,type=Path,required=True)
    a=parser.parse_args();report=audit(a.experiment,a.policy);save_json(a.output,report)
    print(json.dumps(dict(games=report['games'],counts=report['counts']),indent=2))
