"""Export the selected full-information policy for the native in-game runtime."""
import argparse, hashlib, json, struct, sys
from pathlib import Path
ROOT=Path(__file__).resolve().parents[2]
sys.path.insert(0,str(ROOT/'Tools/ZeroDepthTraining'))
import torch
from league import load_frozen_policy

def export(source, output):
    torch.set_num_threads(1)
    policy, manifest=load_frozen_policy(source,allow_external_calibration=True)
    if policy.config.width!=512 or policy.config.key_width!=64:raise ValueError('Unsupported architecture')
    if policy.bag_layout!=(256,192,24,10.):raise ValueError('Unexpected histogram schema')
    if policy.catalog['observation_schema']!='shards-zero-depth-observation-v2':raise ValueError('Unexpected observation schema')
    calibration=manifest['required_inference_calibration']
    if calibration!={'scry_temperature':64.,'scry_finish_bias':0.}:raise ValueError('Unexpected calibration')
    names=['trunk.0.weight','trunk.0.bias','trunk.2.weight','trunk.2.bias','query.weight','query.bias',
           'candidate.0.weight','candidate.0.bias','candidate_bias.weight','value.weight','value.bias',
           'zone_projection.weight','menu_projection.weight','kind_prior','optional_exploration']
    state=policy.state_dict(); arrays={n:state[n] for n in names}
    arrays['embedding_table']=policy.embedding_table().detach()
    arrays['known_projection']=(state['trunk.0.weight'][:,178:256]-state['known_top_anchor'])*state['known_top_enabled']
    output.parent.mkdir(parents=True,exist_ok=True)
    with output.open('wb') as f:
        f.write(struct.pack('<5if',0x534F4932,24576,48,512,len(policy.catalog['card_ids']),64.))
        f.write(struct.pack('<i',len(policy.catalog['card_ids'])))
        for card in policy.catalog['card_ids']:
            b=card.encode();f.write(struct.pack('<i',len(b)));f.write(b)
        f.write(struct.pack('<i',len(arrays)))
        for name,t in arrays.items():
            b=name.encode();a=t.detach().cpu().contiguous().numpy().astype('<f4')
            f.write(struct.pack('<i',len(b)));f.write(b);f.write(struct.pack('<i',a.size));f.write(a.tobytes())
    metadata=dict(schema='shards-nyou-release-v1',display_name='Nyou Haïai',source_policy_sha256=manifest['policy_file_sha256'],
        policy_sha256=hashlib.sha256(output.read_bytes()).hexdigest(),width=512,parameters=sum(p.numel() for p in policy.parameters()),
        observation_schema=policy.catalog['observation_schema'],required_inference_calibration=calibration,
        search_settings_resource='AI/nyou-search-settings',runtime='native C# CPU inference and background public-information search',
        note='Frozen model from the October 8 statistics run; CPU/GPU arithmetic is not bit-identical. Original AI remains separately available.')
    output.with_name('nyou-policy-metadata.json').write_text(json.dumps(metadata,indent=2)+'\n')
    print(json.dumps(metadata))
if __name__=='__main__':
    p=argparse.ArgumentParser();p.add_argument('source',type=Path);p.add_argument('output',type=Path);a=p.parse_args();export(a.source,a.output)
