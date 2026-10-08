"""Collect authorized real-engine observations for numerical/performance probes."""
import argparse
from pathlib import Path

import numpy as np

from bench_common import save_json, source_fingerprint
from pipeline_bench import Host


def main():
    p=argparse.ArgumentParser(description=__doc__)
    p.add_argument("--output",type=Path,required=True)
    p.add_argument("--steps",type=int,default=1024)
    args=p.parse_args()
    if not 1 <= args.steps <= 4096:
        p.error("Bounded collector requires1..4096steps")
    host=Host(64,4,seed=90817,transport="shared",split_branches=8)
    rng=np.random.default_rng(27182)
    obs,candidates,masks,recorded_actions=[],[],[],[]
    try:
        for step in range(args.steps):
            if step%8==0:
                indices=rng.choice(64,size=16,replace=False)
                obs.append(host.obs[indices].copy())
                candidates.append(host.candidates[indices].copy())
                masks.append(host.mask[indices].copy())
            bias=np.asarray([4,2,2,1,3,2,1,1,0,1,-4,-20,0,0,0,0],dtype=np.float32)
            cumulative=(np.exp(host.candidates[:,:,:16]@bias)*host.mask).cumsum(axis=1)
            action=(cumulative<rng.random((64,1))*cumulative[:,-1:]).sum(axis=1)
            if step%8==0:
                recorded_actions.append(action[indices].astype(np.int64))
            host.advance(action)
        obs,candidates,mask=map(np.concatenate,(obs,candidates,masks))
        codes=np.unique(np.round(candidates[:,:,16][mask.astype(bool)]*192).astype(int))
        args.output.parent.mkdir(parents=True,exist_ok=True)
        np.savez_compressed(args.output,obs=obs,candidates=candidates,mask=mask,
                            actions=np.concatenate(recorded_actions))
        save_json(args.output.with_suffix(".json"),{
            "source_sha256":source_fingerprint(),"rows":len(obs),"steps":args.steps,
            "wrapper_steps":host.metrics[2],"engine_submissions":host.metrics[3],
            "completed_games":host.metrics[4],"truncated_games":host.metrics[5],
            "decision_rows":int((obs[:,4]>0).sum()),"out_of_turn_rows":int((obs[:,1]==0).sum()),
            "candidate_kinds_seen":np.flatnonzero(candidates[:,:,:16].sum(axis=(0,1))).tolist(),
            "card_definition_ids_seen":codes[codes!=0].tolist(),
            "no_card_sentinel_seen":bool(np.any(codes==0)),
            "legal_count_mean":float(mask.sum(1).mean()),"legal_count_max":float(mask.sum(1).max()),
            "scope":"Untrained exercise distribution; no claim of rare-effect or optimal-play coverage"})
        print(f"Saved {len(obs)} real observations to {args.output}")
    finally:
        host.close()


if __name__=="__main__":
    main()
