"""CPU-only FP16 transport parity check on saved authorized observations.

This tests FP32 -> FP16 -> FP32 inputs, not half-precision model execution.
Parameters are deterministic untrained weights; this is not strength evidence.
The fixed one-thread/row bounds keep it a small validation probe. No CUDA calls.
"""
from __future__ import annotations

import argparse
import hashlib
from pathlib import Path

import numpy as np
import torch

from bench_common import save_json, source_fingerprint
from model import CandidatePolicy, validate_legal_mask


def half_roundtrip(tensor):
    return tensor.to(torch.float16).to(torch.float32)


def new_metrics():
    return {
        "max_legal_logit_abs_error":0.0,
        "max_probability_abs_error":0.0,
        "max_row_total_variation":0.0,
        "sum_row_total_variation":0.0,
        "max_selected_log_probability_abs_error":0.0,
        "argmax_changes":0,
        "rows":0,
    }


def update_metrics(metrics, original, quantized, mask, actions):
    logp=original.log_softmax(-1)
    quantized_logp=quantized.log_softmax(-1)
    probability_error=(logp.exp()-quantized_logp.exp()).abs()
    tv=probability_error.sum(-1)*0.5
    selected_error=(logp.gather(1,actions[:,None])-quantized_logp.gather(1,actions[:,None])).abs()
    metrics["max_legal_logit_abs_error"]=max(metrics["max_legal_logit_abs_error"],float((original-quantized)[mask].abs().max()))
    metrics["max_probability_abs_error"]=max(metrics["max_probability_abs_error"],float(probability_error.max()))
    metrics["max_row_total_variation"]=max(metrics["max_row_total_variation"],float(tv.max()))
    metrics["sum_row_total_variation"]+=float(tv.sum())
    metrics["max_selected_log_probability_abs_error"]=max(metrics["max_selected_log_probability_abs_error"],float(selected_error.max()))
    metrics["argmax_changes"]+=int((original.argmax(-1)!=quantized.argmax(-1)).sum())
    metrics["rows"]+=len(original)


def main():
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--fixture",type=Path,default=Path(__file__).with_name("results")/"real-states.npz")
    parser.add_argument("--rows",type=int,default=2048)
    parser.add_argument("--batch",type=int,default=64)
    parser.add_argument("--width",type=int,choices=(128,256,512),default=256)
    parser.add_argument("--seed",type=int,default=20260925)
    parser.add_argument("--output",type=Path,required=True)
    args=parser.parse_args()
    if not 1<=args.rows<=2048 or not 1<=args.batch<=128:
        parser.error("Require rows 1..2048 and batch 1..128")
    if args.fixture.stat().st_size>128*1024*1024:
        parser.error("Fixture exceeds the 128 MiB validation input bound")
    torch.set_num_threads(1)
    torch.set_num_interop_threads(1)
    torch.manual_seed(args.seed)
    with np.load(args.fixture,allow_pickle=False) as fixture:
        observations=fixture["obs"]
        candidates=fixture["candidates"]
        masks=fixture["mask"]
        if observations.ndim!=2 or observations.shape[1]!=2048 or len(observations)<1:
            raise ValueError("Expected nonempty observation shape [N,2048]")
        n=min(args.rows,len(observations))
        indices=np.linspace(0,len(observations)-1,n,dtype=np.int64)
        obs=torch.from_numpy(observations[indices].astype(np.float32,copy=True))
        candidate=torch.from_numpy(candidates[indices].astype(np.float32,copy=True))
        mask_float=torch.from_numpy(masks[indices].astype(np.float32,copy=True))
        actions=torch.from_numpy(fixture["actions"][indices].astype(np.int64,copy=True))
    if candidate.shape!=(n,64,32) or mask_float.shape!=(n,64) or actions.shape!=(n,):
        raise ValueError("Unexpected candidate/mask/action fixture shapes")
    if not bool(((mask_float==0)|(mask_float==1)).all()):
        raise ValueError("Fixture mask must contain only exact zero and one")
    mask=mask_float.bool()
    validate_legal_mask(mask)
    if bool(((actions<0)|(actions>=64)).any()) or not bool(mask.gather(1,actions[:,None]).all()):
        raise ValueError("Fixture includes an illegal recorded action")
    if not all(bool(torch.isfinite(value).all()) for value in (obs,candidate,mask_float)):
        raise ValueError("Nonfinite source fixture")
    quantized_obs,quantized_candidate,quantized_mask=map(half_roundtrip,(obs,candidate,mask_float))
    if not all(bool(torch.isfinite(value).all()) for value in (quantized_obs,quantized_candidate,quantized_mask)):
        raise ValueError("FP16 transport overflowed a real observation")
    if not torch.equal(mask_float,quantized_mask):
        raise AssertionError("FP16 changed a legal action mask")

    # 0 is no-card/padding. The other codes cover all 192 reserved definition
    # slots, not a claim that the catalog contains 192 actual definitions.
    codes=torch.arange(193)
    recovered_codes=(half_roundtrip(codes.float()/192)*192).round().long()
    if not torch.equal(codes,recovered_codes):
        raise AssertionError("FP16 changed a categorical card identity")
    original_ids=(candidate[...,16]*192).round().long()
    quantized_ids=(quantized_candidate[...,16]*192).round().long()
    if not torch.equal(original_ids,quantized_ids):
        raise AssertionError("FP16 changed a real fixture candidate identity")

    model=CandidatePolicy(width=args.width,scorer="embedded").eval()
    raw_metrics,exercise_metrics=new_metrics(),new_metrics()
    max_value_error=0.0
    bias=torch.tensor([4,2,2,1,3,2,1,1,0,1,-4,-20,0,0,0,0],dtype=torch.float32)
    with torch.inference_mode():
        for first in range(0,n,args.batch):
            last=min(n,first+args.batch)
            logits,values=model(obs[first:last],candidate[first:last],mask[first:last])
            quantized_logits,quantized_values=model(quantized_obs[first:last],quantized_candidate[first:last],quantized_mask[first:last].bool())
            if not all(bool(torch.isfinite(value).all()) for value in (logits,values,quantized_logits,quantized_values)):
                raise AssertionError("Nonfinite policy output after transport quantization")
            max_value_error=max(max_value_error,float((values-quantized_values).abs().max()))
            update_metrics(raw_metrics,logits,quantized_logits,mask[first:last],actions[first:last])
            exercise=logits*.02+candidate[first:last,:,:16]@bias
            quantized_exercise=quantized_logits*.02+quantized_candidate[first:last,:,:16]@bias
            update_metrics(exercise_metrics,exercise,quantized_exercise,mask[first:last],actions[first:last])
    for metrics in (raw_metrics,exercise_metrics):
        metrics["mean_row_total_variation"]=metrics.pop("sum_row_total_variation")/metrics["rows"]

    # Numeric amounts are intentionally lossy even though masks/identity survive.
    # This synthetic boundary probe is not a claim these powers appeared in play.
    amounts=torch.arange(10001)
    restored_amounts=(half_roundtrip(amounts.float()/1000)*1000).round().long()
    changed=torch.nonzero(restored_amounts!=amounts).flatten()
    passed=(max_value_error<=0.02 and raw_metrics["max_legal_logit_abs_error"]<=0.02
            and raw_metrics["max_probability_abs_error"]<=0.002
            and raw_metrics["max_row_total_variation"]<=0.005)
    result={
        "passed":passed,"scope":"CPU FP32 model with FP16-roundtripped inputs; untrained weights; numerical validation only",
        "configuration":vars(args)|{"fixture":str(args.fixture),"output":str(args.output)},
        "torch":torch.__version__,"device":"cpu","cpu_threads":1,
        "source_sha256":source_fingerprint(),
        "fixture_sha256":hashlib.sha256(args.fixture.read_bytes()).hexdigest(),
        "rows_checked":n,"all_identity_codes_tested":193,"all_identity_codes_preserved":True,
        "real_candidate_ids_preserved":True,"legal_masks_preserved":True,
        "all_inputs_and_outputs_finite":True,
        "max_observation_abs_error":float((obs-quantized_obs).abs().max()),
        "max_candidate_feature_abs_error":float((candidate-quantized_candidate).abs().max()),
        "max_value_abs_error":max_value_error,
        "raw_policy":raw_metrics,"exercise_policy":exercise_metrics,
        "acceptance_thresholds":{"value_abs":0.02,"legal_logit_abs":0.02,"probability_abs":0.002,"row_total_variation":0.005},
        "numeric_amount_probe":{"scale":1000,"integer_range":[0,10000],
            "changed_integer_values":int(len(changed)),
            "first_examples":[{"original":int(i),"restored":int(restored_amounts[i])} for i in changed[:8]],
            "interpretation":"Lossy numeric transport; no guarantee for unseen large counts or a future trained model"},
    }
    save_json(args.output,result)
    print({key:result[key] for key in ("passed","rows_checked","all_identity_codes_preserved","max_value_abs_error")})
    if not passed:
        raise SystemExit(1)


if __name__=="__main__":
    main()
