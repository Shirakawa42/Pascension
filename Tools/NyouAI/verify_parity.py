"""Compare native output to the exact frozen training model on recorded engine states."""
import argparse,json,sys
from pathlib import Path
ROOT=Path(__file__).resolve().parents[2]
sys.path[:0]=[str(ROOT/'Tools/ZeroDepthTraining'),str(ROOT/'Tools/TrainingDiagnosis')]
import numpy as np
import torch
from league import load_frozen_policy
from scry_policy import calibrate
p=argparse.ArgumentParser();p.add_argument('policy',type=Path);p.add_argument('fixtures',type=Path);p.add_argument('report',type=Path);a=p.parse_args()
torch.set_num_threads(1)
policy,manifest=load_frozen_policy(a.policy,allow_external_calibration=True)
rows=np.fromfile(a.fixtures,dtype='<f4').reshape(-1,24576+3072+64+65)
x=torch.from_numpy(rows[:,:24576].copy());c=torch.from_numpy(rows[:,24576:27648].copy()).reshape(-1,64,48);m=torch.from_numpy(rows[:,27648:27712].copy())
with torch.no_grad():
 policy.cache_frozen_table();z,v=policy(x,c,m);z=calibrate(z,x,c,m,context_count=26,scry_context=18,temperature=64.)
native=torch.from_numpy(rows[:,27712:27776]);native_value=torch.from_numpy(rows[:,-1])
pdiff=(native.softmax(-1)-z.softmax(-1)).abs().max().item();vdiff=(native_value-v).abs().max().item();logit=(native-z).abs()[m.bool()].max().item()
report=dict(passed=pdiff<.001 and vdiff<.001,rows=len(rows),max_probability_error=pdiff,max_value_error=vdiff,max_raw_logit_error=logit,
            greedy_agreement=(native.argmax(-1)==z.argmax(-1)).float().mean().item(),source_policy_sha256=manifest['policy_file_sha256'],scry_rows=int(((x[:,157]*26).round()==18).sum()),device='cpu')
a.report.write_text(json.dumps(report,indent=2)+'\n');print(json.dumps(report));raise SystemExit(0 if report['passed'] else 1)
