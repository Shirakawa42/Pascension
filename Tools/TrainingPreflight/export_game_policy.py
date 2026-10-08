"""Export frozen policy tensors and real-observation parity fixtures, never train."""
import importlib
import argparse,json,struct,sys,hashlib
from pathlib import Path
import numpy as np
import torch
p=argparse.ArgumentParser();p.add_argument('--runtime',type=Path,required=True);p.add_argument('--checkpoint',type=Path,required=True);p.add_argument('--output',type=Path,required=True);p.add_argument('--fixtures',type=Path);p.add_argument('--runtime-module',default='variant_v12_runtime');p.add_argument('--catalog',type=Path);a=p.parse_args()
sys.path[:0]=[str(a.runtime/'Tools/TrainingPreflight/experiments'),str(a.runtime/'Tools/TrainingPreflight')]
runtime_module=importlib.import_module(a.runtime_module)
install,variant_identity=runtime_module.install,runtime_module.variant_identity
install()
import evaluate_checkpoints as e,learning_model
e.identity=variant_identity;e.LearningPolicy=learning_model.LearningPolicy
selection=e.load_selection(a.checkpoint,'learner');policy=e.materialize_policy(selection,'cpu');torch.set_num_threads(1)
with torch.inference_mode():
 tensors={k:v.detach().float().cpu().numpy() for k,v in policy.state_dict().items()}
 table=policy.semantic_table().cpu();tensors['semantic_table']=table.numpy();tensors['combined_embeddings']=(table+policy.core.card_embedding.weight).numpy()
 a.output.parent.mkdir(parents=True,exist_ok=True)
 with a.output.open('wb') as f:
  f.write(struct.pack('<iif?i',0x534f4931,policy.config.width,policy.config.input_clip,policy.config.value_tanh,len(tensors)))
  for name,array in tensors.items():
   raw=name.encode();f.write(struct.pack('<i',len(raw)));f.write(raw);f.write(struct.pack('<i',array.size));f.write(array.astype('<f4').tobytes())
 metadata=dict(selection.metadata,export_sha256=hashlib.sha256(a.output.read_bytes()).hexdigest(),format='native-generic-effects-v1',training=False)
 # Unity Resources uses extensionless names: metadata must not collide with
 # the binary policy TextAsset at the same resource path.
 metadata_path=a.output.with_name(a.output.stem+'-metadata.json')
 metadata_path.write_text(json.dumps(metadata,indent=2))
 if a.fixtures:
  from wire_v10 import Host
  host=Host(8,2,seed=0x7400000000000000,split_branches=8)
  fixtures=[]
  try:
   for step in range(600):
    obs=torch.as_tensor(host.obs);cand=torch.as_tensor(host.candidates);mask=torch.as_tensor(host.mask)
    logits,values=policy(obs,cand,mask);probs=logits.softmax(-1)
    if step%3==0:
     row=step%8;fixtures.append(dict(observation=obs[row].tolist(),candidates=cand[row].flatten().tolist(),mask=mask[row].tolist(),probabilities=probs[row].tolist(),value=values[row].item()))
    actions=torch.multinomial(probs,1).flatten().numpy().astype(np.int32);host.advance(actions)
  finally:host.close()
  a.fixtures.parent.mkdir(parents=True,exist_ok=True);a.fixtures.write_text(json.dumps(fixtures))
 print(json.dumps(dict(export=str(a.output),metadata=str(metadata_path),sha256=metadata['export_sha256'])))
